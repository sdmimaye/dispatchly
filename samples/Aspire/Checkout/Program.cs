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

using var host = builder.Build();
await host.StartAsync();

var publisher = host.Services.GetRequiredService<IMessagePublisher>();
var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

Console.WriteLine("Checkout publishes one order every 5 seconds.");

try
{
    while (!stopping.IsCancellationRequested)
    {
        var orderId = $"ord-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        try
        {
            var id = await publisher.PublishAsync(new OrderPlaced(orderId), stopping);
            Console.WriteLine($"Checkout published {orderId} as {id}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Checkout failed to publish {orderId}: {ex}");
        }

        await timer.WaitForNextTickAsync(stopping);
    }
}
catch (OperationCanceledException)
{
}

await host.WaitForShutdownAsync();
