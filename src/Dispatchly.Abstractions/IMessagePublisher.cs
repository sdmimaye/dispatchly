namespace Dispatchly.Abstractions;

/// <summary>Enqueues a message on the registered transport.</summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Enqueues <paramref name="message" />. A returned <see cref="MessageId" /> means the transport accepted it.
    /// Delivery is at least once. The in-memory transport drops anything still queued when the process exits.
    /// </summary>
    ValueTask<MessageId> PublishAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default);
}
