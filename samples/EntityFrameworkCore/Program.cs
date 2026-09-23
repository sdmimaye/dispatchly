using Dispatchly.Abstractions;
using Dispatchly.DependencyInjection;
using Dispatchly.EntityFrameworkCore;
using Dispatchly.Transport.PostgreSql;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddDbContext<OrdersDbContext>((services, options) =>
{
    options.UseNpgsql(connectionString);
    options.UseDispatchlyOutbox(services);
});

using var host = builder.Build();
await host.StartAsync();
Console.WriteLine("Listening. Saving an order enlists OrderPlaced on that save's transaction.");

await using (var scope = host.Services.CreateAsyncScope())
{
    var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    await context.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS orders (
            id uuid PRIMARY KEY,
            number character varying(64) NOT NULL
        )
        """);

    var order = new Order { Id = Guid.NewGuid(), Number = "1001" };
    order.Place();
    context.Orders.Add(order);
    await context.SaveChangesAsync();
    Console.WriteLine($"Saved order {order.Number}. The domain event committed with the order.");
}

await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
await host.StopAsync();
return 0;

internal sealed record OrderPlaced(string OrderId);

internal sealed class Order : DomainEventSource
{
    public Guid Id { get; set; }

    public string Number { get; set; } = "";

    public void Place() => Raise(new OrderPlaced(Number));
}

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Id).HasColumnName("id");
            entity.Property(order => order.Number).HasColumnName("number").HasMaxLength(64).IsRequired();
        });
    }
}

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
