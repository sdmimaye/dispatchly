using Dispatchly.Abstractions;
using Dispatchly.DependencyInjection;
using Dispatchly.Transport.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("dispatchly")
    ?? builder.Configuration["DISPATCHLY_POSTGRES"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "Set ConnectionStrings:dispatchly or DISPATCHLY_POSTGRES to a PostgreSQL connection string.");
    return 1;
}

var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
builder.Services.AddSingleton(delivered);
builder.Services.AddDispatchly()
    .AddHandler<OrderPlaced, OrderPlacedHandler>()
    .UsePostgresTransport(options =>
    {
        options.ConnectionString = connectionString;
        options.ScheduleRedelivery = false;
    });

using var host = builder.Build();
await host.StartAsync();
Console.WriteLine("Listening. pg_cron is not scheduled; this process delivers with NOTIFY.");

var id = await host.Services.GetRequiredService<IMessagePublisher>().PublishAsync(new OrderPlaced("1001"));
Console.WriteLine($"Enqueued {id}.");

await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
await host.StopAsync();
return 0;

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
