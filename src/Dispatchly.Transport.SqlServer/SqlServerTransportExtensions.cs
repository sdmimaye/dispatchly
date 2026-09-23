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

    /// <summary>
    /// Skips a handler when its <see cref="MessageId" /> is already in <c>idempotency_inbox</c>.
    /// The inbox row and handler writes that use <see cref="MessageContext.GetRequiredFeature{TFeature}" />
    /// of <see cref="System.Data.Common.DbTransaction" /> commit together.
    /// Call this after <see cref="UseSqlServerTransport" />.
    /// </summary>
    public static DispatchlyBuilder UseSqlServerIdempotency(this DispatchlyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!string.Equals(builder.TransportName, "SQL Server", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Call UseSqlServerTransport before UseSqlServerIdempotency.");
        }

        RequireOptions(builder).IdempotencyEnabled = true;
        builder.Services.AddSingleton<IIdempotencyStore, SqlServerIdempotencyStore>();
        return builder.UseBehavior<IdempotencyBehavior>();
    }

    private static SqlServerTransportOptions RequireOptions(DispatchlyBuilder builder)
    {
        foreach (var descriptor in builder.Services)
        {
            if (descriptor.ServiceType == typeof(SqlServerTransportOptions)
                && descriptor.ImplementationInstance is SqlServerTransportOptions options)
            {
                return options;
            }
        }

        throw new InvalidOperationException("Call UseSqlServerTransport before UseSqlServerIdempotency.");
    }
}
