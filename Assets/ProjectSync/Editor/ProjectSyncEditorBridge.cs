using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectSync.UnityEditor
{
    [InitializeOnLoad]
    internal static class ProjectSyncEditorBridge
    {
        private const int ProtocolVersion = 1;
        private static readonly ConcurrentQueue<PendingRequest> Pending = new ConcurrentQueue<PendingRequest>();
        private static readonly ConcurrentQueue<string> Diagnostics = new ConcurrentQueue<string>();
        private static readonly CancellationTokenSource Shutdown = new CancellationTokenSource();
        private static readonly string PipeName = CreatePipeName();

        static ProjectSyncEditorBridge()
        {
            EditorApplication.update += ProcessMainThreadWork;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            _ = Task.Run(() => ServerLoopAsync(Shutdown.Token));
        }

        private static async Task ServerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(
                               PipeName,
                               PipeDirection.InOut,
                               1,
                               PipeTransmissionMode.Byte,
                               PipeOptions.Asynchronous))
                    using (cancellationToken.Register(pipe.Dispose))
                    {
                        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                        using (var reader = new StreamReader(
                                   pipe,
                                   Encoding.UTF8,
                                   true,
                                   1024,
                                   true))
                        using (var writer = new StreamWriter(
                                   pipe,
                                   new UTF8Encoding(false),
                                   1024,
                                   true))
                        {
                            writer.AutoFlush = true;
                            var line = await reader.ReadLineAsync().ConfigureAwait(false);
                            var response = await DispatchAsync(line, cancellationToken).ConfigureAwait(false);
                            await writer.WriteLineAsync(JsonUtility.ToJson(response)).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException exception)
                {
                    Diagnostics.Enqueue("ProjectSync Unity bridge I/O error: " + exception.Message);
                }
                catch (Exception exception)
                {
                    Diagnostics.Enqueue("ProjectSync Unity bridge error: " + exception);
                }
            }
        }

        private static Task<BridgeResponse> DispatchAsync(string json, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Task.FromResult(BridgeResponse.Failure(
                    string.Empty,
                    "empty_request",
                    "Request body is empty."));
            }

            BridgeRequest request;
            try
            {
                request = JsonUtility.FromJson<BridgeRequest>(json);
            }
            catch (ArgumentException exception)
            {
                return Task.FromResult(BridgeResponse.Failure(
                    string.Empty,
                    "invalid_json",
                    exception.Message));
            }

            if (request == null || request.protocolVersion != ProtocolVersion ||
                string.IsNullOrWhiteSpace(request.operationId))
            {
                return Task.FromResult(BridgeResponse.Failure(
                    request != null ? request.operationId : string.Empty,
                    "protocol_mismatch",
                    "Protocol version and operation ID are required."));
            }

            var completion = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            _ = completion.Task.ContinueWith(
                _ => registration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Pending.Enqueue(new PendingRequest(request, completion));
            return completion.Task;
        }

        private static void ProcessMainThreadWork()
        {
            while (Diagnostics.TryDequeue(out var diagnostic))
            {
                Debug.LogWarning(diagnostic);
            }

            while (Pending.TryDequeue(out var pending))
            {
                try
                {
                    pending.Completion.TrySetResult(Execute(pending.Request));
                }
                catch (Exception exception)
                {
                    pending.Completion.TrySetResult(BridgeResponse.Failure(
                        pending.Request.operationId,
                        "unity_save_exception",
                        exception.Message));
                }
            }
        }

        private static BridgeResponse Execute(BridgeRequest request)
        {
            if (!string.Equals(request.command, "save_all", StringComparison.Ordinal))
            {
                return BridgeResponse.Failure(
                    request.operationId,
                    "unknown_command",
                    "The requested Unity bridge command is not supported.");
            }

            if (EditorApplication.isCompiling)
            {
                return BridgeResponse.Failure(request.operationId, "unity_compiling", "Unity is compiling scripts.");
            }

            if (EditorApplication.isUpdating)
            {
                return BridgeResponse.Failure(request.operationId, "unity_importing", "Unity is importing assets.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return BridgeResponse.Failure(request.operationId, "unity_play_mode", "Exit Play Mode before saving.");
            }

            var scenePaths = new List<string>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.isDirty && string.IsNullOrWhiteSpace(scene.path))
                {
                    return BridgeResponse.Failure(
                        request.operationId,
                        "untitled_scene_requires_user_save",
                        "An unsaved untitled Scene must be named in Unity before ProjectSync can save it.");
                }

                if (!string.IsNullOrWhiteSpace(scene.path))
                {
                    scenePaths.Add(scene.path);
                }
            }

            if (!EditorSceneManager.SaveOpenScenes())
            {
                return BridgeResponse.Failure(
                    request.operationId,
                    "scene_save_failed",
                    "Unity did not confirm that all open Scenes were saved.");
            }

            AssetDatabase.SaveAssets();
            return BridgeResponse.Success(request.operationId, scenePaths.ToArray());
        }

        private static string CreatePipeName()
        {
            var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            byte[] hash;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(projectPath));
            }

            var prefix = BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            return "projectsync-unity-" + prefix;
        }

        private static void Stop()
        {
            if (!Shutdown.IsCancellationRequested)
            {
                Shutdown.Cancel();
            }
        }

        private sealed class PendingRequest
        {
            public PendingRequest(BridgeRequest request, TaskCompletionSource<BridgeResponse> completion)
            {
                Request = request;
                Completion = completion;
            }

            public BridgeRequest Request { get; }
            public TaskCompletionSource<BridgeResponse> Completion { get; }
        }

        [Serializable]
        private sealed class BridgeRequest
        {
            public int protocolVersion;
            public string operationId = string.Empty;
            public string command = string.Empty;
        }

        [Serializable]
        private sealed class BridgeResponse
        {
            public int protocolVersion = ProtocolVersion;
            public string operationId = string.Empty;
            public bool success;
            public string errorCode = string.Empty;
            public string message = string.Empty;
            public string[] scenePaths = new string[0];

            public static BridgeResponse Success(string operationId, string[] scenePaths)
            {
                return new BridgeResponse
                {
                    operationId = operationId,
                    success = true,
                    errorCode = string.Empty,
                    message = "Unity Scenes and Assets were saved.",
                    scenePaths = scenePaths
                };
            }

            public static BridgeResponse Failure(string operationId, string errorCode, string message)
            {
                return new BridgeResponse
                {
                    operationId = operationId,
                    success = false,
                    errorCode = errorCode,
                    message = message,
                    scenePaths = new string[0]
                };
            }
        }
    }
}
