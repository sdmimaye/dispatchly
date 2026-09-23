using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatchly.Transport.PostgreSql.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresOutboxEndToEndTests
{
    private const string Table = "placed_orders";

    private readonly PostgresFixture _fixture;

    public PostgresOutboxEndToEndTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PublishAsync_TwoHostsDeliverTheCommittedPayloadOnce()
    {
        var inbox = new Inbox();
        var schema = NewSchema();
        using var publisher = await StartAsync(schema, publishOnly: true);
        using var consumer = await StartAsync(schema, publishOnly: false, inbox);
        var id = await PublishAsync(publisher, new OrderPlaced("1001", 2));

        var received = await inbox.WaitForSingleAsync();
        Assert.Equal(id.Value, received.MessageId);
        Assert.Equal("1001", received.OrderId);
        Assert.Equal(2, received.Quantity);
        Assert.Equal(1, received.Attempt);
        Assert.InRange(received.EnqueuedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(5));

        var stored = await WaitForOutboxAsync(schema, id.Value, row => row is { Delivered: true });
        Assert.Equal("1001", stored.OrderId);
        Assert.Equal(2, stored.Quantity);
        Assert.Equal(1, stored.AttemptCount);
        Assert.True(stored.Delivered);
        Assert.True(await TableExistsAsync(schema, Table));
        Assert.False(await TableExistsAsync(schema, "order_placed"));

        await consumer.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
        await Task.Delay(250);
        Assert.Equal([1], inbox.Attempts);
    }

    [Fact]
    public async Task PublishAsync_ThreeMessagesAreDelivered()
    {
        var inbox = new Inbox { ExpectedSuccesses = 3 };
        var schema = NewSchema();
        using var host = await StartAsync(schema, publishOnly: false, inbox);
        var published = await PublishAsync(
            host,
            new OrderPlaced("A-1", 1),
            new OrderPlaced("A-2", 2),
            new OrderPlaced("A-3", 4));

        await inbox.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["A-1", "A-2", "A-3"], inbox.Received.Select(message => message.OrderId).Order().ToArray());
        Assert.Equal([1, 2, 4], inbox.Received.OrderBy(message => message.OrderId).Select(message => message.Quantity).ToArray());
        Assert.Equal(published.Select(id => id.Value).Order().ToArray(), inbox.Received.Select(message => message.MessageId).Order().ToArray());
        Assert.Equal(3, await CountAsync(schema, Table));
    }

    [Fact]
    public async Task FailedDelivery_RecordsTheErrorThenRedeliversTheSameMessage()
    {
        var inbox = new Inbox { SucceedOnAttempt = 2 };
        var schema = NewSchema();
        using var host = await StartAsync(schema, publishOnly: false, inbox, options => options.MaxAttempts = 3);
        var id = await PublishAsync(host, new OrderPlaced("1001", 7));

        var failed = await WaitForOutboxAsync(schema, id.Value, row => row is { Delivered: false, LastError: not null });
        Assert.Equal("1001", failed.OrderId);
        Assert.Equal(7, failed.Quantity);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Contains("gateway down", failed.LastError, StringComparison.Ordinal);

        await WaitForOutboxAsync(schema, id.Value, row => row is { Due: true });
        await host.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
        var received = await inbox.WaitForSingleAsync();
        Assert.Equal("1001", received.OrderId);
        Assert.Equal(7, received.Quantity);
        Assert.Equal(id.Value, received.MessageId);
        Assert.Equal([1, 2], inbox.Attempts);

        var stored = await WaitForOutboxAsync(schema, id.Value, row => row is { Delivered: true });
        Assert.Equal(2, stored.AttemptCount);
        Assert.Equal("1001", stored.OrderId);
    }

    [Fact]
    public async Task ExhaustedDelivery_PreservesThePayloadInTheDeadLetterTable()
    {
        var inbox = new Inbox { SucceedOnAttempt = int.MaxValue };
        var schema = NewSchema();
        using var host = await StartAsync(schema, publishOnly: false, inbox, options => options.MaxAttempts = 1);
        var id = await PublishAsync(host, new OrderPlaced("poison", 3));

        var deadLetter = await WaitForDeadLetterAsync(schema, id.Value);
        Assert.Equal("poison", deadLetter.OrderId);
        Assert.Equal(3, deadLetter.Quantity);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.Contains("gateway down", deadLetter.LastError, StringComparison.Ordinal);
        Assert.Null(await ReadOutboxAsync(schema, Table, id.Value));
        Assert.Equal([1], inbox.Attempts);
    }

    [Fact]
    public async Task PublishAsync_MessageCommittedWhileTheListenerIsDownIsDeliveredByRedelivery()
    {
        var schema = NewSchema();
        MessageId id;
        using (var publisher = await StartAsync(schema, publishOnly: true))
        {
            id = await PublishAsync(publisher, new OrderPlaced("1001", 5));
            await publisher.StopAsync();
        }

        var waiting = await ReadOutboxAsync(schema, Table, id.Value);
        Assert.NotNull(waiting);
        Assert.Equal(0, waiting.AttemptCount);
        Assert.False(waiting.Delivered);

        var inbox = new Inbox();
        using var consumer = await StartAsync(schema, publishOnly: false, inbox);
        await Task.Delay(250);
        Assert.Empty(inbox.Attempts);

        await consumer.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
        var received = await inbox.WaitForSingleAsync();
        Assert.Equal(id.Value, received.MessageId);
        Assert.Equal("1001", received.OrderId);
        Assert.Equal(5, received.Quantity);
        Assert.Equal(1, received.Attempt);
        var stored = await WaitForOutboxAsync(schema, id.Value, row => row is { Delivered: true });
        Assert.Equal(1, stored.AttemptCount);
    }

    private async Task<IHost> StartAsync(
        string schema,
        bool publishOnly,
        Inbox? inbox = null,
        Action<PostgresTransportOptions>? configure = null)
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

        dispatchly.UsePostgresTransport(options =>
        {
            options.ConnectionString = _fixture.ConnectionString;
            options.Schema = schema;
            options.Channel = schema;
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

    private static async Task<MessageId> PublishAsync(IHost host, OrderPlaced message)
    {
        var ids = await PublishAsync(host, [message]);
        return ids[0];
    }

    private static async Task<IReadOnlyList<MessageId>> PublishAsync(IHost host, params OrderPlaced[] messages)
    {
        var publisher = host.Services.GetRequiredService<IMessagePublisher>();
        var ids = new List<MessageId>(messages.Length);
        foreach (var message in messages)
        {
            ids.Add(await publisher.PublishAsync(message));
        }

        return ids;
    }

    private async Task<StoredMessage> WaitForOutboxAsync(string schema, Guid id, Func<StoredMessage?, bool> condition)
    {
        StoredMessage? row = null;
        await WaitUntilAsync(async () =>
        {
            row = await ReadOutboxAsync(schema, Table, id);
            return condition(row);
        });
        return row!;
    }

    private async Task<StoredMessage> WaitForDeadLetterAsync(string schema, Guid id)
    {
        StoredMessage? row = null;
        await WaitUntilAsync(async () =>
        {
            row = await ReadDeadLetterAsync(schema, id);
            return row is not null;
        });
        return row!;
    }

    private Task<StoredMessage?> ReadOutboxAsync(string schema, string table, Guid id) =>
        ReadMessageAsync(
            schema,
            table,
            id,
            """
            SELECT payload->>'orderId',
                   payload->>'quantity',
                   attempt_count,
                   delivered_at IS NOT NULL,
                   last_error,
                   locked_until IS NULL OR locked_until <= clock_timestamp()
            """);

    private Task<StoredMessage?> ReadDeadLetterAsync(string schema, Guid id) =>
        ReadMessageAsync(
            schema,
            Table + "_dead_letter",
            id,
            """
            SELECT payload->>'orderId',
                   payload->>'quantity',
                   attempt_count,
                   TRUE,
                   last_error,
                   TRUE
            """);

    private async Task<StoredMessage?> ReadMessageAsync(string schema, string table, Guid id, string select)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            {select}
            FROM {IdentifierRules.Quote(schema)}.{IdentifierRules.Quote(table)}
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new StoredMessage(
            reader.GetString(0),
            int.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt32(2),
            reader.GetBoolean(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5));
    }

    private async Task<int> CountAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {IdentifierRules.Quote(schema)}.{IdentifierRules.Quote(table)}",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> TableExistsAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = @schema AND table_name = @table)
            """,
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static string NewSchema() => "s" + Guid.NewGuid().ToString("N")[..12];

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
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
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _successes;

        public int SucceedOnAttempt { get; init; } = 1;

        public int ExpectedSuccesses { get; init; } = 1;

        public Task Completed => _completed.Task;

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

        public IReadOnlyList<int> Attempts => Received.Select(message => message.Attempt).ToArray();

        public async Task<Received> WaitForSingleAsync()
        {
            await Completed.WaitAsync(TimeSpan.FromSeconds(10));
            return Assert.Single(Received, message => message.Attempt >= SucceedOnAttempt);
        }

        public Task ReceiveAsync(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
        {
            int attemptsForMessage;
            lock (_lock)
            {
                _received.Add(new Received(message.OrderId, message.Quantity, context.Id.Value, context.Attempt, context.EnqueuedAt));
                attemptsForMessage = _received.Count(item => item.MessageId == context.Id.Value);
            }

            if (attemptsForMessage < SucceedOnAttempt)
            {
                throw new InvalidOperationException("gateway down");
            }

            bool done;
            lock (_lock)
            {
                _successes++;
                done = _successes == ExpectedSuccesses;
            }

            if (done)
            {
                _completed.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    public sealed record Received(string OrderId, int Quantity, Guid MessageId, int Attempt, DateTimeOffset EnqueuedAt);

    private sealed record StoredMessage(
        string OrderId,
        int Quantity,
        int AttemptCount,
        bool Delivered,
        string? LastError,
        bool Due);
}
