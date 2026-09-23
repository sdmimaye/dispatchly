using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dispatchly.EntityFrameworkCore.Tests;

public sealed record OrderSchema(string Name);

[DispatchlyTable("order_placed")]
public sealed record OrderPlaced(string OrderId, int Quantity);

public sealed class Order : DomainEventSource
{
    public Guid Id { get; set; }

    public string Number { get; set; } = "";

    public void Place(int quantity) => Raise(new OrderPlaced(Number, quantity));
}

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options, OrderSchema schema) : DbContext(options)
{
    public string Schema { get; } = schema.Name;

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders", Schema);
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Id).HasColumnName("id");
            entity.Property(order => order.Number).HasColumnName("number").HasMaxLength(64).IsRequired();
        });
    }
}

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    private readonly Inbox _inbox;

    public OrderPlacedHandler(Inbox inbox) => _inbox = inbox;

    public Task HandleAsync(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
    {
        _inbox.Receive(message);
        return Task.CompletedTask;
    }
}

public sealed class Inbox
{
    private readonly Lock _gate = new();
    private readonly List<OrderPlaced> _received = [];
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Expected { get; init; } = 1;

    public void Receive(OrderPlaced message)
    {
        bool done;
        lock (_gate)
        {
            _received.Add(message);
            done = _received.Count >= Expected;
        }

        if (done)
        {
            _completed.TrySetResult();
        }
    }

    public async Task<IReadOnlyList<OrderPlaced>> WaitAsync()
    {
        await _completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            return _received.ToArray();
        }
    }
}

public sealed class OrderSchemaCacheKeyFactory : IModelCacheKeyFactory
{
    public OrderSchemaCacheKeyFactory(ModelCacheKeyFactoryDependencies dependencies)
    {
    }

    public object Create(DbContext context, bool designTime) =>
        context is OrdersDbContext orders
            ? (orders.GetType(), orders.Schema, designTime)
            : context.GetType();
}
