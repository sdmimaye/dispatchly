using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.DependencyInjection;

/// <summary>Registers the Dispatchly core services.</summary>
public static class DispatchlyServiceCollectionExtensions
{
    /// <summary>Adds the message catalog and dispatcher. Calling it again returns the existing builder.</summary>
    public static DispatchlyBuilder AddDispatchly(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(DispatchlyBuilder)
                && descriptor.ImplementationInstance is DispatchlyBuilder existing)
            {
                return existing;
            }
        }

        var catalog = new MessageTypeCatalog();
        var builder = new DispatchlyBuilder(services, catalog);
        services.AddSingleton(catalog);
        services.AddSingleton<IMessageTypeCatalog>(catalog);
        services.AddSingleton<IMessageSerializer, CatalogMessageSerializer>();
        services.AddSingleton(builder);
        services.AddSingleton(static provider =>
            new MessagePipeline(provider.GetRequiredService<DispatchlyBuilder>().BehaviorFactories.ToArray()));
        services.AddSingleton<IMessageDispatcher, MessageDispatcher>();
        return builder;
    }
}
