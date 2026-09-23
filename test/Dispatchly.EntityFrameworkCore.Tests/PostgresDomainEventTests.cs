using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatchly.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresDomainEventTests
{
    private readonly PostgresFixture _fixture;

    public PostgresDomainEventTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SaveChangesAsync_CommitsTheAggregateAndTheOutboxTogether()
    {
        var inbox = new Inbox();
        using var host = await StartAsync(inbox);
        await CreateOrdersTableAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var order = new Order { Id = Guid.NewGuid(), Number = "1001" };
        order.Place(2);
        context.Orders.Add(order);

        await context.SaveChangesAsync();

        Assert.Empty(((IDomainEventSource)order).DomainEvents);
        Assert.Equal(1, await CountAsync(host, "orders"));
        var received = Assert.Single(await inbox.WaitAsync());
        Assert.Equal("1001", received.OrderId);
        Assert.Equal(2, received.Quantity);
        Assert.Equal(1, await CountAsync(host, "order_placed"));
    }

    [Fact]
    public async Task SaveChangesAsync_FailedSaveLeavesTheEventAndNoOutboxRow()
    {
        using var host = await StartAsync(new Inbox());
        await CreateOrdersTableAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var order = new Order { Id = Guid.NewGuid(), Number = "bad" };
        order.Place(2);
        context.Orders.Add(order);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Single(((IDomainEventSource)order).DomainEvents);
        Assert.Equal(0, await CountAsync(host, "order_placed"));
        Assert.Equal(0, await CountAsync(host, "orders"));
    }

    [Fact]
    public async Task SaveChangesAsync_CallerRollbackLeavesNoRow()
    {
        using var host = await StartAsync(new Inbox());
        await CreateOrdersTableAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var order = new Order { Id = Guid.NewGuid(), Number = "1001" };
        order.Place(2);
        context.Orders.Add(order);
        await using var transaction = await context.Database.BeginTransactionAsync();

        await context.SaveChangesAsync();
        Assert.Single(((IDomainEventSource)order).DomainEvents);
        await transaction.RollbackAsync();

        Assert.Single(((IDomainEventSource)order).DomainEvents);
        Assert.Equal(0, await CountAsync(host, "order_placed"));
        Assert.Equal(0, await CountAsync(host, "orders"));
    }

    [Fact]
    public async Task SaveChangesAsync_SecondSaveInTheSameTransactionEnlistsOnce()
    {
        var inbox = new Inbox();
        using var host = await StartAsync(inbox);
        await CreateOrdersTableAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var order = new Order { Id = Guid.NewGuid(), Number = "1001" };
        order.Place(2);
        context.Orders.Add(order);
        await using var transaction = await context.Database.BeginTransactionAsync();

        await context.SaveChangesAsync();
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        Assert.Empty(((IDomainEventSource)order).DomainEvents);
        Assert.Equal(1, await CountAsync(host, "order_placed"));
        Assert.Single(await inbox.WaitAsync());
    }

    private async Task<IHost> StartAsync(Inbox inbox, string? schema = null)
    {
        schema ??= NewSchema();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(inbox);
        builder.Services.AddSingleton(new OrderSchema(schema));
        builder.Services.AddDispatchly()
            .AddHandler<OrderPlaced, OrderPlacedHandler>()
            .UsePostgresTransport(options =>
            {
                options.ConnectionString = _fixture.ConnectionString;
                options.Schema = schema;
                options.Channel = schema;
                options.ScheduleRedelivery = false;
                options.CronJobName = schema;
                options.VisibilityTimeout = TimeSpan.FromMilliseconds(50);
                options.MaxBackoff = TimeSpan.FromMilliseconds(50);
            });
        builder.Services.AddDbContext<OrdersDbContext>((services, options) =>
        {
            options.UseNpgsql(_fixture.ConnectionString);
            options.UseDispatchlyOutbox(services);
            options.ReplaceService<IModelCacheKeyFactory, OrderSchemaCacheKeyFactory>();
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private async Task CreateOrdersTableAsync(IHost host)
    {
        var schema = host.Services.GetRequiredService<OrderSchema>().Name;
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            CREATE TABLE {IdentifierRules.Quote(schema)}.{IdentifierRules.Quote("orders")} (
                id uuid NOT NULL PRIMARY KEY,
                number character varying(64) NOT NULL,
                CONSTRAINT ck_orders_number CHECK (number <> 'bad')
            )
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(IHost host, string table)
    {
        var schema = host.Services.GetRequiredService<OrderSchema>().Name;
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {IdentifierRules.Quote(schema)}.{IdentifierRules.Quote(table)}",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static string NewSchema() => "s" + Guid.NewGuid().ToString("N")[..12];
}
