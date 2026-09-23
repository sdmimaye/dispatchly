using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatchly;

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
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _run;

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

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
        connection.Notification += (_, args) => _notifications.Writer.TryWrite(args.Payload);
        await using (var listen = new NpgsqlCommand($"LISTEN {IdentifierRules.Quote(_options.Channel)};", connection))
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
