namespace ProjectSync.Core;

public sealed record AbortSceneTaskRequest(
    string TaskId,
    SessionAuthorityRequest Authority,
    OperationId OperationId);

public sealed record AbortSceneTaskResult(TaskLifecycle Lifecycle, string PreservationEvidence);

public sealed class AbortSceneTaskWorkflow
{
    private const string Kind = "abort-scene-task";
    private readonly ICoordinationStateStore _stateStore;
    private readonly IOperationJournal _journal;
    private readonly ILocalDataProtector _localData;
    private readonly ILfsLockGateway _locks;

    public AbortSceneTaskWorkflow(
        ICoordinationStateStore stateStore,
        IOperationJournal journal,
        ILocalDataProtector localData,
        ILfsLockGateway locks)
    {
        _stateStore = stateStore;
        _journal = journal;
        _localData = localData;
        _locks = locks;
    }

    public async Task<Outcome<AbortSceneTaskResult>> ExecuteAsync(
        AbortSceneTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await _journal.LoadOrCreateAsync(
            OperationCheckpoint.Create(request.OperationId, Kind, request.TaskId),
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(checkpoint.Kind, Kind, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.SubjectId, request.TaskId, StringComparison.Ordinal))
        {
            return Outcome<AbortSceneTaskResult>.Failure(Problem.Create(
                "operation_identity_reused",
                ProblemCategory.Conflict,
                retryable: false,
                request.OperationId,
                "journal",
                "Operation ID is already bound to another workflow or Task."));
        }

        if (!checkpoint.Data.ContainsKey("preservationEvidence"))
        {
            var authority = await RequireAuthorityAsync(request, cancellationToken).ConfigureAwait(false);
            if (!authority.IsSuccess)
            {
                return Outcome<AbortSceneTaskResult>.Failure(authority.Problem!);
            }

            var preservation = await _localData.InspectAndPreserveAsync(
                request.TaskId,
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            if (!preservation.IsSuccess)
            {
                return Outcome<AbortSceneTaskResult>.Failure(preservation.Problem!);
            }

            checkpoint = checkpoint.Advance(
                "local_data_preserved",
                values:
                [
                    ("preservationEvidence", preservation.Value!.EvidenceLocation),
                    ("lockId", request.Authority.LfsLockId),
                    ("scenePath", request.Authority.ScenePath)
                ]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        if (!checkpoint.Data.ContainsKey("sessionRevoked"))
        {
            var revoke = await RevokeSessionAsync(request, cancellationToken).ConfigureAwait(false);
            if (!revoke.IsSuccess)
            {
                return Outcome<AbortSceneTaskResult>.Failure(revoke.Problem!);
            }

            checkpoint = checkpoint.Advance("session_revoked", values: [("sessionRevoked", "true")]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var lockId = checkpoint.Data["lockId"];
        var scenePath = checkpoint.Data["scenePath"];
        if (!checkpoint.Data.ContainsKey("unlockConfirmed"))
        {
            var unlock = await _locks.ReleaseAsync(
                lockId,
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            if (!unlock.IsSuccess)
            {
                return Outcome<AbortSceneTaskResult>.Failure(unlock.Problem!);
            }

            var verify = await _locks.VerifyUnlockedAsync(scenePath, cancellationToken).ConfigureAwait(false);
            if (!verify.IsSuccess)
            {
                return Outcome<AbortSceneTaskResult>.Failure(verify.Problem!);
            }

            if (verify.Value is not true)
            {
                return Outcome<AbortSceneTaskResult>.Failure(Problem.Create(
                    "lfs_unlock_not_confirmed",
                    ProblemCategory.RecoveryRequired,
                    retryable: true,
                    request.OperationId,
                    "verify_unlock",
                    "Remote LFS lock release was not confirmed."));
            }

            checkpoint = checkpoint.Advance("unlock_confirmed", values: [("unlockConfirmed", "true")]);
            await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var completion = await CompleteAbortAsync(request.TaskId, request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (!completion.IsSuccess)
        {
            return Outcome<AbortSceneTaskResult>.Failure(completion.Problem!);
        }

        checkpoint = checkpoint.Advance("completed", isTerminal: true);
        await _journal.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return Outcome<AbortSceneTaskResult>.Success(new AbortSceneTaskResult(
            completion.Value!.Lifecycle,
            checkpoint.Data["preservationEvidence"]));
    }

    private async Task<Outcome<Unit>> RequireAuthorityAsync(
        AbortSceneTaskRequest request,
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
                "abort_preflight",
                "Task coordination state does not exist."));
        }

        return SessionAuthority.RequireMutationPermission(
            snapshot.Value,
            request.Authority,
            request.OperationId,
            "abort_preflight");
    }

    private async Task<Outcome<CoordinationAggregate>> RevokeSessionAsync(
        AbortSceneTaskRequest request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await _stateStore.ReadAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Value is null)
            {
                return Missing(request.OperationId, "revoke_session");
            }

            var current = snapshot.Value;
            if (current.Lifecycle == TaskLifecycle.UnlockPending && current.ActiveSession is null)
            {
                return Outcome<CoordinationAggregate>.Success(current);
            }

            var authority = SessionAuthority.RequireMutationPermission(
                current,
                request.Authority,
                request.OperationId,
                "revoke_session");
            if (!authority.IsSuccess)
            {
                return Outcome<CoordinationAggregate>.Failure(authority.Problem!);
            }

            var next = CoordinationTransitions.RevokeSessionForAbort(current, request.OperationId);
            var write = await _stateStore.CompareExchangeAsync(
                request.TaskId,
                current.Revision,
                next,
                cancellationToken).ConfigureAwait(false);
            if (write.Status == CasWriteStatus.Written)
            {
                return Outcome<CoordinationAggregate>.Success(write.Current);
            }
        }

        return CasExhausted(request.OperationId, "revoke_session");
    }

    private async Task<Outcome<CoordinationAggregate>> CompleteAbortAsync(
        string taskId,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await _stateStore.ReadAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Value is null)
            {
                return Missing(operationId, "complete_abort");
            }

            var current = snapshot.Value;
            if (current.Lifecycle == TaskLifecycle.Aborted)
            {
                return Outcome<CoordinationAggregate>.Success(current);
            }

            if (current.Lifecycle != TaskLifecycle.UnlockPending || current.ActiveSession is not null)
            {
                return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                    "abort_precondition_failed",
                    ProblemCategory.RecoveryRequired,
                    retryable: false,
                    operationId,
                    "complete_abort",
                    "Abort can complete only after session revocation and unlock confirmation."));
            }

            var next = CoordinationTransitions.CompleteAbort(current, operationId);
            var write = await _stateStore.CompareExchangeAsync(
                taskId,
                current.Revision,
                next,
                cancellationToken).ConfigureAwait(false);
            if (write.Status == CasWriteStatus.Written)
            {
                return Outcome<CoordinationAggregate>.Success(write.Current);
            }
        }

        return CasExhausted(operationId, "complete_abort");
    }

    private static Outcome<CoordinationAggregate> Missing(OperationId operationId, string phase) =>
        Outcome<CoordinationAggregate>.Failure(Problem.Create(
            "coordination_missing",
            ProblemCategory.RecoveryRequired,
            retryable: false,
            operationId,
            phase,
            "Task coordination state does not exist."));

    private static Outcome<CoordinationAggregate> CasExhausted(OperationId operationId, string phase) =>
        Outcome<CoordinationAggregate>.Failure(Problem.Create(
            "coordination_cas_exhausted",
            ProblemCategory.Conflict,
            retryable: true,
            operationId,
            phase,
            "Coordination state changed repeatedly."));
}
