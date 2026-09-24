namespace Dispatchly.Transport.SqlServer;

/// <summary>Options for the SQL Server transport.</summary>
public sealed class SqlServerTransportOptions
{
    /// <summary>Connection string for publishing, receiving, and schema provisioning.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Schema that holds outbox tables. The default is <c>dispatchly</c>.</summary>
    public string Schema { get; set; } = "dispatchly";

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

    /// <summary>
    /// SQL Server Agent schedule for <c>redeliver_undelivered</c>.
    /// <c>* * * * *</c> runs every minute. <c>*/n * * * *</c> runs every <c>n</c> minutes.
    /// </summary>
    public string CronSchedule { get; set; } = "* * * * *";

    /// <summary>SQL Server Agent job name. The default is <c>dispatchly_redeliver</c>.</summary>
    public string CronJobName { get; set; } = "dispatchly_redeliver";

    /// <summary>Maximum due messages notified by one redelivery pass. The default is 100.</summary>
    public int RedeliveryBatchSize { get; set; } = 100;

    /// <summary>
    /// When true, provisioning creates a SQL Server Agent job for redelivery.
    /// When Agent is not running, startup throws. The default is true.
    /// </summary>
    public bool ScheduleRedelivery { get; set; } = true;

    internal bool IdempotencyEnabled { get; set; }

    internal int HeartbeatTimeoutMilliseconds { get; private set; } = 5_000;

    internal TimeSpan HeartbeatInterval =>
        TimeSpan.FromMilliseconds(Math.Max(1, HeartbeatTimeout.TotalMilliseconds / 3));

    internal int RedeliveryIntervalMinutes { get; private set; } = 1;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("SQL Server connection string is required.", nameof(ConnectionString));
        }

        Schema = IdentifierRules.ValidateSchema(Schema);
        CronJobName = IdentifierRules.ValidateSchema(CronJobName);
        HeartbeatTimeoutMilliseconds = SqlServerDelay.Milliseconds(HeartbeatTimeout, nameof(HeartbeatTimeout));
        RedeliveryIntervalMinutes = ParseIntervalMinutes(CronSchedule);
        _ = SqlServerDelay.Milliseconds(VisibilityTimeout, nameof(VisibilityTimeout));
        _ = SqlServerDelay.Milliseconds(MaxBackoff, nameof(MaxBackoff));

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

        if (RedeliveryBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RedeliveryBatchSize), "RedeliveryBatchSize must be at least 1.");
        }
    }

    private static int ParseIntervalMinutes(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron) || cron.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "CronSchedule must be '* * * * *' or '*/n * * * *' with n from 1 to 60.",
                nameof(CronSchedule));
        }

        if (cron == "* * * * *")
        {
            return 1;
        }

        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5
            && parts[0].StartsWith("*/", StringComparison.Ordinal)
            && parts[1] == "*"
            && parts[2] == "*"
            && parts[3] == "*"
            && parts[4] == "*"
            && int.TryParse(parts[0].AsSpan(2), out var minutes)
            && minutes is >= 1 and <= 60)
        {
            return minutes;
        }

        throw new ArgumentException(
            "CronSchedule must be '* * * * *' or '*/n * * * *' with n from 1 to 60. SQL Server Agent does not accept a general cron expression.",
            nameof(CronSchedule));
    }
}
