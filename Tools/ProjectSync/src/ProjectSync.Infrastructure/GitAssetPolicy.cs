using ProjectSync.Core;

namespace ProjectSync.Infrastructure;

public static class GitAssetPolicy
{
    private const long RegularGitMaximumBytes = 100L * 1024 * 1024;
    private const long FreeLfsMaximumBytes = 2_000_000_000;

    internal static async Task<Outcome<Unit>> ValidateAsync(
        string repositoryPath,
        SafeGitProcessRunner runner,
        TimeSpan timeout,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };

            foreach (var root in new[] { "Assets", "Packages", "ProjectSettings" })
            {
                var directory = Path.Combine(repositoryPath, root);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isScene = file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                    var fileLength = new FileInfo(file).Length;
                    var isLarge = fileLength > RegularGitMaximumBytes;
                    if (!isScene && !isLarge)
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(repositoryPath, file).Replace('\\', '/');
                    var attribute = await runner.RunAsync(
                        repositoryPath,
                        ["check-attr", "filter", "--", relativePath],
                        operationId,
                        timeout,
                        cancellationToken).ConfigureAwait(false);
                    if (!attribute.IsSuccess)
                    {
                        return Outcome<Unit>.Failure(attribute.Problem!);
                    }

                    if (attribute.Value!.ExitCode != 0)
                    {
                        return Failure("asset_attribute_unknown", operationId, relativePath,
                            "保存前にGit追跡設定を確認できませんでした。");
                    }

                    var usesLfs = attribute.Value.StandardOutput.TrimEnd().EndsWith(": filter: lfs", StringComparison.Ordinal);
                    if (isScene && usesLfs)
                    {
                        return Failure("scene_lfs_conflicts_with_merge", operationId, relativePath,
                            "SceneがLFS対象です。同じSceneを別Branchで編集して統合するには通常GitのYAML管理が必要です。");
                    }

                    if (isLarge && !usesLfs)
                    {
                        return Failure("large_asset_not_lfs", operationId, relativePath,
                            "通常Gitの100 MiB上限を超えます。管理者がこの素材のLFS追跡を設定してください。");
                    }

                    if (isLarge && fileLength > FreeLfsMaximumBytes)
                    {
                        return Failure("lfs_file_exceeds_free_limit", operationId, relativePath,
                            "GitHub FreeのLFS単体ファイル上限2 GBを超えます。素材を小さくするか共有方法を見直してください。");
                    }
                }
            }

            return Outcome<Unit>.Success(Unit.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("asset_scan_failed", operationId, "", exception.Message);
        }
    }

    private static Outcome<Unit> Failure(string code, OperationId operationId, string path, string message) =>
        Outcome<Unit>.Failure(Problem.Create(
            code,
            ProblemCategory.Configuration,
            retryable: false,
            operationId,
            "asset_preflight",
            string.IsNullOrEmpty(path) ? message : $"{path}: {message}"));
}
