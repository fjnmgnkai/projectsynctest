namespace ProjectSync.Core;

public static class TaskBranchPolicy
{
    public static Outcome<Unit> RequireTaskBranch(
        string? branch,
        OperationId operationId,
        string phase)
    {
        if (string.IsNullOrWhiteSpace(branch) ||
            !branch.StartsWith("task/", StringComparison.Ordinal) ||
            branch.Length > 200)
        {
            return Invalid(operationId, phase);
        }

        var segments = branch["task/".Length..].Split('/');
        if (segments.Any(segment => segment.Length is < 1 or > 80 ||
                                    !IsAsciiAlphaNumeric(segment[0]) ||
                                    !IsAsciiAlphaNumeric(segment[^1]) ||
                                    segment.Any(character =>
                                        !IsAsciiAlphaNumeric(character) && character is not '-' and not '_')))
        {
            return Invalid(operationId, phase);
        }

        return Outcome<Unit>.Success(Unit.Value);
    }

    private static bool IsAsciiAlphaNumeric(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static Outcome<Unit> Invalid(OperationId operationId, string phase) =>
        Outcome<Unit>.Failure(Problem.Create(
            "task_branch_invalid",
            ProblemCategory.Authorization,
            retryable: false,
            operationId,
            phase,
            "ProjectSync writes only to a valid task/... branch; main is read-only."));
}
