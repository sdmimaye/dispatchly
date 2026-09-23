using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dispatchly.Transport.PostgreSql;

/// <summary>Registers the PostgreSQL transport.</summary>
public static class PostgresTransportExtensions
{
    /// <summary>
    /// Uses PostgreSQL as the durable outbox. <see cref="IMessagePublisher.PublishAsync{TMessage}" />
    /// inserts and commits the row before it returns. <see cref="IMessageOutbox.EnlistAsync{TMessage}" />
    /// inserts on a caller-supplied <c>NpgsqlTransaction</c> and leaves that transaction uncommitted.
    /// The host listens for <c>NOTIFY</c> and does not poll.
    /// </summary>
    public static DispatchlyBuilder UsePostgresTransport(
        this DispatchlyBuilder builder,
        Action<PostgresTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new PostgresTransportOptions();
        configure(options);
        options.Validate();
        builder.EnsureSingleTransport("PostgreSQL");

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.ConnectionString));
        builder.Services.AddSingleton<PostgresMessageStore>();
        builder.Services.AddSingleton<PostgresSchemaProvisioner>();
        builder.Services.AddSingleton<IPostgresMaintenance, PostgresMaintenance>();
        builder.Services.AddSingleton<IMessagePublisher, PostgresMessagePublisher>();
        builder.Services.AddSingleton<IMessageOutbox, PostgresMessageOutbox>();
        builder.Services.AddHostedService<PostgresListenService>();
        return builder;
    }

    /// <summary>
    /// Skips a handler when its <see cref="MessageId" /> is already in <c>idempotency_inbox</c>.
    /// The inbox row and handler writes that use <see cref="MessageContext.GetRequiredFeature{TFeature}" />
    /// of <see cref="System.Data.Common.DbTransaction" /> commit together.
    /// Call this after <see cref="UsePostgresTransport" />.
    /// </summary>
    public static DispatchlyBuilder UsePostgresIdempotency(this DispatchlyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!string.Equals(builder.TransportName, "PostgreSQL", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Call UsePostgresTransport before UsePostgresIdempotency.");
        }

        RequireOptions(builder).IdempotencyEnabled = true;
        builder.Services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
        return builder.UseBehavior<IdempotencyBehavior>();
    }

    private static PostgresTransportOptions RequireOptions(DispatchlyBuilder builder)
    {
        foreach (var descriptor in builder.Services)
        {
            if (descriptor.ServiceType == typeof(PostgresTransportOptions)
                && descriptor.ImplementationInstance is PostgresTransportOptions options)
            {
                return options;
            }
        }

        throw new InvalidOperationException("Call UsePostgresTransport before UsePostgresIdempotency.");
    }
}
