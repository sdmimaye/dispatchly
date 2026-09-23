namespace Dispatchly;

/// <summary>Handles one message type. Implementations must be idempotent.</summary>
public interface IMessageHandler<TMessage>
{
    /// <summary>Handles a delivery of <paramref name="message" />.</summary>
    Task HandleAsync(TMessage message, MessageContext context, CancellationToken cancellationToken);
}
