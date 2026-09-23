namespace Dispatchly.Core;

/// <summary>Serializer backed by the message catalog.</summary>
public sealed class CatalogMessageSerializer : IMessageSerializer
{
    private readonly IMessageTypeCatalog _catalog;

    /// <summary>Creates a serializer.</summary>
    public CatalogMessageSerializer(IMessageTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <inheritdoc />
    public string Serialize(Type messageType, object message)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(message);
        return _catalog.GetRequired(messageType).Serialize(message);
    }

    /// <inheritdoc />
    public object Deserialize(Type messageType, string payload)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        return _catalog.GetRequired(messageType).Deserialize(payload)
            ?? throw new InvalidOperationException($"Message '{messageType.FullName}' deserialized to null.");
    }
}
