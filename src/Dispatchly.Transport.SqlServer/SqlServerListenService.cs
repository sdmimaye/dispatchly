using System.Text;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatchly.Transport.SqlServer;

internal sealed class SqlServerListenService : IHostedService
{
    private readonly SqlServerTransportOptions _options;
    private readonly SqlServerSchemaProvisioner _provisioner;
    private readonly SqlServerMessageStore _store;
    private readonly IMessageTypeCatalog _catalog;
    private readonly IMessageDispatcher _dispatcher;
    private readonly ILogger<SqlServerListenService> _logger;
    private readonly Channel<string> _notifications = Channel.CreateUnbounded<string>();
    private readonly Lock _inFlightLock = new();
    private readonly List<Task> _inFlight = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _heartbeatStopping = new();
    private readonly CancellationTokenSource _receiveStopping = new();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _run;
    private string? _consumerId;
    private string? _queue;
    private string? _targetService;
    private string? _initiatorService;

    public SqlServerListenService(
        SqlServerTransportOptions options,
        SqlServerSchemaProvisioner provisioner,
        SqlServerMessageStore store,
        IMessageTypeCatalog catalog,
        IMessageDispatcher dispatcher,
        ILogger<SqlServerListenService> logger)
    {
        _options = options;
        _provisioner = provisioner;
        _store = store;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = RunAsync(_stopping.Token);
        return _listening.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Stops heartbeats and leaves the consumer row in place. Used to simulate a crash.</summary>
    internal void Abandon()
    {
        _heartbeatStopping.Cancel();
        _receiveStopping.Cancel();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _heartbeatStopping.Cancel();
        await DeleteConsumerAsync(cancellationToken).ConfigureAwait(false);
        await _stopping.CancelAsync().ConfigureAwait(false);
        _notifications.Writer.TryComplete();
        if (_run is not null)
        {
            await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Task[] pending;
        lock (_inFlightLock)
        {
            pending = _inFlight.ToArray();
        }

        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        await DropQueueAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var worker = ProcessAsync(stoppingToken);
        try
        {
            await _provisioner.ProvisionAsync(stoppingToken).ConfigureAwait(false);
            var tables = _catalog.Registrations
                .Where(registration => registration.HasHandlers)
                .Select(registration => registration.TableName)
                .ToArray();
            if (tables.Length == 0)
            {
                _listening.TrySetResult();
                return;
            }

            await RegisterAsync(tables, stoppingToken).ConfigureAwait(false);
            var heartbeat = HeartbeatAsync(stoppingToken);
            try
            {
                await ListenAsync(stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                _heartbeatStopping.Cancel();
                await heartbeat.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _listening.TrySetException(exception);
            _logger.LogError(exception, "SQL Server transport failed to start.");
        }
        finally
        {
            _notifications.Writer.TryComplete();
            await worker.ConfigureAwait(false);
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _receiveStopping.Token);
        var cancellationToken = linked.Token;
        var signaled = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new SqlConnection(_options.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (!signaled)
                {
                    signaled = true;
                    _listening.TrySetResult();
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    // RECEIVE auto-commits, so a handler failure cannot roll it back and disable the queue.
                    var body = await ReceiveOnceAsync(connection, cancellationToken).ConfigureAwait(false);
                    if (body is not null)
                    {
                        _notifications.Writer.TryWrite(body);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogError(exception, "SQL Server WAITFOR RECEIVE failed. Reconnecting.");
                if (!signaled)
                {
                    _listening.TrySetException(exception);
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> ReceiveOnceAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        Guid? handle = null;
        string? type = null;
        byte[]? body = null;
        await using (var command = new SqlCommand(SqlServerSql.Receive(_options, _queue!), connection)
        {
            CommandTimeout = 0,
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                handle = reader.GetGuid(0);
                type = reader.GetString(1);
                if (!reader.IsDBNull(2))
                {
                    body = (byte[])reader.GetValue(2);
                }
            }
        }

        if (handle is null)
        {
            return null;
        }

        if (!string.Equals(type, SqlServerIdentifiers.MessageType(_options.Schema), StringComparison.Ordinal))
        {
            await using var end = new SqlCommand("END CONVERSATION @handle;", connection);
            end.Parameters.Add(new SqlParameter("@handle", System.Data.SqlDbType.UniqueIdentifier) { Value = handle.Value });
            await end.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (body is null || body.Length == 0)
        {
            return null;
        }

        return Encoding.Unicode.GetString(body);
    }

    private async Task ProcessAsync(CancellationToken stoppingToken)
    {
        await foreach (var payload in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var work = HandleSafeAsync(payload, stoppingToken);
            lock (_inFlightLock)
            {
                _inFlight.Add(work);
            }
        }
    }

    private async Task HandleSafeAsync(string payload, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to handle a SQL Server notification.");
        }
    }

    private async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        if (!NotificationPayload.TryParse(payload, out var id, out var table))
        {
            _logger.LogWarning("Ignoring a SQL Server notification with an unrecognized payload.");
            return;
        }

        if (!_catalog.TryGetByTable(table, out var registration))
        {
            _logger.LogWarning("Ignoring a notification for unregistered table {Table}.", table);
            return;
        }

        if (!registration.HasHandlers)
        {
            return;
        }

        var claim = await _store.ClaimAsync(table, id, cancellationToken).ConfigureAwait(false);
        if (claim is not ClaimResult.Ready ready)
        {
            return;
        }

        var context = new MessageContext(new MessageId(id), ready.Attempt, ready.EnqueuedAt);
        try
        {
            await _dispatcher.DispatchAsync(registration.MessageType, ready.Payload, context, cancellationToken)
                .ConfigureAwait(false);
            await _store.AcknowledgeAsync(table, id, ready.Attempt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = Truncate(exception);
            if (ready.Attempt >= _options.MaxAttempts)
            {
                await _store.MoveFailureToDeadLetterAsync(table, id, ready.Attempt, error, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogError(exception, "Message {MessageId} was dead-lettered after {Attempt} attempts.", id, ready.Attempt);
                return;
            }

            var backoff = DeliveryBackoff.ForAttempt(_options.VisibilityTimeout, _options.MaxBackoff, ready.Attempt);
            await _store.RecordFailureAsync(table, id, ready.Attempt, error, backoff, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning(
                exception,
                "Message {MessageId} failed on attempt {Attempt} and will be retried after {Backoff}.",
                id,
                ready.Attempt,
                backoff);
        }
    }

    private static string Truncate(Exception exception)
    {
        var text = exception.ToString();
        return text.Length <= 4000 ? text : text[..4000];
    }

    private async Task RegisterAsync(string[] tables, CancellationToken cancellationToken)
    {
        _consumerId = Guid.NewGuid().ToString("N");
        _queue = SqlServerIdentifiers.ConsumerQueue(_consumerId);
        _targetService = SqlServerIdentifiers.TargetService(_options.Schema, _consumerId);
        _initiatorService = SqlServerIdentifiers.InitiatorService(_options.Schema, _consumerId);
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSql.EnsureQueue(_options, _queue), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSql.EnsureServices(_options, _consumerId), cancellationToken).ConfigureAwait(false);
        await using var insert = new SqlCommand(
            $"""
            INSERT INTO {SqlServerSql.Qualify(_options.Schema, IdentifierRules.ConsumerTable)}
                (id, queue_name, target_service, initiator_service, heartbeat_at, timeout_ms)
            VALUES (@id, @queue, @target, @initiator, SYSUTCDATETIME(), @timeout_ms);
            """,
            connection);
        insert.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.NVarChar, 32) { Value = _consumerId });
        insert.Parameters.Add(new SqlParameter("@queue", System.Data.SqlDbType.NVarChar, 128) { Value = _queue });
        insert.Parameters.Add(new SqlParameter("@target", System.Data.SqlDbType.NVarChar, 256) { Value = _targetService });
        insert.Parameters.Add(new SqlParameter("@initiator", System.Data.SqlDbType.NVarChar, 256) { Value = _initiatorService });
        insert.Parameters.Add(new SqlParameter("@timeout_ms", System.Data.SqlDbType.Int) { Value = _options.HeartbeatTimeoutMilliseconds });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            await using var membership = new SqlCommand(
                $"""
                INSERT INTO {SqlServerSql.Qualify(_options.Schema, IdentifierRules.ConsumerMembershipTable)}
                    (consumer_id, table_name)
                VALUES (@id, @table_name);
                """,
                connection);
            membership.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.NVarChar, 32) { Value = _consumerId });
            membership.Parameters.Add(new SqlParameter("@table_name", System.Data.SqlDbType.NVarChar, 128) { Value = table });
            await membership.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _heartbeatStopping.Token);
        var cancellationToken = linked.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                await using var connection = new SqlConnection(_options.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new SqlCommand(
                    $"""
                    UPDATE {SqlServerSql.Qualify(_options.Schema, IdentifierRules.ConsumerTable)}
                    SET heartbeat_at = SYSUTCDATETIME()
                    WHERE id = @id;
                    EXEC {SqlServerSql.Qualify(_options.Schema, "retire_stale_consumers")};
                    """,
                    connection);
                command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.NVarChar, 32) { Value = _consumerId! });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "SQL Server consumer heartbeat failed.");
            }
        }
    }

    private async Task DeleteConsumerAsync(CancellationToken cancellationToken)
    {
        if (_consumerId is null)
        {
            return;
        }

        var id = _consumerId;
        _consumerId = null;
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            $"DELETE FROM {SqlServerSql.Qualify(_options.Schema, IdentifierRules.ConsumerTable)} WHERE id = @id;",
            connection);
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.NVarChar, 32) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DropQueueAsync(CancellationToken cancellationToken)
    {
        if (_queue is null || _targetService is null || _initiatorService is null)
        {
            return;
        }

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SqlServerSql.DropConsumer(_options), connection);
        command.Parameters.Add(new SqlParameter("@queue", System.Data.SqlDbType.NVarChar, 128) { Value = _queue });
        command.Parameters.Add(new SqlParameter("@target", System.Data.SqlDbType.NVarChar, 256) { Value = _targetService });
        command.Parameters.Add(new SqlParameter("@initiator", System.Data.SqlDbType.NVarChar, 256) { Value = _initiatorService });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

}
