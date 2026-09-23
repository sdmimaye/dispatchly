namespace Dispatchly.Transport.SqlServer;

internal abstract record ClaimResult
{
    private ClaimResult()
    {
    }

    public sealed record Ready(string Payload, DateTimeOffset EnqueuedAt, int Attempt) : ClaimResult;

    public sealed record Ignored : ClaimResult
    {
        public static Ignored Instance { get; } = new();
    }
}
