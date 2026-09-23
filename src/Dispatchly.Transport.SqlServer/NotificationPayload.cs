using System.Text;

namespace Dispatchly;

internal static class NotificationPayload
{
    public static bool TryParse(string json, out Guid id, out string table)
    {
        id = Guid.Empty;
        table = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        var reader = new System.Text.Json.Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        string? idText = null;
        string? tableText = null;
        while (reader.Read())
        {
            if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName)
            {
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
            {
                return false;
            }

            if (name == "id")
            {
                idText = reader.GetString();
            }
            else if (name == "table")
            {
                tableText = reader.GetString();
            }
        }

        if (idText is null || tableText is null || !Guid.TryParse(idText, out id))
        {
            return false;
        }

        table = tableText;
        return true;
    }
}
