namespace Dispatchly.Abstractions;

/// <summary>Remembers message identifiers that have already been handled.</summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Starts a lease for <paramref name="id" />.
    /// Returns null when that identifier was already completed.
    /// </summary>
    Task<IIdempotencyLease?> TryLeaseAsync(MessageId id, CancellationToken cancellationToken);
}
