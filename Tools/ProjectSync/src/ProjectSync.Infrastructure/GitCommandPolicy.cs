using System.Diagnostics;
using System.Text;
using ProjectSync.Core;

namespace ProjectSync.Infrastructure;

public static class GitCommandPolicy
{
    private static readonly HashSet<string> ForceOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--force",
        "--force-with-lease",
        "--force-if-includes",
        "-f"
    };

    public static Outcome<Unit> Validate(IReadOnlyList<string> arguments, OperationId operationId)
    {
        if (arguments.Count == 0)
        {
            return Reject("git_command_missing", "A Git subcommand is required.", operationId);
        }

        var command = arguments[0];
        if (string.Equals(command, "stash", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "reset", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "clean", StringComparison.OrdinalIgnoreCase))
        {
            return Reject("git_destructive_command_forbidden", $"git {command} is forbidden by ProjectSync policy.", operationId);
        }

        if (arguments.Any(arg => ForceOptions.Contains(arg)) ||
            arguments.Any(arg => arg.StartsWith("--force=", StringComparison.OrdinalIgnoreCase)))
        {
            return Reject("git_force_forbidden", "Force operations are forbidden by ProjectSync policy.", operationId);
        }

        if (string.Equals(command, "push", StringComparison.OrdinalIgnoreCase) &&
            arguments.Any(arg => string.Equals(arg, "--delete", StringComparison.OrdinalIgnoreCase)))
        {
            return Reject("git_remote_delete_forbidden", "Remote deletion is not available through the normal ProjectSync command path.", operationId);
        }

        return Outcome<Unit>.Success(Unit.Value);
    }

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

public sealed class SafeGitProcessRunner
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
