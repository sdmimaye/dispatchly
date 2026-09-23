namespace Dispatchly;

/// <summary>Continues the message pipeline.</summary>
public delegate Task MessagePipelineDelegate(MessageEnvelope envelope, CancellationToken cancellationToken);

/// <summary>A step around message delivery. Call <c>next</c> to continue. Return without calling it to stop.</summary>
public interface IMessageBehavior
{
    /// <summary>Runs this step, then optionally the rest of the pipeline.</summary>
    Task InvokeAsync(MessageEnvelope envelope, MessagePipelineDelegate next, CancellationToken cancellationToken);
}
