using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly;

/// <summary>Collects handler registrations and the chosen transport.</summary>
public sealed class DispatchlyBuilder
{
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
