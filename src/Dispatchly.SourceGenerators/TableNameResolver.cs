using System;
using System.Security.Cryptography;
using System.Text;
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
                    error = $"'{configured}' must match ^[a-z_][a-z0-9_]*$ and be at most {MaxTableNameLength} characters, and must not be reserved.";
                    return null;
                }

                error = null;
                return configured;
            }
        }

        var snake = ToSnakeCase(message.Name);
        if (!IsValidTable(snake))
        {
            error = $"Derived table '{snake}' is not a valid PostgreSQL identifier.";
            return null;
        }

        error = null;
        return snake;
    }

    private static bool IsValidTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)
            || tableName.Length > MaxTableNameLength
            || !Regex.IsMatch(tableName, "^[a-z_][a-z0-9_]*$"))
        {
            return false;
        }

        return tableName is not ("outbox_table" or "settings");
    }

    private static string ToSnakeCase(string name)
    {
        var tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name.Substring(0, tick);
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current))
            {
                var hasPrevious = i > 0;
                var previous = hasPrevious ? name[i - 1] : '\0';
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                var previousIsLowerOrDigit = hasPrevious && (char.IsLower(previous) || char.IsDigit(previous));
                if (hasPrevious && (previousIsLowerOrDigit || (char.IsUpper(previous) && nextIsLower)))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(char.ToLowerInvariant(current));
            }
        }

        var snake = builder.ToString();
        if (IsValidTable(snake))
        {
            return snake;
        }

        var hash = BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(name)))
            .Replace("-", string.Empty)
            .Substring(0, 8)
            .ToLowerInvariant();
        var prefixLength = MaxTableNameLength - hash.Length - 1;
        var prefix = snake.Length > prefixLength ? snake.Substring(0, prefixLength).TrimEnd('_') : snake;
        if (prefix.Length == 0 || !Regex.IsMatch(prefix, "^[a-z_][a-z0-9_]*$"))
        {
            prefix = "message";
        }

        return prefix + "_" + hash;
    }
}
