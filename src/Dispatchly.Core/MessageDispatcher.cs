using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly;

/// <summary>Creates a dependency-injection scope for each delivery.</summary>
public sealed class MessageDispatcher : IMessageDispatcher
{
    private readonly IMessageTypeCatalog _catalog;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates a dispatcher.</summary>
    public MessageDispatcher(IMessageTypeCatalog catalog, IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _catalog = catalog;
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task DispatchAsync(
        Type messageType,
        string payload,
        MessageContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentNullException.ThrowIfNull(context);

        var registration = _catalog.GetRequired(messageType);
        var message = registration.Deserialize(payload)
            ?? throw new InvalidOperationException($"Message '{messageType.FullName}' deserialized to null.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        await registration.Dispatch(scope.ServiceProvider, message, context, cancellationToken).ConfigureAwait(false);
    }
}
