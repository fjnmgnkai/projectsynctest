namespace ProjectSync.Core;

public sealed record StartSceneTaskRequest(
    string TaskId,
    string BaseMainSha,
    string TaskBranch,
    string ScenePath,
    string GitHubUser,
    string DeviceId,
    string SessionId,
    OperationId OperationId);

public sealed record StartSceneTaskResult(
    CoordinationAggregate Coordination,
    IssueReference Issue,
    LfsLockReference Lock);

public sealed class StartSceneTaskWorkflow
{
    private const string Kind = "start-scene-task";
    private readonly ICoordinationStateStore _stateStore;
    private readonly IOperationJournal _journal;
    private readonly ITaskIssueGateway _issues;
    private readonly ILfsLockGateway _locks;

    public StartSceneTaskWorkflow(
        ICoordinationStateStore stateStore,
        IOperationJournal journal,
        ITaskIssueGateway issues,
        ILfsLockGateway locks)
    {
        _stateStore = stateStore;
        _journal = journal;
        _issues = issues;
        _locks = locks;
    }

    public async Task<Outcome<StartSceneTaskResult>> ExecuteAsync(
        StartSceneTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var branch = TaskBranchPolicy.RequireTaskBranch(request.TaskBranch, request.OperationId, "start_preflight");
        if (!branch.IsSuccess)
        {
            return Outcome<StartSceneTaskResult>.Failure(branch.Problem!);
        }

        var checkpoint = await _journal.LoadOrCreateAsync(
            OperationCheckpoint.Create(request.OperationId, Kind, request.TaskId),
            cancellationToken).ConfigureAwait(false);

        var shapeProblem = ValidateCheckpoint(checkpoint, request);
        if (shapeProblem is not null)
        {
            return Outcome<StartSceneTaskResult>.Failure(shapeProblem);
        }

        var claimOutcome = await EnsureClaimAsync(request, cancellationToken).ConfigureAwait(false);
        if (!claimOutcome.IsSuccess)
        {
            return Outcome<StartSceneTaskResult>.Failure(claimOutcome.Problem!);
        }

        checkpoint = checkpoint.Advance("session_claimed");
        await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);

