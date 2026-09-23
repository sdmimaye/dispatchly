using Dispatchly;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(delivered);
builder.Services.AddDispatchly()
    .AddHandler<OrderPlaced, OrderPlacedHandler>()
    .UseInMemoryTransport(options => options.MaxAttempts = 3);

using var host = builder.Build();
await host.StartAsync();

var id = await host.Services.GetRequiredService<IMessagePublisher>().PublishAsync(new OrderPlaced("1001"));
Console.WriteLine($"Enqueued {id}. In-memory delivery is lost if the process exits.");
await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await host.StopAsync();

internal sealed record OrderPlaced(string OrderId);

internal sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    private readonly TaskCompletionSource _delivered;

    public OrderPlacedHandler(TaskCompletionSource delivered) => _delivered = delivered;

    public Task HandleAsync(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Handled {message.OrderId} on attempt {context.Attempt}.");
        _delivered.TrySetResult();
        return Task.CompletedTask;
    }
}
