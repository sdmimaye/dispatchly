using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly;

/// <summary>Registers the in-memory transport.</summary>
public static class InMemoryTransportExtensions
{
    /// <summary>
    /// Uses a process-local queue. <see cref="IMessagePublisher.PublishAsync{TMessage}" /> enqueues immediately.
    /// Messages still in the queue are lost when the process exits.
    /// </summary>
    public static DispatchlyBuilder UseInMemoryTransport(
        this DispatchlyBuilder builder,
        Action<InMemoryTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new InMemoryTransportOptions();
        configure?.Invoke(options);
        options.Validate();
        builder.EnsureSingleTransport("InMemory");

        var queue = Channel.CreateUnbounded<InMemoryEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var deadLetters = new InMemoryDeadLetterStore();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(queue);
        builder.Services.AddSingleton(deadLetters);
        builder.Services.AddSingleton<IInMemoryDeadLetterStore>(deadLetters);
        builder.Services.AddSingleton<IMessagePublisher, InMemoryMessagePublisher>();
        builder.Services.AddHostedService<InMemoryDeliveryService>();
        return builder;
    }

    /// <summary>
    /// Skips a second in-process delivery of the same <see cref="MessageId" />.
    /// The record is lost when the process exits, together with any queued messages.
    /// Call this after <see cref="UseInMemoryTransport" />.
    /// </summary>
    public static DispatchlyBuilder UseInMemoryIdempotency(this DispatchlyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!string.Equals(builder.TransportName, "InMemory", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Call UseInMemoryTransport before UseInMemoryIdempotency.");
        }

        builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return builder.UseBehavior<IdempotencyBehavior>();
    }
}
