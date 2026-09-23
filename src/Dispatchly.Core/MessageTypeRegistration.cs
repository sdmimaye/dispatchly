namespace Dispatchly;

/// <summary>A message type registered for serialization and delivery.</summary>
public sealed class MessageTypeRegistration
{
    private readonly List<Type> _handlerTypes = [];

    /// <summary>CLR message type.</summary>
    public required Type MessageType { get; init; }

    /// <summary>Unqualified PostgreSQL table name.</summary>
    public required string TableName { get; init; }

    /// <summary>Serializes a message instance to JSON.</summary>
    public required Func<object, string> Serialize { get; init; }

    /// <summary>Deserializes JSON to a message instance.</summary>
    public required Func<string, object?> Deserialize { get; init; }

    /// <summary>Invokes every handler for the message inside an existing scope.</summary>
    public required Func<IServiceProvider, object, MessageContext, CancellationToken, Task> Dispatch { get; init; }

    /// <summary>True when at least one handler is registered. Publish-only messages stay false.</summary>
    public bool HasHandlers => _handlerTypes.Count > 0;

    internal IReadOnlyList<Type> HandlerTypes => _handlerTypes;

    internal void AddHandler(Type handlerType)
    {
        if (_handlerTypes.Contains(handlerType))
        {
            throw new InvalidOperationException(
                $"Handler '{handlerType.FullName}' is already registered for '{MessageType.FullName}'.");
        }

        _handlerTypes.Add(handlerType);
    }
}
