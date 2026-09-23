using System.Threading.Channels;

namespace Dispatchly.Transport.InMemory;

internal sealed class InMemoryMessagePublisher : IMessagePublisher
{
    private readonly IMessageTypeCatalog _catalog;
    private readonly Channel<InMemoryEnvelope> _queue;

    public InMemoryMessagePublisher(IMessageTypeCatalog catalog, Channel<InMemoryEnvelope> queue)
    {
        _catalog = catalog;
        _queue = queue;
    }

    public ValueTask<MessageId> PublishAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var registration = _catalog.GetRequired(typeof(TMessage));
        if (!registration.HasHandlers)
        {
            throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' has no handler. The in-memory transport only delivers inside this process.");
        }

        var id = MessageId.New();
        var envelope = new InMemoryEnvelope(
            id,
            typeof(TMessage),
            registration.Serialize(message),
            DateTimeOffset.UtcNow,
            Attempt: 0);

        if (!_queue.Writer.TryWrite(envelope))
        {
            throw new InvalidOperationException("The in-memory transport is not accepting messages.");
        }

        return ValueTask.FromResult(id);
    }
}
