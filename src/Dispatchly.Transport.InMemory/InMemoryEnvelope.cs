namespace Dispatchly;

internal sealed record InMemoryEnvelope(
    MessageId Id,
    Type MessageType,
    string Payload,
    DateTimeOffset EnqueuedAt,
    int Attempt);
