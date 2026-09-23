using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatchly.Transport.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerIdempotencyTests
{
    private readonly SqlServerFixture _fixture;

    public SqlServerIdempotencyTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Redelivery_SkipsAMessageWhoseWritesAlreadyCommitted()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = 1 };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe);
        await ExecuteAsync(schema, "CREATE TABLE {0}.effect (id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, message_id uniqueidentifier NOT NULL)");
        await PublishAsync(host, new CreditRequested("1001"));
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => CountAsync(schema, IdentifierRules.IdempotencyInboxTable, "1 = 1"));

        Assert.Equal([1], probe.Attempts);
        Assert.Equal(1, await CountAsync(schema, "effect", "1 = 1"));
        await WaitUntilAsync(() => CountAsync(schema, "credit_requested", "attempt_count = 1 AND delivered_at IS NOT NULL"));

        await ExecuteAsync(schema, "UPDATE {0}.credit_requested SET delivered_at = NULL, locked_until = NULL");
        await host.Services.GetRequiredService<ISqlServerMaintenance>().RedeliverAsync();
        await WaitUntilAsync(() => CountAsync(schema, "credit_requested", "attempt_count = 2 AND delivered_at IS NOT NULL"));

        Assert.Equal([1], probe.Attempts);
        Assert.Equal(1, await CountAsync(schema, "effect", "1 = 1"));
        Assert.Equal(1, await CountAsync(schema, IdentifierRules.IdempotencyInboxTable, "1 = 1"));
    }

    [Fact]
    public async Task HandlerFailure_RollsBackTheSideEffectAndTheInboxRow()
    {
        var probe = new DeliveryProbe { SucceedOnAttempt = int.MaxValue };
        var schema = NewSchema();
        using var host = await StartAsync(schema, probe, options => options.MaxAttempts = 1);
        await ExecuteAsync(schema, "CREATE TABLE {0}.effect (id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, message_id uniqueidentifier NOT NULL)");
        await PublishAsync(host, new CreditRequested("1001"));
        await WaitUntilAsync(() => Task.FromResult(probe.Attempts.Count >= 1));
        await WaitUntilAsync(() => CountAsync(schema, "credit_requested_dead_letter", "1 = 1"));

        Assert.Equal([1], probe.Attempts);
        Assert.Equal(0, await CountAsync(schema, "effect", "1 = 1"));
        Assert.Equal(0, await CountAsync(schema, IdentifierRules.IdempotencyInboxTable, "1 = 1"));
    }

    [Fact]
    public void UseSqlServerIdempotency_RequiresTheTransport()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddDispatchly().UseSqlServerIdempotency());
        Assert.Contains("UseSqlServerTransport", exception.Message, StringComparison.Ordinal);
    }

    private async Task<IHost> StartAsync(
        string schema,
        DeliveryProbe probe,
        Action<SqlServerTransportOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(probe);
        builder.Services.AddSingleton(new EffectSchema(schema));
        builder.Services.AddDispatchly()
            .AddHandler<CreditRequested, CreditHandler>()
            .UseSqlServerTransport(options =>
            {
                options.ConnectionString = _fixture.ConnectionString;
                options.Schema = schema;
                options.ScheduleRedelivery = false;
                options.CronJobName = schema;
                configure?.Invoke(options);
            })
            .UseSqlServerIdempotency();
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static Task PublishAsync(IHost host, CreditRequested message) =>
        host.Services.GetRequiredService<IMessagePublisher>().PublishAsync(message).AsTask();

    private async Task<int> CountAsync(string schema, string table, string where)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM {Quote(schema)}.{Quote(table)} WHERE {where}",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string schema, string sqlFormat)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, sqlFormat, Quote(schema));
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string NewSchema() => "s" + Guid.NewGuid().ToString("N")[..12];

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
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
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
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
            command.CommandText = $"INSERT INTO {Quote(_schema)}.effect (message_id) VALUES (@id)";
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
