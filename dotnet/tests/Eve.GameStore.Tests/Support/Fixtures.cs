using System.Text.Json.Nodes;

namespace Eve.GameStore.Tests;

internal static class Fixtures
{
    public static JsonNode? Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "gamestore", name);
        return JsonNode.Parse(File.ReadAllText(path));
    }

    public static string Canon(JsonNode? node) => JsonCanonical.ToText(node);

    public static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;
}
