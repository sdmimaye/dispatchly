namespace Dispatchly;

/// <summary>Delivery metadata passed to a handler.</summary>
public sealed class MessageContext
{
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
}
