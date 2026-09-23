using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dispatchly;

/// <summary>Registers the PostgreSQL transport.</summary>
public static class PostgresTransportExtensions
{
    /// <summary>
    /// Uses PostgreSQL as the durable outbox. <see cref="IMessagePublisher.PublishAsync{TMessage}" />
    /// inserts and commits the row before it returns. The host listens for <c>NOTIFY</c> and does not poll.
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
        builder.Services.AddHostedService<PostgresListenService>();
        return builder;
    }
}
