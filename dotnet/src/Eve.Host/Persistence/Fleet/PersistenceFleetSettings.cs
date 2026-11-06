using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eve.Host;

public static class PersistenceFleetSettings
{
    public static JsonObject? FindSetup(JsonObject characterTable, long characterId, string setupName)
    {
        var wantedName = setupName.Trim();
        if (characterId <= 0 || wantedName.Length == 0) return null;

        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var settings = (characterTable[key] as JsonObject)?["characterSettings"] as JsonObject;
        var savedSetups = settings?["fleetSetups"];
        if (savedSetups is JsonValue serialized && serialized.TryGetValue<string>(out var json))
        {
            try { savedSetups = JsonNode.Parse(json); }
            catch (JsonException) { return null; }
        }

        var entries = savedSetups switch
        {
            JsonArray array => array.AsEnumerable(),
            JsonObject obj => obj.Select(entry => entry.Value),
            _ => Enumerable.Empty<JsonNode?>()
        };
        foreach (var entry in entries)
        {
            if (entry is not JsonObject setup ||
                !string.Equals(setup["setupName"]?.ToString().Trim(), wantedName, StringComparison.Ordinal))
                continue;
            return setup.DeepClone().AsObject();
        }
        return null;
    }
}
