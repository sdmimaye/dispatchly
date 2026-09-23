using System;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Dispatchly.SourceGenerators;

internal static class TableNameResolver
{
    private const int MaxTableNameLength = 51;

    public static string? Resolve(INamedTypeSymbol message, out string? error)
    {
        foreach (var attribute in message.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "DispatchlyTableAttribute"
                && attribute.AttributeClass.ContainingNamespace.ToDisplayString() == "Dispatchly.Abstractions"
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string configured)
            {
                if (!IsValidTable(configured))
                {
                    error = $"'{configured}' must match ^[A-Za-z_][A-Za-z0-9_]*$ and be at most {MaxTableNameLength} characters, and must not be reserved.";
                    return null;
                }

                error = null;
                return configured;
            }
        }

        error = null;
        return null;
    }

    private static bool IsValidTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)
            || tableName.Length > MaxTableNameLength
            || !Regex.IsMatch(tableName, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            return false;
        }

        return !tableName.Equals("outbox_table", StringComparison.OrdinalIgnoreCase)
            && !tableName.Equals("settings", StringComparison.OrdinalIgnoreCase)
            && !tableName.Equals("idempotency_inbox", StringComparison.OrdinalIgnoreCase);
    }
}
