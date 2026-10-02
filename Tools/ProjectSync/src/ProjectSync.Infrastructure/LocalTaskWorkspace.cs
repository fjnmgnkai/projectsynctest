using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectSync.Core;

namespace ProjectSync.Infrastructure;

public sealed record WorkspaceInspection(string Branch, string Origin, bool HasLocalChanges);
public sealed record LocalSaveResult(string Branch, string CommitSha);
public sealed record LocalSubmissionResult(string Url, string SubmittedSha);

/// <summary>
/// Local-first Task workflow. Git owns branch/commit facts; the per-clone file only
/// records an unfinished save so a retry never discards a successful commit.
/// </summary>
public sealed class LocalTaskWorkspace
{
    private const string MainRef = "refs/heads/main";
    private const string SubmittedPrefix = "<!-- ProjectSync-Submitted-SHA: ";
    private readonly string _repositoryPath;
    private readonly SafeGitProcessRunner _git = new();
    private readonly GitCliTaskGateway _snapshots;
    private readonly UnityBridgeClient _unity;
    private readonly Func<bool> _unityEditorRunning;
    private readonly string _localDirectory;
    private readonly TimeSpan _gitTimeout;

    public LocalTaskWorkspace(
        string repositoryPath,
        TimeSpan? gitTimeout = null,
        Func<bool>? unityEditorRunning = null)
    {
        _repositoryPath = Path.GetFullPath(repositoryPath);
        _snapshots = new GitCliTaskGateway(_repositoryPath, gitTimeout ?? TimeSpan.FromMinutes(30));
        _unity = new UnityBridgeClient(_repositoryPath, TimeSpan.FromMinutes(2));
        _unityEditorRunning = unityEditorRunning ?? IsAnyUnityEditorRunning;
        _localDirectory = Path.Combine(_repositoryPath, "UserSettings", "ProjectSync");
        _gitTimeout = gitTimeout ?? TimeSpan.FromMinutes(2);
    }

    public async Task<Outcome<WorkspaceInspection>> InspectAsync(CancellationToken cancellationToken = default)
    {
        var operationId = OperationId.New();
        var branch = await CurrentBranchAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (!branch.IsSuccess)
        {
            return Outcome<WorkspaceInspection>.Failure(branch.Problem!);
        }

        var origin = await RunGitAsync(["remote", "get-url", "origin"], operationId, "inspect_origin", cancellationToken)
            .ConfigureAwait(false);
        if (!origin.IsSuccess)
        {
            return Outcome<WorkspaceInspection>.Failure(origin.Problem!);
        }

        var status = await ReadStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
        return status.IsSuccess
            ? Outcome<WorkspaceInspection>.Success(new WorkspaceInspection(
                branch.Value!, origin.Value!.StandardOutput.Trim(), status.Value!.Length != 0))
            : Outcome<WorkspaceInspection>.Failure(status.Problem!);
    }

    public async Task<Outcome<string>> StartAsync(string title, CancellationToken cancellationToken = default)
    {
        var operationId = OperationId.New();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120)
        {
            return Failure<string>("task_title_invalid", ProblemCategory.Validation, operationId,
                "start", "作業名を1～120文字で入力してください。");
        }

        try
        {
            using var lease = AcquireLocalLease();
            var preflight = await RequireCleanAndUnityClosedAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (!preflight.IsSuccess)
            {
                return Outcome<string>.Failure(preflight.Problem!);
            }

            var pending = await ReadSaveStateAsync(cancellationToken).ConfigureAwait(false);
            if (pending is { Completed: false })
            {
                return Failure<string>("save_recovery_required", ProblemCategory.RecoveryRequired, operationId,
                    "start", "未完了の保存があります。元のTaskに戻り、保存を再試行してください。");
            }

            var fetched = await RunGitAsync(
                ["fetch", "--no-tags", "origin", MainRef], operationId, "fetch_main", cancellationToken)
                .ConfigureAwait(false);
            if (!fetched.IsSuccess)
            {
                return Outcome<string>.Failure(fetched.Problem!);
            }

            var baseSha = await ReadShaAsync("FETCH_HEAD", operationId, cancellationToken).ConfigureAwait(false);
            if (!baseSha.IsSuccess)
            {
                return baseSha;
            }

            var remoteMain = await ReadRemoteRefAsync(MainRef, operationId, cancellationToken).ConfigureAwait(false);
            if (!remoteMain.IsSuccess)
            {
                return Outcome<string>.Failure(remoteMain.Problem!);
            }

            if (!string.Equals(baseSha.Value, remoteMain.Value, StringComparison.OrdinalIgnoreCase))
            {
                return Failure<string>("main_changed_during_start", ProblemCategory.Conflict, operationId,
                    "start", "mainが取得中に更新されました。状態を再確認してからやり直してください。");
            }

            var branch = $"task/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            var switched = await RunGitAsync(
                ["switch", "-c", branch, "--no-track", "FETCH_HEAD"],
                operationId, "create_task_branch", cancellationToken).ConfigureAwait(false);
            if (!switched.IsSuccess)
            {
                return Outcome<string>.Failure(switched.Problem!);
            }

