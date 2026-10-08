using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record DungeonEditorDungeon(
    long DungeonId,
    long? DungeonNameId,
    string DungeonName,
    long? FactionId,
    long? ArchetypeId);

public sealed record DungeonEditorOption(string Name, long Id);

public sealed record DungeonEditorRoom(long Id, string Name);

public sealed record DungeonEditorCatalog(
    IReadOnlyList<DungeonEditorDungeon> Dungeons,
    IReadOnlyList<DungeonEditorOption> Archetypes,
    IReadOnlyList<long> Factions,
    IReadOnlyDictionary<long, IReadOnlyList<DungeonEditorRoom>> RoomsByDungeonId,
    PyValue RoomObjectPaletteData)
{
    public static DungeonEditorCatalog Empty { get; } = new(
        [], [], [], new Dictionary<long, IReadOnlyList<DungeonEditorRoom>>(), new PyDict([]));
}

public sealed record DungeonIceSystemAuthority(long SourceDungeonId, int SlotCount);

public sealed record DungeonCacheSnapshot(
    DungeonEditorCatalog EditorCatalog,
    IReadOnlyList<JsonObject> Instances,
    IReadOnlyDictionary<long, int> OreAnomalySpawnCountsBySystem,
    IReadOnlyDictionary<long, DungeonIceSystemAuthority> IceSystemAuthorityBySystem,
    IReadOnlyDictionary<long, int> VisibleWormholeSignatureCountsBySystem)
{
    public static DungeonCacheSnapshot Empty { get; } = new(
        DungeonEditorCatalog.Empty,
        [],
        new Dictionary<long, int>(),
        new Dictionary<long, DungeonIceSystemAuthority>(),
        new Dictionary<long, int>());
}

/// <summary>The client dungeon editor RPCs, backed by SDE and optional editor data.</summary>
public sealed class DungeonService(
    Func<CancellationToken, ValueTask<DungeonEditorCatalog>>? readCatalog = null) : IService
{
    private readonly Func<CancellationToken, ValueTask<DungeonEditorCatalog>> _readCatalog =
        readCatalog ?? (static _ => ValueTask.FromResult(DungeonEditorCatalog.Empty));

    public string Name => "dungeon";

    public bool HasMethod(string method) => method is
        "DEGetArchetypes" or "DEGetDungeons" or "DEGetFactions" or
        "DEGetRoomObjectPaletteData" or "DEGetRooms";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        var catalog = await _readCatalog(call.Cancellation).ConfigureAwait(false);
        return call.Method switch
        {
            "DEGetArchetypes" => new PyList(catalog.Archetypes
                .Select(option => (PyValue)Py.Tuple(new PyStr(option.Name), Py.Number(option.Id)))
                .ToArray()),
            "DEGetDungeons" => GetDungeons(catalog, call),
            "DEGetFactions" => new PyList(catalog.Factions
                .Order()
                .Select(id => (PyValue)Py.Number(id))
                .ToArray()),
            "DEGetRoomObjectPaletteData" => catalog.RoomObjectPaletteData,
            "DEGetRooms" => GetRooms(catalog, call),
            _ => PyNone.Instance,
        };
    }

    private static PyValue GetDungeons(DungeonEditorCatalog catalog, ServiceCall call)
    {
        var dungeonId = OptionalInteger(call.Kwarg("dungeonID") ?? call.Arg(0));
        var archetypeId = OptionalInteger(call.Kwarg("archetypeID"));
        var factionId = OptionalInteger(call.Kwarg("factionID"));
        return new PyList(catalog.Dungeons
            .Where(dungeon => dungeonId is null || dungeon.DungeonId == dungeonId)
            .Where(dungeon => archetypeId is null || dungeon.ArchetypeId == archetypeId)
            .Where(dungeon => factionId is null || dungeon.FactionId == factionId)
            .Select(dungeon => (PyValue)Py.KeyVal(
                ("dungeonID", Py.Number(dungeon.DungeonId)),
                ("dungeonNameID", NullableNumber(dungeon.DungeonNameId)),
                ("dungeonName", new PyStr(dungeon.DungeonName)),
                ("factionID", NullableNumber(dungeon.FactionId)),
                ("archetypeID", NullableNumber(dungeon.ArchetypeId))))
            .ToArray());
    }

    private static PyValue GetRooms(DungeonEditorCatalog catalog, ServiceCall call)
    {
        var dungeonId = OptionalInteger(call.Kwarg("dungeonID") ?? call.Arg(0));
        if (dungeonId is not { } id || !catalog.RoomsByDungeonId.TryGetValue(id, out var rooms))
            return new PyList([]);
        return new PyList(rooms.Select(room => (PyValue)Py.Tuple(
            Py.Number(room.Id), new PyStr(room.Name))).ToArray());
    }

    private static long? OptionalInteger(PyValue? value) => value switch
    {
        null or PyNone => null,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value >= long.MinValue && real.Value <= long.MaxValue =>
            (long)Math.Truncate(real.Value),
        PyStr { Value: "All" } => null,
        PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static PyValue NullableNumber(long? value) =>
        value is { } number ? Py.Number(number) : PyNone.Instance;
}

