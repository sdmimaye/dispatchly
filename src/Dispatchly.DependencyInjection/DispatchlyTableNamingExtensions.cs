namespace Dispatchly.DependencyInjection;

/// <summary>Configures how unspecified message table names are derived.</summary>
public static class DispatchlyTableNamingExtensions
{
    /// <summary>
    /// Sets the naming strategy used when a message has no <see cref="DispatchlyTableAttribute" /> and no table name argument.
    /// Call this before registering messages. The default is <see cref="TableNaming.SnakeCase" />.
    /// </summary>
    public static DispatchlyBuilder UseTableNaming(this DispatchlyBuilder builder, TableNaming naming)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.SetTableNaming(naming);
        return builder;
    }
}
