using Npgsql;

namespace Dispatchly;

internal sealed class PostgresSchemaProvisioner
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresTransportOptions _options;
    private readonly IMessageTypeCatalog _catalog;

    public PostgresSchemaProvisioner(
        NpgsqlDataSource dataSource,
        PostgresTransportOptions options,
        IMessageTypeCatalog catalog)
    {
        _dataSource = dataSource;
        _options = options;
        _catalog = catalog;
    }

    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            $"CREATE SCHEMA IF NOT EXISTS {IdentifierRules.Quote(_options.Schema)};",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            $"""
            CREATE TABLE IF NOT EXISTS {PostgresSql.Qualify(_options.Schema, "outbox_table")} (
                table_name text PRIMARY KEY
            );
            """,
            cancellationToken).ConfigureAwait(false);
        if (_options.IdempotencyEnabled)
        {
            await ExecuteAsync(connection, PostgresSql.CreateIdempotencyInbox(_options), cancellationToken)
                .ConfigureAwait(false);
        }

        await ExecuteAsync(connection, PostgresSql.NotifyFunction(_options), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, PostgresSql.RedeliverFunction(_options), cancellationToken).ConfigureAwait(false);

        foreach (var registration in _catalog.Registrations)
        {
            await ExecuteAsync(connection, PostgresSql.CreateOutbox(_options, registration.TableName), cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, PostgresSql.CreateDeadLetter(_options, registration.TableName), cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, PostgresSql.CreateTrigger(_options, registration.TableName), cancellationToken)
                .ConfigureAwait(false);
            await using var register = new NpgsqlCommand(
                $"""
                INSERT INTO {PostgresSql.Qualify(_options.Schema, "outbox_table")} (table_name)
                VALUES (@table_name)
                ON CONFLICT (table_name) DO NOTHING;
                """,
                connection);
            register.Parameters.AddWithValue("table_name", registration.TableName);
            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_options.ScheduleRedelivery)
        {
            await ScheduleCronAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScheduleCronAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS pg_cron;", cancellationToken)
                .ConfigureAwait(false);
            await using (var unschedule = new NpgsqlCommand(
                "SELECT cron.unschedule(jobname) FROM cron.job WHERE jobname = @name;",
                connection))
            {
                unschedule.Parameters.AddWithValue("name", _options.CronJobName);
                await unschedule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var commandText =
                $"SELECT {PostgresSql.Qualify(_options.Schema, "redeliver_undelivered")}({_options.RedeliveryBatchSize})";
            await using var schedule = new NpgsqlCommand(
                "SELECT cron.schedule(@name, @schedule, @command);",
                connection);
            schedule.Parameters.AddWithValue("name", _options.CronJobName);
            schedule.Parameters.AddWithValue("schedule", _options.CronSchedule);
            schedule.Parameters.AddWithValue("command", commandText);
            await schedule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "PostgreSQL transport could not schedule redelivery because pg_cron is not available. Install pg_cron, add it to shared_preload_libraries, create the extension, or set PostgresTransportOptions.ScheduleRedelivery to false and call IPostgresMaintenance.RedeliverAsync from your own scheduler.",
                exception);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
