namespace ProjectSync.Core;

public static class TrustedServiceContract
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record CoordinationCasRequest(
    int SchemaVersion,
    string OperationId,
    string RepositoryId,
    string TaskId,
    long ExpectedRevision,
    CoordinationAggregate Next);

public sealed record CoordinationCasResponse(
    int SchemaVersion,
    string OperationId,
    CasWriteStatus Status,
    CoordinationAggregate Current,
    Problem? Problem);

public sealed record CandidateValidationRequest(
    int SchemaVersion,
    string OperationId,
    string RepositoryId,
    long PullRequestNumber,
    CandidateIdentity Candidate);

public sealed record SquashMergeRequest(
    int SchemaVersion,
    string OperationId,
    string RepositoryId,
    long PullRequestNumber,
    CandidateIdentity Candidate,
    string ExpectedPullRequestHeadSha,
    string ExpectedBaseMainSha);

public sealed record DeploymentProjectionRequest(
    int SchemaVersion,
    string OperationId,
    string RepositoryId,
    string BuildAttemptId,
    string BuildTargetSha,
    bool AutoMerge)
{
    public static DeploymentProjectionRequest Create(
        OperationId operationId,
        string repositoryId,
        BuildAttempt attempt) =>
        new(
            TrustedServiceContract.CurrentSchemaVersion,
            operationId.Value,
            repositoryId,
            attempt.BuildAttemptId,
            attempt.BuildTargetSha,
            AutoMerge: false);
}
