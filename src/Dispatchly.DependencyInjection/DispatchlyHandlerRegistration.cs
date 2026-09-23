using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.DependencyInjection;

/// <summary>Registers message handlers on a <see cref="DispatchlyBuilder" />.</summary>
public static class DispatchlyHandlerRegistration
{
    /// <summary>
    /// Registers <typeparamref name="TMessage" /> for publishing without a handler.
    /// The PostgreSQL listener does not claim that type. The in-memory transport rejects publish.
    /// </summary>
    public static DispatchlyBuilder AddMessage<TMessage>(
        this DispatchlyBuilder builder,
        JsonTypeInfo<TMessage> jsonTypeInfo,
        string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        RegisterCatalog(builder, jsonTypeInfo, tableName, handlerType: null);
        return builder;
    }

    /// <summary>
    /// Registers <typeparamref name="TMessage" /> for publishing without a handler.
    /// Honors <see cref="DispatchlyTableAttribute" />. Not compatible with trimming or AOT.
    /// </summary>
    [RequiresUnreferencedCode("Reflection-based JSON serialization and DispatchlyTableAttribute lookup are not compatible with trimming or AOT.")]
    [RequiresDynamicCode("Reflection-based JSON serialization requires dynamic code.")]
    public static DispatchlyBuilder AddMessage<TMessage>(this DispatchlyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var typeInfo = (JsonTypeInfo<TMessage>)options.GetTypeInfo(typeof(TMessage));
        return builder.AddMessage(typeInfo, MessageTableNames.FromType(typeof(TMessage), builder.TableNaming));
    }

    /// <summary>Registers a handler with source-generated or caller-supplied JSON metadata.</summary>
    public static DispatchlyBuilder AddHandler<TMessage, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this DispatchlyBuilder builder,
        JsonTypeInfo<TMessage> jsonTypeInfo,
        string? tableName = null)
        where THandler : class, IMessageHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        RegisterServices<TMessage, THandler>(builder);
        RegisterCatalog(builder, jsonTypeInfo, tableName, typeof(THandler));
        return builder;
    }

    /// <summary>
    /// Registers a handler and serializes with reflection-based JSON.
    /// Honors <see cref="DispatchlyTableAttribute" />. Not compatible with trimming or AOT.
    /// </summary>
    [RequiresUnreferencedCode("Reflection-based JSON serialization and DispatchlyTableAttribute lookup are not compatible with trimming or AOT.")]
    [RequiresDynamicCode("Reflection-based JSON serialization requires dynamic code.")]
    public static DispatchlyBuilder AddHandler<TMessage, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this DispatchlyBuilder builder)
        where THandler : class, IMessageHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var typeInfo = (JsonTypeInfo<TMessage>)options.GetTypeInfo(typeof(TMessage));
        var tableName = MessageTableNames.FromType(typeof(TMessage), builder.TableNaming);
        return builder.AddHandler<TMessage, THandler>(typeInfo, tableName);
    }

    private static void RegisterCatalog<TMessage>(
        DispatchlyBuilder builder,
        JsonTypeInfo<TMessage> jsonTypeInfo,
        string? tableName,
        Type? handlerType)
    {
        var resolvedTable = string.IsNullOrWhiteSpace(tableName)
            ? IdentifierRules.FromClrName(typeof(TMessage).Name, builder.TableNaming)
            : IdentifierRules.ValidateTable(tableName);

        builder.Catalog.GetOrAdd(
            typeof(TMessage),
            resolvedTable,
            message => JsonSerializer.Serialize((TMessage)message, jsonTypeInfo),
            payload => JsonSerializer.Deserialize(payload, jsonTypeInfo),
            static (provider, message, context, cancellationToken) =>
                InvokeHandlers((TMessage)message, context, provider, cancellationToken),
            handlerType);
    }

    private static void RegisterServices<TMessage, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(DispatchlyBuilder builder)
        where THandler : class, IMessageHandler<TMessage>
    {
        builder.Services.AddScoped<THandler>();
        builder.Services.AddScoped<IMessageHandler<TMessage>>(provider => provider.GetRequiredService<THandler>());
    }

    private static async Task InvokeHandlers<TMessage>(
        TMessage message,
        MessageContext context,
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var invoked = false;
        foreach (var handler in provider.GetServices<IMessageHandler<TMessage>>())
        {
            invoked = true;
            await handler.HandleAsync(message, context, cancellationToken).ConfigureAwait(false);
        }

        if (!invoked)
        {
            throw new InvalidOperationException($"No handlers are registered for message '{typeof(TMessage).FullName}'.");
        }
    }
}
