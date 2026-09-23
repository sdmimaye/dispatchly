using Microsoft.Data.SqlClient;

namespace Dispatchly;

internal sealed class SqlServerSchemaProvisioner
{
    private readonly SqlServerTransportOptions _options;
    private readonly IMessageTypeCatalog _catalog;

    public SqlServerSchemaProvisioner(SqlServerTransportOptions options, IMessageTypeCatalog catalog)
    {
        _options = options;
        _catalog = catalog;
    }

    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureBrokerAsync(connection, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = {SqlServerIdentifiers.UnicodeLiteral(_options.Schema)})
                EXEC(N'CREATE SCHEMA {SqlServerIdentifiers.Quote(_options.Schema)}');
            """,
            cancellationToken).ConfigureAwait(false);
        if (_options.IdempotencyEnabled)
        {
            await ExecuteAsync(connection, SqlServerSql.CreateIdempotencyInbox(_options), cancellationToken)
                .ConfigureAwait(false);
        }

        await ExecuteAsync(connection, SqlServerSql.CreateRegistry(_options), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSql.EnsureMessageType(_options), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSql.EnsureContract(_options), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSql.NotifyProcedure(_options), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSql.RedeliverProcedure(_options), cancellationToken).ConfigureAwait(false);

        foreach (var registration in _catalog.Registrations)
        {
            await ExecuteAsync(connection, SqlServerSql.CreateOutbox(_options, registration.TableName), cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, SqlServerSql.CreateDeadLetter(_options, registration.TableName), cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, SqlServerSql.EnsureQueue(_options, registration.TableName), cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, SqlServerSql.EnsureServices(_options, registration.TableName), cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, SqlServerSql.CreateTrigger(_options, registration.TableName), cancellationToken)
                .ConfigureAwait(false);
            await using var register = new SqlCommand(
                $"""
                IF NOT EXISTS (
                    SELECT 1 FROM {SqlServerSql.Qualify(_options.Schema, "outbox_table")} WHERE table_name = @table_name)
                    INSERT INTO {SqlServerSql.Qualify(_options.Schema, "outbox_table")} (table_name) VALUES (@table_name);
                """,
                connection);
            register.Parameters.Add(new SqlParameter("@table_name", System.Data.SqlDbType.NVarChar, 128)
            {
                Value = registration.TableName,
            });
            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_options.ScheduleRedelivery)
        {
            await ScheduleAgentAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureBrokerAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT DB_NAME(), CONVERT(bit, is_broker_enabled)
            FROM sys.databases
            WHERE database_id = DB_ID();
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("SQL Server transport could not read the current database.");
        }

        var database = reader.GetString(0);
        var enabled = reader.GetBoolean(1);
        if (IsSystemDatabase(database))
        {
            throw new InvalidOperationException(
                "SQL Server transport requires a user database with Service Broker enabled. The connection string is using a system database.");
        }

        if (!enabled)
        {
            throw new InvalidOperationException(
                $"SQL Server transport requires Service Broker in database '{database}'. Run ALTER DATABASE {SqlServerIdentifiers.Quote(database)} SET ENABLE_BROKER. Azure SQL Database does not support Service Broker.");
        }
    }

    private async Task ScheduleAgentAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await AgentIsRunningAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "SQL Server transport could not schedule redelivery because SQL Server Agent is not running. Start SQL Server Agent, or set SqlServerTransportOptions.ScheduleRedelivery to false and call ISqlServerMaintenance.RedeliverAsync from your own scheduler.");
        }

        try
        {
            var commandText =
                $"EXEC {SqlServerSql.Qualify(_options.Schema, "redeliver_undelivered")} @batch_size = {_options.RedeliveryBatchSize}";
            await using var schedule = new SqlCommand(
                """
                DECLARE @job_id uniqueidentifier;
                SELECT @job_id = job_id FROM msdb.dbo.sysjobs WHERE name = @name;
                IF @job_id IS NOT NULL
                    EXEC msdb.dbo.sp_delete_job @job_id = @job_id, @delete_unused_schedule = 1;

                IF EXISTS (SELECT 1 FROM msdb.dbo.sysschedules WHERE name = @name)
                    EXEC msdb.dbo.sp_delete_schedule @schedule_name = @name, @force_delete = 1;

                EXEC msdb.dbo.sp_add_job
                    @job_name = @name,
                    @enabled = 1,
                    @description = N'Dispatchly redelivery';

                EXEC msdb.dbo.sp_add_jobstep
                    @job_name = @name,
                    @step_name = N'redeliver',
                    @subsystem = N'TSQL',
                    @database_name = DB_NAME(),
                    @command = @command;

                EXEC msdb.dbo.sp_add_schedule
                    @schedule_name = @name,
                    @freq_type = 4,
                    @freq_interval = 1,
                    @freq_subday_type = 4,
                    @freq_subday_interval = @minutes,
                    @active_start_time = 0,
                    @active_end_time = 235959;

                EXEC msdb.dbo.sp_attach_schedule @job_name = @name, @schedule_name = @name;
                EXEC msdb.dbo.sp_add_jobserver @job_name = @name;
                """,
                connection);
            schedule.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = _options.CronJobName });
            schedule.Parameters.Add(new SqlParameter("@command", System.Data.SqlDbType.NVarChar, -1) { Value = commandText });
            schedule.Parameters.Add(new SqlParameter("@minutes", System.Data.SqlDbType.Int) { Value = _options.RedeliveryIntervalMinutes });
            await schedule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "SQL Server transport could not schedule redelivery on SQL Server Agent. Confirm the login can create jobs in msdb, or set SqlServerTransportOptions.ScheduleRedelivery to false and call ISqlServerMaintenance.RedeliverAsync from your own scheduler.",
                exception);
        }
    }

    private static async Task<bool> AgentIsRunningAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand(
                """
                SELECT TOP (1) status_desc
                FROM sys.dm_server_services
                WHERE servicename LIKE N'%SQL Server Agent%'
                """,
                connection);
            var status = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static bool IsSystemDatabase(string database) =>
        database.Equals("master", StringComparison.OrdinalIgnoreCase)
        || database.Equals("tempdb", StringComparison.OrdinalIgnoreCase)
        || database.Equals("model", StringComparison.OrdinalIgnoreCase)
        || database.Equals("msdb", StringComparison.OrdinalIgnoreCase);

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
