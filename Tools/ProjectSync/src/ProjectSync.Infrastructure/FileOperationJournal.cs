using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectSync.Core;

namespace ProjectSync.Infrastructure;

public sealed class FileOperationJournal : IOperationJournal, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileOperationJournal(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task<OperationCheckpoint> LoadOrCreateAsync(
        OperationCheckpoint seed,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(seed.OperationId);
            if (File.Exists(path))
            {
                await using var read = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await JsonSerializer.DeserializeAsync<OperationCheckpoint>(
                           read,
                           JsonOptions,
                           cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException($"Operation journal '{path}' is empty or invalid.");
            }

            await SaveWithoutLockAsync(path, seed, cancellationToken).ConfigureAwait(false);
            return seed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(OperationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveWithoutLockAsync(GetPath(checkpoint.OperationId), checkpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }

    private async Task SaveWithoutLockAsync(
        string path,
        OperationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var write = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(write, checkpoint, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await write.FlushAsync(cancellationToken).ConfigureAwait(false);
            write.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private string GetPath(string operationId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(operationId));
        var name = Convert.ToHexString(bytes).ToLowerInvariant();
        return Path.Combine(_directory, name + ".json");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
