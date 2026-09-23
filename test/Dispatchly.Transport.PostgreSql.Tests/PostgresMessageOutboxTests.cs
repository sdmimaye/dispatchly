using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatchly.Transport.PostgreSql.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMessageOutboxTests
{
    private const string Table = "placed_orders";

    private readonly PostgresFixture _fixture;

    public PostgresMessageOutboxTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EnlistAsync_RollbackLeavesNoRow()
    {
        var schema = NewSchema();
        using var host = await StartAsync(schema, new Inbox());
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await host.Services.GetRequiredService<IMessageOutbox>()
            .EnlistAsync(new OrderPlaced("1001", 2), transaction);

        await transaction.RollbackAsync();

        Assert.Equal(0, await CountAsync(schema, id.Value));
    }

    [Fact]
    public async Task EnlistAsync_CommitDeliversTheMessage()
    {
        var inbox = new Inbox();
        var schema = NewSchema();
        using var host = await StartAsync(schema, inbox);
        MessageId id;
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            id = await host.Services.GetRequiredService<IMessageOutbox>()
                .EnlistAsync(new OrderPlaced("1001", 2), transaction);
            await transaction.CommitAsync();
        }

        var received = await inbox.WaitAsync();
        Assert.Equal(id.Value, received.MessageId);
        Assert.Equal("1001", received.OrderId);
        Assert.Equal(2, received.Quantity);
        Assert.Equal(1, await CountAsync(schema, id.Value));
    }

    [Fact]
    public async Task EnlistAsync_RejectsANonNpgsqlTransaction()
    {
        var schema = NewSchema();
        using var host = await StartAsync(schema, new Inbox());
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Services.GetRequiredService<IMessageOutbox>()
                .EnlistAsync(new OrderPlaced("1001", 1), new UnrelatedTransaction(), CancellationToken.None)
                .AsTask());
        Assert.Contains("NpgsqlTransaction", exception.Message, StringComparison.Ordinal);
    }

    private async Task<IHost> StartAsync(string schema, Inbox inbox)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(inbox);
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
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private async Task<int> CountAsync(string schema, Guid id)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT COUNT(*)
            FROM {IdentifierRules.Quote(schema)}.{IdentifierRules.Quote(Table)}
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static string NewSchema() => "s" + Guid.NewGuid().ToString("N")[..12];

    [DispatchlyTable(Table)]
    public sealed record OrderPlaced(string OrderId, int Quantity);

    public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
    {
        private readonly Inbox _inbox;

        public OrderPlacedHandler(Inbox inbox) => _inbox = inbox;

        public Task HandleAsync(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
        {
            _inbox.Receive(message, context);
            return Task.CompletedTask;
        }
    }

    public sealed class Inbox
    {
        private readonly TaskCompletionSource<Received> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Receive(OrderPlaced message, MessageContext context) =>
            _received.TrySetResult(new Received(message.OrderId, message.Quantity, context.Id.Value));

        public async Task<Received> WaitAsync() =>
            await _received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public sealed record Received(string OrderId, int Quantity, Guid MessageId);

    private sealed class UnrelatedTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;

        protected override DbConnection? DbConnection => null;

        public override void Commit()
        {
        }

        public override void Rollback()
        {
        }
    }
}
