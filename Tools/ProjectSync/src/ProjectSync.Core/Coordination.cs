namespace ProjectSync.Core;

public enum TaskLifecycle
{
    Starting,
    Active,
    Saving,
    SavedRemote,
    Submitted,
    Validating,
    MergeReady,
    Merged,
    Completed,
    OnHold,
    AbortPreparing,
    UnlockPending,
    Aborted,
    RecoveryRequired
}

public enum CoordinationHealth
{
    Healthy,
    RecoveryRequired
}

public sealed record SessionFence(
    string GitHubUser,
    string DeviceId,
    string SessionId,
    long Generation,
    string LfsLockId,
    string ScenePath);

public sealed record SessionStartClaim(
    string GitHubUser,
    string DeviceId,
    string SessionId,
    long Generation,
    string ScenePath,
    string OperationId);

public sealed record CoordinationAggregate(
    string TaskId,
    long Revision,
    long Generation,
    TaskLifecycle Lifecycle,
    CoordinationHealth Health,
    string BaseMainSha,
    string TaskBranch,
    SessionStartClaim? StartClaim,
    SessionFence? ActiveSession,
    string? RecoveryReason,
    string LastOperationId)
{
    public static CoordinationAggregate CreateStarting(
        string taskId,
        string baseMainSha,
        string taskBranch,
        OperationId operationId) =>
        new(
            taskId,
            Revision: 1,
            Generation: 0,
            TaskLifecycle.Starting,
            CoordinationHealth.Healthy,
            baseMainSha,
            taskBranch,
            StartClaim: null,
            ActiveSession: null,
            RecoveryReason: null,
            operationId.Value);
}

public sealed record CoordinationSnapshot(CoordinationAggregate? Value)
{
    public long Revision => Value?.Revision ?? 0;
}

public enum CasWriteStatus
{
    Written,
    Conflict
}

public sealed record CasWriteResult(CasWriteStatus Status, CoordinationAggregate Current);

public interface ICoordinationStateStore
{
    Task<CoordinationSnapshot> ReadAsync(string taskId, CancellationToken cancellationToken);

    Task<CasWriteResult> CompareExchangeAsync(
        string taskId,
        long expectedRevision,
        CoordinationAggregate next,
        CancellationToken cancellationToken);
}

public sealed record SessionAuthorityRequest(
    string TaskId,
    string GitHubUser,
    string DeviceId,
    string SessionId,
    long Generation,
    string LfsLockId,
    string ScenePath);

public static class SessionAuthority
{
    public static Outcome<Unit> RequireMutationPermission(
        CoordinationAggregate aggregate,
        SessionAuthorityRequest request,
        OperationId operationId,
        string phase)
    {
        if (aggregate.Health != CoordinationHealth.Healthy ||
            aggregate.Lifecycle == TaskLifecycle.RecoveryRequired)
        {
            return Outcome<Unit>.Failure(Problem.Create(
                "coordination_recovery_required",
                ProblemCategory.RecoveryRequired,
                retryable: false,
                operationId,
                phase,
                "Coordination state requires explicit recovery."));
        }

        var session = aggregate.ActiveSession;
        var lifecycleAllowsMutation = aggregate.Lifecycle is
            TaskLifecycle.Active or
            TaskLifecycle.Saving or
            TaskLifecycle.SavedRemote;
        var matches = lifecycleAllowsMutation &&
                      session is not null &&
                      string.Equals(aggregate.TaskId, request.TaskId, StringComparison.Ordinal) &&
                      string.Equals(session.GitHubUser, request.GitHubUser, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(session.DeviceId, request.DeviceId, StringComparison.Ordinal) &&
                      string.Equals(session.SessionId, request.SessionId, StringComparison.Ordinal) &&
                      session.Generation == request.Generation &&
                      string.Equals(session.LfsLockId, request.LfsLockId, StringComparison.Ordinal) &&
                      string.Equals(session.ScenePath, request.ScenePath, StringComparison.Ordinal);

        return matches
            ? Outcome<Unit>.Success(Unit.Value)
            : Outcome<Unit>.Failure(Problem.Create(
                "session_fence_mismatch",
                ProblemCategory.Authorization,
                retryable: false,
                operationId,
                phase,
                "Task, user, device, session, generation, lock, and scene path must all match the active coordination state."));
    }
}

public static class CoordinationTransitions
{
    public static Outcome<CoordinationAggregate> ClaimSessionStart(
        CoordinationAggregate current,
        string githubUser,
        string deviceId,
        string sessionId,
        string scenePath,
        OperationId operationId)
    {
        if (current.Health != CoordinationHealth.Healthy)
        {
            return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                "coordination_recovery_required",
                ProblemCategory.RecoveryRequired,
                retryable: false,
                operationId,
                "claim_session",
                "A recovery-required aggregate cannot claim a session."));
        }

