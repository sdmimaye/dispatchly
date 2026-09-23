namespace Dispatchly;

/// <summary>Serializes registered message types.</summary>
public interface IMessageSerializer
{
    /// <summary>Serializes <paramref name="message" /> with the registration for <paramref name="messageType" />.</summary>
    string Serialize(Type messageType, object message);

    /// <summary>Deserializes <paramref name="payload" /> as <paramref name="messageType" />.</summary>
    object Deserialize(Type messageType, string payload);
}
