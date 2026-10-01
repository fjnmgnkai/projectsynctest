namespace ProjectSync.Core;

public sealed record SaveTaskRequest(
    string TaskId,
    string TaskBranch,
    SessionAuthorityRequest Authority,
    string CommitMessage,
    OperationId OperationId);

public sealed record SaveTaskResult(string CommitSha, DateTimeOffset RemoteVerifiedAt);

public sealed class SaveTaskWorkflow
{
    private const string Kind = "save-task";
    private readonly ICoordinationStateStore _stateStore;
    private readonly IOperationJournal _journal;
    private readonly IUnitySaveGateway _unity;
    private readonly IGitTaskGateway _git;

    public SaveTaskWorkflow(
        ICoordinationStateStore stateStore,
        IOperationJournal journal,
        IUnitySaveGateway unity,
        IGitTaskGateway git)
    {
        _stateStore = stateStore;
        _journal = journal;
        _unity = unity;
        _git = git;
    }

    public async Task<Outcome<SaveTaskResult>> ExecuteAsync(
        SaveTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var branch = TaskBranchPolicy.RequireTaskBranch(request.TaskBranch, request.OperationId, "save_preflight");
        if (!branch.IsSuccess)
        {
            return Outcome<SaveTaskResult>.Failure(branch.Problem!);
        }

        var checkpoint = await _journal.LoadOrCreateAsync(
            OperationCheckpoint.Create(request.OperationId, Kind, request.TaskId),
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(checkpoint.Kind, Kind, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.SubjectId, request.TaskId, StringComparison.Ordinal))
        {
            return Outcome<SaveTaskResult>.Failure(Problem.Create(
                "operation_identity_reused",
                ProblemCategory.Conflict,
                retryable: false,
                request.OperationId,
                "journal",
                "Operation ID is already bound to another workflow or Task."));
        }

        if (checkpoint.Data.TryGetValue("taskBranch", out var recordedBranch))
        {
            if (!string.Equals(recordedBranch, request.TaskBranch, StringComparison.Ordinal))
            {
                return Outcome<SaveTaskResult>.Failure(Problem.Create(
                    "operation_branch_reused",
                    ProblemCategory.Conflict,
                    retryable: false,
                    request.OperationId,
                    "journal",
                    "A save operation cannot be retried for a different Task branch."));
            }
        }
        else
        {
            var snapshot = await _stateStore.ReadAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(snapshot.Value?.TaskBranch, request.TaskBranch, StringComparison.Ordinal))
            {
                return Outcome<SaveTaskResult>.Failure(Problem.Create(
                    "task_branch_mismatch",
                    ProblemCategory.Authorization,
                    retryable: false,
                    request.OperationId,
                    "save_preflight",
                    "Save target must exactly match the Task branch recorded in coordination state."));
            }

            checkpoint = checkpoint.Advance(
                checkpoint.Phase,
                checkpoint.IsTerminal,
                values: [("taskBranch", request.TaskBranch)]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        if (checkpoint.IsTerminal &&
            checkpoint.Data.TryGetValue("commitSha", out var completedCommit) &&
            checkpoint.Data.TryGetValue("remoteVerifiedAt", out var verifiedAtText) &&
            DateTimeOffset.TryParse(verifiedAtText, out var completedAt))
        {
            return Outcome<SaveTaskResult>.Success(new SaveTaskResult(completedCommit, completedAt));
        }

        var authority = await RequireAuthorityAsync(request, "save_preflight", cancellationToken).ConfigureAwait(false);
        if (!authority.IsSuccess)
        {
            return Outcome<SaveTaskResult>.Failure(authority.Problem!);
        }

        if (!checkpoint.Data.ContainsKey("unitySavedAt"))
        {
            var unityOutcome = await _unity.SaveOpenScenesAndAssetsAsync(
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            if (!unityOutcome.IsSuccess)
            {
                return Outcome<SaveTaskResult>.Failure(unityOutcome.Problem!);
            }

            checkpoint = checkpoint.Advance(
                "unity_saved",
                values: [("unitySavedAt", unityOutcome.Value!.SavedAt.ToString("O"))]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        string commitSha;
        if (checkpoint.Data.TryGetValue("commitSha", out var recordedCommit))
        {
            commitSha = recordedCommit;
        }
        else
        {
            authority = await RequireAuthorityAsync(request, "create_snapshot", cancellationToken).ConfigureAwait(false);
            if (!authority.IsSuccess)
            {
                return Outcome<SaveTaskResult>.Failure(authority.Problem!);
            }

            var commitOutcome = await _git.CreateSnapshotAsync(
                request.TaskBranch,
                request.CommitMessage,
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            if (!commitOutcome.IsSuccess)
            {
                return Outcome<SaveTaskResult>.Failure(commitOutcome.Problem!);
            }

            commitSha = commitOutcome.Value!;
            checkpoint = checkpoint.Advance("commit_created", values: [("commitSha", commitSha)]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        if (!checkpoint.Data.ContainsKey("pushConfirmed"))
        {
            authority = await RequireAuthorityAsync(request, "push", cancellationToken).ConfigureAwait(false);
            if (!authority.IsSuccess)
            {
                return Outcome<SaveTaskResult>.Failure(authority.Problem!);
            }

            var pushOutcome = await _git.PushCommitAsync(
                request.TaskBranch,
                commitSha,
                request.OperationId,
                cancellationToken).ConfigureAwait(false);

            var reachableOutcome = await _git.IsCommitReachableAsync(
                request.TaskBranch,
                commitSha,
                cancellationToken).ConfigureAwait(false);
            if (!reachableOutcome.IsSuccess)
            {
                return Outcome<SaveTaskResult>.Failure(reachableOutcome.Problem!);
            }

            if (reachableOutcome.Value is not true)
            {
                return Outcome<SaveTaskResult>.Failure(
                    pushOutcome.Problem ?? Problem.Create(
                        "remote_commit_not_reachable",
                        ProblemCategory.ExternalSystem,
                        retryable: true,
                        request.OperationId,
                        "verify_remote",
                        "The commit is not yet reachable from the remote Task branch."));
            }

            checkpoint = checkpoint.Advance(
                "remote_verified",
                values: [("pushConfirmed", "true")]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var verifiedAt = DateTimeOffset.UtcNow;
        checkpoint = checkpoint.Advance(
            "completed",
            isTerminal: true,
            values: [("remoteVerifiedAt", verifiedAt.ToString("O"))]);
        await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return Outcome<SaveTaskResult>.Success(new SaveTaskResult(commitSha, verifiedAt));
    }

    private async Task<Outcome<Unit>> RequireAuthorityAsync(
        SaveTaskRequest request,
        string phase,
        CancellationToken cancellationToken)
    {
        var snapshot = await _stateStore.ReadAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
        if (snapshot.Value is null)
        {
            return Outcome<Unit>.Failure(Problem.Create(
                "coordination_missing",
                ProblemCategory.Authorization,
                retryable: false,
                request.OperationId,
                phase,
                "Task coordination state does not exist."));
        }

        if (!string.Equals(snapshot.Value.TaskBranch, request.TaskBranch, StringComparison.Ordinal))
        {
            return Outcome<Unit>.Failure(Problem.Create(
                "task_branch_mismatch",
                ProblemCategory.Authorization,
                retryable: false,
                request.OperationId,
                phase,
                "Save target must exactly match the Task branch recorded in coordination state."));
        }

        return SessionAuthority.RequireMutationPermission(
            snapshot.Value,
            request.Authority,
            request.OperationId,
            phase);
    }
}
