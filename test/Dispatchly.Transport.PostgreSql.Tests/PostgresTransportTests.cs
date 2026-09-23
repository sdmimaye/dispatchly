using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatchly.Transport.PostgreSql.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresTransportTests
{
    private readonly PostgresFixture _fixture;

    public PostgresTransportTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PublishAsync_CommittedMessageIsDeliveredAndAcknowledged()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 1 };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe);
        await PublishAsync(host, new ProbeMessage { Text = "hello" });
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1], probe.Attempts);
        Assert.Equal(1, await CountAsync(schema, "probe_message", "delivered_at IS NOT NULL"));
    }

    [Fact]
    public async Task FailedDelivery_IsRetriedThenAcknowledged()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 2 };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe, options =>
        {
            options.MaxAttempts = 3;
            options.MaxBackoff = TimeSpan.FromMilliseconds(50);
            options.VisibilityTimeout = TimeSpan.FromMilliseconds(50);
        });
        await PublishAsync(host, new ProbeMessage { Text = "retry" });
        await WaitUntilAsync(() => probe.Attempts.Count >= 1);
        await Task.Delay(80);
        await host.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1, 2], probe.Attempts);
        Assert.Equal(1, await CountAsync(schema, "probe_message", "delivered_at IS NOT NULL"));
    }

    [Fact]
    public async Task ExhaustedDelivery_MovesTheRowToTheDeadLetterTable()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = int.MaxValue };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe, options =>
        {
            options.MaxAttempts = 2;
            options.MaxBackoff = TimeSpan.FromMilliseconds(30);
            options.VisibilityTimeout = TimeSpan.FromMilliseconds(30);
        });
        await PublishAsync(host, new ProbeMessage { Text = "poison" });
        await WaitUntilAsync(() => probe.Attempts.Count >= 1);
        await Task.Delay(60);
        await host.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
        await WaitUntilAsync(() => probe.Attempts.Count >= 2);
        await WaitUntilAsync(async () => await CountAsync(schema, "probe_message_dead_letter", "TRUE") == 1);
        Assert.Equal(0, await CountAsync(schema, "probe_message", "TRUE"));
    }

    [Fact]
    public async Task ExpiredLock_CanBeClaimedAgain()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 1 };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Gate = release.Task;
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe, options => options.VisibilityTimeout = TimeSpan.FromMinutes(5));
        await PublishAsync(host, new ProbeMessage { Text = "lock" });
        await WaitUntilAsync(() => probe.Attempts.Count >= 1);
        await ExecuteAsync(schema, "UPDATE {0}.probe_message SET locked_until = clock_timestamp() - interval '1 minute' WHERE delivered_at IS NULL");
        await host.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
        await WaitUntilAsync(() => probe.Attempts.Count >= 2);
        release.TrySetResult();
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1, 2], probe.Attempts);
        Assert.Equal(1, await CountAsync(schema, "probe_message", "delivered_at IS NOT NULL"));
    }

    [Fact]
    public async Task ActiveLock_IsNotRedelivered()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 1 };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Gate = release.Task;
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe, options => options.VisibilityTimeout = TimeSpan.FromMinutes(5));
        await PublishAsync(host, new ProbeMessage { Text = "held" });
        await WaitUntilAsync(() => probe.Attempts.Count >= 1);
        await host.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
        await Task.Delay(250);
        Assert.Equal([1], probe.Attempts);
        release.TrySetResult();
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TwoMessageTypes_CreateFourTables()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 1 };
        var invoices = new InvoiceProbe { SucceedOnAttempt = 1 };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe, configureServices: services =>
        {
            services.AddSingleton(invoices);
            services.AddDispatchly().AddHandler<InvoiceIssued, InvoiceIssuedHandler>();
        });
        await PublishAsync(host, new ProbeMessage { Text = "a" });
        await PublishAsync(host, new InvoiceIssued("inv-1"));
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        await invoices.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        var tables = await TableNamesAsync(schema);
        Assert.Contains("probe_message", tables);
        Assert.Contains("probe_message_dead_letter", tables);
        Assert.Contains("invoice_issued", tables);
        Assert.Contains("invoice_issued_dead_letter", tables);
        Assert.Contains("outbox_table", tables);
    }

    [Fact]
    public async Task CustomSchema_IsUsedForOutboxTables()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 1 };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe);
        await PublishAsync(host, new ProbeMessage { Text = "schema" });
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("probe_message", await TableNamesAsync(schema));
        Assert.DoesNotContain("probe_message", await TableNamesAsync("dispatchly"));
    }

    [Fact]
    public async Task PublishOnlyHost_LeavesDeliveryToAnotherHost()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 1 };
        var schema = NewSchema();
        using var publisher = await StartAsync(schema, probe, publishOnly: true);
        using var consumer = await StartAsync(schema, probe);
        await PublishAsync(publisher, new ProbeMessage { Text = "shared" });
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1], probe.Attempts);
        Assert.Equal(1, await CountAsync(schema, "probe_message", "delivered_at IS NOT NULL"));
    }

    [Fact]
    public async Task ScheduleRedelivery_WithoutPgCronThrows()
    {
        var probe = new DeliveryProbe();
        var schema = NewSchema();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(schema, probe, options =>
        {
            options.ScheduleRedelivery = true;
        }));
        Assert.Contains("pg_cron", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IHost> StartAsync(
        string schema,
        DeliveryProbe probe,
        Action<PostgresTransportOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        bool publishOnly = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Services.AddSingleton(probe);
        configureServices?.Invoke(builder.Services);
        var dispatchly = builder.Services.AddDispatchly();
        if (publishOnly)
        {
            dispatchly.AddMessage<ProbeMessage>();
        }
        else
        {
            dispatchly.AddHandler<ProbeMessage, ProbeMessageHandler>();
        }

        dispatchly.UsePostgresTransport(options =>
            {
                options.ConnectionString = _fixture.ConnectionString;
                options.Schema = schema;
                options.Channel = schema;
                options.ScheduleRedelivery = false;
                options.CronJobName = schema;
                options.MaxBackoff = TimeSpan.FromMilliseconds(50);
                configure?.Invoke(options);
            });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static Task PublishAsync<TMessage>(IHost host, TMessage message) =>
        host.Services.GetRequiredService<IMessagePublisher>().PublishAsync(message).AsTask();

    private async Task<int> CountAsync(string schema, string table, string where)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {IdentifierRules.Quote(schema)}.{IdentifierRules.Quote(table)} WHERE {where}",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string schema, string sqlFormat)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, sqlFormat, IdentifierRules.Quote(schema));
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> TableNamesAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string NewSchema() => "s" + Guid.NewGuid().ToString("N")[..12];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(20);
        }
    }

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

    public sealed class ProbeMessage
    {
        public string Text { get; set; } = "";
    }

    public sealed record InvoiceIssued(string Number);

    public sealed class ProbeMessageHandler : IMessageHandler<ProbeMessage>
    {
        private readonly DeliveryProbe _probe;

        public ProbeMessageHandler(DeliveryProbe probe) => _probe = probe;

        public Task HandleAsync(ProbeMessage message, MessageContext context, CancellationToken cancellationToken) =>
            _probe.ReceiveAsync(context, cancellationToken);
    }

    public sealed class InvoiceProbe : DeliveryProbe;

    public sealed class InvoiceIssuedHandler : IMessageHandler<InvoiceIssued>
    {
        private readonly InvoiceProbe _probe;

        public InvoiceIssuedHandler(InvoiceProbe probe) => _probe = probe;

        public Task HandleAsync(InvoiceIssued message, MessageContext context, CancellationToken cancellationToken) =>
            _probe.ReceiveAsync(context, cancellationToken);
    }

    public class DeliveryProbe
    {
        private readonly Lock _lock = new();
        private readonly List<int> _attempts = [];
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<int> Attempts
        {
            get
            {
                lock (_lock)
                {
                    return _attempts.ToArray();
                }
            }
        }

        public int SucceedOnAttempt { get; set; } = 1;

        public Task Gate { get; set; } = Task.CompletedTask;

        public Task Completed => _completed.Task;

        public async Task ReceiveAsync(MessageContext context, CancellationToken cancellationToken)
        {
            int count;
            lock (_lock)
            {
                _attempts.Add(context.Attempt);
                count = _attempts.Count;
            }

            await Gate.WaitAsync(cancellationToken);
            if (count < SucceedOnAttempt)
            {
                throw new InvalidOperationException("not yet");
            }

            _completed.TrySetResult();
        }
    }
}
