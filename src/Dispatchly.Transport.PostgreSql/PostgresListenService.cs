using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatchly.Transport.PostgreSql;

internal sealed class PostgresListenService : IHostedService
{
    private readonly PostgresTransportOptions _options;
    private readonly PostgresSchemaProvisioner _provisioner;
    private readonly PostgresMessageStore _store;
    private readonly IMessageTypeCatalog _catalog;
    private readonly IMessageDispatcher _dispatcher;
    private readonly ILogger<PostgresListenService> _logger;
    private readonly Channel<string> _notifications = Channel.CreateUnbounded<string>();
    private readonly Lock _inFlightLock = new();
    private readonly List<Task> _inFlight = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _heartbeatStopping = new();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _run;
    private string? _consumerId;
    private string? _channel;

    public PostgresListenService(
        PostgresTransportOptions options,
        PostgresSchemaProvisioner provisioner,
        PostgresMessageStore store,
        IMessageTypeCatalog catalog,
        IMessageDispatcher dispatcher,
        ILogger<PostgresListenService> logger)
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
    internal void Abandon() => _heartbeatStopping.Cancel();

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _heartbeatStopping.Cancel();
        await UnregisterAsync(cancellationToken).ConfigureAwait(false);
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
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var worker = ProcessAsync(stoppingToken);
        try
        {
            await _provisioner.ProvisionAsync(stoppingToken).ConfigureAwait(false);
            var handled = _catalog.Registrations.Where(registration => registration.HasHandlers).Select(registration => registration.TableName).ToArray();
            if (handled.Length == 0)
            {
                _listening.TrySetResult();
                return;
            }

            await RegisterAsync(handled, stoppingToken).ConfigureAwait(false);
            var heartbeat = HeartbeatAsync(stoppingToken);
            try
            {
                await ListenUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
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
            _logger.LogError(exception, "PostgreSQL transport failed to start.");
        }
        finally
        {
            _notifications.Writer.TryComplete();
            await worker.ConfigureAwait(false);
        }
    }

    private async Task ListenUntilStoppedAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "PostgreSQL LISTEN connection failed. Reconnecting.");
                if (!_listening.Task.IsCompleted)
                {
                    _listening.TrySetException(exception);
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RegisterAsync(string[] tables, CancellationToken cancellationToken)
    {
        _consumerId = Guid.NewGuid().ToString("N");
        _channel = IdentifierRules.ValidateChannel("c" + _consumerId);
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {PostgresSql.Qualify(_options.Schema, IdentifierRules.ConsumerTable)}
                (id, channel, heartbeat_at, timeout_ms)
            VALUES (@id, @channel, clock_timestamp(), @timeout_ms);
            """,
            connection);
        insert.Parameters.AddWithValue("id", _consumerId);
        insert.Parameters.AddWithValue("channel", _channel);
        insert.Parameters.AddWithValue("timeout_ms", _options.HeartbeatTimeoutMilliseconds);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            await using var membership = new NpgsqlCommand(
                $"""
                INSERT INTO {PostgresSql.Qualify(_options.Schema, IdentifierRules.ConsumerMembershipTable)}
                    (consumer_id, table_name)
                VALUES (@id, @table_name);
                """,
                connection);
            membership.Parameters.AddWithValue("id", _consumerId);
            membership.Parameters.AddWithValue("table_name", table);
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
                await using var connection = new NpgsqlConnection(_options.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {PostgresSql.Qualify(_options.Schema, IdentifierRules.ConsumerTable)}
                    SET heartbeat_at = clock_timestamp()
                    WHERE id = @id;
                    """,
                    connection);
                command.Parameters.AddWithValue("id", _consumerId!);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "PostgreSQL consumer heartbeat failed.");
            }
        }
    }

    private async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        if (_consumerId is null)
        {
            return;
        }

        var id = _consumerId;
        _consumerId = null;
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {PostgresSql.Qualify(_options.Schema, IdentifierRules.ConsumerTable)} WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
        connection.Notification += (_, args) => _notifications.Writer.TryWrite(args.Payload);
        await using (var listen = new NpgsqlCommand($"LISTEN {IdentifierRules.Quote(_channel!)};", connection))
        {
            await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
        }

        _listening.TrySetResult();
        while (!stoppingToken.IsCancellationRequested)
        {
            await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
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
            _logger.LogError(exception, "Failed to handle a PostgreSQL notification.");
        }
    }

    private async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        if (!NotificationPayload.TryParse(payload, out var id, out var table))
        {
            _logger.LogWarning("Ignoring a PostgreSQL notification with an unrecognized payload.");
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
}
