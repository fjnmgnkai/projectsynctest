using ProjectSync.Core;

namespace ProjectSync.Infrastructure;

public sealed class GitCliTaskGateway : IGitTaskGateway
{
    private readonly string _repositoryPath;
    private readonly SafeGitProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public GitCliTaskGateway(string repositoryPath, TimeSpan? timeout = null)
    {
        _repositoryPath = Path.GetFullPath(repositoryPath);
        _runner = new SafeGitProcessRunner();
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<Outcome<string>> CreateSnapshotAsync(
        string taskBranch,
        string message,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        var branch = TaskBranchPolicy.RequireTaskBranch(taskBranch, operationId, "snapshot_preflight");
        if (!branch.IsSuccess)
        {
            return Outcome<string>.Failure(branch.Problem!);
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return Failure<string>("commit_message_missing", ProblemCategory.Validation, operationId, "snapshot_preflight", "A snapshot message is required.");
        }

        var checkedOut = await RequireCheckedOutBranchAsync(taskBranch, operationId, cancellationToken).ConfigureAwait(false);
        if (!checkedOut.IsSuccess)
        {
            return Outcome<string>.Failure(checkedOut.Problem!);
        }

        var head = await ReadHeadAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (!head.IsSuccess)
        {
            return head;
        }

        var priorOperation = await HeadHasOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (!priorOperation.IsSuccess)
        {
            return Outcome<string>.Failure(priorOperation.Problem!);
        }

        if (priorOperation.Value is true)
        {
            return head;
        }

        var status = await RunRequiredAsync(
            ["status", "--porcelain=v1", "--untracked-files=all"],
            operationId,
            "snapshot_status",
            cancellationToken).ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return Outcome<string>.Failure(status.Problem!);
        }

        if (string.IsNullOrEmpty(status.Value!.StandardOutput))
        {
            return head;
        }

        var stage = await RunRequiredAsync(["add", "-A"], operationId, "snapshot_stage", cancellationToken).ConfigureAwait(false);
        if (!stage.IsSuccess)
        {
            return Outcome<string>.Failure(stage.Problem!);
        }

        var commit = await RunRequiredAsync(
            ["commit", "-m", message, "-m", $"ProjectSync-Operation-Id: {operationId.Value}"],
            operationId,
            "snapshot_commit",
            cancellationToken).ConfigureAwait(false);
        if (!commit.IsSuccess)
        {
            // A hook or a lost process response may have created the commit.
            var afterFailure = await ReadHeadAsync(operationId, cancellationToken).ConfigureAwait(false);
            var committed = await HeadHasOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            return afterFailure.IsSuccess && committed.IsSuccess && committed.Value is true
                ? afterFailure
                : Outcome<string>.Failure(commit.Problem!);
        }

        var created = await ReadHeadAsync(operationId, cancellationToken).ConfigureAwait(false);
        var verified = await HeadHasOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess && verified.IsSuccess && verified.Value is true
            ? created
            : Failure<string>(
                "snapshot_commit_unconfirmed",
                ProblemCategory.RecoveryRequired,
                operationId,
                "snapshot_commit",
                "The snapshot commit could not be identified after Git returned.");
    }

