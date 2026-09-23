namespace Dispatchly;

/// <summary>Mutable catalog populated while handlers are registered.</summary>
public sealed class MessageTypeCatalog : IMessageTypeCatalog
{
    private readonly Dictionary<Type, MessageTypeRegistration> _byType = [];
    private readonly Dictionary<string, MessageTypeRegistration> _byTable = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyCollection<MessageTypeRegistration> Registrations => _byType.Values;

    /// <inheritdoc />
    public bool TryGet(Type messageType, out MessageTypeRegistration registration) =>
        _byType.TryGetValue(messageType, out registration!);

    /// <inheritdoc />
    public bool TryGetByTable(string tableName, out MessageTypeRegistration registration) =>
        _byTable.TryGetValue(tableName, out registration!);

    /// <inheritdoc />
    public MessageTypeRegistration GetRequired(Type messageType)
    {
        if (TryGet(messageType, out var registration))
        {
            return registration;
        }

        throw new InvalidOperationException($"Message type '{messageType.FullName}' is not registered.");
    }

    internal MessageTypeRegistration GetOrAdd(
        Type messageType,
        string tableName,
        Func<object, string> serialize,
        Func<string, object?> deserialize,
        Func<IServiceProvider, object, MessageContext, CancellationToken, Task> dispatch,
        Type? handlerType)
    {
        if (_byType.TryGetValue(messageType, out var existing))
        {
            if (!string.Equals(existing.TableName, tableName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Message '{messageType.FullName}' is already registered with table '{existing.TableName}'.");
            }

            if (handlerType is not null)
            {
                existing.AddHandler(handlerType);
            }

            return existing;
        }

        if (_byTable.ContainsKey(tableName))
        {
            throw new InvalidOperationException(
                $"Table '{tableName}' is already used by message '{_byTable[tableName].MessageType.FullName}'.");
        }

        var created = new MessageTypeRegistration
        {
            MessageType = messageType,
            TableName = tableName,
            Serialize = serialize,
            Deserialize = deserialize,
            Dispatch = dispatch,
        };
        if (handlerType is not null)
        {
            created.AddHandler(handlerType);
        }
        _byType.Add(messageType, created);
        _byTable.Add(tableName, created);
        return created;
    }
}
