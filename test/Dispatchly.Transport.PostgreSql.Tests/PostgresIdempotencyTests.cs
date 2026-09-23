using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatchly.Transport.PostgreSql.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresIdempotencyTests
{
    private readonly PostgresFixture _fixture;

    public PostgresIdempotencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Redelivery_SkipsAMessageWhoseWritesAlreadyCommitted()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 1 };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe);
        await ExecuteAsync(schema, "CREATE TABLE {0}.effect (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, message_id uuid NOT NULL)");
        await PublishAsync(host, new CreditRequested("1001"));
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => CountAsync(schema, IdentifierRules.IdempotencyInboxTable, "TRUE"));

        Assert.Equal([1], probe.Attempts);
        Assert.Equal(1, await CountAsync(schema, "effect", "TRUE"));
        await WaitUntilAsync(() => CountAsync(schema, "credit_requested", "attempt_count = 1 AND delivered_at IS NOT NULL"));

        await ExecuteAsync(schema, "UPDATE {0}.credit_requested SET delivered_at = NULL, locked_until = NULL");
        await host.Services.GetRequiredService<IPostgresMaintenance>().RedeliverAsync();
        await WaitUntilAsync(() => CountAsync(schema, "credit_requested", "attempt_count = 2 AND delivered_at IS NOT NULL"));

        Assert.Equal([1], probe.Attempts);
        Assert.Equal(1, await CountAsync(schema, "effect", "TRUE"));
        Assert.Equal(1, await CountAsync(schema, IdentifierRules.IdempotencyInboxTable, "TRUE"));
    }

    [Fact]
    public async Task HandlerFailure_RollsBackTheSideEffectAndTheInboxRow()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = int.MaxValue };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe, options => options.MaxAttempts = 1);
        await ExecuteAsync(schema, "CREATE TABLE {0}.effect (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, message_id uuid NOT NULL)");
        await PublishAsync(host, new CreditRequested("1001"));
        await WaitUntilAsync(() => Task.FromResult(probe.Attempts.Count >= 1));
        await WaitUntilAsync(() => CountAsync(schema, "credit_requested_dead_letter", "TRUE"));

        Assert.Equal([1], probe.Attempts);
        Assert.Equal(0, await CountAsync(schema, "effect", "TRUE"));
        Assert.Equal(0, await CountAsync(schema, IdentifierRules.IdempotencyInboxTable, "TRUE"));
    }

    [Fact]
    public void UsePostgresIdempotency_RequiresTheTransport()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddDispatchly().UsePostgresIdempotency());
        Assert.Contains("UsePostgresTransport", exception.Message, StringComparison.Ordinal);
    }

    private async Task<IHost> StartAsync(
        string schema,
        DeliveryProbe probe,
        Action<PostgresTransportOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(probe);
        builder.Services.AddSingleton(new EffectSchema(schema));
        builder.Services.AddDispatchly()
            .AddHandler<CreditRequested, CreditHandler>()
            .UsePostgresTransport(options =>
            {
                options.ConnectionString = _fixture.ConnectionString;
                options.Schema = schema;
                options.Channel = schema;
                options.ScheduleRedelivery = false;
                options.CronJobName = schema;
                configure?.Invoke(options);
            })
            .UsePostgresIdempotency();
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static Task PublishAsync(IHost host, CreditRequested message) =>
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

    private static async Task WaitUntilAsync(Func<Task<int>> query)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (await query() != 1)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(20);
        }
    }

    public sealed record CreditRequested(string Account);

    public sealed record EffectSchema(string Name);

    public sealed class CreditHandler : IMessageHandler<CreditRequested>
    {
        private readonly DeliveryProbe _probe;
        private readonly string _schema;

        public CreditHandler(DeliveryProbe probe, EffectSchema schema)
        {
            _probe = probe;
            _schema = schema.Name;
        }

        public async Task HandleAsync(CreditRequested message, MessageContext context, CancellationToken cancellationToken)
        {
            var transaction = context.GetRequiredFeature<DbTransaction>();
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {IdentifierRules.Quote(_schema)}.effect (message_id) VALUES (@id)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = context.Id.Value;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await _probe.ReceiveAsync(context);
        }
    }

    public sealed class DeliveryProbe
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

        public Task Completed => _completed.Task;

        public Task ReceiveAsync(MessageContext context)
        {
            int count;
            lock (_lock)
            {
                _attempts.Add(context.Attempt);
                count = _attempts.Count;
            }

            if (count < SucceedOnAttempt)
            {
                throw new InvalidOperationException("not yet");
            }

            _completed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
