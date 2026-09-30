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
        False(GitCommandPolicy.Validate(["reset", "--hard", "HEAD"], operationId).IsSuccess, "reset must be rejected.");
        False(GitCommandPolicy.Validate(["stash", "push"], operationId).IsSuccess, "stash must be rejected.");
        False(GitCommandPolicy.Validate(["push", "--force", "origin", "task"], operationId).IsSuccess, "force push must be rejected.");
        False(GitCommandPolicy.Validate(["clean", "-fd"], operationId).IsSuccess, "automatic deletion must be rejected.");
        True(GitCommandPolicy.Validate(["push", "origin", "task/task-1"], operationId).IsSuccess, "Normal Task push should pass policy.");
        return Task.CompletedTask;
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
