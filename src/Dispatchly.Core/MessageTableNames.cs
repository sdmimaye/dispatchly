using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Dispatchly.Core;

/// <summary>Resolves the outbox table name for a message type.</summary>
public static class MessageTableNames
{
    /// <summary>
    /// Resolves a table name from <paramref name="messageType" />.
    /// <see cref="DispatchlyTableAttribute" /> wins over <paramref name="naming" />.
    /// </summary>
    [RequiresUnreferencedCode("Reading DispatchlyTableAttribute uses reflection.")]
    public static string FromType(Type messageType, TableNaming naming = TableNaming.SnakeCase)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        var attribute = messageType.GetCustomAttribute<DispatchlyTableAttribute>();
        if (attribute is not null)
        {
            return IdentifierRules.ValidateTable(attribute.TableName);
        }

        return IdentifierRules.FromClrName(messageType.Name, naming);
    }
}
