using Npgsql;

namespace Eve.Data;

/// <summary>Accepts both a <c>postgres://</c> URL (what the tooling and Rust crates use) and an Npgsql keyword string.</summary>
public static class PgConnection
{
    public static string ToConnectionString(string urlOrKeywords)
    {
        var text = urlOrKeywords.Trim();
        if (!text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }
        var uri = new Uri(text);
        var builder = new NpgsqlConnectionStringBuilder { Host = uri.Host };
        if (!uri.IsDefaultPort && uri.Port > 0)
        {
            builder.Port = uri.Port;
        }
        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (database.Length > 0)
        {
            builder.Database = database;
        }
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 2)
            {
                builder.Password = Uri.UnescapeDataString(parts[1]);
            }
        }
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            builder[Uri.UnescapeDataString(kv[0])] = kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return builder.ConnectionString;
    }
}
