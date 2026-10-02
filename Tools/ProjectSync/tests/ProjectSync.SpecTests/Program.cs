using System.Diagnostics;
using ProjectSync.Core;
using ProjectSync.Infrastructure;

namespace ProjectSync.SpecTests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("same-account concurrent start permits exactly one session", ConcurrentStartPermitsOneSessionAsync),
        ("start retry reuses the same issue lock and generation", StartRetryIsIdempotentAsync),
        ("issue success and lock failure enters recovery required", LockFailureAfterIssueRequiresRecoveryAsync),
        ("save retry preserves commit and retries only push", SaveRetryPreservesCommitAsync),
        ("abort cannot complete until remote unlock is confirmed", AbortWaitsForUnlockAsync),
        ("candidate validation is invalidated by every identity change", CandidateIdentityInvalidatesAsync),
        ("build target SHA never follows later main", BuildTargetShaIsFixedAsync),
        ("deployment projection forces auto_merge false", DeploymentProjectionDisablesAutoMergeAsync),
        ("forbidden Git operations are rejected before process spawn", GitPolicyRejectsForbiddenCommandsAsync),
        ("invalid Task branch is rejected before external start effects", InvalidStartBranchHasNoEffectsAsync),
        ("save refuses a branch that differs from coordination state", SaveBranchMismatchHasNoEffectsAsync),
        ("save branch binding survives journal reload", SaveBranchBindingSurvivesReloadAsync),
        ("real Git Task snapshot and push leave main unchanged", GitCliTaskGatewayKeepsMainReadOnlyAsync),
        ("local-first Task save retries the same commit after remote failure", LocalFirstSaveRecoversAsync),
        ("large ordinary assets and LFS Scenes are rejected before commit", AssetPolicyRejectsUnsafeTrackingAsync),
        ("operation journal survives process-memory loss", FileJournalPersistsAsync)
    ];

    public static async Task<int> Main()
    {
        var failures = new List<string>();
        foreach (var (name, run) in Tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures.Add(name);
                Console.Error.WriteLine($"FAIL {name}{Environment.NewLine}{exception}");
            }
        }

        Console.WriteLine($"{Tests.Length - failures.Count}/{Tests.Length} tests passed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task ConcurrentStartPermitsOneSessionAsync()
    {
        var fixture = new StartFixture();
        var first = fixture.CreateRequest("device-a", "session-a", "op-a");
        var second = fixture.CreateRequest("device-b", "session-b", "op-b");

        var results = await Task.WhenAll(
            fixture.Workflow.ExecuteAsync(first),
            fixture.Workflow.ExecuteAsync(second)).ConfigureAwait(false);

        Equal(1, results.Count(result => result.IsSuccess), "Exactly one start must succeed.");
        Equal(1, results.Count(result => !result.IsSuccess), "Exactly one start must be rejected.");
        var state = (await fixture.State.ReadAsync("task-100", default).ConfigureAwait(false)).Value;
        NotNull(state, "Coordination state must exist.");
        Equal(TaskLifecycle.Active, state!.Lifecycle, "Winner must become active.");
        Equal(1L, state.Generation, "Only one generation may be issued.");
        Equal(1, fixture.Issues.UniqueIssueCount, "Only one Task Issue may exist.");
        Equal(1, fixture.Locks.LockCount, "Only one LFS lock may exist.");
    }

    private static async Task StartRetryIsIdempotentAsync()
    {
        var fixture = new StartFixture();
        var request = fixture.CreateRequest("device-a", "session-a", "op-a");
        var first = await fixture.Workflow.ExecuteAsync(request).ConfigureAwait(false);
        var second = await fixture.Workflow.ExecuteAsync(request).ConfigureAwait(false);

        True(first.IsSuccess && second.IsSuccess, "Both the first execution and retry must succeed.");
        Equal(first.Value!.Coordination.Generation, second.Value!.Coordination.Generation, "Retry must not increment generation.");
        Equal(first.Value.Lock.LockId, second.Value.Lock.LockId, "Retry must reuse the same lock.");
        Equal(1, fixture.Issues.UniqueIssueCount, "Retry must not create another Issue.");
        Equal(1, fixture.Locks.AcquireSuccessCount, "Retry must not acquire another lock.");
    }

    private static async Task LockFailureAfterIssueRequiresRecoveryAsync()
    {
        var fixture = new StartFixture();
        fixture.Locks.FailAcquire = true;
        var result = await fixture.Workflow.ExecuteAsync(
            fixture.CreateRequest("device-a", "session-a", "op-a")).ConfigureAwait(false);

        False(result.IsSuccess, "Start must fail when the LFS lock cannot be acquired.");
        var state = (await fixture.State.ReadAsync("task-100", default).ConfigureAwait(false)).Value;
        NotNull(state, "Coordination state must be retained for recovery.");
        Equal(CoordinationHealth.RecoveryRequired, state!.Health, "Partial cross-system success must require recovery.");
        Equal(TaskLifecycle.RecoveryRequired, state.Lifecycle, "Task must be stopped in RecoveryRequired.");
        Equal(1, fixture.Issues.UniqueIssueCount, "The successful Issue must be retained.");
    }

    private static async Task SaveRetryPreservesCommitAsync()
    {
        var state = new InMemoryCoordinationStateStore();
        var active = CreateActiveAggregate("task-save", "lock-save", "Assets/Scenes/Main.unity");
        await SeedAsync(state, active).ConfigureAwait(false);
        var journal = new InMemoryOperationJournal();
        var unity = new FakeUnitySaveGateway();
        var git = new FakeGitTaskGateway { FailPush = true, RemoteReachable = false };
        var workflow = new SaveTaskWorkflow(state, journal, unity, git);
        var request = new SaveTaskRequest(
            "task-save",
            "task/task-save",
            AuthorityFor(active),
            "ProjectSync snapshot",
            new OperationId("save-op"));

        var first = await workflow.ExecuteAsync(request).ConfigureAwait(false);
        False(first.IsSuccess, "First push must fail.");
        Equal(1, unity.SaveCalls, "Unity save must complete once.");
        Equal(1, git.CommitCalls, "Snapshot commit must remain after push failure.");

        var wrongBranchRetry = await workflow.ExecuteAsync(
            request with { TaskBranch = "task/other" }).ConfigureAwait(false);
        False(wrongBranchRetry.IsSuccess, "The operation ID must not be rebound to another Task branch.");
        Equal("operation_branch_reused", wrongBranchRetry.Problem!.ErrorCode, "The journal must bind the branch identity.");
        Equal(1, git.CommitCalls, "Rejected retry must not create another commit.");

        git.FailPush = false;
        git.RemoteReachable = true;
        var retry = await workflow.ExecuteAsync(request).ConfigureAwait(false);
        True(retry.IsSuccess, "Retry must finish after remote reachability is confirmed.");
        Equal(1, unity.SaveCalls, "Retry must not repeat an already committed Unity-save stage.");
        Equal(1, git.CommitCalls, "Retry must not create a second commit.");
        Equal(2, git.PushCalls, "Only push must be retried.");
        Equal("commit-1", retry.Value!.CommitSha, "The original commit must be pushed.");
    }

    private static async Task AbortWaitsForUnlockAsync()
    {
        var state = new InMemoryCoordinationStateStore();
        var active = CreateActiveAggregate("task-abort", "lock-abort", "Assets/Scenes/Main.unity");
        await SeedAsync(state, active).ConfigureAwait(false);
        var journal = new InMemoryOperationJournal();
        var protector = new FakeLocalDataProtector();
        var locks = new FakeLfsLockGateway();
        locks.Seed(new LfsLockReference("lock-abort", "Assets/Scenes/Main.unity", "same-user"));
        locks.FailRelease = true;
        var workflow = new AbortSceneTaskWorkflow(state, journal, protector, locks);
        var request = new AbortSceneTaskRequest(
            "task-abort",
            AuthorityFor(active),
            new OperationId("abort-op"));

        var first = await workflow.ExecuteAsync(request).ConfigureAwait(false);
        False(first.IsSuccess, "Abort must not complete when unlock fails.");
        var pending = (await state.ReadAsync("task-abort", default).ConfigureAwait(false)).Value;
        Equal(TaskLifecycle.UnlockPending, pending!.Lifecycle, "Task must remain UnlockPending.");
        True(pending.ActiveSession is null, "Old session must stay revoked.");

        locks.FailRelease = false;
        var retry = await workflow.ExecuteAsync(request).ConfigureAwait(false);
        True(retry.IsSuccess, "Abort retry must complete after verified unlock.");
        var aborted = (await state.ReadAsync("task-abort", default).ConfigureAwait(false)).Value;
        Equal(TaskLifecycle.Aborted, aborted!.Lifecycle, "Task must become Aborted only after verification.");
        Equal(1, protector.Calls, "Local preservation must not be repeated.");
    }

    private static Task CandidateIdentityInvalidatesAsync()
    {
        var baseline = new CandidateIdentity("submitted", "base", "candidate", "resolution", 3);
        True(baseline.IsValidationApplicableTo(baseline), "Exact identity must reuse validation.");
        False(baseline.IsValidationApplicableTo(baseline with { SubmittedCommitSha = "submitted-2" }), "Submitted SHA change must invalidate.");
        False(baseline.IsValidationApplicableTo(baseline with { BaseMainSha = "base-2" }), "Base Main change must invalidate.");
        False(baseline.IsValidationApplicableTo(baseline with { CandidateSha = "candidate-2" }), "Candidate change must invalidate.");
        False(baseline.IsValidationApplicableTo(baseline with { ConflictResolutionDigest = "resolution-2" }), "Resolution change must invalidate.");
        False(baseline.IsValidationApplicableTo(baseline with { Generation = 4 }), "Generation change must invalidate.");
        return Task.CompletedTask;
    }

    private static Task BuildTargetShaIsFixedAsync()
    {
        var attempt = BuildAttempt.Start(
                "build-1",
                "main-sha-at-start",
                "wrld_test",
                "Windows",
                "builder-a",
                DateTimeOffset.UtcNow)
            .BeginBuild()
            .RecordUpload(UploadOutcome.Success, "upload-receipt")
            .MarkDeploymentRecordPending()
            .Complete(DateTimeOffset.UtcNow.AddMinutes(1));

        Equal("main-sha-at-start", attempt.BuildTargetSha, "BuildTargetSHA must never follow later main.");
        Equal(UploadOutcome.Success, attempt.UploadOutcome, "Upload result must be independently recorded.");
        return Task.CompletedTask;
    }

    private static Task DeploymentProjectionDisablesAutoMergeAsync()
    {
        var attempt = BuildAttempt.Start(
            "build-1",
            "fixed-sha",
            "wrld_test",
            "Windows",
            "builder-a",
            DateTimeOffset.UtcNow);
        var projection = DeploymentProjectionRequest.Create(new OperationId("deploy-op"), "owner/repo", attempt);
        False(projection.AutoMerge, "GitHub Deployment auto_merge must be false.");
        Equal("fixed-sha", projection.BuildTargetSha, "Deployment must target the fixed SHA.");
        return Task.CompletedTask;
    }

    private static Task GitPolicyRejectsForbiddenCommandsAsync()
    {
        var operationId = new OperationId("git-policy");
        var sha = new string('a', 40);
        False(GitCommandPolicy.Validate(["reset", "--hard", "HEAD"], operationId).IsSuccess, "reset must be rejected.");
        False(GitCommandPolicy.Validate(["stash", "push"], operationId).IsSuccess, "stash must be rejected.");
        False(GitCommandPolicy.Validate(["push", "--force", "origin", "task"], operationId).IsSuccess, "force push must be rejected.");
        False(GitCommandPolicy.Validate(["clean", "-fd"], operationId).IsSuccess, "automatic deletion must be rejected.");
        False(GitCommandPolicy.Validate(["push", "--porcelain", "origin", $"{sha}:refs/heads/main"], operationId).IsSuccess, "main push must be rejected.");
        False(GitCommandPolicy.Validate(["push", "--mirror", "origin"], operationId).IsSuccess, "mirror push must be rejected.");
        False(GitCommandPolicy.Validate(["-c", "alias.push=...", "push"], operationId).IsSuccess, "Git global options must not bypass the command allowlist.");
        False(GitCommandPolicy.Validate(["branch", "-D", "task/task-1"], operationId).IsSuccess, "local branch deletion must not use this command path.");
        True(GitCommandPolicy.Validate(["push", "--porcelain", "origin", $"{sha}:refs/heads/task/task-1"], operationId).IsSuccess, "Exact Task ref push should pass policy.");
        False(TaskBranchPolicy.RequireTaskBranch("main", operationId, "test").IsSuccess, "main is not a Task branch.");
        False(TaskBranchPolicy.RequireTaskBranch("task/../main", operationId, "test").IsSuccess, "Path-like traversal is not a Task branch.");
        False(TaskBranchPolicy.RequireTaskBranch("task/a.lock", operationId, "test").IsSuccess, "Git special ref suffixes are not accepted.");
        return Task.CompletedTask;
    }

    private static async Task InvalidStartBranchHasNoEffectsAsync()
    {
        var fixture = new StartFixture();
        var request = fixture.CreateRequest("device-a", "session-a", "invalid-start") with { TaskBranch = "main" };
        var result = await fixture.Workflow.ExecuteAsync(request).ConfigureAwait(false);
        False(result.IsSuccess, "A Scene Task must not use main as its Task branch.");
        Equal("task_branch_invalid", result.Problem!.ErrorCode, "The branch guard must be explicit.");
        Equal(0, fixture.Issues.UniqueIssueCount, "No Issue should be created.");
        Equal(0, fixture.Locks.LockCount, "No LFS lock should be acquired.");
        True((await fixture.State.ReadAsync(request.TaskId, default).ConfigureAwait(false)).Value is null,
            "No coordination state should be claimed.");
    }

    private static async Task SaveBranchMismatchHasNoEffectsAsync()
    {
        var state = new InMemoryCoordinationStateStore();
        var active = CreateActiveAggregate("task-save", "lock-save", "Assets/Scenes/Main.unity");
        await SeedAsync(state, active).ConfigureAwait(false);
        var unity = new FakeUnitySaveGateway();
        var git = new FakeGitTaskGateway();
        var workflow = new SaveTaskWorkflow(state, new InMemoryOperationJournal(), unity, git);
        var request = new SaveTaskRequest(
            active.TaskId,
            "task/another-task",
            AuthorityFor(active),
            "snapshot",
            new OperationId("wrong-branch"));

        var result = await workflow.ExecuteAsync(request).ConfigureAwait(false);
        False(result.IsSuccess, "A valid-looking but different branch must be rejected.");
        Equal("task_branch_mismatch", result.Problem!.ErrorCode, "Coordination state must own the Task branch identity.");
        Equal(0, unity.SaveCalls, "Unity save must not start when branch identity is wrong.");
        Equal(0, git.CommitCalls, "Git commit must not start when branch identity is wrong.");
        Equal(0, git.PushCalls, "Git push must not start when branch identity is wrong.");
    }

    private static async Task SaveBranchBindingSurvivesReloadAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "projectsync-tests", Guid.NewGuid().ToString("N"));
        var state = new InMemoryCoordinationStateStore();
        var active = CreateActiveAggregate("task-reload", "lock-reload", "Assets/Scenes/Main.unity");
        await SeedAsync(state, active).ConfigureAwait(false);
        var unity = new FakeUnitySaveGateway();
        var git = new FakeGitTaskGateway { FailPush = true, RemoteReachable = false };
        var request = new SaveTaskRequest(
            active.TaskId,
            active.TaskBranch,
            AuthorityFor(active),
            "snapshot",
            new OperationId("save-reload"));

        try
        {
            using (var firstJournal = new FileOperationJournal(directory))
            {
                var first = await new SaveTaskWorkflow(state, firstJournal, unity, git)
                    .ExecuteAsync(request).ConfigureAwait(false);
                False(first.IsSuccess, "The first push should fail.");
            }

            using var reloadedJournal = new FileOperationJournal(directory);
            var resumed = new SaveTaskWorkflow(state, reloadedJournal, unity, git);
            var rebound = await resumed.ExecuteAsync(
                request with { TaskBranch = "task/other" }).ConfigureAwait(false);
            False(rebound.IsSuccess, "A reloaded save operation must reject a different Task branch.");
            Equal("operation_branch_reused", rebound.Problem!.ErrorCode, "Branch identity must survive memory loss.");

            git.FailPush = false;
            git.RemoteReachable = true;
            var success = await resumed.ExecuteAsync(request).ConfigureAwait(false);
            True(success.IsSuccess, "The original branch can resume after reload.");
            Equal(1, unity.SaveCalls, "Reload must not repeat Unity save.");
            Equal(1, git.CommitCalls, "Reload must not create a second commit.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task GitCliTaskGatewayKeepsMainReadOnlyAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "projectsync-tests", Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(directory, "remote.git");
        var working = Path.Combine(directory, "working");
        Directory.CreateDirectory(directory);
        try
        {
            RunGit(directory, "init", "--bare", remote);
            RunGit(directory, "init", "-b", "main", working);
            RunGit(working, "config", "user.name", "ProjectSync Test");
            RunGit(working, "config", "user.email", "test@example.invalid");
            Directory.CreateDirectory(Path.Combine(working, "Assets"));
            Directory.CreateDirectory(Path.Combine(working, "Packages"));
            Directory.CreateDirectory(Path.Combine(working, "ProjectSettings"));
            File.WriteAllText(Path.Combine(working, "Assets", "sample.txt"), "baseline");
            File.WriteAllText(Path.Combine(working, "Packages", "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(working, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: test");
            File.WriteAllText(Path.Combine(working, ".gitattributes"), "*.unity text eol=lf\n");
            File.WriteAllText(Path.Combine(working, ".gitignore"), "/UserSettings/\n/Assets.zip\n");
            RunGit(working, "add", "Assets", "Packages", "ProjectSettings", ".gitattributes", ".gitignore");
            RunGit(working, "commit", "-m", "baseline");
            RunGit(working, "remote", "add", "origin", remote);
            RunGit(working, "push", "origin", "main");
            var mainSha = RunGit(working, "rev-parse", "HEAD").Trim();
            RunGit(working, "switch", "-c", "task/test-save");
            File.WriteAllText(Path.Combine(working, "Assets", "sample.txt"), "Task change");

            var operationId = new OperationId("real-git-save");
            var gateway = new GitCliTaskGateway(working);
            var snapshot = await gateway.CreateSnapshotAsync(
                "task/test-save", "ProjectSync Task snapshot", operationId, default).ConfigureAwait(false);
            True(snapshot.IsSuccess, "A real Task snapshot should succeed: " + snapshot.Problem?.Message);
            True(GitCommandPolicy.IsFullSha(snapshot.Value), "The snapshot must return a full commit SHA.");

            var retry = await gateway.CreateSnapshotAsync(
                "task/test-save", "ProjectSync Task snapshot", operationId, default).ConfigureAwait(false);
            True(retry.IsSuccess, "Retry must find the existing snapshot.");
            Equal(snapshot.Value, retry.Value, "One operation must not produce a second commit.");
            Equal("1", RunGit(working, "rev-list", "--count", "main..HEAD").Trim(), "Only one Task commit should exist.");

            var push = await gateway.PushCommitAsync("task/test-save", snapshot.Value!, operationId, default).ConfigureAwait(false);
            True(push.IsSuccess, "Task ref push should succeed: " + push.Problem?.Message);
            var reachable = await gateway.IsCommitReachableAsync("task/test-save", snapshot.Value!, default).ConfigureAwait(false);
            True(reachable.IsSuccess && reachable.Value is true, "Snapshot must be reachable on the remote Task ref.");

            var mainRemote = RunGit(working, "ls-remote", "--heads", "origin", "refs/heads/main").Split('\t')[0];
            var taskRemote = RunGit(working, "ls-remote", "--heads", "origin", "refs/heads/task/test-save").Split('\t')[0];
            Equal(mainSha, mainRemote, "Remote main must remain at its original SHA.");
            Equal(snapshot.Value, taskRemote, "Only the Task ref should receive the snapshot.");

            File.WriteAllText(Path.Combine(working, "Assets", "later.txt"), "later Task work");
            RunGit(working, "add", "Assets/later.txt");
            RunGit(working, "commit", "-m", "later Task commit");
            RunGit(working, "push", "origin", "task/test-save");
            var laterSha = RunGit(working, "rev-parse", "HEAD").Trim();
            var olderIsReachable = await gateway.IsCommitReachableAsync(
                "task/test-save", snapshot.Value!, default).ConfigureAwait(false);
            True(olderIsReachable.IsSuccess && olderIsReachable.Value is true,
                "The saved commit remains reachable when the remote Task branch advances.");
            var nonFastForward = await gateway.PushCommitAsync(
                "task/test-save", snapshot.Value!, operationId, default).ConfigureAwait(false);
            False(nonFastForward.IsSuccess, "An older snapshot must not overwrite the newer remote Task commit.");
            Equal(laterSha, RunGit(working, "ls-remote", "--heads", "origin", "refs/heads/task/test-save").Split('\t')[0],
                "A rejected non-fast-forward push must preserve remote Task work.");

            var forbidden = await gateway.PushCommitAsync("main", snapshot.Value!, operationId, default).ConfigureAwait(false);
            False(forbidden.IsSuccess, "The gateway must refuse main even when the caller supplies a valid SHA.");
            False(GitCommandPolicy.Validate(
                    ["push", "origin", "HEAD:refs/heads/main"], operationId).IsSuccess,
                "The low-level command policy must refuse a direct main refspec.");
            Equal(mainSha, RunGit(working, "ls-remote", "--heads", "origin", "refs/heads/main").Split('\t')[0],
                "Forbidden requests must not mutate remote main.");

            RunGit(working, "switch", "main");
            File.WriteAllText(Path.Combine(working, "Assets", "sample.txt"), "must not save on main");
            var wrongCheckout = await gateway.CreateSnapshotAsync(
                "task/test-save", "must fail", new OperationId("wrong-checkout"), default).ConfigureAwait(false);
            False(wrongCheckout.IsSuccess, "Snapshot must fail when main is checked out.");
            Equal(mainSha, RunGit(working, "rev-parse", "HEAD").Trim(), "Rejected snapshot must not commit to local main.");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task LocalFirstSaveRecoversAsync()
    {
        var (directory, remote, working, mainSha) = CreateLocalFixture();
        try
        {
            var workspace = new LocalTaskWorkspace(working, unityEditorRunning: () => false);
            var started = await workspace.StartAsync("Scene work").ConfigureAwait(false);
            True(started.IsSuccess, "Task must start from remote main: " + started.Problem?.Message);
            Equal(mainSha, RunGit(working, "rev-parse", "HEAD").Trim(), "A new Task must start at remote main HEAD.");
            True(started.Value!.StartsWith("task/", StringComparison.Ordinal), "A Task branch is required.");

            File.WriteAllText(Path.Combine(working, "Assets.zip"), "local backup; never stage");
            RunGit(working, "add", "-f", "Assets.zip");
            File.WriteAllText(Path.Combine(working, "Assets", "sample.txt"), "first Task edit");
            var first = await workspace.SaveAsync().ConfigureAwait(false);
            True(first.IsSuccess, "First Task save must reach the remote: " + first.Problem?.Message);
            Equal(string.Empty, RunGit(working, "ls-tree", "-r", "--name-only", "HEAD", "Assets.zip").Trim(),
                "Even a previously staged root backup ZIP must never enter a Task snapshot.");
            True(File.Exists(Path.Combine(working, "Assets.zip")), "The excluded backup must remain on disk.");
            RunGit(working, "rm", "--cached", "--", "Assets.zip");
            Equal(mainSha, RunGit(working, "ls-remote", "--heads", "origin", "refs/heads/main").Split('\t')[0],
                "Save must leave remote main unchanged.");

            File.WriteAllText(Path.Combine(working, "Assets", "sample.txt"), "second Task edit");
            RunGit(working, "remote", "set-url", "origin", Path.Combine(directory, "unavailable.git"));
            var failed = await workspace.SaveAsync().ConfigureAwait(false);
            False(failed.IsSuccess, "Unavailable remote must not be reported as saved.");
            var committedSha = RunGit(working, "rev-parse", "HEAD").Trim();
            False(string.Equals(committedSha, first.Value!.CommitSha, StringComparison.Ordinal),
                "The second local snapshot must survive a failed push.");

            RunGit(working, "remote", "set-url", "origin", remote);
            var restored = new LocalTaskWorkspace(working, unityEditorRunning: () => false);
            var retried = await restored.SaveAsync().ConfigureAwait(false);
            True(retried.IsSuccess, "Restarted workspace must retry the push: " + retried.Problem?.Message);
            Equal(committedSha, retried.Value!.CommitSha, "Retry must not create a new commit.");
            Equal("2", RunGit(working, "rev-list", "--count", "main..HEAD").Trim(),
                "Two edits must make exactly two Task commits.");
            Equal(committedSha,
                RunGit(working, "ls-remote", "--heads", "origin", "refs/heads/" + started.Value).Split('\t')[0],
                "The remote Task branch must reach the retried commit.");
            Equal(mainSha, RunGit(working, "ls-remote", "--heads", "origin", "refs/heads/main").Split('\t')[0],
                "Retry must still leave main untouched.");

            var submitted = await restored.SubmitAsync("Scene work").ConfigureAwait(false);
            False(submitted.IsSuccess, "A non-GitHub fixture must not create a PR.");
            Equal("github_origin_required", submitted.Problem!.ErrorCode,
                "Submission must validate its GitHub origin.");

            var next = await restored.StartAsync("Next task").ConfigureAwait(false);
            True(next.IsSuccess, "A clean finished Task may start another Task.");
            Equal(mainSha, RunGit(working, "rev-parse", "HEAD").Trim(),
                "New Task must use main, not the previous Task HEAD.");
            var resumed = await restored.ResumeAsync(started.Value).ConfigureAwait(false);
            True(resumed.IsSuccess, "An existing local Task can be resumed.");
            Equal(committedSha, RunGit(working, "rev-parse", "HEAD").Trim(),
                "Resuming a Task must restore its own commit without merging main.");
        }
        finally
        {
            DeleteLocalFixture(directory);
        }
    }

    private static async Task AssetPolicyRejectsUnsafeTrackingAsync()
    {
        var (directory, _, working, mainSha) = CreateLocalFixture();
        try
        {
            RunGit(working, "switch", "-c", "task/asset-policy");
            var largePath = Path.Combine(working, "Assets", "Large.fbx");
            using (var large = new FileStream(largePath, FileMode.CreateNew, FileAccess.Write))
            {
                large.SetLength(101L * 1024 * 1024);
            }

            var gateway = new GitCliTaskGateway(working);
            var rejected = await gateway.CreateSnapshotAsync(
                "task/asset-policy", "large asset", new OperationId("large-asset"), default)
                .ConfigureAwait(false);
            False(rejected.IsSuccess, "An ordinary Git file over 100 MiB must be rejected before commit.");
            Equal("large_asset_not_lfs", rejected.Problem!.ErrorCode, "The error must identify LFS setup.");
            Equal(mainSha, RunGit(working, "rev-parse", "HEAD").Trim(), "Rejected save must preserve HEAD.");

            File.Delete(largePath);
            File.WriteAllText(Path.Combine(working, "Assets", "Main.unity"), "%YAML 1.1\n");
            File.WriteAllText(Path.Combine(working, ".gitattributes"), "*.unity filter=lfs diff=lfs merge=lfs -text\n");
            var sceneRejected = await gateway.CreateSnapshotAsync(
                "task/asset-policy", "scene", new OperationId("scene-lfs"), default)
                .ConfigureAwait(false);
            False(sceneRejected.IsSuccess, "An LFS Scene must be rejected for concurrent editing.");
            Equal("scene_lfs_conflicts_with_merge", sceneRejected.Problem!.ErrorCode,
                "The conflict with text merging must be explicit.");
            Equal(mainSha, RunGit(working, "rev-parse", "HEAD").Trim(), "Rejected Scene must preserve HEAD.");
        }
        finally
        {
            DeleteLocalFixture(directory);
        }
    }

    private static (string Directory, string Remote, string Working, string MainSha) CreateLocalFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "projectsync-tests", Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(directory, "remote.git");
        var working = Path.Combine(directory, "working");
        Directory.CreateDirectory(directory);
        RunGit(directory, "init", "--bare", remote);
        RunGit(directory, "init", "-b", "main", working);
        RunGit(working, "config", "user.name", "ProjectSync Test");
        RunGit(working, "config", "user.email", "test@example.invalid");
        Directory.CreateDirectory(Path.Combine(working, "Assets"));
        Directory.CreateDirectory(Path.Combine(working, "Packages"));
        Directory.CreateDirectory(Path.Combine(working, "ProjectSettings"));
        File.WriteAllText(Path.Combine(working, "Assets", "sample.txt"), "main baseline");
        File.WriteAllText(Path.Combine(working, "Packages", "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(working, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: test");
        File.WriteAllText(Path.Combine(working, ".gitattributes"), "*.unity text eol=lf\n");
        File.WriteAllText(Path.Combine(working, ".gitignore"), "/UserSettings/\n/Assets.zip\n");
        RunGit(working, "add", "Assets", "Packages", "ProjectSettings", ".gitattributes", ".gitignore");
        RunGit(working, "commit", "-m", "main baseline");
        RunGit(working, "remote", "add", "origin", remote);
        RunGit(working, "push", "origin", "main");
        return (directory, remote, working, RunGit(working, "rev-parse", "HEAD").Trim());
    }

    private static void DeleteLocalFixture(string directory)
    {
        var fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "projectsync-tests"));
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a path outside the test fixture root.");
        }

        if (Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(target, recursive: true);
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start in test fixture.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git fixture command failed ({process.ExitCode}): {string.Join(' ', arguments)}{Environment.NewLine}{error}");
        }

        return output;
    }

    private static async Task FileJournalPersistsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "projectsync-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var operationId = new OperationId("persistent-op");
            using (var first = new FileOperationJournal(directory))
            {
                var checkpoint = await first.LoadOrCreateAsync(
                    OperationCheckpoint.Create(operationId, "test", "subject"),
                    default).ConfigureAwait(false);
                await first.SaveAsync(
                    checkpoint.Advance("commit_created", values: [("commitSha", "abc123")]),
                    default).ConfigureAwait(false);
            }

            using var reloaded = new FileOperationJournal(directory);
            var restored = await reloaded.LoadOrCreateAsync(
                OperationCheckpoint.Create(operationId, "test", "subject"),
                default).ConfigureAwait(false);
            Equal("commit_created", restored.Phase, "Journal phase must survive memory loss.");
            Equal("abc123", restored.Data["commitSha"], "Journal data must survive memory loss.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static CoordinationAggregate CreateActiveAggregate(string taskId, string lockId, string scenePath) =>
        new(
            taskId,
            Revision: 1,
            Generation: 1,
            TaskLifecycle.Active,
            CoordinationHealth.Healthy,
            BaseMainSha: "base-main",
            TaskBranch: "task/" + taskId,
            StartClaim: null,
            ActiveSession: new SessionFence("same-user", "device-a", "session-a", 1, lockId, scenePath),
            RecoveryReason: null,
            LastOperationId: "seed");

    private static SessionAuthorityRequest AuthorityFor(CoordinationAggregate aggregate)
    {
        var session = aggregate.ActiveSession!;
        return new SessionAuthorityRequest(
            aggregate.TaskId,
            session.GitHubUser,
            session.DeviceId,
            session.SessionId,
            session.Generation,
            session.LfsLockId,
            session.ScenePath);
    }

    private static async Task SeedAsync(InMemoryCoordinationStateStore store, CoordinationAggregate aggregate)
    {
        var result = await store.CompareExchangeAsync(
            aggregate.TaskId,
            expectedRevision: 0,
            aggregate,
            default).ConfigureAwait(false);
        Equal(CasWriteStatus.Written, result.Status, "Fixture seed must be written.");
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void NotNull(object? value, string message) => True(value is not null, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class StartFixture
    {
        public StartFixture()
        {
            Workflow = new StartSceneTaskWorkflow(State, Journal, Issues, Locks);
        }

        public InMemoryCoordinationStateStore State { get; } = new();
        public InMemoryOperationJournal Journal { get; } = new();
        public FakeIssueGateway Issues { get; } = new();
        public FakeLfsLockGateway Locks { get; } = new();
        public StartSceneTaskWorkflow Workflow { get; }

        public StartSceneTaskRequest CreateRequest(string deviceId, string sessionId, string operationId) =>
            new(
                "task-100",
                "base-main",
                "task/task-100",
                "Assets/Scenes/Main.unity",
                "same-user",
                deviceId,
                sessionId,
                new OperationId(operationId));
    }

    private sealed class FakeIssueGateway : ITaskIssueGateway
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IssueReference> _issues = new(StringComparer.Ordinal);

        public int UniqueIssueCount
        {
            get
            {
                lock (_gate)
                {
                    return _issues.Count;
                }
            }
        }

        public Task<Outcome<IssueReference>> EnsureTaskIssueAsync(
            string taskId,
            OperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_issues.TryGetValue(taskId, out var issue))
                {
                    issue = new IssueReference(_issues.Count + 1, "https://example.invalid/issues/" + (_issues.Count + 1));
                    _issues[taskId] = issue;
                }

                return Task.FromResult(Outcome<IssueReference>.Success(issue));
            }
        }
    }

    private sealed class FakeLfsLockGateway : ILfsLockGateway
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, LfsLockReference> _locks = new(StringComparer.Ordinal);
        public bool FailAcquire { get; set; }
        public bool FailRelease { get; set; }
        public int AcquireSuccessCount { get; private set; }

        public int LockCount
        {
            get
            {
                lock (_gate)
                {
                    return _locks.Count;
                }
            }
        }

        public void Seed(LfsLockReference lockReference)
        {
            lock (_gate)
            {
                _locks[lockReference.Path] = lockReference;
            }
        }

        public Task<Outcome<LfsLockReference>> AcquireAsync(
            string path,
            string githubUser,
            OperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (FailAcquire)
                {
                    return Task.FromResult(Outcome<LfsLockReference>.Failure(Problem.Create(
                        "lfs_lock_failed",
                        ProblemCategory.ExternalSystem,
                        retryable: true,
                        operationId,
                        "lfs_lock",
                        "Injected lock failure.")));
                }

                if (_locks.TryGetValue(path, out var existing))
                {
                    return Task.FromResult(Outcome<LfsLockReference>.Failure(Problem.Create(
                        "lfs_lock_held",
                        ProblemCategory.Conflict,
                        retryable: false,
                        operationId,
                        "lfs_lock",
                        "Path is already locked by " + existing.Owner + ".")));
                }

                var created = new LfsLockReference("lock-" + (_locks.Count + 1), path, githubUser);
                _locks[path] = created;
                AcquireSuccessCount++;
                return Task.FromResult(Outcome<LfsLockReference>.Success(created));
            }
        }

        public Task<Outcome<Unit>> ReleaseAsync(
            string lockId,
            OperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (FailRelease)
                {
                    return Task.FromResult(Outcome<Unit>.Failure(Problem.Create(
                        "lfs_unlock_failed",
                        ProblemCategory.ExternalSystem,
                        retryable: true,
                        operationId,
                        "lfs_unlock",
                        "Injected unlock failure.")));
                }

                var pair = _locks.FirstOrDefault(candidate => string.Equals(candidate.Value.LockId, lockId, StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    _locks.Remove(pair.Key);
                }

                return Task.FromResult(Outcome<Unit>.Success(Unit.Value));
            }
        }

        public Task<Outcome<bool>> VerifyUnlockedAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(Outcome<bool>.Success(!_locks.ContainsKey(path)));
            }
        }
    }

    private sealed class FakeUnitySaveGateway : IUnitySaveGateway
    {
        public int SaveCalls { get; private set; }

        public Task<Outcome<UnitySaveReceipt>> SaveOpenScenesAndAssetsAsync(
            OperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCalls++;
            return Task.FromResult(Outcome<UnitySaveReceipt>.Success(
                new UnitySaveReceipt(DateTimeOffset.UtcNow, ["Assets/Scenes/Main.unity"])));
        }
    }

    private sealed class FakeGitTaskGateway : IGitTaskGateway
    {
        public bool FailPush { get; set; }
        public bool RemoteReachable { get; set; }
        public int CommitCalls { get; private set; }
        public int PushCalls { get; private set; }

        public Task<Outcome<string>> CreateSnapshotAsync(
            string taskBranch,
            string message,
            OperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCalls++;
            return Task.FromResult(Outcome<string>.Success("commit-" + CommitCalls));
        }

        public Task<Outcome<Unit>> PushCommitAsync(
            string taskBranch,
            string commitSha,
            OperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PushCalls++;
            return Task.FromResult(FailPush
                ? Outcome<Unit>.Failure(Problem.Create(
                    "push_failed",
                    ProblemCategory.Transport,
                    retryable: true,
                    operationId,
                    "push",
                    "Injected push failure."))
                : Outcome<Unit>.Success(Unit.Value));
        }

        public Task<Outcome<bool>> IsCommitReachableAsync(
            string taskBranch,
            string commitSha,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Outcome<bool>.Success(RemoteReachable));
        }
    }

    private sealed class FakeLocalDataProtector : ILocalDataProtector
    {
        public int Calls { get; private set; }

        public Task<Outcome<LocalPreservationReceipt>> InspectAndPreserveAsync(
            string taskId,
            OperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Outcome<LocalPreservationReceipt>.Success(
                new LocalPreservationReceipt(true, "evidence/" + taskId, DateTimeOffset.UtcNow)));
        }
    }
}
