namespace ProjectSync.Core;

public enum ProblemCategory
{
    Validation,
    Authorization,
    Conflict,
    Transport,
    ExternalSystem,
    RecoveryRequired,
    Configuration
}

public sealed record Problem(
    string ErrorCode,
    ProblemCategory Category,
    bool Retryable,
    string OperationId,
    string Phase,
    string Message)
{
    public static Problem Create(
        string errorCode,
        ProblemCategory category,
        bool retryable,
        OperationId operationId,
        string phase,
        string message) =>
        new(errorCode, category, retryable, operationId.Value, phase, message);
}

public readonly record struct Outcome<T>(T? Value, Problem? Problem)
{
    public bool IsSuccess => Problem is null;

    public static Outcome<T> Success(T value) => new(value, null);

    public static Outcome<T> Failure(Problem problem) => new(default, problem);
}

public readonly record struct Unit
{
    public static readonly Unit Value = new();
}

public readonly record struct OperationId
{
    public OperationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Operation ID is required.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static OperationId New() => new(Guid.NewGuid().ToString("N"));
}
