namespace ProjectSync.Core;

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