            var current = await CurrentBranchAsync(operationId, cancellationToken).ConfigureAwait(false);
            return current.IsSuccess && string.Equals(current.Value, branch, StringComparison.Ordinal)
                ? Outcome<string>.Success(branch)
                : Failure<string>("branch_switch_unconfirmed", ProblemCategory.RecoveryRequired, operationId,
                    "start", "Branch作成後の状態を確認できません。手動で状態を確認してください。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Failure<string>("workspace_busy", ProblemCategory.Conflict, operationId,
                "start", "別のProjectSync操作中、またはローカル状態を読み書きできません。");
        }
    }

    public async Task<Outcome<string>> ResumeAsync(string taskBranch, CancellationToken cancellationToken = default)
    {
        var operationId = OperationId.New();
        var valid = TaskBranchPolicy.RequireTaskBranch(taskBranch, operationId, "resume");
        if (!valid.IsSuccess)
        {
            return Outcome<string>.Failure(valid.Problem!);
        }

        try
        {
            using var lease = AcquireLocalLease();
            var current = await CurrentBranchAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (!current.IsSuccess)
            {
                return current;
            }

            if (string.Equals(current.Value, taskBranch, StringComparison.Ordinal))
            {
                return Outcome<string>.Success(taskBranch);
            }

            var pending = await ReadSaveStateAsync(cancellationToken).ConfigureAwait(false);
            if (pending is { Completed: false } &&
                !string.Equals(pending.Branch, taskBranch, StringComparison.Ordinal))
            {
                return Failure<string>("save_recovery_required", ProblemCategory.RecoveryRequired, operationId,
                    "resume", "別のTaskで未完了の保存があります。先にそのTaskを復旧してください。");
            }

            var preflight = await RequireCleanAndUnityClosedAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (!preflight.IsSuccess)
            {
                return Outcome<string>.Failure(preflight.Problem!);
            }

            var switched = await RunGitAsync(["switch", "--no-guess", taskBranch], operationId, "resume", cancellationToken)
                .ConfigureAwait(false);
            return switched.IsSuccess
                ? Outcome<string>.Success(taskBranch)
                : Outcome<string>.Failure(switched.Problem!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Failure<string>("workspace_busy", ProblemCategory.Conflict, operationId,
                "resume", "別のProjectSync操作中、またはローカル状態を読み書きできません。");
        }
    }

    public async Task<Outcome<LocalSaveResult>> SaveAsync(CancellationToken cancellationToken = default)
    {
        var attemptId = OperationId.New();
        try
        {
            using var lease = AcquireLocalLease();
            var branch = await CurrentBranchAsync(attemptId, cancellationToken).ConfigureAwait(false);
            if (!branch.IsSuccess)
            {
                return Outcome<LocalSaveResult>.Failure(branch.Problem!);
            }

            var valid = TaskBranchPolicy.RequireTaskBranch(branch.Value, attemptId, "save");
            if (!valid.IsSuccess)
            {
                return Outcome<LocalSaveResult>.Failure(valid.Problem!);
            }

            var state = await ReadSaveStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is { Completed: false } && !string.Equals(state.Branch, branch.Value, StringComparison.Ordinal))
            {
                return Failure<LocalSaveResult>("save_branch_mismatch", ProblemCategory.RecoveryRequired, attemptId,
                    "save", "未完了の保存が別のTaskに属しています。Branchを戻して復旧してください。");
            }

            if (state is null || state.Completed)
            {
                state = new LocalSaveState(attemptId.Value, branch.Value!, "new", null, false);
                await WriteSaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            }

            var operationId = new OperationId(state.OperationId);
            if (state.Phase == "new")
            {
                if (_unityEditorRunning())
                {
                    var unitySaved = await _unity.SaveOpenScenesAndAssetsAsync(operationId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!unitySaved.IsSuccess)
                    {
                        return Outcome<LocalSaveResult>.Failure(unitySaved.Problem!);
                    }
                }

                state = state with { Phase = "unity_saved" };
                await WriteSaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            }

            if (state.Phase == "unity_saved")
            {
                var snapshot = await _snapshots.CreateSnapshotAsync(
                    state.Branch, "ProjectSync 作業保存", operationId, cancellationToken).ConfigureAwait(false);
                if (!snapshot.IsSuccess)
                {
                    return Outcome<LocalSaveResult>.Failure(snapshot.Problem!);
                }

                state = state with { Phase = "commit_created", CommitSha = snapshot.Value };
                await WriteSaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            }

            if (state.Phase == "commit_created" && state.CommitSha is not null)
            {
                var reachable = await _snapshots.IsCommitReachableAsync(
                    state.Branch, state.CommitSha, cancellationToken).ConfigureAwait(false);
                if (!reachable.IsSuccess || reachable.Value is not true)
                {
                    var pushed = await _snapshots.PushCommitAsync(
                        state.Branch, state.CommitSha, operationId, cancellationToken).ConfigureAwait(false);
                    reachable = await _snapshots.IsCommitReachableAsync(
                        state.Branch, state.CommitSha, cancellationToken).ConfigureAwait(false);
                    if (!reachable.IsSuccess)
                    {
                        return Outcome<LocalSaveResult>.Failure(reachable.Problem!);
                    }

                    if (reachable.Value is not true)
                    {
                        return Outcome<LocalSaveResult>.Failure(pushed.Problem ?? Problem.Create(
                            "remote_commit_unconfirmed", ProblemCategory.ExternalSystem, true, operationId,
                            "save", "CommitはPCに残っています。GitHubへの到達を確認できません。保存を再試行してください。"));
                    }
                }

                state = state with { Phase = "remote_verified", Completed = true };
                await WriteSaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return state.Completed && state.CommitSha is not null
                ? Outcome<LocalSaveResult>.Success(new LocalSaveResult(state.Branch, state.CommitSha))
                : Failure<LocalSaveResult>("save_state_invalid", ProblemCategory.RecoveryRequired, operationId,
                    "save", "保存状態が不整合です。ローカルのデータを保持して停止しました。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Failure<LocalSaveResult>("save_journal_unavailable", ProblemCategory.RecoveryRequired,
                attemptId, "save", "保存記録を読み書きできません。PC上の変更は削除していません。");
        }
    }

    public async Task<Outcome<LocalSubmissionResult>> SubmitAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var operationId = OperationId.New();
        try
        {
            using var lease = AcquireLocalLease();
            var branch = await CurrentBranchAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (!branch.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(branch.Problem!);
            }

            var valid = TaskBranchPolicy.RequireTaskBranch(branch.Value, operationId, "submit");
            if (!valid.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(valid.Problem!);
            }

            var pending = await ReadSaveStateAsync(cancellationToken).ConfigureAwait(false);
            if (pending is { Completed: false })
            {
                return Failure<LocalSubmissionResult>("save_pending", ProblemCategory.RecoveryRequired, operationId,
                    "submit", "未完了の保存があります。先に保存を再試行してください。");
            }

            var status = await ReadStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (!status.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(status.Problem!);
            }

            if (status.Value!.Length != 0)
            {
                return Failure<LocalSubmissionResult>("local_changes_not_saved", ProblemCategory.Validation,
                    operationId, "submit", "ローカル変更があります。先に作業を保存してください。");
            }

            var sha = await ReadShaAsync("HEAD", operationId, cancellationToken).ConfigureAwait(false);
            if (!sha.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(sha.Problem!);
            }

            var reachable = await _snapshots.IsCommitReachableAsync(branch.Value!, sha.Value!, cancellationToken)
                .ConfigureAwait(false);
            if (!reachable.IsSuccess || reachable.Value is not true)
            {
                return Failure<LocalSubmissionResult>("commit_not_on_remote", ProblemCategory.Validation,
                    operationId, "submit", "現在のCommitをRemoteで確認できません。先に作業を保存してください。");
            }

            var remoteHead = await ReadRemoteRefAsync(
                "refs/heads/" + branch.Value, operationId, cancellationToken).ConfigureAwait(false);
            if (!remoteHead.IsSuccess ||
                !string.Equals(remoteHead.Value, sha.Value, StringComparison.OrdinalIgnoreCase))
            {
                return Failure<LocalSubmissionResult>("remote_task_head_changed", ProblemCategory.Conflict,
                    operationId, "submit", "Remote Task BranchのHEADが現在のCommitと異なります。提出前に確認してください。");
            }

            var origin = await RunGitAsync(["remote", "get-url", "origin"], operationId, "submit", cancellationToken)
                .ConfigureAwait(false);
            if (!origin.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(origin.Problem!);
            }

            var repository = ParseGithubRepository(origin.Value!.StandardOutput.Trim());
            if (repository is null)
            {
                return Failure<LocalSubmissionResult>("github_origin_required", ProblemCategory.Configuration,
                    operationId, "submit", "originはgithub.comのRepositoryである必要があります。");
            }

            var submitted = await SubmitPullRequestAsync(
                repository, branch.Value!, sha.Value!, string.IsNullOrWhiteSpace(title) ? branch.Value! : title,
                operationId, cancellationToken).ConfigureAwait(false);
            return submitted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Failure<LocalSubmissionResult>("submission_state_unknown", ProblemCategory.RecoveryRequired,
                operationId, "submit", "提出状態を確認できません。PRを確認してから再試行してください。");
        }
    }

    private async Task<Outcome<LocalSubmissionResult>> SubmitPullRequestAsync(
        string repository,
        string branch,
        string sha,
        string title,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        var listed = await ListPullRequestsAsync(repository, branch, operationId, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return Outcome<LocalSubmissionResult>.Failure(listed.Problem!);
        }

        var matching = listed.Value!;
        if (matching.Length == 0)
        {
            var body = $"{SubmittedPrefix}{sha} -->\n\n提出Commit: `{sha}`\n管理者はこのSHAを確認してから統合してください。";
            var created = await RunGithubAsync(
                ["pr", "create", "--repo", repository, "--base", "main", "--head", branch,
                    "--title", title, "--body", body], operationId, "create_pr", cancellationToken)
                .ConfigureAwait(false);
            // A lost response may still mean the PR was created. Re-read before deciding.
            listed = await ListPullRequestsAsync(repository, branch, operationId, cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(listed.Problem!);
            }

            matching = listed.Value!;
            if (matching.Length == 0 && !created.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(created.Problem!);
            }
        }

        if (matching.Length != 1 ||
            !string.Equals(matching[0].HeadRefOid, sha, StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(matching[0].Url, UriKind.Absolute, out var pullRequestUri) ||
            pullRequestUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(pullRequestUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !pullRequestUri.AbsolutePath.StartsWith("/" + repository + "/pull/", StringComparison.OrdinalIgnoreCase))
        {
            return Failure<LocalSubmissionResult>("submitted_sha_mismatch", ProblemCategory.RecoveryRequired,
                operationId, "submit", "PRのHEADまたは提出SHAが一致しません。既存PRを確認してください。");
        }

        if (!TryUpdateSubmissionBody(matching[0].Body, sha, out var updatedBody))
        {
            return Failure<LocalSubmissionResult>("submission_marker_invalid", ProblemCategory.RecoveryRequired,
                operationId, "submit", "既存PRの提出SHA表示が不正です。PR本文を確認してください。");
        }

        if (!string.Equals(matching[0].Body, updatedBody, StringComparison.Ordinal))
        {
            // Re-read immediately before editing so an observed concurrent change cannot be overwritten.
            var fresh = await ListPullRequestsAsync(repository, branch, operationId, cancellationToken)
                .ConfigureAwait(false);
            if (!fresh.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(fresh.Problem!);
            }

            if (fresh.Value!.Length != 1 ||
                !string.Equals(fresh.Value[0].Url, matching[0].Url, StringComparison.Ordinal) ||
                !string.Equals(fresh.Value[0].HeadRefOid, sha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(fresh.Value[0].Body, matching[0].Body, StringComparison.Ordinal))
            {
                return Failure<LocalSubmissionResult>("pr_changed_during_submission", ProblemCategory.Conflict,
                    operationId, "submit", "提出中にPRが変更されました。内容を確認してから再試行してください。");
            }

            // A lost gh response is inconclusive. The final read below decides whether this SHA was recorded.
            _ = await RunGithubAsync(
                ["pr", "edit", matching[0].Url!, "--repo", repository, "--body", updatedBody],
                operationId, "resubmit_pr", cancellationToken).ConfigureAwait(false);
            var afterEdit = await ListPullRequestsAsync(repository, branch, operationId, cancellationToken)
                .ConfigureAwait(false);
            if (!afterEdit.IsSuccess)
            {
                return Outcome<LocalSubmissionResult>.Failure(afterEdit.Problem!);
            }

            if (afterEdit.Value!.Length != 1 ||
                !string.Equals(afterEdit.Value[0].Url, matching[0].Url, StringComparison.Ordinal) ||
                !string.Equals(afterEdit.Value[0].HeadRefOid, sha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(afterEdit.Value[0].Body, updatedBody, StringComparison.Ordinal))
            {
                return Failure<LocalSubmissionResult>("resubmission_unconfirmed", ProblemCategory.RecoveryRequired,
                    operationId, "submit", "再提出の結果を確認できません。PR本文とHEADを確認してください。");
            }
        }

        return Outcome<LocalSubmissionResult>.Success(new LocalSubmissionResult(matching[0].Url!, sha));
    }

    internal static bool TryUpdateSubmissionBody(string? body, string sha, out string updatedBody)
    {
        updatedBody = string.Empty;
        if (body is null || !GitCommandPolicy.IsFullSha(sha))
        {
            return false;
        }

        var markerAt = body.IndexOf(SubmittedPrefix, StringComparison.Ordinal);
        if (markerAt < 0 ||
            body.IndexOf(SubmittedPrefix, markerAt + SubmittedPrefix.Length, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var shaAt = markerAt + SubmittedPrefix.Length;
        var markerEnd = body.IndexOf(" -->", shaAt, StringComparison.Ordinal);
        if (markerEnd < 0)
        {
            return false;
        }

        var oldSha = body[shaAt..markerEnd];
        if (!GitCommandPolicy.IsFullSha(oldSha))
        {
            return false;
        }

        var oldDisplay = $"提出Commit: `{oldSha}`";
        var displayAt = body.IndexOf(oldDisplay, markerEnd + 4, StringComparison.Ordinal);
        if (displayAt < 0 ||
            body.IndexOf(oldDisplay, displayAt + oldDisplay.Length, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var newMarker = SubmittedPrefix + sha + " -->";
        var oldMarker = SubmittedPrefix + oldSha + " -->";
        updatedBody = body.Replace(oldMarker, newMarker, StringComparison.Ordinal)
            .Replace(oldDisplay, $"提出Commit: `{sha}`", StringComparison.Ordinal);
        return true;
    }

    private async Task<Outcome<PullRequestView[]>> ListPullRequestsAsync(
        string repository, string branch, OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await RunGithubAsync(
            ["pr", "list", "--repo", repository, "--head", branch, "--base", "main", "--state", "open",
                "--json", "url,headRefOid,body,headRefName,baseRefName"],
            operationId, "list_pr", cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Outcome<PullRequestView[]>.Failure(result.Problem!);
        }

        try
        {
            var all = JsonSerializer.Deserialize<PullRequestView[]>(result.Value!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return Outcome<PullRequestView[]>.Success(all.Where(pr =>
                string.Equals(pr.HeadRefName, branch, StringComparison.Ordinal) &&
                string.Equals(pr.BaseRefName, "main", StringComparison.Ordinal)).ToArray());
        }
        catch (JsonException)
        {
            return Failure<PullRequestView[]>("pr_response_invalid", ProblemCategory.RecoveryRequired,
                operationId, "list_pr", "GitHubのPR一覧を解析できません。");
        }
    }

    private async Task<Outcome<string>> RunGithubAsync(
        IReadOnlyList<string> arguments, OperationId operationId, string phase, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("gh")
        {
            WorkingDirectory = _repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failure<string>("gh_start_failed", ProblemCategory.Configuration, operationId,
                    phase, "GitHub CLIを起動できません。インストールとログインを確認してください。");
            }

            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await error.ConfigureAwait(false); // Never surface credentials or provider output.
            var text = await output.ConfigureAwait(false);
            return process.ExitCode == 0
                ? Outcome<string>.Success(text)
                : Failure<string>("gh_command_failed", ProblemCategory.ExternalSystem, operationId,
                    phase, "GitHub操作が完了しませんでした。ghのログインとRepository権限を確認してください。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return Failure<string>("gh_outcome_unknown", ProblemCategory.RecoveryRequired, operationId,
                phase, "GitHubの応答が不明です。PRの実状態を確認してから再試行してください。");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Failure<string>("gh_not_installed", ProblemCategory.Configuration, operationId,
                phase, "GitHub CLI (gh) が見つかりません。");
        }
    }

    private async Task<Outcome<Unit>> RequireCleanAndUnityClosedAsync(
        OperationId operationId, CancellationToken cancellationToken)
    {
        if (_unityEditorRunning())
        {
            return Failure<Unit>("unity_must_be_closed", ProblemCategory.Validation, operationId,
                "branch_switch", "Branch切替前にUnity Editorを閉じてください。");
        }

        var status = await ReadStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return Outcome<Unit>.Failure(status.Problem!);
        }

        return status.Value!.Length == 0
            ? Outcome<Unit>.Success(Unit.Value)
            : Failure<Unit>("local_changes_present", ProblemCategory.Validation, operationId,
                "branch_switch", "ローカル変更・未記録ファイルがあります。保存または保全してから切り替えてください。");
    }

    private async Task<Outcome<string>> ReadStatusAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            ["status", "--porcelain=v1", "--untracked-files=all"], operationId, "status", cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Outcome<string>.Success(result.Value!.StandardOutput)
            : Outcome<string>.Failure(result.Problem!);
    }

    private async Task<Outcome<string>> CurrentBranchAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            ["symbolic-ref", "--quiet", "--short", "HEAD"], operationId, "current_branch", cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Outcome<string>.Success(result.Value!.StandardOutput.Trim())
            : Outcome<string>.Failure(result.Problem!);
    }

    private async Task<Outcome<string>> ReadShaAsync(
        string reference, OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            ["rev-parse", "--verify", reference], operationId, "read_sha", cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Outcome<string>.Failure(result.Problem!);
        }

        var sha = result.Value!.StandardOutput.Trim();
        return GitCommandPolicy.IsFullSha(sha)
            ? Outcome<string>.Success(sha)
            : Failure<string>("sha_invalid", ProblemCategory.RecoveryRequired, operationId,
                "read_sha", "Gitが有効なCommit SHAを返しませんでした。");
    }

    private async Task<Outcome<string>> ReadRemoteRefAsync(
        string reference, OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            ["ls-remote", "--exit-code", "--heads", "origin", reference],
            operationId, "remote_ref", cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Outcome<string>.Failure(result.Problem!);
        }

        var fields = result.Value!.StandardOutput.Trim().Split('\t');
        return fields.Length == 2 && fields[1] == reference && GitCommandPolicy.IsFullSha(fields[0])
            ? Outcome<string>.Success(fields[0])
            : Failure<string>("remote_ref_invalid", ProblemCategory.RecoveryRequired, operationId,
                "remote_ref", "RemoteのBranch SHAを確認できません。");
    }

    private async Task<Outcome<GitCommandResult>> RunGitAsync(
        IReadOnlyList<string> arguments, OperationId operationId, string phase, CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(_repositoryPath, arguments, operationId, _gitTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        return result.Value!.ExitCode == 0
            ? result
            : Failure<GitCommandResult>("git_command_failed", ProblemCategory.ExternalSystem, operationId,
                phase, "Git操作に失敗しました。ローカル状態を保持して停止しました。");
    }

    private FileStream AcquireLocalLease()
    {
        Directory.CreateDirectory(_localDirectory);
        return new FileStream(Path.Combine(_localDirectory, "operation.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private async Task<LocalSaveState?> ReadSaveStateAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_localDirectory, "save-state.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<LocalSaveState>(read, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Save journal is empty.");
    }

    private async Task WriteSaveStateAsync(LocalSaveState state, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_localDirectory, "save-state.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var write = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(write, state, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await write.FlushAsync(cancellationToken).ConfigureAwait(false);
            write.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsAnyUnityEditorRunning()
    {
        var processes = Process.GetProcessesByName("Unity");
        try
        {
            return processes.Length != 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static string? ParseGithubRepository(string origin)
    {
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment))
        {
            origin = uri.AbsolutePath.Trim('/');
        }
        else if (origin.StartsWith("git@github.com:", StringComparison.Ordinal))
        {
            origin = origin["git@github.com:".Length..];
        }
        else
        {
            return null;
        }

        if (origin.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            origin = origin[..^4];
        }

        return Regex.IsMatch(origin, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z") ? origin : null;
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
            // Already exited.
        }
    }

    private static Outcome<T> Failure<T>(
        string code, ProblemCategory category, OperationId operationId, string phase, string message) =>
        Outcome<T>.Failure(Problem.Create(code, category, retryable: false, operationId, phase, message));

    private sealed record LocalSaveState(
        string OperationId, string Branch, string Phase, string? CommitSha, bool Completed);

    private sealed record PullRequestView(
        string? Url, string? HeadRefOid, string? Body, string? HeadRefName, string? BaseRefName);
}