    public async Task<Outcome<Unit>> PushCommitAsync(
        string taskBranch,
        string commitSha,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        var branch = TaskBranchPolicy.RequireTaskBranch(taskBranch, operationId, "push_preflight");
        if (!branch.IsSuccess)
        {
            return branch;
        }

        if (!GitCommandPolicy.IsFullSha(commitSha))
        {
            return Failure<Unit>("commit_sha_invalid", ProblemCategory.Validation, operationId, "push_preflight", "A full commit SHA is required.");
        }

        var checkedOut = await RequireCheckedOutBranchAsync(taskBranch, operationId, cancellationToken).ConfigureAwait(false);
        if (!checkedOut.IsSuccess)
        {
            return checkedOut;
        }

        var result = await RunRequiredAsync(
            ["push", "--porcelain", "origin", $"{commitSha}:refs/heads/{taskBranch}"],
            operationId,
            "push_task",
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Outcome<Unit>.Success(Unit.Value) : Outcome<Unit>.Failure(result.Problem!);
    }

    public async Task<Outcome<bool>> IsCommitReachableAsync(
        string taskBranch,
        string commitSha,
        CancellationToken cancellationToken)
    {
        var operationId = new OperationId("verify-remote-" + Guid.NewGuid().ToString("N"));
        var branch = TaskBranchPolicy.RequireTaskBranch(taskBranch, operationId, "verify_remote");
        if (!branch.IsSuccess)
        {
            return Outcome<bool>.Failure(branch.Problem!);
        }

        if (!GitCommandPolicy.IsFullSha(commitSha))
        {
            return Failure<bool>("commit_sha_invalid", ProblemCategory.Validation, operationId, "verify_remote", "A full commit SHA is required.");
        }

        var taskRef = $"refs/heads/{taskBranch}";
        var listing = await _runner.RunAsync(
            _repositoryPath,
            ["ls-remote", "--exit-code", "--heads", "origin", taskRef],
            operationId,
            _timeout,
            cancellationToken).ConfigureAwait(false);
        if (!listing.IsSuccess)
        {
            return Outcome<bool>.Failure(listing.Problem!);
        }

        if (listing.Value!.ExitCode == 2)
        {
            return Outcome<bool>.Success(false);
        }

        if (listing.Value.ExitCode != 0)
        {
            return GitFailure<bool>(operationId, "verify_remote", "Remote Task ref could not be read.");
        }

        var fields = listing.Value.StandardOutput.Trim().Split('\t');
        if (fields.Length != 2 ||
            !GitCommandPolicy.IsFullSha(fields[0]) ||
            !string.Equals(fields[1], taskRef, StringComparison.Ordinal))
        {
            return Failure<bool>("remote_ref_unexpected", ProblemCategory.RecoveryRequired, operationId, "verify_remote", "Remote Task ref response is not the expected single ref.");
        }

        var remoteSha = fields[0];
        if (string.Equals(remoteSha, commitSha, StringComparison.OrdinalIgnoreCase))
        {
            return Outcome<bool>.Success(true);
        }

        var fetched = await RunRequiredAsync(
            ["fetch", "--no-tags", "origin", taskRef],
            operationId,
            "verify_remote_fetch",
            cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess)
        {
            return Outcome<bool>.Failure(fetched.Problem!);
        }

        var fetchHead = await RunRequiredAsync(
            ["rev-parse", "--verify", "FETCH_HEAD"],
            operationId,
            "verify_remote_fetch",
            cancellationToken).ConfigureAwait(false);
        if (!fetchHead.IsSuccess)
        {
            return Outcome<bool>.Failure(fetchHead.Problem!);
        }

        if (!string.Equals(fetchHead.Value!.StandardOutput.Trim(), remoteSha, StringComparison.OrdinalIgnoreCase))
        {
            return Failure<bool>("remote_ref_changed_during_verify", ProblemCategory.Conflict, operationId, "verify_remote", "Remote Task ref changed while checking reachability.");
        }

        var ancestor = await _runner.RunAsync(
            _repositoryPath,
            ["merge-base", "--is-ancestor", commitSha, remoteSha],
            operationId,
            _timeout,
            cancellationToken).ConfigureAwait(false);
        if (!ancestor.IsSuccess)
        {
            return Outcome<bool>.Failure(ancestor.Problem!);
        }

        return ancestor.Value!.ExitCode switch
        {
            0 => Outcome<bool>.Success(true),
            1 => Outcome<bool>.Success(false),
            _ => GitFailure<bool>(operationId, "verify_remote", "Remote commit ancestry could not be checked.")
        };
    }

    private async Task<Outcome<Unit>> RequireCheckedOutBranchAsync(
        string taskBranch,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        var current = await RunRequiredAsync(
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            operationId,
            "branch_preflight",
            cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return Outcome<Unit>.Failure(current.Problem!);
        }

        return string.Equals(current.Value!.StandardOutput.Trim(), taskBranch, StringComparison.Ordinal)
            ? Outcome<Unit>.Success(Unit.Value)
            : Failure<Unit>("checked_out_branch_mismatch", ProblemCategory.Authorization, operationId, "branch_preflight", "The checked-out branch is not the requested Task branch.");
    }

    private async Task<Outcome<string>> ReadHeadAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync(
            ["rev-parse", "--verify", "HEAD"],
            operationId,
            "read_head",
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Outcome<string>.Failure(result.Problem!);
        }

        var sha = result.Value!.StandardOutput.Trim();
        return GitCommandPolicy.IsFullSha(sha)
            ? Outcome<string>.Success(sha)
            : Failure<string>("head_sha_invalid", ProblemCategory.RecoveryRequired, operationId, "read_head", "Git returned an invalid HEAD SHA.");
    }

    private async Task<Outcome<bool>> HeadHasOperationAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync(
            ["log", "-1", "--format=%B"],
            operationId,
            "inspect_snapshot",
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Outcome<bool>.Failure(result.Problem!);
        }

        var trailer = $"ProjectSync-Operation-Id: {operationId.Value}";
        return Outcome<bool>.Success(result.Value!.StandardOutput
            .Split('\n', StringSplitOptions.TrimEntries)
            .Any(line => string.Equals(line, trailer, StringComparison.Ordinal)));
    }

    private async Task<Outcome<GitCommandResult>> RunRequiredAsync(
        IReadOnlyList<string> arguments,
        OperationId operationId,
        string phase,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            _repositoryPath,
            arguments,
            operationId,
            _timeout,
            cancellationToken).ConfigureAwait(false);
        return !result.IsSuccess || result.Value!.ExitCode == 0
            ? result
            : GitFailure<GitCommandResult>(operationId, phase, "Git command did not complete successfully. Local and remote facts must be checked before retrying.");
    }

    private static Outcome<T> GitFailure<T>(OperationId operationId, string phase, string message) =>
        Failure<T>("git_command_failed", ProblemCategory.ExternalSystem, operationId, phase, message);

    private static Outcome<T> Failure<T>(
        string code,
        ProblemCategory category,
        OperationId operationId,
        string phase,
        string message) =>
        Outcome<T>.Failure(Problem.Create(code, category, retryable: false, operationId, phase, message));
}
