using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.DependencyInjection;

/// <summary>Collects handler registrations and the chosen transport.</summary>
public sealed class DispatchlyBuilder
{
    private readonly List<Type> _behaviorTypes = [];
    private readonly List<Func<IServiceProvider, IMessageBehavior>> _behaviorFactories = [];
    private string? _transport;

    internal DispatchlyBuilder(IServiceCollection services, MessageTypeCatalog catalog)
    {
        Services = services;
        Catalog = catalog;
    }

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    internal MessageTypeCatalog Catalog { get; }

    internal string? TransportName => _transport;

    internal IReadOnlyList<Func<IServiceProvider, IMessageBehavior>> BehaviorFactories => _behaviorFactories;

    internal void AddBehavior<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior>()
        where TBehavior : class, IMessageBehavior
    {
        var behaviorType = typeof(TBehavior);
        if (_behaviorTypes.Contains(behaviorType))
        {
            throw new InvalidOperationException(
                $"Message behavior '{behaviorType.FullName}' is already registered.");
        }

        _behaviorTypes.Add(behaviorType);
        _behaviorFactories.Add(static services => services.GetRequiredService<TBehavior>());
    }

    internal void EnsureSingleTransport(string transportName)
    {
        if (_transport is not null)
        {
            throw new InvalidOperationException(
                $"Dispatchly transport '{_transport}' is already registered. A second transport ('{transportName}') cannot be added.");
        }

        _transport = transportName;
    }
}
