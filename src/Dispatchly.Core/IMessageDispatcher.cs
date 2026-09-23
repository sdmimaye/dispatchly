namespace Dispatchly;

/// <summary>Delivers a serialized message to its handlers.</summary>
public interface IMessageDispatcher
{
    /// <summary>Deserializes <paramref name="payload" /> and invokes the registered handlers.</summary>
    Task DispatchAsync(
        Type messageType,
        string payload,
        MessageContext context,
        CancellationToken cancellationToken);
}
