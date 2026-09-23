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
}
