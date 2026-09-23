using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.DependencyInjection.Tests;

public class MessageBehaviorTests
{
    [Fact]
    public async Task UseBehavior_RunsTheFirstRegistrationOutsideTheLaterOnes()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddDispatchly()
            .AddHandler<BehaviorPing, BehaviorPingHandler>(BehaviorPingContext.Default.BehaviorPing, "behavior_ping")
            .UseBehavior<OuterBehavior>()
            .UseBehavior<InnerBehavior>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMessageDispatcher>();

        await dispatcher.DispatchAsync(
            typeof(BehaviorPing),
            """{"name":"ada"}""",
            new MessageContext(MessageId.New(), 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(["outer-before", "inner-before", "handler", "inner-after", "outer-after"], log);
    }

    [Fact]
    public void UseBehavior_RejectsTheSameBehaviorTwice()
    {
        var services = new ServiceCollection();
        var builder = services.AddDispatchly().UseBehavior<OuterBehavior>();
        var exception = Assert.Throws<InvalidOperationException>(() => builder.UseBehavior<OuterBehavior>());
        Assert.Contains("OuterBehavior", exception.Message, StringComparison.Ordinal);
    }

    public sealed record BehaviorPing(string Name);

    public sealed class BehaviorPingHandler(List<string> log) : IMessageHandler<BehaviorPing>
    {
        public Task HandleAsync(BehaviorPing message, MessageContext context, CancellationToken cancellationToken)
        {
            log.Add("handler");
            return Task.CompletedTask;
        }
    }

    public sealed class OuterBehavior(List<string> log) : IMessageBehavior
    {
        public async Task InvokeAsync(MessageEnvelope envelope, MessagePipelineDelegate next, CancellationToken cancellationToken)
        {
            log.Add("outer-before");
            await next(envelope, cancellationToken);
            log.Add("outer-after");
        }
    }

    public sealed class InnerBehavior(List<string> log) : IMessageBehavior
    {
        public async Task InvokeAsync(MessageEnvelope envelope, MessagePipelineDelegate next, CancellationToken cancellationToken)
        {
            log.Add("inner-before");
            await next(envelope, cancellationToken);
            log.Add("inner-after");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dispatchly.DependencyInjection.Tests.MessageBehaviorTests.BehaviorPing))]
public sealed partial class BehaviorPingContext : JsonSerializerContext;
