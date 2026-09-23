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
    .AddHandler<OrderPlaced, OrderPlacedHandler>(OrderJsonContext.Default.OrderPlaced)
    .UsePostgresTransport(options =>
    {
        options.ConnectionString = connectionString;
        options.ScheduleRedelivery = false;
    });

using var host = builder.Build();
await host.StartAsync();
await host.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
Console.WriteLine("Shipping is listening for orders.");
await host.WaitForShutdownAsync();

internal sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Shipping handled {message.OrderId} on attempt {context.Attempt}.");
        return Task.CompletedTask;
    }
}
