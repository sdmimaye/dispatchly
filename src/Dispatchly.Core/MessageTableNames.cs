using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Dispatchly;

/// <summary>Resolves the PostgreSQL table name for a message type.</summary>
public static class MessageTableNames
{
    /// <summary>
    /// Resolves a table name from <paramref name="messageType" />, including <see cref="DispatchlyTableAttribute" />.
    /// </summary>
    [RequiresUnreferencedCode("Reading DispatchlyTableAttribute uses reflection.")]
    public static string FromType(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        var attribute = messageType.GetCustomAttribute<DispatchlyTableAttribute>();
        if (attribute is not null)
        {
            return IdentifierRules.ValidateTable(attribute.TableName);
        }

        return IdentifierRules.FromClrName(messageType.Name);
    }
}
