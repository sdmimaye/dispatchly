namespace Dispatchly.Transport.PostgreSql;

/// <summary>Runs the PostgreSQL redelivery function that <c>pg_cron</c> would call.</summary>
public interface IPostgresMaintenance
{
    /// <summary>Notifies due, unacknowledged messages on the transport channel.</summary>
    Task RedeliverAsync(CancellationToken cancellationToken = default);
}
