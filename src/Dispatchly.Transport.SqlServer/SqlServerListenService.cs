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
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _run;

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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
            var tables = _catalog.Registrations
                .Where(registration => registration.HasHandlers)
                .Select(registration => registration.TableName)
                .ToArray();
            if (tables.Length == 0)
            {
                _listening.TrySetResult();
                return;
            }

            var gate = new ListenGate(_listening, tables.Length);
            await Task.WhenAll(tables.Select(table => ListenAsync(table, gate, stoppingToken))).ConfigureAwait(false);
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

    private async Task ListenAsync(string table, ListenGate gate, CancellationToken stoppingToken)
    {
        var signaled = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new SqlConnection(_options.ConnectionString);
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                if (!signaled)
                {
                    signaled = true;
                    gate.Ready();
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    // RECEIVE auto-commits, so a handler failure cannot roll it back and disable the queue.
                    var body = await ReceiveOnceAsync(connection, table, stoppingToken).ConfigureAwait(false);
                    if (body is not null)
                    {
                        _notifications.Writer.TryWrite(body);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogError(exception, "SQL Server WAITFOR RECEIVE failed for {Table}. Reconnecting.", table);
                if (!signaled)
                {
                    gate.Failed(exception);
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> ReceiveOnceAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        Guid? handle = null;
        string? type = null;
        byte[]? body = null;
        await using (var command = new SqlCommand(SqlServerSql.Receive(_options, table), connection)
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

    private sealed class ListenGate
    {
        private readonly TaskCompletionSource _listening;
        private readonly int _expected;
        private int _ready;

        public ListenGate(TaskCompletionSource listening, int expected)
        {
            _listening = listening;
            _expected = expected;
        }

        public void Ready()
        {
            if (Interlocked.Increment(ref _ready) == _expected)
            {
                _listening.TrySetResult();
            }
        }

        public void Failed(Exception exception) => _listening.TrySetException(exception);
    }
}
