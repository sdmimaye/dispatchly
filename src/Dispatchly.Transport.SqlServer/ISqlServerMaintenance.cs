namespace Dispatchly;

/// <summary>Runs the SQL Server redelivery procedure that SQL Server Agent would call.</summary>
public interface ISqlServerMaintenance
{
    /// <summary>Sends a Service Broker wake-up for due, unacknowledged messages.</summary>
    Task RedeliverAsync(CancellationToken cancellationToken = default);
}
