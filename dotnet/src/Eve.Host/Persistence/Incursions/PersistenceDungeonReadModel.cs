using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Content;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host;

public sealed class PersistenceDungeonReadModel(
    Func<string, CancellationToken, Task<IReadOnlyList<SdeRow>>> readSde,
    Func<string, CancellationToken, Task<IReadOnlyList<ContentInstance>>> activeByKind,
    Func<string, CancellationToken, Task<JsonObject>> readGameStoreTable,
    Func<long> nowUnixMs)
{
    private readonly object _catalogLock = new();
    private Task<DungeonEditorCatalog>? _catalogTask;

    public async ValueTask<DungeonEditorCatalog> ReadEditorCatalogAsync(CancellationToken cancellationToken)
    {
        Task<DungeonEditorCatalog> task;
        lock (_catalogLock)
            task = _catalogTask ??= LoadEditorCatalogAsync(cancellationToken);
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_catalogLock)
                if (ReferenceEquals(_catalogTask, task))
                    _catalogTask = null;
            throw;
        }
    }

    public async ValueTask<DungeonCacheSnapshot> ReadCacheSnapshotAsync(CancellationToken cancellationToken)
    {
        var catalogTask = ReadEditorCatalogAsync(cancellationToken);
        var anomaliesTask = activeByKind("anomaly", cancellationToken);
        var signaturesTask = activeByKind("signature", cancellationToken);
        var dungeonStateTask = readGameStoreTable("dungeonRuntimeState", cancellationToken);
        await Task.WhenAll(catalogTask.AsTask(), anomaliesTask, signaturesTask, dungeonStateTask).ConfigureAwait(false);

        var byId = new Dictionary<long, JsonObject>();
        var now = nowUnixMs();
        AddContentInstances(anomaliesTask.Result, "anomaly", now, byId);
        AddContentInstances(signaturesTask.Result, "signature", now, byId);

        var instancesById = dungeonStateTask.Result["instancesByID"] as JsonObject;
        if (instancesById is not null)
        {
            foreach (var (key, node) in instancesById)
            {
                if (node is not JsonObject source)
                    continue;
                var instance = (JsonObject)source.DeepClone();
                if (instance["instanceID"] is null &&
                    long.TryParse(key, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var keyId) && keyId > 0)
                    instance["instanceID"] = keyId;
                var instanceId = Positive(instance["instanceID"]);
                if (instanceId > 0)
                    byId.TryAdd(instanceId, instance);
            }
        }

        return new DungeonCacheSnapshot(
            catalogTask.Result,
            byId.Values.ToArray(),
            new Dictionary<long, int>(),
            new Dictionary<long, DungeonIceSystemAuthority>(),
            new Dictionary<long, int>());
    }

    public async ValueTask<PyValue?> ReadCurrentDungeonAsync(
        long characterId,
        long shipId,
        long currentSystemId,
        double? shipX,
        double? shipY,
        double? shipZ,
        bool isWarping,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || shipId <= 0 || currentSystemId <= 0 ||
            shipX is null || shipY is null || shipZ is null || isWarping)
            return null;

        var missionStateTask = readGameStoreTable("missionRuntimeState", cancellationToken);
        var dungeonStateTask = readGameStoreTable("dungeonRuntimeState", cancellationToken);
        var catalogTask = ReadEditorCatalogAsync(cancellationToken);
        await Task.WhenAll(missionStateTask, dungeonStateTask, catalogTask.AsTask()).ConfigureAwait(false);

        var character = (missionStateTask.Result["charactersByID"] as JsonObject)?
            [characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
        var missions = character?["missionsByAgentID"] as JsonObject;
        var instances = dungeonStateTask.Result["instancesByID"] as JsonObject;
        if (missions is null || instances is null)
            return null;

        foreach (var mission in missions.Select(pair => pair.Value).OfType<JsonObject>())
        {
            if (!string.Equals(Text(mission["runtimeStatus"]), "accepted", StringComparison.OrdinalIgnoreCase))
                continue;

            var instanceId = Positive(mission["dungeonInstanceID"]);
            if (instanceId <= 0 ||
                MissionTrackerGameStoreProjection.IsInActiveMissionDungeon(
                    mission, dungeonStateTask.Result, characterId, currentSystemId, shipId,
                    shipX, shipY, shipZ, isWarping) is not true ||
                instances[instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject instance)
                continue;

            var dungeonId = Positive(instance["dungeonID"] ?? instance["sourceDungeonID"] ??
                mission["dungeonID"] ?? (mission["killMission"] as JsonObject)?["dungeonID"]);
            if (dungeonId <= 0 || !catalogTask.Result.Dungeons.Any(dungeon => dungeon.DungeonId == dungeonId))
                continue;

            var roomId = Positive(instance["currentRoomID"] ?? instance["roomID"] ?? instance["pocketID"]);
            return Py.Tuple(Py.Number(dungeonId), Py.Number(roomId), Py.Number(instanceId), Py.Dict());
        }

        return null;
    }

    private async Task<DungeonEditorCatalog> LoadEditorCatalogAsync(CancellationToken cancellationToken)
    {
        var dungeonsTask = readSde("dungeons", cancellationToken);
        var archetypesTask = readSde("archetypes", cancellationToken);
        var factionsTask = readSde("factions", cancellationToken);
        await Task.WhenAll(dungeonsTask, archetypesTask, factionsTask).ConfigureAwait(false);

        var dungeons = dungeonsTask.Result
            .Select(row => new DungeonEditorDungeon(
                row.Key,
                OptionalInteger(row.Doc, "dungeonNameID"),
                English(row.Doc, "name"),
                OptionalInteger(row.Doc, "factionID"),
                OptionalInteger(row.Doc, "archetypeID")))
            .Where(dungeon => dungeon.DungeonId > 0)
            .ToArray();
        var archetypes = archetypesTask.Result
            .Select(row => new DungeonEditorOption(English(row.Doc, "title"), row.Key))
            .Where(option => option.Id > 0 && option.Name.Length > 0)
            .ToArray();
        var factions = factionsTask.Result
            .Select(row => row.Key)
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray();

        // The installed SDE has no dungeon room or editor palette tables; the service accepts these later.
        return new DungeonEditorCatalog(
            dungeons,
            archetypes,
            factions,
            new Dictionary<long, IReadOnlyList<DungeonEditorRoom>>(),
            new PyDict([]));
    }

    private static void AddContentInstances(
        IReadOnlyList<ContentInstance> instances,
        string siteKind,
        long now,
        Dictionary<long, JsonObject> byId)
    {
        foreach (var content in instances)
        {
            if (content.ExpiresMs is { } expires && expires <= now || ParseObject(content.DataJson) is not { } data)
                continue;
            data["solarSystemID"] ??= content.Owner > 0 ? JsonValue.Create(content.Owner) : null;
            data["instanceID"] ??= JsonValue.Create(content.Id);
            data["siteKind"] ??= JsonValue.Create(siteKind);
            data["instanceScope"] ??= JsonValue.Create("shared");
            data["lifecycleState"] ??= JsonValue.Create(content.State);
            var instanceId = Positive(data["instanceID"]);
            if (instanceId > 0)
                byId.TryAdd(instanceId, data);
        }
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string English(JsonElement document, string property) =>
        document.TryGetProperty(property, out var value) ? SdeReader.English(value) : "";

    private static long? OptionalInteger(JsonElement document, string property)
    {
        if (!document.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static long Positive(JsonNode? value)
    {
        if (value is JsonObject wrapper && wrapper["value"] is { } wrapped)
            return Positive(wrapped);
        if (value is not JsonValue json)
            return 0;
        if (json.TryGetValue<int>(out var smallInteger))
            return Math.Max(0, smallInteger);
        if (json.TryGetValue<long>(out var integer))
            return Math.Max(0, integer);
        return json.TryGetValue<string>(out var text) && long.TryParse(text,
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out integer)
            ? Math.Max(0, integer)
            : 0;
    }

    private static string Text(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) ? text : "";
}
