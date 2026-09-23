using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dispatchly.Transport.InMemory.Tests;

public class InMemoryTransportTests
{
    [Fact]
    public async Task PublishAsync_DeliversAfterEnqueue()
    {
        var probe = new CountingProbe { SucceedOnAttempt = 1 };
        using var host = await StartAsync(probe, options => options.RetryDelay = TimeSpan.Zero);
        await PublishAsync(host, new MemoryPing("one"));
        await probe.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([1], probe.Attempts);
    }

    [Fact]
    public async Task PublishAsync_RetriesThenDeadLetters()
    {
        var probe = new CountingProbe { SucceedOnAttempt = int.MaxValue };
        using var host = await StartAsync(probe, options =>
        {
            options.MaxAttempts = 2;
            options.RetryDelay = TimeSpan.FromMilliseconds(10);
        });
        await PublishAsync(host, new MemoryPing("poison"));
        await WaitUntilAsync(() => probe.Attempts.Count >= 2, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => host.Services.GetRequiredService<IInMemoryDeadLetterStore>().Snapshot().Count == 1,
            TimeSpan.FromSeconds(5));
        var deadLetter = Assert.Single(host.Services.GetRequiredService<IInMemoryDeadLetterStore>().Snapshot());
        Assert.Equal(2, deadLetter.AttemptCount);
        Assert.Contains("not yet", deadLetter.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAsync_WithoutAHandlerThrows()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDispatchly()
            .AddMessage<MemoryPing>()
            .UseInMemoryTransport();
        using var host = builder.Build();
        await host.StartAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Services.GetRequiredService<IMessagePublisher>().PublishAsync(new MemoryPing("x")).AsTask());
        Assert.Contains("no handler", exception.Message, StringComparison.OrdinalIgnoreCase);
        await host.StopAsync();
    }

    [Fact]
    public async Task Idempotency_SecondDispatchOfTheSameIdDoesNotRunTheHandler()
    {
        var probe = new CountingProbe { SucceedOnAttempt = 1 };
        using var host = await StartAsync(probe, options => options.RetryDelay = TimeSpan.Zero, idempotency: true);
        var dispatcher = host.Services.GetRequiredService<IMessageDispatcher>();
        var context = new MessageContext(MessageId.New(), 1, DateTimeOffset.UtcNow);

        await dispatcher.DispatchAsync(typeof(MemoryPing), """{"name":"one"}""", context, CancellationToken.None);
        await dispatcher.DispatchAsync(typeof(MemoryPing), """{"name":"one"}""", context, CancellationToken.None);

        Assert.Equal([1], probe.Attempts);
    }

    [Fact]
    public async Task Idempotency_AFailedDeliveryCanBeRetried()
    {
        var probe = new CountingProbe { SucceedOnAttempt = 2 };
        using var host = await StartAsync(probe, options => options.RetryDelay = TimeSpan.Zero, idempotency: true);
        var dispatcher = host.Services.GetRequiredService<IMessageDispatcher>();
        var context = new MessageContext(MessageId.New(), 1, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(typeof(MemoryPing), """{"name":"one"}""", context, CancellationToken.None));
        await dispatcher.DispatchAsync(typeof(MemoryPing), """{"name":"one"}""", context, CancellationToken.None);

        Assert.Equal([1, 1], probe.Attempts);
    }

    [Fact]
    public void UseInMemoryIdempotency_RequiresTheTransport()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddDispatchly().UseInMemoryIdempotency());
        Assert.Contains("UseInMemoryTransport", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseInMemoryTransport_RejectsASecondTransport()
    {
        var services = new ServiceCollection();
        var builder = services.AddDispatchly().AddHandler<MemoryPing, MemoryPingHandler>();
        builder.UseInMemoryTransport();
        var exception = Assert.Throws<InvalidOperationException>(() => builder.UseInMemoryTransport());
        Assert.Contains("already registered", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<IHost> StartAsync(
        CountingProbe probe,
        Action<InMemoryTransportOptions> configure,
        bool idempotency = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(probe);
        var dispatchly = builder.Services.AddDispatchly()
            .AddHandler<MemoryPing, MemoryPingHandler>()
            .UseInMemoryTransport(configure);
        if (idempotency)
        {
            dispatchly.UseInMemoryIdempotency();
        }

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static Task PublishAsync(IHost host, MemoryPing message) =>
        host.Services.GetRequiredService<IMessagePublisher>().PublishAsync(message).AsTask();

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(15);
        }
    }

    public sealed record MemoryPing(string Name);

    public sealed class MemoryPingHandler : IMessageHandler<MemoryPing>
    {
        private readonly CountingProbe _probe;

        public MemoryPingHandler(CountingProbe probe) => _probe = probe;

        public Task HandleAsync(MemoryPing message, MessageContext context, CancellationToken cancellationToken) =>
            _probe.ReceiveAsync(context);
    }

    public sealed class CountingProbe
    {
        private readonly List<int> _attempts = [];
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<int> Attempts => _attempts;

        public int SucceedOnAttempt { get; set; } = 1;

        public Task Completed => _completed.Task;

        public Task ReceiveAsync(MessageContext context)
        {
            _attempts.Add(context.Attempt);
            if (_attempts.Count < SucceedOnAttempt)
            {
                throw new InvalidOperationException("not yet");
            }

            _completed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
