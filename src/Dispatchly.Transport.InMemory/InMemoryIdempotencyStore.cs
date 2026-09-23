using System.Collections.Concurrent;
using System.Data.Common;

namespace Dispatchly.Transport.InMemory;

internal sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<Guid, LeaseState> _states = new();

    public Task<IIdempotencyLease?> TryLeaseAsync(MessageId id, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_states.TryGetValue(id.Value, out var state))
            {
                if (state == LeaseState.Completed)
                {
                    return Task.FromResult<IIdempotencyLease?>(null);
                }

                throw new InvalidOperationException($"Message '{id}' is already being handled.");
            }

            if (_states.TryAdd(id.Value, LeaseState.InProgress))
            {
                return Task.FromResult<IIdempotencyLease?>(new Lease(this, id.Value));
            }
        }
    }

    private enum LeaseState : byte
    {
        InProgress,
        Completed,
    }

    private sealed class Lease : IIdempotencyLease
    {
        private readonly InMemoryIdempotencyStore _store;
        private readonly Guid _id;
        private bool _committed;

        public Lease(InMemoryIdempotencyStore store, Guid id)
        {
            _store = store;
            _id = id;
        }

        public DbTransaction? Transaction => null;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store._states[_id] = LeaseState.Completed;
            _committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                _store._states.TryRemove(_id, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}
