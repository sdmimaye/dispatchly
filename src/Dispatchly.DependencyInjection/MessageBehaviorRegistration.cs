using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly;

/// <summary>Registers message pipeline behaviors.</summary>
public static class MessageBehaviorRegistration
{
    /// <summary>
    /// Adds a behavior around every delivery. The first registration is the outermost.
    /// </summary>
    public static DispatchlyBuilder UseBehavior<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior>(
        this DispatchlyBuilder builder)
        where TBehavior : class, IMessageBehavior
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddBehavior<TBehavior>();
        builder.Services.AddScoped<TBehavior>();
        return builder;
    }
}
