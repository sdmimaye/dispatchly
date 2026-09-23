using Dispatchly.Abstractions;
using Dispatchly.Core;
using Dispatchly.DependencyInjection;
using Dispatchly.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(delivered);
builder.Services.AddDispatchly()
    .AddHandler<OrderPlaced, OrderPlacedHandler>()
    .UseInMemoryTransport();

using var host = builder.Build();
await host.StartAsync();

var table = host.Services.GetRequiredService<IMessageTypeCatalog>().GetRequired(typeof(OrderPlaced)).TableName;
Console.WriteLine($"Reflection registration reads DispatchlyTable and uses table '{table}'.");
await host.Services.GetRequiredService<IMessagePublisher>().PublishAsync(new OrderPlaced("1001"));
await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await host.StopAsync();

[DispatchlyTable("placed_orders")]
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