/// <summary>The universe-wide instance cache RPCs. All dynamic sources are injectable.</summary>
public sealed class DungeonInstanceCacheMgrService(
    Func<CancellationToken, ValueTask<DungeonCacheSnapshot>>? readSnapshot = null,
    Func<long>? nowUnixMs = null) : IService
{
    private static readonly HashSet<long> CombatArchetypes = [24];
    private static readonly HashSet<long> OreArchetypes = [27];
    private static readonly HashSet<long> IceArchetypes = [28];
    private static readonly HashSet<long> FactionWarfareArchetypes = [33, 34, 35, 36, 68];
    private static readonly HashSet<long> BattlefieldArchetypes = [68];
    private static readonly HashSet<long> HomefrontArchetypes = [70];
    private static readonly HashSet<long> PirateInsurgencyArchetypes = [72, 73, 74, 75, 76, 77, 78, 79];
    private static readonly HashSet<long> IceHeistArchetypes = [74];
    private static readonly HashSet<long> TriglavianArchetypes = [65];
    private static readonly HashSet<long> BulwarkMiningDungeonIds = [14036, 14039, 14040, 14041, 14245, 14246, 14247, 14249];
    private static readonly HashSet<string> ActiveStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "seeded", "active", "paused"
    };

    private readonly Func<CancellationToken, ValueTask<DungeonCacheSnapshot>> _readSnapshot =
        readSnapshot ?? (static _ => ValueTask.FromResult(DungeonCacheSnapshot.Empty));
    private readonly Func<long> _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public string Name => "dungeonInstanceCacheMgr";

    public bool HasMethod(string method) => method is
        "GetBulwarkMiningSiteCounts" or "GetCombatAnomaliesCount" or "GetCombatAnomalyInstances" or
        "GetFactionWarfareBattlefieldInstances" or "GetFactionWarfareInstances" or "GetHomefrontSiteInstances" or
        "GetIceBeltInstances" or "GetIceBeltsCount" or "GetOreAnomaliesCount" or "GetOreAnomaliesCountInRange" or
        "GetOreAnomalyInstances" or "GetPirateInsurgencyIceHeistInstances" or "GetPirateInsurgencyInstances" or
        "GetSignatureInstances" or "GetTriglavianSiteInstances";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        var snapshot = await _readSnapshot(call.Cancellation).ConfigureAwait(false);
        return call.Method switch
        {
            "GetCombatAnomalyInstances" => GroupedEntries(snapshot, CombatArchetypes),
            "GetCombatAnomaliesCount" => GroupedCounts(snapshot, CombatArchetypes),
            "GetSignatureInstances" => SignatureCounts(snapshot),
            "GetIceBeltInstances" => GroupedEntries(snapshot, IceArchetypes, withTarget: true, requireIceAuthority: true),
            "GetIceBeltsCount" => ResourceCounts(snapshot, ice: true),
            "GetOreAnomalyInstances" => GroupedEntries(snapshot, OreArchetypes),
            "GetOreAnomaliesCount" or "GetOreAnomaliesCountInRange" => ResourceCounts(snapshot, ice: false),
            "GetBulwarkMiningSiteCounts" => BulwarkMiningCounts(snapshot),
            "GetFactionWarfareInstances" => GroupedEntries(snapshot, FactionWarfareArchetypes),
            "GetFactionWarfareBattlefieldInstances" => GroupedEntries(snapshot, BattlefieldArchetypes),
            "GetHomefrontSiteInstances" => GroupedEntries(snapshot, HomefrontArchetypes),
            "GetPirateInsurgencyInstances" => GroupedEntries(snapshot, PirateInsurgencyArchetypes),
            "GetPirateInsurgencyIceHeistInstances" => GroupedEntries(snapshot, IceHeistArchetypes),
            "GetTriglavianSiteInstances" => GroupedEntries(snapshot, TriglavianArchetypes),
            _ => PyNone.Instance,
        };
    }

    private static PyValue GroupedEntries(
        DungeonCacheSnapshot snapshot,
        HashSet<long> archetypes,
        bool withTarget = false,
        bool requireIceAuthority = false)
    {
        var entries = HydrateActive(snapshot, "anomaly")
            .Where(site => site.InstanceId > 0 && site.SolarSystemId > 0 && site.DungeonId > 0)
            .Where(site => site.ArchetypeId is { } archetype && archetypes.Contains(archetype))
            .Where(site => !requireIceAuthority || IsAuthorityBackedIceSite(snapshot, site))
            .GroupBy(site => site.SolarSystemId)
            .OrderBy(group => group.Key)
            .Select(group => new KeyValuePair<PyValue, PyValue>(
                Py.Number(group.Key),
                new PyList(group.OrderBy(site => site.InstanceId).ThenBy(site => site.DungeonId)
                    .Select(site => (PyValue)BuildEntry(site, withTarget)).ToArray())));
        return new PyDict(entries.ToArray());
    }

    private static PyValue GroupedCounts(DungeonCacheSnapshot snapshot, HashSet<long> archetypes) =>
        CountDict(HydrateActive(snapshot, "anomaly")
            .Where(site => site.InstanceId > 0 && site.SolarSystemId > 0 && site.DungeonId > 0)
            .Where(site => site.ArchetypeId is { } archetype && archetypes.Contains(archetype))
            .GroupBy(site => site.SolarSystemId)
            .ToDictionary(group => group.Key, group => group.Count()));

    private static PyValue SignatureCounts(DungeonCacheSnapshot snapshot)
    {
        var counts = new Dictionary<long, int>();
        foreach (var site in ActiveSummaries(snapshot, "signature"))
        {
            var systemId = Positive(site["solarSystemID"]);
            if (systemId > 0)
                counts[systemId] = counts.GetValueOrDefault(systemId) + 1;
        }
        foreach (var (systemId, count) in snapshot.VisibleWormholeSignatureCountsBySystem)
        {
            if (systemId > 0 && count > 0)
                counts[systemId] = counts.GetValueOrDefault(systemId) + count;
        }
        return CountDict(counts);
    }

    private PyValue ResourceCounts(DungeonCacheSnapshot snapshot, bool ice)
    {
        var counts = new Dictionary<long, int>();
        if (ice)
        {
            foreach (var (systemId, authority) in snapshot.IceSystemAuthorityBySystem)
                if (systemId > 0)
                    counts[systemId] = Math.Max(0, authority.SlotCount);

            var now = _nowUnixMs();
            foreach (var instance in snapshot.Instances)
            {
                if (!IsGeneratedIce(instance) ||
                    !Text(instance["lifecycleReason"]).Equals("depleted", StringComparison.OrdinalIgnoreCase) ||
                    Integer(instance["timers"]?["expiresAtMs"]) <= now)
                    continue;
                var systemId = Positive(instance["solarSystemID"]);
                if (systemId > 0 && counts.TryGetValue(systemId, out var available))
                    counts[systemId] = Math.Max(0, available - 1);
            }
        }
        else
        {
            foreach (var (systemId, count) in snapshot.OreAnomalySpawnCountsBySystem)
                if (systemId > 0)
                    counts[systemId] = Math.Max(0, count);
        }

        var archetypes = ice ? IceArchetypes : OreArchetypes;
        var activeCounts = HydrateActive(snapshot, "anomaly")
            .Where(site => site.InstanceId > 0 && site.SolarSystemId > 0 && site.DungeonId > 0)
            .Where(site => site.ArchetypeId is { } archetype && archetypes.Contains(archetype))
            .Where(site => !ice || IsAuthorityBackedIceSite(snapshot, site))
            .GroupBy(site => site.SolarSystemId);
        foreach (var group in activeCounts)
            counts[group.Key] = group.Count();
        return CountDict(counts);
    }

    private static PyValue BulwarkMiningCounts(DungeonCacheSnapshot snapshot)
    {
        var counts = new Dictionary<long, int>();
        foreach (var summary in ActiveSummaries(snapshot, "anomaly"))
        {
            var sourceDungeonId = Positive(summary["sourceDungeonID"]);
            var metadata = summary["metadata"] as JsonObject;
            var runtimeFlags = summary["runtimeFlags"] as JsonObject;
            var family = Text(summary["siteFamily"]);
            var origin = Text(summary["siteOrigin"]);
            var isBulwark = BulwarkMiningDungeonIds.Contains(sourceDungeonId) ||
                Boolean(metadata?["bulwarkMiningSite"]) || Boolean(runtimeFlags?["bulwarkMiningSite"]) ||
                (family.Contains("bulwark", StringComparison.OrdinalIgnoreCase) && family.Contains("mining", StringComparison.OrdinalIgnoreCase)) ||
                (origin.Contains("bulwark", StringComparison.OrdinalIgnoreCase) && origin.Contains("mining", StringComparison.OrdinalIgnoreCase));
            var systemId = Positive(summary["solarSystemID"]);
            if (isBulwark && systemId > 0)
                counts[systemId] = counts.GetValueOrDefault(systemId) + 1;
        }
        return CountDict(counts);
    }

    private static IReadOnlyList<JsonObject> ActiveSummaries(DungeonCacheSnapshot snapshot, string siteKind)
    {
        var byId = new Dictionary<long, JsonObject>();
        foreach (var instance in snapshot.Instances)
        {
            if (!IsActiveShared(instance, siteKind))
                continue;
            var id = Positive(instance["instanceID"]);
            if (id > 0)
                byId.TryAdd(id, instance);
        }
        return byId.Values.ToArray();
    }

    private static IEnumerable<HydratedSite> HydrateActive(DungeonCacheSnapshot snapshot, string siteKind)
    {
        var definitions = snapshot.EditorCatalog.Dungeons
            .GroupBy(dungeon => dungeon.DungeonId)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var summary in ActiveSummaries(snapshot, siteKind))
        {
            var site = Hydrate(summary, definitions);
            if (site is not null)
                yield return site;
        }
    }

    private static HydratedSite? Hydrate(
        JsonObject summary,
        IReadOnlyDictionary<long, DungeonEditorDungeon> definitions)
    {
        var instanceId = Positive(summary["instanceID"]);
        var systemId = Positive(summary["solarSystemID"]);
        var sourceDungeonId = Positive(summary["sourceDungeonID"]);
        if (sourceDungeonId <= 0)
            sourceDungeonId = Positive(summary["dungeonID"]);
        if (sourceDungeonId <= 0 && long.TryParse(Text(summary["templateID"]).Split(':').Last(), out var templateDungeonId))
            sourceDungeonId = Positive(templateDungeonId);
        definitions.TryGetValue(sourceDungeonId, out var definition);

        var generatedIce = IsGeneratedIce(summary);
        var metadata = summary["metadata"] as JsonObject;
        var siteId = generatedIce ? Positive(metadata?["siteID"]) : 0;
        if (siteId <= 0)
            siteId = instanceId;
        var archetypeId = Positive(summary["archetypeID"]);
        var factionId = Positive(summary["factionID"]);
        var difficulty = Positive(summary["difficulty"]);
        var position = Position(summary["position"]);
        return new HydratedSite(
            summary,
            instanceId,
            systemId,
            sourceDungeonId,
            archetypeId > 0 ? archetypeId : definition?.ArchetypeId,
            factionId > 0 ? factionId : definition?.FactionId,
            difficulty > 0 ? difficulty : 1,
            Positive(summary["entryObjectTypeID"]) is var entryType && entryType > 0 ? entryType : null,
            Positive(summary["dungeonNameID"]) is var nameId && nameId > 0
                ? nameId
                : definition?.DungeonNameId,
            siteId,
            generatedIce && siteId > 0 && systemId > 0
                ? EncodeTargetId("mining-anomaly", systemId, siteId)
                : null,
            position);
    }

    private static bool IsAuthorityBackedIceSite(DungeonCacheSnapshot snapshot, HydratedSite site) =>
        snapshot.IceSystemAuthorityBySystem.TryGetValue(site.SolarSystemId, out var authority) &&
        authority.SourceDungeonId > 0 && authority.SourceDungeonId == site.DungeonId;

    private static bool IsActiveShared(JsonObject summary, string siteKind) =>
        Text(summary["instanceScope"], "shared").Equals("shared", StringComparison.OrdinalIgnoreCase) &&
        Text(summary["siteKind"]).Equals(siteKind, StringComparison.OrdinalIgnoreCase) &&
        ActiveStates.Contains(Text(summary["lifecycleState"]));

    private static bool IsGeneratedIce(JsonObject summary)
    {
        var metadata = summary["metadata"] as JsonObject;
        var runtimeFlags = summary["runtimeFlags"] as JsonObject;
        return Text(summary["siteFamily"]).Equals("ice", StringComparison.OrdinalIgnoreCase) &&
            (Text(summary["siteOrigin"]).Equals("generatedmining", StringComparison.OrdinalIgnoreCase) ||
             Text(metadata?["providerID"]).Equals("generatedmining", StringComparison.OrdinalIgnoreCase) ||
             Boolean(runtimeFlags?["generatedMining"]));
    }

    private static PyObject BuildEntry(HydratedSite site, bool withTarget)
    {
        var fields = new List<(string Key, PyValue Value)>
        {
            ("dungeonID", Py.Number(site.DungeonId)),
            ("instanceID", Py.Number(site.InstanceId)),
            ("siteID", Py.Number(site.SiteId)),
            ("archetypeID", NullableNumber(site.ArchetypeId)),
            ("factionID", NullableNumber(site.FactionId)),
            ("difficulty", Py.Number(site.Difficulty)),
            ("entryObjectTypeID", NullableNumber(site.EntryObjectTypeId)),
            ("dungeonNameID", NullableNumber(site.DungeonNameId)),
        };
        if (withTarget && site.TargetId is { } targetId)
            fields.Add(("targetID", new PyStr(targetId)));
        if (site.Position is { } position)
            fields.Add(("position", new PyList([Py.Number(position.X), Py.Number(position.Y), Py.Number(position.Z)])));
        return (PyObject)Py.KeyVal(fields.ToArray());
    }

      private static PyValue CountDict(IReadOnlyDictionary<long, int> counts) =>
          new PyDict(counts.Where(entry => entry.Key > 0)
              .OrderBy(entry => entry.Key)
              .Select(entry => new KeyValuePair<PyValue, PyValue>(Py.Number(entry.Key), Py.Number(Math.Max(0, entry.Value))))
              .ToArray());

    private static PyValue NullableNumber(long? value) =>
        value is { } number && number > 0 ? Py.Number(number) : PyNone.Instance;

    private static (double X, double Y, double Z)? Position(JsonNode? node)
    {
        if (node is not JsonObject position)
            return null;
        return (Number(position["x"]), Number(position["y"]), Number(position["z"]));
    }

    private static string EncodeTargetId(string family, long systemId, long siteId)
    {
        var seed = $"{family.ToLowerInvariant()}:{systemId}:{siteId}";
        uint hash = 0;
        foreach (var character in seed)
            hash = unchecked(hash * 31 + character);
        const uint letterSpan = 26 * 26 * 26;
        var numeric = hash % (letterSpan * 1000);
        var letters = numeric / 1000 % letterSpan;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{(char)('A' + letters / (26 * 26))}{(char)('A' + letters / 26 % 26)}{(char)('A' + letters % 26)}-{numeric % 1000:000}");
    }

    private static long Positive(JsonNode? node) => Math.Max(0, Integer(node));

    private static long Integer(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
            return Integer(wrapped);
        if (node is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var smallInteger))
            return smallInteger;
        if (value.TryGetValue<long>(out var integer))
            return integer;
        if (value.TryGetValue<uint>(out var unsignedInteger))
            return unsignedInteger;
        if (value.TryGetValue<double>(out var real) && double.IsFinite(real) && real >= long.MinValue && real <= long.MaxValue)
            return (long)Math.Truncate(real);
        return value.TryGetValue<string>(out var text) && long.TryParse(text,
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out integer)
            ? integer
            : 0;
    }

    private static double Number(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
            return Number(wrapped);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number) && double.IsFinite(number))
                return number;
            if (value.TryGetValue<int>(out var smallInteger))
                return smallInteger;
            if (value.TryGetValue<long>(out var integer))
                return integer;
            if (value.TryGetValue<string>(out var text) && double.TryParse(text,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) &&
                double.IsFinite(number))
                return number;
        }
        return 0;
    }

    private static string Text(JsonNode? node, string fallback = "")
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
            return Text(wrapped, fallback);
        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? string.IsNullOrWhiteSpace(text) ? fallback : text.Trim()
            : fallback;
    }

    private static bool Boolean(JsonNode? node) =>
        node is JsonValue value && (value.TryGetValue<bool>(out var boolean)
            ? boolean
            : value.TryGetValue<long>(out var number) && number != 0);

    private sealed record HydratedSite(
        JsonObject Summary,
        long InstanceId,
        long SolarSystemId,
        long DungeonId,
        long? ArchetypeId,
        long? FactionId,
        long Difficulty,
        long? EntryObjectTypeId,
        long? DungeonNameId,
        long SiteId,
        string? TargetId,
        (double X, double Y, double Z)? Position);
}
