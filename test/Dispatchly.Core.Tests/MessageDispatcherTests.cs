using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.Core.Tests;

public class MessageDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_CreatesAScopePerDelivery()
    {
        var seen = new List<Guid>();
        var catalog = new MessageTypeCatalog();
        catalog.GetOrAdd(
            typeof(Ping),
            "ping",
            static message => """{"name":"ok"}""",
            static _ => new Ping("ok"),
            (provider, _, _, _) =>
            {
                seen.Add(provider.GetRequiredService<ScopeMarker>().Id);
                return Task.CompletedTask;
            },
            typeof(PingHandler));

        var services = new ServiceCollection();
        services.AddSingleton<IMessageTypeCatalog>(catalog);
        services.AddSingleton(new MessagePipeline([]));
        services.AddSingleton<IMessageDispatcher, MessageDispatcher>();
        services.AddScoped<ScopeMarker>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMessageDispatcher>();
        var context = new MessageContext(MessageId.New(), 1, DateTimeOffset.UtcNow);

        await dispatcher.DispatchAsync(typeof(Ping), """{"name":"ok"}""", context, CancellationToken.None);
        await dispatcher.DispatchAsync(typeof(Ping), """{"name":"ok"}""", context, CancellationToken.None);

        Assert.Equal(2, seen.Distinct().Count());
    }

    [Fact]
    public async Task DispatchAsync_UnknownMessageThrows()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMessageTypeCatalog>(new MessageTypeCatalog());
        services.AddSingleton(new MessagePipeline([]));
        services.AddSingleton<IMessageDispatcher, MessageDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMessageDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(
            typeof(Ping),
            "{}",
            new MessageContext(MessageId.New(), 1, DateTimeOffset.UtcNow),
            CancellationToken.None));
    }

    [Fact]
    public void MessageContext_RejectsNonPositiveAttempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageContext(MessageId.New(), 0, DateTimeOffset.UtcNow));
    }

    private sealed record Ping(string Name);

    private sealed class PingHandler;

    private sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }
}
