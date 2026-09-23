namespace Dispatchly;

/// <summary>
/// Skips a handler when <see cref="MessageContext.Id" /> was already completed.
/// When the lease has a <see cref="System.Data.Common.DbTransaction" />, that transaction is stored on
/// <see cref="MessageContext" />. Writes the handler makes with it commit together with the idempotency record.
/// A crash before that commit rolls the writes back and delivery runs again. A crash after it skips the handler.
/// </summary>
public sealed class IdempotencyBehavior : IMessageBehavior
{
    private readonly IIdempotencyStore _store;

    /// <summary>Creates the behavior.</summary>
    public IdempotencyBehavior(IIdempotencyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(MessageEnvelope envelope, MessagePipelineDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(next);

        var lease = await _store.TryLeaseAsync(envelope.Context.Id, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return;
        }

        try
        {
            if (lease.Transaction is not null)
            {
                envelope.Context.SetFeature(lease.Transaction);
            }

            await next(envelope, cancellationToken).ConfigureAwait(false);
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
