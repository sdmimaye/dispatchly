using Dispatchly.Abstractions;
using Dispatchly.DependencyInjection;
using Dispatchly.Transport.PostgreSql;
using Dispatchly.Sample.Aspire.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("dispatchly")
    ?? throw new InvalidOperationException(
        "Connection string 'dispatchly' was not found. Start this app from the Aspire AppHost.");

builder.Services.AddDispatchly()
    .AddMessage<OrderPlaced>(OrderJsonContext.Default.OrderPlaced)
    .UsePostgresTransport(options =>
    {
        options.ConnectionString = connectionString;
        options.ScheduleRedelivery = false;
    });

var publishesPerSecond = builder.Configuration.GetValue("Checkout:PublishesPerSecond", 5);
if (publishesPerSecond < 1)
{
    throw new InvalidOperationException("Checkout:PublishesPerSecond must be at least 1.");
}

using var host = builder.Build();
await host.StartAsync();

var publisher = host.Services.GetRequiredService<IMessagePublisher>();
var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
var sequence = 0L;

Console.WriteLine($"Checkout publishes {publishesPerSecond} orders every second.");

try
{
    while (!stopping.IsCancellationRequested)
    {
        for (var i = 0; i < publishesPerSecond && !stopping.IsCancellationRequested; i++)
        {
            var orderId = $"ord-{Interlocked.Increment(ref sequence):D6}";
            try
            {
                var id = await publisher.PublishAsync(new OrderPlaced(orderId), stopping);
                Console.WriteLine($"Checkout published {orderId} as {id}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Checkout failed to publish {orderId}: {ex}");
            }
        }

        await timer.WaitForNextTickAsync(stopping);
    }
}
catch (OperationCanceledException)
{
}

await host.WaitForShutdownAsync();
