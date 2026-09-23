namespace Dispatchly.Abstractions;

/// <summary>One delivery moving through the message pipeline.</summary>
public sealed class MessageEnvelope
{
    /// <summary>Creates a delivery envelope.</summary>
    public MessageEnvelope(object message, Type messageType, MessageContext context, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        Message = message;
        MessageType = messageType;
        Context = context;
        Services = services;
    }

    /// <summary>Deserialized message.</summary>
    public object Message { get; }

    /// <summary>CLR type of <see cref="Message" />.</summary>
    public Type MessageType { get; }

    /// <summary>Delivery metadata.</summary>
    public MessageContext Context { get; }

    /// <summary>Dependency-injection scope for this delivery.</summary>
    public IServiceProvider Services { get; }
}
