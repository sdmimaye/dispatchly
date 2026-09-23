using System.Data.Common;

namespace Dispatchly.Transport.InMemory;

internal sealed class InMemoryMessageOutbox : IMessageOutbox
{
    public ValueTask<MessageId> EnlistAsync<TMessage>(
        TMessage message,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "The in-memory transport cannot enlist a message on a database transaction.");
    }
}
