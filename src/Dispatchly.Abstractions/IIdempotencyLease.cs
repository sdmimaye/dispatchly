using System.Data.Common;

namespace Dispatchly.Abstractions;

/// <summary>
/// Holds an in-progress idempotency record until <see cref="CommitAsync" />.
/// Disposing without a commit releases the record so delivery can be retried.
/// </summary>
public interface IIdempotencyLease : IAsyncDisposable
{
    /// <summary>
    /// Transaction that owns the idempotency record.
    /// Handler writes that use this transaction commit or roll back with the record.
    /// Null when the store has no database transaction.
    /// </summary>
    DbTransaction? Transaction { get; }

    /// <summary>Keeps the idempotency record. Later deliveries of the same identifier skip the handler.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}
