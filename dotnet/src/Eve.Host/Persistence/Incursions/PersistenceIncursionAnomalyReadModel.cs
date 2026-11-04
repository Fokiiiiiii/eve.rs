using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

public sealed class PersistenceIncursionAnomalyReadModel(
    Func<string, long, CancellationToken, Task<IReadOnlyList<Eve.Content.ContentInstance>>> activeOf,
    Func<string, CancellationToken, Task<JsonObject>> readGameStoreTable,
    Func<long> nowUnixMs,
    Func<CancellationToken, Task<IReadOnlyList<JsonObject>>>? activeIncursions = null,
    Func<long, CancellationToken, Task<IReadOnlyList<JsonObject>>>? activeComplexes = null)
{
    private const long GlobalOwnerId = 0;
    private static readonly HashSet<string> ActiveDungeonStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "seeded", "paused"
    };

    /// <summary>The incursions that are not gone: the Sansha incursions of the lifecycle (<c>IncursionDirector</c>), or none when the host runs without space.</summary>
    public async ValueTask<IReadOnlyList<JsonObject>> ReadIncursionsAsync(CancellationToken cancellationToken) =>
        activeIncursions is null ? Array.Empty<JsonObject>() : await activeIncursions(cancellationToken);

    public async ValueTask<IReadOnlyList<JsonObject>> ReadAnomaliesAsync(
        long solarSystemId,
        CancellationToken cancellationToken)
    {
        if (solarSystemId <= 0)
            return [];
        var now = nowUnixMs();
        var result = new Dictionary<long, JsonObject>();

        foreach (var instance in await activeOf("anomaly", solarSystemId, cancellationToken))
        {
            if (instance.ExpiresMs is { } expires && expires <= now ||
                ParseObject(instance.DataJson) is not { } data)
                continue;
            if (data["solarSystemID"] is null)
                data["solarSystemID"] = solarSystemId;
            if (Positive(Integer(data["solarSystemID"])) != solarSystemId)
                continue;
            var siteId = Positive(Integer(data["siteID"]));
            if (siteId <= 0)
                continue;
            if (data["instanceID"] is null)
                data["instanceID"] = instance.Id;
            result[siteId] = data;
        }

        // the factional warfare complexes of the system (their dungeon sites), listed by their site id
        if (activeComplexes is not null)
        {
            foreach (var complex in await activeComplexes(solarSystemId, cancellationToken))
            {
                if (Positive(Integer(complex["siteID"])) is var complexSite and > 0)
                    result[complexSite] = complex;
            }
        }

        var dungeonState = await readGameStoreTable("dungeonRuntimeState", cancellationToken);
        var instancesById = dungeonState["instancesByID"] as JsonObject;
        if (instancesById is not null)
        {
            foreach (var (key, node) in instancesById)
            {
                if (node is not JsonObject instance ||
                    !ActiveDungeonStates.Contains(Text(instance["lifecycleState"])) ||
                    !string.Equals(Text(instance["siteKind"]), "anomaly", StringComparison.OrdinalIgnoreCase))
                    continue;
                var systemId = Positive(Integer(instance["solarSystemID"]));
                if (systemId != solarSystemId)
                    continue;
                var siteId = Positive(Integer(instance["siteID"] ?? (instance["metadata"] as JsonObject)?["siteID"]));
                var targetId = Text(instance["targetID"]);
                if (siteId <= 0 || targetId.Length == 0)
                    continue;
                var copy = (JsonObject)instance.DeepClone();
                copy["siteID"] = siteId;
                copy["solarSystemID"] = solarSystemId;
                if (copy["instanceID"] is null && long.TryParse(key, out var instanceId) && instanceId > 0)
                    copy["instanceID"] = instanceId;
                result.TryAdd(siteId, copy);
            }
        }

        return result.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray();
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static long Integer(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static long Positive(long value) => value > 0 ? value : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}
