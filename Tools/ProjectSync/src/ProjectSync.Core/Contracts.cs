namespace ProjectSync.Core;

public sealed record OperationCheckpoint(
    string OperationId,
    string Kind,
    string SubjectId,
    string Phase,
    IReadOnlyDictionary<string, string> Data,
    bool IsTerminal)
{
    public static OperationCheckpoint Create(
        OperationId operationId,
        string kind,
        string subjectId) =>
        new(
            operationId.Value,
            kind,
            subjectId,
            "new",
            new Dictionary<string, string>(StringComparer.Ordinal),
            IsTerminal: false);

    public OperationCheckpoint Advance(
        string phase,
        bool isTerminal = false,
        params (string Key, string Value)[] values)
    {
        var data = new Dictionary<string, string>(Data, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            data[key] = value;
        }

        return this with { Phase = phase, Data = data, IsTerminal = isTerminal };
    }
}

public interface IOperationJournal
{
    Task<OperationCheckpoint> LoadOrCreateAsync(
        OperationCheckpoint seed,
        CancellationToken cancellationToken);

    Task SaveAsync(OperationCheckpoint checkpoint, CancellationToken cancellationToken);
}

public sealed record IssueReference(long Number, string Url);

public interface ITaskIssueGateway
{
    Task<Outcome<IssueReference>> EnsureTaskIssueAsync(
        string taskId,
        OperationId operationId,
        CancellationToken cancellationToken);
}

public sealed record LfsLockReference(string LockId, string Path, string Owner);

public interface ILfsLockGateway
{
    Task<Outcome<LfsLockReference>> AcquireAsync(
        string path,
        string githubUser,
        OperationId operationId,
        CancellationToken cancellationToken);

    Task<Outcome<Unit>> ReleaseAsync(
        string lockId,
        OperationId operationId,
        CancellationToken cancellationToken);

    Task<Outcome<bool>> VerifyUnlockedAsync(
        string path,
        CancellationToken cancellationToken);
}

public sealed record UnitySaveReceipt(DateTimeOffset SavedAt, IReadOnlyList<string> ScenePaths);

public interface IUnitySaveGateway
{
    Task<Outcome<UnitySaveReceipt>> SaveOpenScenesAndAssetsAsync(
        OperationId operationId,
        CancellationToken cancellationToken);
}

public interface IGitTaskGateway
{
    Task<Outcome<string>> CreateSnapshotAsync(
        string taskBranch,
        string message,
        OperationId operationId,
        CancellationToken cancellationToken);

    Task<Outcome<Unit>> PushCommitAsync(
        string taskBranch,
        string commitSha,
        OperationId operationId,
        CancellationToken cancellationToken);

    Task<Outcome<bool>> IsCommitReachableAsync(
        string taskBranch,
        string commitSha,
        CancellationToken cancellationToken);
}

public sealed record LocalPreservationReceipt(
    bool HasUnpublishedData,
    string EvidenceLocation,
    DateTimeOffset CheckedAt);

public interface ILocalDataProtector
{
    Task<Outcome<LocalPreservationReceipt>> InspectAndPreserveAsync(
        string taskId,
        OperationId operationId,
        CancellationToken cancellationToken);
}
