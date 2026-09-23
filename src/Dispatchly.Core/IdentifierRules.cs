using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dispatchly;

/// <summary>Validates PostgreSQL identifiers used by Dispatchly.</summary>
public static partial class IdentifierRules
{
    /// <summary>Maximum length of a PostgreSQL identifier in bytes.</summary>
    public const int MaxLength = 63;

    /// <summary>Suffix appended to the dead-letter table.</summary>
    public const string DeadLetterSuffix = "_dead_letter";

    /// <summary>Longest outbox table name that still leaves room for the dead-letter suffix.</summary>
    public const int MaxTableNameLength = MaxLength - 12;

    private static readonly HashSet<string> ReservedTables = new(StringComparer.Ordinal)
    {
        "outbox_table",
        "settings",
    };

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleName();

    /// <summary>Returns <paramref name="schema" /> when it is a safe schema name.</summary>
    public static string ValidateSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema)
            || schema.Length > MaxLength
            || !SimpleName().IsMatch(schema))
        {
            throw new ArgumentException(
                $"Schema '{schema}' must match ^[a-z_][a-z0-9_]*$ and be at most {MaxLength} characters.",
                nameof(schema));
        }

        return schema;
    }

    /// <summary>Returns <paramref name="tableName" /> when it can be used as an outbox table.</summary>
    public static string ValidateTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)
            || tableName.Length > MaxTableNameLength
            || !SimpleName().IsMatch(tableName))
        {
            throw new ArgumentException(
                $"Table '{tableName}' must match ^[a-z_][a-z0-9_]*$ and be at most {MaxTableNameLength} characters.",
                nameof(tableName));
        }

        if (ReservedTables.Contains(tableName))
        {
            throw new ArgumentException($"Table name '{tableName}' is reserved.", nameof(tableName));
        }

        return tableName;
    }

    /// <summary>Returns <paramref name="channel" /> when it can be used as a <c>LISTEN</c> channel.</summary>
    public static string ValidateChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel)
            || channel.Length > MaxLength
            || !SimpleName().IsMatch(channel))
        {
            throw new ArgumentException(
                $"Channel '{channel}' must match ^[a-z_][a-z0-9_]*$ and be at most {MaxLength} characters.",
                nameof(channel));
        }

        return channel;
    }

    /// <summary>Quotes a validated identifier.</summary>
    public static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Builds the dead-letter table name for an outbox table.</summary>
    public static string DeadLetterTable(string tableName) => tableName + DeadLetterSuffix;

    /// <summary>Converts a CLR type name into a PostgreSQL table name.</summary>
    public static string FromClrName(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        var tick = typeName.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            typeName = typeName[..tick];
        }

        return ValidateTable(ToSnakeCase(typeName));
    }

    /// <summary>Converts <paramref name="name" /> to lower snake case.</summary>
    public static string ToSnakeCase(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
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
        if (snake.Length <= MaxTableNameLength && SimpleName().IsMatch(snake) && !ReservedTables.Contains(snake))
        {
            return snake;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        var prefixLength = MaxTableNameLength - hash.Length - 1;
        var prefix = snake.Length > prefixLength ? snake[..prefixLength].TrimEnd('_') : snake;
        if (prefix.Length == 0 || !SimpleName().IsMatch(prefix))
        {
            prefix = "message";
        }

        return ValidateTable(prefix + "_" + hash);
    }
}
