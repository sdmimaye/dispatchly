namespace Dispatchly.Abstractions;

/// <summary>Overrides the outbox table name for a message type.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class DispatchlyTableAttribute : Attribute
{
    /// <summary>Creates an override for a validated outbox table name.</summary>
    public DispatchlyTableAttribute(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableName = tableName;
    }

    /// <summary>Unqualified table name, without the schema.</summary>
    public string TableName { get; }
}
