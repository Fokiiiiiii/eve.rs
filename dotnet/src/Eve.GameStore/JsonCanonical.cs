using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eve.GameStore;

/// <summary>
/// A canonical text of a JSON value: object keys sorted, numbers as doubles.
/// Two documents are equal here when they mean the same thing whatever their key
/// order or number formatting, which is what stored and re-read documents need
/// (PostgreSQL jsonb reorders keys and normalises whitespace).
/// </summary>
public static class JsonCanonical
{
    public static string ToText(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    public static string ToText(string json) => ToText(JsonNode.Parse(json));

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }
                    first = false;
                    builder.Append(JsonSerializer.Serialize(key)).Append(':');
                    Write(builder, value);
                }
                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }
                    Write(builder, array[i]);
                }
                builder.Append(']');
                break;
            case JsonValue value:
                var element = value.GetValue<JsonElement>();
                builder.Append(element.ValueKind == JsonValueKind.Number
                    ? element.GetDouble().ToString("R", CultureInfo.InvariantCulture)
                    : element.GetRawText());
                break;
        }
    }
}
