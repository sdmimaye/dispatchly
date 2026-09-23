using System.Diagnostics.CodeAnalysis;

namespace Dispatchly;

/// <summary>Delivery metadata passed to a handler and to message behaviors.</summary>
public sealed class MessageContext
{
    private readonly Dictionary<Type, object> _features = [];

    /// <summary>Creates delivery metadata.</summary>
    public MessageContext(MessageId id, int attempt, DateTimeOffset enqueuedAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        Id = id;
        Attempt = attempt;
        EnqueuedAt = enqueuedAt;
    }

    /// <summary>Identifier assigned at publish.</summary>
    public MessageId Id { get; }

    /// <summary>One-based delivery attempt.</summary>
    public int Attempt { get; }

    /// <summary>When the message was enqueued.</summary>
    public DateTimeOffset EnqueuedAt { get; }

    /// <summary>Stores a feature for later behaviors and the handler. A second call for the same type replaces the previous feature.</summary>
    public void SetFeature<TFeature>(TFeature feature)
        where TFeature : class
    {
        ArgumentNullException.ThrowIfNull(feature);
        _features[typeof(TFeature)] = feature;
    }

    /// <summary>Returns the feature stored for <typeparamref name="TFeature" />.</summary>
    public bool TryGetFeature<TFeature>([NotNullWhen(true)] out TFeature? feature)
        where TFeature : class
    {
        if (_features.TryGetValue(typeof(TFeature), out var value) && value is TFeature typed)
        {
            feature = typed;
            return true;
        }

        feature = null;
        return false;
    }

    /// <summary>Returns the feature stored for <typeparamref name="TFeature" />.</summary>
    /// <exception cref="InvalidOperationException">No feature of that type was stored for this delivery.</exception>
    public TFeature GetRequiredFeature<TFeature>()
        where TFeature : class
    {
        if (TryGetFeature<TFeature>(out var feature))
        {
            return feature;
        }

        throw new InvalidOperationException(
            $"This delivery has no '{typeof(TFeature).FullName}' feature. Register the behavior that supplies it.");
    }
}
