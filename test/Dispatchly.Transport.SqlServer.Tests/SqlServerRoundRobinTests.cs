using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatchly.Transport.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerRoundRobinTests
{
    private const string Table = "placed_orders";

    private readonly SqlServerFixture _fixture;

    public SqlServerRoundRobinTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TwoHosts_AlternateMessages()
    {
        var schema = NewSchema();
        var first = new Inbox();
        var second = new Inbox();
        using var left = await StartAsync(schema, first);
        using var right = await StartAsync(schema, second);
        await PublishManyAsync(left, 10);

        await WaitUntilAsync(() => Task.FromResult(first.Received.Count + second.Received.Count == 10));
        Assert.InRange(Math.Abs(first.Received.Count - second.Received.Count), 0, 1);
        Assert.Equal(10, first.Received.Concat(second.Received).Select(message => message.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task ThirdHost_IsIncludedOnlyOnLaterMessages()
    {
        var schema = NewSchema();
        var first = new Inbox();
        var second = new Inbox();
        var third = new Inbox();
        using var left = await StartAsync(schema, first);
        using var right = await StartAsync(schema, second);
        var early = await PublishManyAsync(left, 4);
        await WaitUntilAsync(() => Task.FromResult(first.Received.Count + second.Received.Count == 4));

        using var joined = await StartAsync(schema, third);
        var later = await PublishManyAsync(left, 6);
        await WaitUntilAsync(() => Task.FromResult(first.Received.Count + second.Received.Count + third.Received.Count == 10));

        Assert.NotEmpty(third.Received);
        Assert.Empty(third.Received.Select(message => message.MessageId).Intersect(early));
        Assert.All(third.Received, message => Assert.Contains(message.MessageId, later));
    }

    [Fact]
    public async Task GracefulStop_RemovesTheHostBeforeTheNextPublish()
    {
        var schema = NewSchema();
        var first = new Inbox();
        var second = new Inbox();
        var left = await StartAsync(schema, first);
        using var right = await StartAsync(schema, second);
        await left.StopAsync();
        await PublishManyAsync(right, 4);

        await WaitUntilAsync(() => Task.FromResult(second.Received.Count == 4));
        Assert.Empty(first.Received);
        Assert.Equal(1, await CountQueuesAsync(schema));
    }

    [Fact]
    public async Task ExpiredHeartbeat_RedeliveryWakesAnotherHost()
    {
        var schema = NewSchema();
        var first = new Inbox { BlockFirstAttempt = true };
        var second = new Inbox { BlockFirstAttempt = true };
        using var left = await StartAsync(schema, first, configure: options => options.HeartbeatTimeout = TimeSpan.FromSeconds(1));
        using var right = await StartAsync(schema, second, configure: options => options.HeartbeatTimeout = TimeSpan.FromSeconds(1));
        var id = (await PublishManyAsync(left, 1)).Single();
        await WaitUntilAsync(() => Task.FromResult(first.Received.Count + second.Received.Count == 1));

        var claimed = first.Received.Count == 1 ? left : right;
        var other = claimed == left ? second : first;
        Abandon(claimed);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        await right.Services.GetRequiredService<ISqlServerMaintenance>().RedeliverAsync();

        await WaitUntilAsync(() => Task.FromResult(other.Received.Count == 1));
        Assert.Equal(id, other.Received.Single().MessageId);
        Assert.Equal(2, other.Received.Single().Attempt);
        Assert.Equal(1, await CountQueuesAsync(schema));
    }

    [Fact]
    public async Task PublishOnlyHost_IsNotInTheRing()
    {
        var schema = NewSchema();
        var inbox = new Inbox();
        using var publisher = await StartAsync(schema, inbox: null, publishOnly: true);
        using var consumer = await StartAsync(schema, inbox);
        Assert.Equal(1, await CountConsumersAsync(schema));
        var id = (await PublishManyAsync(publisher, 1)).Single();
        await WaitUntilAsync(() => Task.FromResult(inbox.Received.Count == 1));
        Assert.Equal(id, inbox.Received.Single().MessageId);
    }

    private static void Abandon(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<SqlServerListenService>().Single().Abandon();

    private async Task<IHost> StartAsync(
        string schema,
        Inbox? inbox,
        bool publishOnly = false,
        Action<SqlServerTransportOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (inbox is not null)
        {
            builder.Services.AddSingleton(inbox);
        }

        var dispatchly = builder.Services.AddDispatchly();
        if (publishOnly)
        {
            dispatchly.AddMessage<OrderPlaced>();
        }
        else
        {
            dispatchly.AddHandler<OrderPlaced, OrderPlacedHandler>();
        }

        dispatchly.UseSqlServerTransport(options =>
        {
            options.ConnectionString = _fixture.ConnectionString;
            options.Schema = schema;
            options.ScheduleRedelivery = false;
            options.CronJobName = schema;
            options.VisibilityTimeout = TimeSpan.FromMilliseconds(50);
            options.MaxBackoff = TimeSpan.FromMilliseconds(50);
            configure?.Invoke(options);
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<Guid[]> PublishManyAsync(IHost host, int count)
    {
        var publisher = host.Services.GetRequiredService<IMessagePublisher>();
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = (await publisher.PublishAsync(new OrderPlaced("n-" + i, i))).Value;
        }

        return ids;
    }

    private async Task<int> CountConsumersAsync(string schema)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM {SqlServerIdentifiers.Qualify(schema, IdentifierRules.ConsumerTable)}",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> CountQueuesAsync(string schema)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.service_queues
            WHERE schema_id = SCHEMA_ID(@schema)
            """,
            connection);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NewSchema() => "s" + Guid.NewGuid().ToString("N")[..12];

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(20);
        }
    }

    [DispatchlyTable(Table)]
    public sealed record OrderPlaced(string OrderId, int Quantity);

    public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
    {
        private readonly Inbox _inbox;

        public OrderPlacedHandler(Inbox inbox) => _inbox = inbox;

        public Task HandleAsync(OrderPlaced message, MessageContext context, CancellationToken cancellationToken) =>
            _inbox.ReceiveAsync(message, context, cancellationToken);
    }

    public sealed class Inbox
    {
        private readonly Lock _lock = new();
        private readonly List<Received> _received = [];

        public bool BlockFirstAttempt { get; init; }

        public IReadOnlyList<Received> Received
        {
            get
            {
                lock (_lock)
                {
                    return _received.ToArray();
                }
            }
        }

        public async Task ReceiveAsync(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _received.Add(new Received(context.Id.Value, context.Attempt));
            }

            if (BlockFirstAttempt && context.Attempt == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public sealed record Received(Guid MessageId, int Attempt);
}