        if (current.StartClaim is not null)
        {
            var claim = current.StartClaim;
            if (string.Equals(claim.OperationId, operationId.Value, StringComparison.Ordinal) &&
                string.Equals(claim.DeviceId, deviceId, StringComparison.Ordinal) &&
                string.Equals(claim.SessionId, sessionId, StringComparison.Ordinal))
            {
                return Outcome<CoordinationAggregate>.Success(current);
            }

            return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                "session_start_claimed",
                ProblemCategory.Conflict,
                retryable: false,
                operationId,
                "claim_session",
                "Another device/session already owns the start claim."));
        }

        if (current.ActiveSession is not null)
        {
            return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                "active_session_exists",
                ProblemCategory.Conflict,
                retryable: false,
                operationId,
                "claim_session",
                "Another device/session is already active."));
        }

        var generation = checked(current.Generation + 1);
        return Outcome<CoordinationAggregate>.Success(current with
        {
            Revision = checked(current.Revision + 1),
            Generation = generation,
            Lifecycle = TaskLifecycle.Starting,
            StartClaim = new SessionStartClaim(
                githubUser,
                deviceId,
                sessionId,
                generation,
                scenePath,
                operationId.Value),
            LastOperationId = operationId.Value
        });
    }

    public static Outcome<CoordinationAggregate> ActivateClaimedSession(
        CoordinationAggregate current,
        string lfsLockId,
        OperationId operationId)
    {
        var claim = current.StartClaim;
        if (claim is null || !string.Equals(claim.OperationId, operationId.Value, StringComparison.Ordinal))
        {
            return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                "session_start_claim_mismatch",
                ProblemCategory.Authorization,
                retryable: false,
                operationId,
                "activate_session",
                "The operation does not own the pending session claim."));
        }

        return Outcome<CoordinationAggregate>.Success(current with
        {
            Revision = checked(current.Revision + 1),
            Lifecycle = TaskLifecycle.Active,
            StartClaim = null,
            ActiveSession = new SessionFence(
                claim.GitHubUser,
                claim.DeviceId,
                claim.SessionId,
                claim.Generation,
                lfsLockId,
                claim.ScenePath),
            LastOperationId = operationId.Value
        });
    }

    public static Outcome<CoordinationAggregate> ActivateSession(
        CoordinationAggregate current,
        string githubUser,
        string deviceId,
        string sessionId,
        string lfsLockId,
        string scenePath,
        OperationId operationId)
    {
        if (current.Health != CoordinationHealth.Healthy)
        {
            return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                "coordination_recovery_required",
                ProblemCategory.RecoveryRequired,
                retryable: false,
                operationId,
                "activate_session",
                "A recovery-required aggregate cannot activate a session."));
        }

        if (current.ActiveSession is not null)
        {
            var existing = current.ActiveSession;
            if (string.Equals(current.LastOperationId, operationId.Value, StringComparison.Ordinal) &&
                string.Equals(existing.DeviceId, deviceId, StringComparison.Ordinal) &&
                string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal))
            {
                return Outcome<CoordinationAggregate>.Success(current);
            }

            return Outcome<CoordinationAggregate>.Failure(Problem.Create(
                "active_session_exists",
                ProblemCategory.Conflict,
                retryable: false,
                operationId,
                "activate_session",
                "Another device/session is already active."));
        }

        var generation = checked(current.Generation + 1);
        return Outcome<CoordinationAggregate>.Success(current with
        {
            Revision = checked(current.Revision + 1),
            Generation = generation,
            Lifecycle = TaskLifecycle.Active,
            StartClaim = null,
            ActiveSession = new SessionFence(
                githubUser,
                deviceId,
                sessionId,
                generation,
                lfsLockId,
                scenePath),
            LastOperationId = operationId.Value
        });
    }

    public static CoordinationAggregate MarkRecoveryRequired(
        CoordinationAggregate current,
        string reason,
        OperationId operationId) =>
        current with
        {
            Revision = checked(current.Revision + 1),
            Lifecycle = TaskLifecycle.RecoveryRequired,
            Health = CoordinationHealth.RecoveryRequired,
            RecoveryReason = reason,
            LastOperationId = operationId.Value
        };

    public static CoordinationAggregate RevokeSessionForAbort(
        CoordinationAggregate current,
        OperationId operationId) =>
        current with
        {
            Revision = checked(current.Revision + 1),
            Generation = checked(current.Generation + 1),
            Lifecycle = TaskLifecycle.UnlockPending,
            StartClaim = null,
            ActiveSession = null,
            LastOperationId = operationId.Value
        };

    public static CoordinationAggregate CompleteAbort(
        CoordinationAggregate current,
        OperationId operationId) =>
        current with
        {
            Revision = checked(current.Revision + 1),
            Lifecycle = TaskLifecycle.Aborted,
            StartClaim = null,
            ActiveSession = null,
            LastOperationId = operationId.Value
        };
}
