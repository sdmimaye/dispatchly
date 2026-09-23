namespace Dispatchly;

/// <summary>Identifier assigned when a message is published.</summary>
public readonly record struct MessageId(Guid Value)
{
    /// <summary>Creates a time-ordered identifier.</summary>
    public static MessageId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
