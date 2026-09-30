using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectSync.Core;

namespace ProjectSync.Infrastructure;

public sealed class UnityBridgeClient : IUnitySaveGateway
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public UnityBridgeClient(string unityProjectPath, TimeSpan? connectTimeout = null)
    {
        _pipeName = UnityBridgeProtocol.CreatePipeName(unityProjectPath);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<Outcome<UnitySaveReceipt>> SaveOpenScenesAndAssetsAsync(
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            await using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);
            var request = new UnityBridgeRequest(1, operationId.Value, "save_all");
            await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);

            var responseLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return Failure("unity_bridge_empty_response", operationId, "Unity bridge returned no response.", retryable: true);
            }

            var response = JsonSerializer.Deserialize<UnityBridgeResponse>(responseLine);
            if (response is null || response.ProtocolVersion != 1 ||
                !string.Equals(response.OperationId, operationId.Value, StringComparison.Ordinal))
            {
                return Failure("unity_bridge_protocol_error", operationId, "Unity bridge response did not match the request.", retryable: false);
            }

            if (!response.Success)
            {
                return Failure(
                    string.IsNullOrWhiteSpace(response.ErrorCode) ? "unity_save_failed" : response.ErrorCode,
                    operationId,
                    response.Message,
                    retryable: false);
            }

            return Outcome<UnitySaveReceipt>.Success(new UnitySaveReceipt(
                DateTimeOffset.UtcNow,
                response.ScenePaths ?? []));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                "unity_bridge_outcome_unknown",
                operationId,
                "Unity bridge timed out. Disk state must be inspected before retrying.",
                retryable: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failure("unity_bridge_unavailable", operationId, exception.Message, retryable: true);
        }
    }

    private static Outcome<UnitySaveReceipt> Failure(
        string code,
        OperationId operationId,
        string message,
        bool retryable) =>
        Outcome<UnitySaveReceipt>.Failure(Problem.Create(
            code,
            ProblemCategory.Transport,
            retryable,
            operationId,
            "unity_save",
            message));
}

public static class UnityBridgeProtocol
{
    public static string CreatePipeName(string unityProjectPath)
    {
        var normalized = Path.GetFullPath(unityProjectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "projectsync-unity-" + Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
}

public sealed record UnityBridgeRequest(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("command")] string Command);

public sealed record UnityBridgeResponse(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("errorCode")] string ErrorCode,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("scenePaths")] string[]? ScenePaths);
