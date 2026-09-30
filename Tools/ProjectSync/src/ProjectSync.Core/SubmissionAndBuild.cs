namespace ProjectSync.Core;

public sealed record CandidateIdentity(
    string SubmittedCommitSha,
    string BaseMainSha,
    string CandidateSha,
    string ConflictResolutionDigest,
    long Generation)
{
    public bool IsValidationApplicableTo(CandidateIdentity current) =>
        string.Equals(SubmittedCommitSha, current.SubmittedCommitSha, StringComparison.Ordinal) &&
        string.Equals(BaseMainSha, current.BaseMainSha, StringComparison.Ordinal) &&
        string.Equals(CandidateSha, current.CandidateSha, StringComparison.Ordinal) &&
        string.Equals(ConflictResolutionDigest, current.ConflictResolutionDigest, StringComparison.Ordinal) &&
        Generation == current.Generation;
}

public enum UploadOutcome
{
    Unknown,
    Success,
    Failed
}

public enum BuildAttemptLifecycle
{
    Preparing,
    Building,
    UploadRecorded,
    DeploymentRecordPending,
    Completed,
    NeedsReview
}

public sealed record BuildAttempt(
    string BuildAttemptId,
    string BuildTargetSha,
    string WorldId,
    string Platform,
    string Operator,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    BuildAttemptLifecycle Lifecycle,
    UploadOutcome UploadOutcome,
    string? UploadEvidence)
{
    public static BuildAttempt Start(
        string buildAttemptId,
        string buildTargetSha,
        string worldId,
        string platform,
        string @operator,
        DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(buildTargetSha))
        {
            throw new ArgumentException("BuildTargetSHA is required.", nameof(buildTargetSha));
        }

        return new BuildAttempt(
            buildAttemptId,
            buildTargetSha,
            worldId,
            platform,
            @operator,
            startedAt,
            EndedAt: null,
            BuildAttemptLifecycle.Preparing,
            UploadOutcome.Unknown,
            UploadEvidence: null);
    }

    public BuildAttempt BeginBuild() => this with { Lifecycle = BuildAttemptLifecycle.Building };

    public BuildAttempt RecordUpload(UploadOutcome outcome, string? evidence) =>
        this with
        {
            Lifecycle = BuildAttemptLifecycle.UploadRecorded,
            UploadOutcome = outcome,
            UploadEvidence = evidence
        };

    public BuildAttempt MarkDeploymentRecordPending() =>
        this with { Lifecycle = BuildAttemptLifecycle.DeploymentRecordPending };

    public BuildAttempt Complete(DateTimeOffset endedAt) =>
        this with { Lifecycle = BuildAttemptLifecycle.Completed, EndedAt = endedAt };

    public BuildAttempt MarkNeedsReview(DateTimeOffset endedAt) =>
        this with { Lifecycle = BuildAttemptLifecycle.NeedsReview, EndedAt = endedAt };
}
