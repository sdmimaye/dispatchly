using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.DependencyInjection.Tests;

public class DispatchlyBuilderTests
{
    [Fact]
    public async Task AddHandler_RegistersHandlerAndTable()
    {
        var services = new ServiceCollection();
        services.AddDispatchly().AddHandler<ManualPing, ManualPingHandler>(ManualPingContext.Default.ManualPing, "manual_ping");
        await using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<IMessageTypeCatalog>();
        var registration = catalog.GetRequired(typeof(ManualPing));
        Assert.Equal("manual_ping", registration.TableName);
        Assert.Contains("\"name\":\"ada\"", registration.Serialize(new ManualPing("ada")), StringComparison.Ordinal);

        await using var scope = provider.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IMessageHandler<ManualPing>>().ToArray();
        Assert.Single(handlers);
        await handlers[0].HandleAsync(
            new ManualPing("ada"),
            new MessageContext(MessageId.New(), 1, DateTimeOffset.UtcNow),
            CancellationToken.None);
    }

    [Fact]
    public void AddMessage_RegistersSerializationWithoutAHandler()
    {
        var services = new ServiceCollection();
        services.AddDispatchly().AddMessage<ManualPing>(ManualPingContext.Default.ManualPing, "manual_ping");
        using var provider = services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IMessageTypeCatalog>().GetRequired(typeof(ManualPing));
        Assert.Equal("manual_ping", registration.TableName);
        Assert.False(registration.HasHandlers);
        Assert.Empty(provider.GetServices<IMessageHandler<ManualPing>>());
    }

    [Fact]
    public void AddHandler_AfterAddMessage_AddsTheHandler()
    {
        var services = new ServiceCollection();
        services.AddDispatchly()
            .AddMessage<ManualPing>(ManualPingContext.Default.ManualPing, "manual_ping")
            .AddHandler<ManualPing, ManualPingHandler>(ManualPingContext.Default.ManualPing, "manual_ping");
        using var provider = services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IMessageTypeCatalog>().GetRequired(typeof(ManualPing));
        Assert.True(registration.HasHandlers);
        using var scope = provider.CreateScope();
        Assert.Single(scope.ServiceProvider.GetServices<IMessageHandler<ManualPing>>());
    }

    [Fact]
    public void AddHandler_ReflectionOverloadReadsTableAttribute()
    {
        var services = new ServiceCollection();
        services.AddDispatchly().AddHandler<AttributedPing, AttributedPingHandler>();
        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<IMessageTypeCatalog>().GetRequired(typeof(AttributedPing));
        Assert.Equal("named_ping", registration.TableName);
    }

    [Fact]
    public void AddDispatchly_IsIdempotent()
    {
        var services = new ServiceCollection();
        var first = services.AddDispatchly();
        var second = services.AddDispatchly();
        Assert.Same(first, second);
    }

    [Fact]
    public void AddHandler_RejectsDuplicateTable()
    {
        var services = new ServiceCollection();
        var builder = services.AddDispatchly();
        builder.AddHandler<ManualPing, ManualPingHandler>(ManualPingContext.Default.ManualPing, "shared_table");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddHandler<OtherPing, OtherPingHandler>(OtherPingContext.Default.OtherPing, "shared_table"));
        Assert.Contains("shared_table", exception.Message, StringComparison.Ordinal);
    }

    public sealed record ManualPing(string Name);

    public sealed class ManualPingHandler : IMessageHandler<ManualPing>
    {
        public Task HandleAsync(ManualPing message, MessageContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    [DispatchlyTable("named_ping")]
    public sealed record AttributedPing(string Name);

    public sealed class AttributedPingHandler : IMessageHandler<AttributedPing>
    {
        public Task HandleAsync(AttributedPing message, MessageContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    public sealed record OtherPing(string Name);

    public sealed class OtherPingHandler : IMessageHandler<OtherPing>
    {
        public Task HandleAsync(OtherPing message, MessageContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dispatchly.DependencyInjection.Tests.DispatchlyBuilderTests.ManualPing))]
public sealed partial class ManualPingContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dispatchly.DependencyInjection.Tests.DispatchlyBuilderTests.OtherPing))]
public sealed partial class OtherPingContext : JsonSerializerContext;
