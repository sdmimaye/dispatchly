namespace Dispatchly.Abstractions;

/// <summary>Overrides the PostgreSQL table name for a message type.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class DispatchlyTableAttribute : Attribute
{
    /// <summary>Creates an override for a validated PostgreSQL identifier.</summary>
    public DispatchlyTableAttribute(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableName = tableName;
    }

    /// <summary>Unqualified table name, without the schema.</summary>
    public string TableName { get; }
}
