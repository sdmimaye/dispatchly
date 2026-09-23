namespace Dispatchly.Transport.InMemory;

/// <summary>Options for the in-memory transport.</summary>
public sealed class InMemoryTransportOptions
{
    /// <summary>Deliveries attempted before the message is dead-lettered. The default is 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Delay before a failed delivery is tried again. The default is 20 milliseconds.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    internal void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be at least 1.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "RetryDelay cannot be negative.");
        }
    }
}
