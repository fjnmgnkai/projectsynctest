using System.Diagnostics;
using ProjectSync.Core;
using ProjectSync.Infrastructure;

namespace ProjectSync.SpecTests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] is "--live-start" or "--live-save" or "--live-submit")
        {
            // Explicit opt-in: these commands mutate and, for save, push the selected real repository.
            var workspace = new LocalTaskWorkspace(args[1]);
            if (args[0] == "--live-start")
            {
                var started = await workspace.StartAsync("ProjectSync 実Commit・Push確認");
                Console.WriteLine(started.IsSuccess ? $"BRANCH={started.Value}" : $"ERROR={started.Problem}");
                return started.IsSuccess ? 0 : 1;
            }

            if (args[0] == "--live-submit")
            {
                var submitted = await workspace.SubmitAsync("ProjectSync 実動作確認");
                Console.WriteLine(submitted.IsSuccess
                    ? $"PR={submitted.Value!.Url} SHA={submitted.Value.SubmittedSha}"
                    : $"ERROR={submitted.Problem}");
                return submitted.IsSuccess ? 0 : 1;
            }

            var saved = await workspace.SaveAsync();
            Console.WriteLine(saved.IsSuccess
                ? $"BRANCH={saved.Value!.Branch} SHA={saved.Value.CommitSha}"
                : $"ERROR={saved.Problem}");
            return saved.IsSuccess ? 0 : 1;
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: no arguments, --live-start, --live-save, or --live-submit <Unity clone>.");
            return 2;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("forbidden Git operations", GitPolicyAsync),
            ("local Task start/save/push and recovery", LocalWorkflowAsync),
            ("Unity project lock detection", UnityLockAsync),
            ("submitted SHA body binding", SubmissionBindingAsync),
            ("LFS Scene and large ordinary asset rejection", AssetPolicyAsync)
        };
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {error}");
            }
        }

        Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
        return failed == 0 ? 0 : 1;
    }

    private static Task GitPolicyAsync()
    {
        var id = new OperationId("policy-test");
        var sha = new string('a', 40);
        Check(!GitCommandPolicy.Validate(["reset", "--hard", "HEAD"], id).IsSuccess, "hard reset accepted");
        Check(!GitCommandPolicy.Validate(["stash", "push"], id).IsSuccess, "stash accepted");
        Check(!GitCommandPolicy.Validate(["push", "--force", "origin", "task/a"], id).IsSuccess, "force push accepted");
        Check(!GitCommandPolicy.Validate(["clean", "-fd"], id).IsSuccess, "clean accepted");
        Check(!GitCommandPolicy.Validate(["push", "origin", $"{sha}:refs/heads/main"], id).IsSuccess, "main push accepted");
        Check(GitCommandPolicy.Validate(["push", "--porcelain", "origin", $"{sha}:refs/heads/task/a"], id).IsSuccess, "Task push rejected");
        Check(!TaskBranchPolicy.RequireTaskBranch("main", id, "test").IsSuccess, "main branch accepted");
        var owner = GitCommandFailure.FromResult(
            new GitCommandResult(128, "", "fatal: detected dubious ownership in repository"),
            @"C:\Unity\Team", id, "status");
        Check(owner.ErrorCode == "git_repository_owner_untrusted" &&
              owner.Message.Contains("safe.directory", StringComparison.Ordinal) &&
              owner.Message.Contains("GitHubアカウントの違いではありません", StringComparison.Ordinal),
            "Local ownership failure must be actionable and distinguish GitHub identity");
        return Task.CompletedTask;
    }

    private static async Task LocalWorkflowAsync()
    {
        using var fixture = new LocalGitFixture();
        var workspace = new LocalTaskWorkspace(fixture.Working, unityEditorRunning: () => false);
        var started = await workspace.StartAsync("test");
        Check(started.IsSuccess, "Task start failed: " + started.Problem);
        Check(fixture.Git("rev-parse", "HEAD").Trim() == fixture.MainSha, "Task not based on remote main");
        File.WriteAllText(Path.Combine(fixture.Working, "Assets", "sample.txt"), "first edit");
        File.WriteAllText(Path.Combine(fixture.Working, "Assets.zip"), "local backup");
        fixture.Git("add", "-f", "Assets.zip");
        var saved = await workspace.SaveAsync();
        Check(saved.IsSuccess, "First save failed: " + saved.Problem);
        Check(fixture.RemoteSha(started.Value!) == saved.Value!.CommitSha, "Remote Task SHA mismatch");
        Check(fixture.RemoteSha("main") == fixture.MainSha, "Remote main changed");
        Check(fixture.Git("ls-tree", "-r", "--name-only", "HEAD", "Assets.zip").Trim().Length == 0, "Backup entered commit");
        fixture.Git("rm", "--cached", "--", "Assets.zip");

        File.WriteAllText(Path.Combine(fixture.Working, "Assets", "sample.txt"), "second edit");
        fixture.Git("remote", "set-url", "origin", Path.Combine(fixture.Root, "unavailable.git"));
        var failed = await workspace.SaveAsync();
        Check(!failed.IsSuccess, "Unavailable remote reported success");
        var retainedSha = fixture.Git("rev-parse", "HEAD").Trim();
        Check(retainedSha != saved.Value.CommitSha, "Local commit lost after push failure");
        fixture.Git("remote", "set-url", "origin", fixture.Remote);
        var retried = await new LocalTaskWorkspace(fixture.Working, unityEditorRunning: () => false).SaveAsync();
        Check(retried.IsSuccess, "Push retry failed: " + retried.Problem);
        Check(retried.Value!.CommitSha == retainedSha, "Retry created another commit");
        Check(fixture.RemoteSha(started.Value!) == retainedSha, "Retry did not reach remote");
        Check(fixture.RemoteSha("main") == fixture.MainSha, "Retry changed main");

        var next = await workspace.StartAsync("next");
        Check(next.IsSuccess && fixture.Git("rev-parse", "HEAD").Trim() == fixture.MainSha,
            "Next Task did not start from main");
        var resumed = await workspace.ResumeAsync(started.Value!);
        Check(resumed.IsSuccess && fixture.Git("rev-parse", "HEAD").Trim() == retainedSha,
            "Task resume did not restore Task HEAD");
    }

    private static async Task UnityLockAsync()
    {
        using var fixture = new LocalGitFixture();
        var other = Path.Combine(fixture.Root, "other", "Temp");
        Directory.CreateDirectory(other);
        using var otherLock = new FileStream(Path.Combine(other, "UnityLockfile"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        Check(!LocalTaskWorkspace.IsUnityEditorRunningForProject(fixture.Working), "Other Editor blocked this clone");
        var workspace = new LocalTaskWorkspace(fixture.Working);
        Check((await workspace.StartAsync("other editor open")).IsSuccess, "Other Editor blocked Task start");
        var temp = Path.Combine(fixture.Working, "Temp");
        Directory.CreateDirectory(temp);
        using (var ownLock = new FileStream(Path.Combine(temp, "UnityLockfile"), FileMode.CreateNew,
                   FileAccess.ReadWrite, FileShare.None))
        {
            Check(LocalTaskWorkspace.IsUnityEditorRunningForProject(fixture.Working), "Own Editor not detected");
            var blocked = await workspace.StartAsync("must block");
            Check(!blocked.IsSuccess && blocked.Problem!.ErrorCode == "unity_must_be_closed", "Own Editor did not block switch");
        }

        Check(!LocalTaskWorkspace.IsUnityEditorRunningForProject(fixture.Working), "Stale unlocked lockfile blocked project");
    }

    private static Task SubmissionBindingAsync()
    {
        const string oldSha = "1111111111111111111111111111111111111111";
        const string newSha = "2222222222222222222222222222222222222222";
        var body = $"<!-- ProjectSync-Submitted-SHA: {oldSha} -->\n\n提出Commit: `{oldSha}`\n管理者メモ\n";
        Check(LocalTaskWorkspace.TryUpdateSubmissionBody(body, newSha, out var updated), "Owned body not updated");
        Check(updated.Contains(newSha) && !updated.Contains(oldSha) && updated.Contains("管理者メモ"), "SHA or notes corrupted");
        Check(!LocalTaskWorkspace.TryUpdateSubmissionBody("unowned PR", newSha, out _), "Foreign PR adopted");
        Check(!LocalTaskWorkspace.TryUpdateSubmissionBody(body + body, newSha, out _), "Duplicate marker accepted");
        return Task.CompletedTask;
    }

    private static async Task AssetPolicyAsync()
    {
        using var fixture = new LocalGitFixture();
        fixture.Git("switch", "-c", "task/asset-policy");
        var gateway = new GitCliTaskGateway(fixture.Working);
        var largePath = Path.Combine(fixture.Working, "Assets", "Large.fbx");
        using (var file = new FileStream(largePath, FileMode.CreateNew, FileAccess.Write))
        {
            file.SetLength(101L * 1024 * 1024);
        }

        var large = await gateway.CreateSnapshotAsync("task/asset-policy", "test", new OperationId("large"), default);
        Check(!large.IsSuccess && large.Problem!.ErrorCode == "large_asset_not_lfs", "Large ordinary asset accepted");
        File.Delete(largePath);
        File.WriteAllText(Path.Combine(fixture.Working, "Assets", "Main.unity"), "%YAML 1.1\n");
        File.WriteAllText(Path.Combine(fixture.Working, ".gitattributes"), "*.unity filter=lfs diff=lfs merge=lfs -text\n");
        var scene = await gateway.CreateSnapshotAsync("task/asset-policy", "test", new OperationId("scene"), default);
        Check(!scene.IsSuccess && scene.Problem!.ErrorCode == "scene_lfs_conflicts_with_merge", "LFS Scene accepted");
        Check(fixture.Git("rev-parse", "HEAD").Trim() == fixture.MainSha, "Rejected content committed");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class LocalGitFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "projectsync-tests", Guid.NewGuid().ToString("N"));
        public string Remote => Path.Combine(Root, "remote.git");
        public string Working => Path.Combine(Root, "working");
        public string MainSha { get; }

        public LocalGitFixture()
        {
            Directory.CreateDirectory(Root);
            RunGit(Root, "init", "--bare", Remote);
            RunGit(Root, "init", "-b", "main", Working);
            Git("config", "user.name", "ProjectSync Test");
            Git("config", "user.email", "test@example.invalid");
            Directory.CreateDirectory(Path.Combine(Working, "Assets"));
            Directory.CreateDirectory(Path.Combine(Working, "Packages"));
            Directory.CreateDirectory(Path.Combine(Working, "ProjectSettings"));
            File.WriteAllText(Path.Combine(Working, "Assets", "sample.txt"), "baseline");
            File.WriteAllText(Path.Combine(Working, "Packages", "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(Working, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: test");
            File.WriteAllText(Path.Combine(Working, ".gitattributes"), "*.unity text eol=lf\n");
            File.WriteAllText(Path.Combine(Working, ".gitignore"), "/UserSettings/\n/Assets.zip\n");
            Git("add", "Assets", "Packages", "ProjectSettings", ".gitattributes", ".gitignore");
            Git("commit", "-m", "baseline");
            Git("remote", "add", "origin", Remote);
            Git("push", "origin", "main");
            MainSha = Git("rev-parse", "HEAD").Trim();
        }

        public string Git(params string[] args) => RunGit(Working, args);
        public string RemoteSha(string branch) => Git("ls-remote", "--heads", "origin", "refs/heads/" + branch).Split('\t')[0];

        public void Dispose()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "projectsync-tests"));
            var path = Path.GetFullPath(Root);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture outside allowed test root");
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(path, recursive: true);
        }

        private static string RunGit(string directory, params string[] args)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Git did not start");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
            return output;
        }
    }
}
