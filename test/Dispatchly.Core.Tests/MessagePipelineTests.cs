using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.Core.Tests;

public class MessagePipelineTests
{
    [Fact]
    public async Task InvokeAsync_RunsTheFirstBehaviorOutsideTheLaterOnes()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<OuterBehavior>();
        services.AddScoped<InnerBehavior>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var pipeline = new MessagePipeline(
        [
            static current => current.GetRequiredService<OuterBehavior>(),
            static current => current.GetRequiredService<InnerBehavior>(),
        ]);
        var envelope = Envelope(scope.ServiceProvider);

        await pipeline.InvokeAsync(
            scope.ServiceProvider,
            envelope,
            (_, _) =>
            {
                log.Add("handler");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["outer-before", "inner-before", "handler", "inner-after", "outer-after"], log);
    }

    [Fact]
    public async Task InvokeAsync_ABehaviorCanStopThePipeline()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<StoppingBehavior>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var pipeline = new MessagePipeline([static current => current.GetRequiredService<StoppingBehavior>()]);

        await pipeline.InvokeAsync(
            scope.ServiceProvider,
            Envelope(scope.ServiceProvider),
            (_, _) =>
            {
                log.Add("handler");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["stopped"], log);
    }

    [Fact]
    public async Task IdempotencyBehavior_SkipsACompletedMessageAndRetriesAfterAFailure()
    {
        var store = new RecordingStore();
        var behavior = new IdempotencyBehavior(store);
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var completed = Envelope(provider);
        var failed = Envelope(provider);
        var calls = 0;

        await behavior.InvokeAsync(completed, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None);
        await behavior.InvokeAsync(completed, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(1, store.Commits);

        await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.InvokeAsync(failed, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("not yet");
        }, CancellationToken.None));
        await behavior.InvokeAsync(failed, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal(2, store.Commits);
    }

    private static MessageEnvelope Envelope(IServiceProvider services) =>
        new(new object(), typeof(object), new MessageContext(MessageId.New(), 1, DateTimeOffset.UtcNow), services);

    private sealed class OuterBehavior(List<string> log) : IMessageBehavior
    {
        public async Task InvokeAsync(MessageEnvelope envelope, MessagePipelineDelegate next, CancellationToken cancellationToken)
        {
            log.Add("outer-before");
            await next(envelope, cancellationToken);
            log.Add("outer-after");
        }
    }

    private sealed class InnerBehavior(List<string> log) : IMessageBehavior
    {
        public async Task InvokeAsync(MessageEnvelope envelope, MessagePipelineDelegate next, CancellationToken cancellationToken)
        {
            log.Add("inner-before");
            await next(envelope, cancellationToken);
            log.Add("inner-after");
        }
    }

    private sealed class StoppingBehavior(List<string> log) : IMessageBehavior
    {
        public Task InvokeAsync(MessageEnvelope envelope, MessagePipelineDelegate next, CancellationToken cancellationToken)
        {
            log.Add("stopped");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStore : IIdempotencyStore
    {
        private readonly HashSet<Guid> _completed = [];

        public int Commits { get; private set; }

        public Task<IIdempotencyLease?> TryLeaseAsync(MessageId id, CancellationToken cancellationToken)
        {
            if (_completed.Contains(id.Value))
            {
                return Task.FromResult<IIdempotencyLease?>(null);
            }

            return Task.FromResult<IIdempotencyLease?>(new Lease(this, id.Value));
        }

        private sealed class Lease(RecordingStore store, Guid id) : IIdempotencyLease
        {
            private bool _committed;

            public System.Data.Common.DbTransaction? Transaction => null;

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                store._completed.Add(id);
                store.Commits++;
                _committed = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!_committed)
                {
                    store._completed.Remove(id);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
