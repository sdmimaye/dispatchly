namespace Dispatchly;

internal static class SqlServerIdentifiers
{
    public static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string UnicodeLiteral(string value) =>
        "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string Qualify(string schema, string name) =>
        Quote(schema) + "." + Quote(name);

    public static string MessageType(string schema) => schema + "/notify";

    public static string Contract(string schema) => schema + "/contract";

    public static string TargetService(string schema, string table) => schema + "/" + table + "/target";

    public static string InitiatorService(string schema, string table) => schema + "/" + table + "/initiator";

    public static string Queue(string table) => table + "_queue";
}
