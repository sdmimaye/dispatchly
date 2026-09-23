using Dispatchly.Abstractions;

namespace Dispatchly.SourceGenerators.Tests;

public sealed record GeneratedOrder(string OrderId, int Quantity);

public sealed class GeneratedOrderHandler : IMessageHandler<GeneratedOrder>
{
    private readonly GeneratedOrderProbe _probe;

    public GeneratedOrderHandler(GeneratedOrderProbe probe) => _probe = probe;

    public Task HandleAsync(GeneratedOrder message, MessageContext context, CancellationToken cancellationToken)
    {
        _probe.Delivered.TrySetResult(message);
        return Task.CompletedTask;
    }
}

public sealed class GeneratedOrderProbe
{
    public TaskCompletionSource<GeneratedOrder> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
