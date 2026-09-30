using System.Collections.Concurrent;
using ProjectSync.Core;

namespace ProjectSync.Infrastructure;

public sealed class InMemoryCoordinationStateStore : ICoordinationStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CoordinationAggregate> _states = new(StringComparer.Ordinal);

    public Task<CoordinationSnapshot> ReadAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _states.TryGetValue(taskId, out var value);
            return Task.FromResult(new CoordinationSnapshot(value));
        }
    }

    public Task<CasWriteResult> CompareExchangeAsync(
        string taskId,
        long expectedRevision,
        CoordinationAggregate next,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _states.TryGetValue(taskId, out var current);
            var actualRevision = current?.Revision ?? 0;
            if (actualRevision != expectedRevision)
            {
                if (current is null)
                {
                    throw new InvalidOperationException("A CAS conflict without current state is impossible for a non-negative revision.");
                }

                return Task.FromResult(new CasWriteResult(CasWriteStatus.Conflict, current));
            }

            if (next.Revision != expectedRevision + 1)
            {
                throw new InvalidOperationException("The next coordination revision must increment exactly once.");
            }

            _states[taskId] = next;
            return Task.FromResult(new CasWriteResult(CasWriteStatus.Written, next));
        }
    }
}

public sealed class InMemoryOperationJournal : IOperationJournal
{
    private readonly ConcurrentDictionary<string, OperationCheckpoint> _operations = new(StringComparer.Ordinal);

    public Task<OperationCheckpoint> LoadOrCreateAsync(
        OperationCheckpoint seed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _operations.GetOrAdd(seed.OperationId, seed);
        return Task.FromResult(result);
    }

    public Task SaveAsync(OperationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operations.AddOrUpdate(
            checkpoint.OperationId,
            checkpoint,
            (_, current) =>
            {
                if (!string.Equals(current.Kind, checkpoint.Kind, StringComparison.Ordinal) ||
                    !string.Equals(current.SubjectId, checkpoint.SubjectId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("An operation ID cannot be rebound to another workflow or subject.");
                }

                return checkpoint;
            });
        return Task.CompletedTask;
    }
}
