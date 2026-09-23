namespace Dispatchly.Core;

/// <summary>Runs registered message behaviors around the handler invocation.</summary>
internal sealed class MessagePipeline
{
    private readonly IReadOnlyList<Func<IServiceProvider, IMessageBehavior>> _behaviors;

    public MessagePipeline(IReadOnlyList<Func<IServiceProvider, IMessageBehavior>> behaviors)
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        _behaviors = behaviors;
    }

    public async Task InvokeAsync(
        IServiceProvider services,
        MessageEnvelope envelope,
        MessagePipelineDelegate terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(terminal);

        var next = terminal;
        for (var index = _behaviors.Count - 1; index >= 0; index--)
        {
            var behavior = _behaviors[index](services);
            var downstream = next;
            next = (current, token) => behavior.InvokeAsync(current, downstream, token);
        }

        await next(envelope, cancellationToken).ConfigureAwait(false);
    }
}
