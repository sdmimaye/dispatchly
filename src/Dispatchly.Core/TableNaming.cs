namespace Dispatchly.Core;

/// <summary>How an unspecified message table name is derived from the CLR type name.</summary>
public enum TableNaming
{
    /// <summary>Lower snake case. <c>OrderPlaced</c> becomes <c>order_placed</c>.</summary>
    SnakeCase,

    /// <summary>The CLR type name. <c>OrderPlaced</c> stays <c>OrderPlaced</c>.</summary>
    PascalCase,
}
