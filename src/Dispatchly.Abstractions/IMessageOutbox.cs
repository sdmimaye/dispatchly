using System.Data.Common;

namespace Dispatchly.Abstractions;

/// <summary>Inserts a message into the outbox on a transaction the caller commits.</summary>
public interface IMessageOutbox
{
    /// <summary>
    /// Inserts <paramref name="message" /> on <paramref name="transaction" /> and leaves that transaction uncommitted.
    /// A returned <see cref="MessageId" /> means the insert succeeded. Delivery starts after the caller commits,
    /// when the transport's insert trigger wakes the host.
    /// PostgreSQL requires an <c>NpgsqlTransaction</c>. SQL Server requires a <c>SqlTransaction</c>.
    /// The in-memory transport throws <see cref="NotSupportedException" />.
    /// </summary>
    ValueTask<MessageId> EnlistAsync<TMessage>(
        TMessage message,
        DbTransaction transaction,
        CancellationToken cancellationToken = default);
}
