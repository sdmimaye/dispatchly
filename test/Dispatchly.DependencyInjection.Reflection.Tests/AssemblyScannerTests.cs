using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.DependencyInjection.Reflection.Tests;

public class AssemblyScannerTests
{
    [Fact]
    public void AddHandlersFromAssemblies_RegistersPublicHandlers()
    {
        var services = new ServiceCollection();
        services.AddDispatchly().AddHandlersFromAssemblies(typeof(AssemblyScannerTests).Assembly);
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IMessageTypeCatalog>();

        Assert.True(catalog.TryGet(typeof(ScannedPing), out var registration));
        Assert.Equal("scanned_ping", registration.TableName);
        Assert.False(catalog.TryGet(typeof(IgnoredPing), out _));
    }

    public sealed record ScannedPing(string Name);

    public sealed class ScannedPingHandler : IMessageHandler<ScannedPing>
    {
        public Task HandleAsync(ScannedPing message, MessageContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    public sealed record IgnoredPing(string Name);

    internal sealed class IgnoredPingHandler : IMessageHandler<IgnoredPing>
    {
        public Task HandleAsync(IgnoredPing message, MessageContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    public abstract class AbstractPingHandler : IMessageHandler<ScannedPing>
    {
        public abstract Task HandleAsync(ScannedPing message, MessageContext context, CancellationToken cancellationToken);
    }
}
