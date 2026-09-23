namespace Dispatchly;

/// <summary>A message the in-memory transport gave up on.</summary>
public sealed record InMemoryDeadLetter(
    MessageId Id,
    string MessageType,
    string Payload,
    int AttemptCount,
    string Error,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset DeadLetteredAt);
