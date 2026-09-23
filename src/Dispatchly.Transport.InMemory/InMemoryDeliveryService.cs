using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatchly;

internal sealed class InMemoryDeliveryService : BackgroundService
{
    private readonly Channel<InMemoryEnvelope> _queue;
    private readonly IMessageDispatcher _dispatcher;
    private readonly InMemoryTransportOptions _options;
    private readonly InMemoryDeadLetterStore _deadLetters;
    private readonly ILogger<InMemoryDeliveryService> _logger;

    public InMemoryDeliveryService(
        Channel<InMemoryEnvelope> queue,
        IMessageDispatcher dispatcher,
        InMemoryTransportOptions options,
        InMemoryDeadLetterStore deadLetters,
        ILogger<InMemoryDeliveryService> logger)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        _options = options;
        _deadLetters = deadLetters;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await DeliverAsync(envelope, stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverAsync(InMemoryEnvelope envelope, CancellationToken stoppingToken)
    {
        var attempt = envelope.Attempt + 1;
        var context = new MessageContext(envelope.Id, attempt, envelope.EnqueuedAt);
        try
        {
            await _dispatcher.DispatchAsync(envelope.MessageType, envelope.Payload, context, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (attempt >= _options.MaxAttempts)
            {
                _deadLetters.Add(new InMemoryDeadLetter(
                    envelope.Id,
                    envelope.MessageType.FullName ?? envelope.MessageType.Name,
                    envelope.Payload,
                    attempt,
                    Truncate(exception),
                    envelope.EnqueuedAt,
                    DateTimeOffset.UtcNow));
                _logger.LogError(
                    exception,
                    "In-memory message {MessageId} was dead-lettered after {Attempt} attempts.",
                    envelope.Id,
                    attempt);
                return;
            }

            _logger.LogWarning(
                exception,
                "In-memory message {MessageId} failed on attempt {Attempt} and will be retried.",
                envelope.Id,
                attempt);
            _ = RetryLaterAsync(envelope with { Attempt = attempt }, stoppingToken);
        }
    }

    private async Task RetryLaterAsync(InMemoryEnvelope envelope, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_options.RetryDelay, stoppingToken).ConfigureAwait(false);
            await _queue.Writer.WriteAsync(envelope, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static string Truncate(Exception exception)
    {
        var text = exception.ToString();
        return text.Length <= 4000 ? text : text[..4000];
    }
}
