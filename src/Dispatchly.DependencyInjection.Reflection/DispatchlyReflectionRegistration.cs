using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Dispatchly;

/// <summary>Registers handlers discovered by scanning assemblies.</summary>
public static class DispatchlyReflectionRegistration
{
    /// <summary>
    /// Registers every public, non-abstract <see cref="IMessageHandler{TMessage}" /> in <paramref name="assemblies" />.
    /// Not compatible with trimming or AOT.
    /// </summary>
    [RequiresUnreferencedCode("Assembly scanning is not compatible with trimming or AOT. Use the source generator or AddHandler with JsonTypeInfo.")]
    [RequiresDynamicCode("Reflection-based JSON serialization requires dynamic code.")]
    public static DispatchlyBuilder AddHandlersFromAssemblies(this DispatchlyBuilder builder, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var assembly in assemblies)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            foreach (var type in GetTypes(assembly))
            {
                if (type is not { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
                {
                    continue;
                }

                if (!type.IsPublic && !type.IsNestedPublic)
                {
                    continue;
                }

                foreach (var implemented in type.GetInterfaces())
                {
                    if (!implemented.IsGenericType
                        || implemented.GetGenericTypeDefinition() != typeof(IMessageHandler<>))
                    {
                        continue;
                    }

                    var messageType = implemented.GetGenericArguments()[0];
                    RegisterMethod.MakeGenericMethod(messageType, type).Invoke(null, [builder]);
                }
            }
        }

        return builder;
    }

    private static readonly MethodInfo RegisterMethod = typeof(DispatchlyReflectionRegistration)
        .GetMethod(nameof(Register), BindingFlags.NonPublic | BindingFlags.Static)!;

    [RequiresUnreferencedCode("Calls the reflection JSON handler registration.")]
    [RequiresDynamicCode("Calls the reflection JSON handler registration.")]
    private static void Register<TMessage, THandler>(DispatchlyBuilder builder)
        where THandler : class, IMessageHandler<TMessage> =>
        builder.AddHandler<TMessage, THandler>();

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
