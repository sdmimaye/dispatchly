namespace Dispatchly.Transport.PostgreSql;

/// <summary>Options for the PostgreSQL transport.</summary>
public sealed class PostgresTransportOptions
{
    /// <summary>Connection string for publishing, listening, and schema provisioning.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Schema that holds outbox tables. The default is <c>dispatchly</c>.</summary>
    public string Schema { get; set; } = "dispatchly";

    /// <summary>
    /// Kept for existing configuration. Wake-ups use a private channel per handling host, not this value.
    /// The default is <c>dispatchly</c>.
    /// </summary>
    public string Channel { get; set; } = "dispatchly";

    /// <summary>
    /// A handling host leaves the ring after this long without a heartbeat. The default is 5 seconds.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a claimed message stays invisible to other workers. The default is 30 seconds.</summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Deliveries attempted before the message is dead-lettered. The default is 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Upper bound for retry backoff. The default is 5 minutes.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary><c>pg_cron</c> schedule for <c>redeliver_undelivered</c>. The default is every minute.</summary>
    public string CronSchedule { get; set; } = "* * * * *";

    /// <summary><c>pg_cron</c> job name. The default is <c>dispatchly_redeliver</c>.</summary>
    public string CronJobName { get; set; } = "dispatchly_redeliver";

    /// <summary>Maximum due messages notified by one redelivery pass. The default is 100.</summary>
    public int RedeliveryBatchSize { get; set; } = 100;

    /// <summary>
    /// When true, provisioning creates <c>pg_cron</c> and schedules redelivery.
    /// When the extension is missing, startup throws. The default is true.
    /// </summary>
    public bool ScheduleRedelivery { get; set; } = true;

    internal bool IdempotencyEnabled { get; set; }

    internal int HeartbeatTimeoutMilliseconds { get; private set; } = 5_000;

    internal TimeSpan HeartbeatInterval =>
        TimeSpan.FromMilliseconds(Math.Max(1, HeartbeatTimeout.TotalMilliseconds / 3));

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(ConnectionString));
        }

        Schema = IdentifierRules.ValidateSchema(Schema);
        Channel = IdentifierRules.ValidateChannel(Channel);
        CronJobName = IdentifierRules.ValidateSchema(CronJobName);
        HeartbeatTimeoutMilliseconds = Milliseconds(HeartbeatTimeout, nameof(HeartbeatTimeout));

        if (VisibilityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(VisibilityTimeout), "VisibilityTimeout must be positive.");
        }

        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be at least 1.");
        }

        if (MaxBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBackoff), "MaxBackoff must be positive.");
        }

        if (string.IsNullOrWhiteSpace(CronSchedule) || CronSchedule.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("CronSchedule is required.", nameof(CronSchedule));
        }

        if (RedeliveryBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RedeliveryBatchSize), "RedeliveryBatchSize must be at least 1.");
        }
    }

    private static int Milliseconds(TimeSpan value, string paramName)
    {
        var milliseconds = Math.Ceiling(value.TotalMilliseconds);
        if (milliseconds < 1 || milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName, "The interval must be between 1 millisecond and 24 days.");
        }

        return (int)milliseconds;
    }
}
