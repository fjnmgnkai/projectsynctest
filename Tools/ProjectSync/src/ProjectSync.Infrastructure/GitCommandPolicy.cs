using System.Diagnostics;
using System.Text;
using ProjectSync.Core;

namespace ProjectSync.Infrastructure;

public static class GitCommandPolicy
{
    public static Outcome<Unit> Validate(IReadOnlyList<string> arguments, OperationId operationId)
    {
        if (arguments.Count == 0)
        {
            return Reject("git_command_missing", "A Git subcommand is required.", operationId);
        }

        var command = arguments[0];
        var allowed = command switch
        {
            "symbolic-ref" => Matches(arguments, "symbolic-ref", "--quiet", "--short", "HEAD"),
            "status" => Matches(arguments, "status", "--porcelain=v1", "--untracked-files=all"),
            "rev-parse" => Matches(arguments, "rev-parse", "--verify", "HEAD") ||
                           Matches(arguments, "rev-parse", "--verify", "FETCH_HEAD"),
            "log" => Matches(arguments, "log", "-1", "--format=%B"),
            "add" => Matches(arguments, "add", "-A"),
            "commit" => arguments.Count == 5 &&
                        arguments[1] == "-m" &&
                        !string.IsNullOrWhiteSpace(arguments[2]) &&
                        arguments[3] == "-m" &&
                        arguments[4] == $"ProjectSync-Operation-Id: {operationId.Value}",
            "push" => arguments.Count == 4 &&
                      arguments[1] == "--porcelain" &&
                      arguments[2] == "origin" &&
                      IsTaskRefspec(arguments[3], operationId),
            "ls-remote" => arguments.Count == 5 &&
                           arguments[1] == "--exit-code" &&
                           arguments[2] == "--heads" &&
                           arguments[3] == "origin" &&
                           IsTaskRef(arguments[4], operationId),
            "fetch" => arguments.Count == 4 &&
                       arguments[1] == "--no-tags" &&
                       arguments[2] == "origin" &&
                       IsTaskRef(arguments[3], operationId),
            "merge-base" => arguments.Count == 4 &&
                            arguments[1] == "--is-ancestor" &&
                            IsFullSha(arguments[2]) &&
                            IsFullSha(arguments[3]),
            _ => false
        };

        return allowed
            ? Outcome<Unit>.Success(Unit.Value)
            : Reject("git_command_not_allowlisted", "This exact Git command is not available through ProjectSync.", operationId);
    }

    public static bool IsFullSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool Matches(IReadOnlyList<string> arguments, params string[] expected) =>
        arguments.Count == expected.Length &&
        arguments.Zip(expected).All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));

    private static bool IsTaskRefspec(string refspec, OperationId operationId)
    {
        var separator = refspec.IndexOf(':');
        return separator > 0 &&
               separator == refspec.LastIndexOf(':') &&
               IsFullSha(refspec[..separator]) &&
               IsTaskRef(refspec[(separator + 1)..], operationId);
    }

    private static bool IsTaskRef(string reference, OperationId operationId) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) &&
        TaskBranchPolicy.RequireTaskBranch(
            reference["refs/heads/".Length..],
            operationId,
            "git_policy").IsSuccess;

    private static Outcome<Unit> Reject(string code, string message, OperationId operationId) =>
        Outcome<Unit>.Failure(Problem.Create(
            code,
            ProblemCategory.Authorization,
            retryable: false,
            operationId,
            "git_policy",
            message));
}

public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class SafeGitProcessRunner
{
    public async Task<Outcome<GitCommandResult>> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        OperationId operationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var policy = GitCommandPolicy.Validate(arguments, operationId);
        if (!policy.IsSuccess)
        {
            return Outcome<GitCommandResult>.Failure(policy.Problem!);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failure("git_start_failed", "Git process did not start.", operationId, retryable: true);
            }

            process.StandardInput.Close();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return Outcome<GitCommandResult>.Success(new GitCommandResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return Failure(
                "git_outcome_unknown",
                "Git timed out. The caller must inspect local and remote state before retrying.",
                operationId,
                retryable: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failure("git_start_failed", exception.Message, operationId, retryable: true);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the checks.
        }
    }

    private static Outcome<GitCommandResult> Failure(
        string code,
        string message,
        OperationId operationId,
        bool retryable) =>
        Outcome<GitCommandResult>.Failure(Problem.Create(
            code,
            ProblemCategory.Transport,
            retryable,
            operationId,
            "git_process",
            message));
}