        IssueReference issue;
        if (checkpoint.Data.TryGetValue("issueNumber", out var issueNumberText) &&
            long.TryParse(issueNumberText, out var issueNumber) &&
            checkpoint.Data.TryGetValue("issueUrl", out var issueUrl))
        {
            issue = new IssueReference(issueNumber, issueUrl);
        }
        else
        {
            var issueOutcome = await _issues.EnsureTaskIssueAsync(
                request.TaskId,
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            if (!issueOutcome.IsSuccess)
            {
                await MarkRecoveryRequiredAsync(
                    request.TaskId,
                    "Task coordination was claimed but Issue creation could not be confirmed.",
                    request.OperationId,
                    cancellationToken).ConfigureAwait(false);
                return Outcome<StartSceneTaskResult>.Failure(issueOutcome.Problem!);
            }

            issue = issueOutcome.Value!;
            checkpoint = checkpoint.Advance(
                "issue_created",
                values:
                [
                    ("issueNumber", issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("issueUrl", issue.Url)
                ]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        LfsLockReference lockReference;
        if (checkpoint.Data.TryGetValue("lockId", out var lockId))
        {
            lockReference = new LfsLockReference(lockId, request.ScenePath, request.GitHubUser);
        }
        else
        {
            var lockOutcome = await _locks.AcquireAsync(
                request.ScenePath,
                request.GitHubUser,
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            if (!lockOutcome.IsSuccess)
            {
                await MarkRecoveryRequiredAsync(
                    request.TaskId,
                    "Issue exists and the LFS lock acquisition did not complete successfully.",
                    request.OperationId,
                    cancellationToken).ConfigureAwait(false);
                checkpoint = checkpoint.Advance("recovery_required");
                await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                return Outcome<StartSceneTaskResult>.Failure(lockOutcome.Problem!);
            }

            lockReference = lockOutcome.Value!;
            checkpoint = checkpoint.Advance("lock_acquired", values: [("lockId", lockReference.LockId)]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var activationOutcome = await ActivateClaimAsync(
            request.TaskId,
            lockReference.LockId,
            request.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (!activationOutcome.IsSuccess)
        {
            await MarkRecoveryRequiredAsync(
                request.TaskId,
                "LFS lock exists but coordination activation could not be confirmed.",
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            checkpoint = checkpoint.Advance("recovery_required");
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            return Outcome<StartSceneTaskResult>.Failure(activationOutcome.Problem!);
        }

        checkpoint = checkpoint.Advance("completed", isTerminal: true);
        await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return Outcome<StartSceneTaskResult>.Success(
            new StartSceneTaskResult(activationOutcome.Value!, issue, lockReference));
    }

    private async Task<Outcome<CoordinationAggregate>> EnsureClaimAsync(
        StartSceneTaskRequest request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await _stateStore.ReadAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
            var current = snapshot.Value;
            if (current is null)
            {
                var seed = CoordinationAggregate.CreateStarting(
                    request.TaskId,
                    request.BaseMainSha,
                    request.TaskBranch,
                    request.OperationId);
                var create = await _stateStore.CompareExchangeAsync(
                    request.TaskId,
                    expectedRevision: 0,
                    seed,
                    cancellationToken).ConfigureAwait(false);
                if (create.Status == CasWriteStatus.Conflict)
                {
                    continue;
                }

                current = create.Current;
            }

            if (!string.Equals(current.BaseMainSha, request.BaseMainSha, StringComparison.Ordinal) ||
                !string.Equals(current.TaskBranch, request.TaskBranch, StringComparison.Ordinal))
            {
                return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                    "task_identity_mismatch",
                    ProblemCategory.Conflict,
                    retryable: false,
                    request.OperationId,
                    "claim_session",
                    "Existing Task branch or Base Main does not match this start request."));
            }

            if (current.ActiveSession is not null &&
                string.Equals(current.LastOperationId, request.OperationId.Value, StringComparison.Ordinal) &&
                string.Equals(current.ActiveSession.DeviceId, request.DeviceId, StringComparison.Ordinal) &&
                string.Equals(current.ActiveSession.SessionId, request.SessionId, StringComparison.Ordinal))
            {
                return Outcome<CoordinationAggregate>.Success(current);
            }

            var transition = CoordinationTransitions.ClaimSessionStart(
                current,
                request.GitHubUser,
                request.DeviceId,
                request.SessionId,
                request.ScenePath,
                request.OperationId);
            if (!transition.IsSuccess)
            {
                return transition;
            }

            if (ReferenceEquals(transition.Value, current) || transition.Value == current)
            {
                return Outcome<CoordinationAggregate>.Success(current);
            }

            var write = await _stateStore.CompareExchangeAsync(
                request.TaskId,
                current.Revision,
                transition.Value!,
                cancellationToken).ConfigureAwait(false);
            if (write.Status == CasWriteStatus.Written)
            {
                return Outcome<CoordinationAggregate>.Success(write.Current);
            }
        }

        return Outcome<CoordinationAggregate>.Failure(Problem.Create(
            "coordination_cas_exhausted",
            ProblemCategory.Conflict,
            retryable: true,
            request.OperationId,
            "claim_session",
            "Coordination state changed repeatedly while claiming the session."));
    }

    private async Task<Outcome<CoordinationAggregate>> ActivateClaimAsync(
        string taskId,
        string lockId,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await _stateStore.ReadAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Value is null)
            {
                return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                    "coordination_missing",
                    ProblemCategory.RecoveryRequired,
                    retryable: false,
                    operationId,
                    "activate_session",
                    "Coordination state disappeared after acquiring the lock."));
            }

            var current = snapshot.Value;
            if (current.ActiveSession is not null &&
                string.Equals(current.LastOperationId, operationId.Value, StringComparison.Ordinal) &&
                string.Equals(current.ActiveSession.LfsLockId, lockId, StringComparison.Ordinal))
            {
                return Outcome<CoordinationAggregate>.Success(current);
            }

            var transition = CoordinationTransitions.ActivateClaimedSession(current, lockId, operationId);
            if (!transition.IsSuccess)
            {
                return transition;
            }

            var write = await _stateStore.CompareExchangeAsync(
                taskId,
                current.Revision,
                transition.Value!,
                cancellationToken).ConfigureAwait(false);
            if (write.Status == CasWriteStatus.Written)
            {
                return Outcome<CoordinationAggregate>.Success(write.Current);
            }
        }

        return Outcome<CoordinationAggregate>.Failure(Problem.Create(
            "coordination_cas_exhausted",
            ProblemCategory.Conflict,
            retryable: true,
            operationId,
            "activate_session",
            "Coordination state changed repeatedly while activating the session."));
    }

    private async Task MarkRecoveryRequiredAsync(
        string taskId,
        string reason,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await _stateStore.ReadAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Value is null || snapshot.Value.Health == CoordinationHealth.RecoveryRequired)
            {
                return;
            }

            var current = snapshot.Value;
            var next = CoordinationTransitions.MarkRecoveryRequired(current, reason, operationId);
            var write = await _stateStore.CompareExchangeAsync(
                taskId,
                current.Revision,
                next,
                cancellationToken).ConfigureAwait(false);
            if (write.Status == CasWriteStatus.Written)
            {
                return;
            }
        }
    }

    private static Problem? ValidateCheckpoint(
        OperationCheckpoint checkpoint,
        StartSceneTaskRequest request)
    {
        if (!string.Equals(checkpoint.Kind, Kind, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.SubjectId, request.TaskId, StringComparison.Ordinal))
        {
            return Problem.Create(
                "operation_identity_reused",
                ProblemCategory.Conflict,
                retryable: false,
                request.OperationId,
                "journal",
                "Operation ID is already bound to another workflow or Task.");
        }

        return null;
    }
}
