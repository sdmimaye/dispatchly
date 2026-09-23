using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly;

/// <summary>Registers the SQL Server transport.</summary>
public static class SqlServerTransportExtensions
{
    /// <summary>
    /// Uses SQL Server as the durable outbox. <see cref="IMessagePublisher.PublishAsync{TMessage}" />
    /// inserts and commits the row before it returns. An insert trigger sends a Service Broker message,
    /// and the host blocks in <c>WAITFOR (RECEIVE)</c>. It does not poll.
    /// Azure SQL Database is unsupported because it has no Service Broker.
    /// </summary>
    public static DispatchlyBuilder UseSqlServerTransport(
        this DispatchlyBuilder builder,
        Action<SqlServerTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new SqlServerTransportOptions();
        configure(options);
        options.Validate();
        builder.EnsureSingleTransport("SQL Server");

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SqlServerMessageStore>();
        builder.Services.AddSingleton<SqlServerSchemaProvisioner>();
        builder.Services.AddSingleton<ISqlServerMaintenance, SqlServerMaintenance>();
        builder.Services.AddSingleton<IMessagePublisher, SqlServerMessagePublisher>();
        builder.Services.AddHostedService<SqlServerListenService>();
        return builder;
    }
}
