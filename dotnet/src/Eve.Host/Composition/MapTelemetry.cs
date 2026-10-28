using System.Globalization;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;

namespace Eve.Host;

public static class MapTelemetryState
{
    private const long FiletimeEpochMs = 11_644_473_600_000;
    private const long FiletimeEpochTicks = 116_444_736_000_000_000;
    private const long JumpRetentionMs = 7 * 24 * 60 * 60 * 1000;
    private const int MaxJumpEvents = 10_000;

    public static JsonObject Empty() => new()
    {
        ["_meta"] = new JsonObject { ["schemaVersion"] = 1 },
        ["visitsByCharacterID"] = new JsonObject(),
        ["jumpEvents"] = new JsonArray(),
    };

    public static void RecordLocation(JsonObject state, long characterId, long solarSystemId, long nowMs)
    {
        if (characterId <= 0 || solarSystemId <= 0)
            return;

        var character = Character(state, characterId);
        var previousSystemId = PositiveLong(character["lastSolarSystemID"]);
        var systems = ChildObject(character, "systems");
        var key = solarSystemId.ToString(CultureInfo.InvariantCulture);
        var entry = ChildObject(systems, key);
        var visits = (int)Math.Min(int.MaxValue, PositiveLong(entry["visits"]));
        if (previousSystemId != solarSystemId || visits <= 0)
            visits++;

        character["lastSolarSystemID"] = solarSystemId;
        entry["solarSystemID"] = solarSystemId;
        entry["visits"] = visits;
        entry["lastDateTime"] = FiletimeFromUnixMs(nowMs).ToString(CultureInfo.InvariantCulture);
        entry["lastDateTimeMs"] = nowMs;
    }

    public static long LastSolarSystemId(JsonObject state, long characterId) =>
        characterId > 0
            ? PositiveLong((state["visitsByCharacterID"] as JsonObject)?
                [characterId.ToString(CultureInfo.InvariantCulture)] is JsonObject character
                    ? character["lastSolarSystemID"]
                    : null)
            : 0;

    public static void RecordJump(JsonObject state, long characterId, long fromSystemId, long toSystemId, long nowMs)
    {
        if (characterId <= 0 || fromSystemId <= 0 || toSystemId <= 0 || fromSystemId == toSystemId)
            return;

        var events = state["jumpEvents"] as JsonArray;
        if (events is null)
            state["jumpEvents"] = events = [];

        events.Add(new JsonObject
        {
            ["atMs"] = nowMs,
            ["atFiletime"] = FiletimeFromUnixMs(nowMs).ToString(CultureInfo.InvariantCulture),
            ["characterID"] = characterId,
            ["fromSolarSystemID"] = fromSystemId,
            ["toSolarSystemID"] = toSystemId,
        });

        var cutoff = nowMs - JumpRetentionMs;
        var retained = events
            .OfType<JsonObject>()
            .Where(row => PositiveLong(row["atMs"]) >= cutoff)
            .TakeLast(MaxJumpEvents)
            .Select(row => (JsonNode?)row.DeepClone())
            .ToArray();
        state["jumpEvents"] = new JsonArray(retained);
    }

    public static PyValue SolarSystemVisits(JsonObject state, long characterId)
    {
        var systems = (state["visitsByCharacterID"] as JsonObject)?
            [characterId.ToString(CultureInfo.InvariantCulture)] is JsonObject character
                ? character["systems"] as JsonObject
                : null;
        var rows = systems?.Select(pair => pair.Value as JsonObject)
            .Where(row => row is not null)
            .Select(row => (Row: row!, SystemId: PositiveLong(row!["solarSystemID"]), Visits: PositiveLong(row!["visits"])))
            .Where(entry => entry.SystemId > 0 && entry.Visits > 0 && IsPublicSystem(entry.SystemId))
            .OrderBy(entry => entry.SystemId)
            .Select(entry => (PyValue)Py.Tuple(
                new PyLong(ParseFiletime(entry.Row["lastDateTime"])),
                new PyInt(entry.SystemId),
                new PyInt(entry.Visits)))
            .ToArray() ?? [];

        return Py.Rowset(["lastDateTime", "solarSystemID", "visits"], rows);
    }

    public static PyValue History(
        JsonObject telemetry,
        JsonObject killmailStore,
        long statId,
        long hours,
        long nowMs,
        IReadOnlyDictionary<long, int> typeCategories,
        IReadOnlySet<long> capsuleTypeIds,
        IReadOnlySet<long> factionIds)
    {
        var counts = new SortedDictionary<long, (long Total, long Faction, long Pods)>();
        var windowHours = Math.Clamp(hours > 0 ? hours : 1, 1, 24);
        var cutoff = nowMs - windowHours * 60 * 60 * 1000;

        if (statId == 1)
        {
            if (telemetry["jumpEvents"] is JsonArray jumps)
            {
                foreach (var jump in jumps.OfType<JsonObject>())
                {
                    var at = PositiveLong(jump["atMs"]);
                    var systemId = PositiveLong(jump["toSolarSystemID"] ?? jump["solarSystemID"]);
                    if (at < cutoff || at > nowMs + 60_000 || !IsPublicSystem(systemId))
                        continue;
                    Add(systemId, 1, 0, 0);
                }
            }
        }
        else if (statId is 3 or 5)
        {
            foreach (var record in KillmailRecords(killmailStore))
            {
                var systemId = PositiveLong(record["solarSystemID"]);
                var shipTypeId = PositiveLong(record["victimShipTypeID"]);
                var victimCharacterId = PositiveLong(record["victimCharacterID"]);
                var factionId = PositiveLong(record["victimFactionID"]);
                var time = UnixMs(record["killTime"] ?? record["killmailTime"]);
                if (!IsPublicSystem(systemId) || shipTypeId <= 0 ||
                    (typeCategories.TryGetValue(shipTypeId, out var category) && category != 6) ||
                    time <= 0 || time < cutoff || time > nowMs + 60_000)
                    continue;

                var capsule = capsuleTypeIds.Contains(shipTypeId);
                if (statId == 3)
                {
                    if (capsule)
                        Add(systemId, 0, 0, 1);
                    else
                        Add(systemId, 1, victimCharacterId > 0 ? 0 : 1, 0);
                }
                else if (!capsule && factionIds.Contains(factionId))
                {
                    Add(systemId, 1, victimCharacterId > 0 ? 0 : 1, 0);
                }
            }
        }

        return Py.Rowset(
            ["solarSystemID", "value1", "value2", "value3"],
            counts.Select(pair => (PyValue)Py.Tuple(
                new PyInt(pair.Key),
                new PyInt(pair.Value.Total),
                new PyInt(pair.Value.Faction),
                new PyInt(pair.Value.Pods))).ToArray());

        void Add(long systemId, long total, long faction, long pods)
        {
            counts.TryGetValue(systemId, out var current);
            counts[systemId] = (current.Total + total, current.Faction + faction, current.Pods + pods);
        }
    }

    public static IReadOnlyDictionary<long, int> ActiveBeaconCounts(
        IEnumerable<DeployedRecord> fields,
        IReadOnlySet<long> fleetOwnerIds,
        long nowMs)
    {
        var counts = new SortedDictionary<long, int>();
        foreach (var field in fields)
        {
            var active = field.ExpiresAtMs > nowMs &&
                (field.Kind == "cyno_field" ||
                 field.Kind == "mobile_cyno_beacon" && field.ActivateAtMs <= nowMs);
            if (!active || !fleetOwnerIds.Contains(field.OwnerId) || !IsPublicSystem(field.SolarSystemId))
                continue;
            counts[field.SolarSystemId] = counts.GetValueOrDefault(field.SolarSystemId) + 1;
        }
        return counts;
    }

    public static bool IsPublicSystem(long systemId) =>
        systemId > 0 && systemId is not (>= 32_000_001 and <= 32_000_200);

    public static long PositiveLong(JsonNode? value) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : 0;

    private static JsonObject Character(JsonObject state, long characterId)
    {
        var characters = ChildObject(state, "visitsByCharacterID");
        return ChildObject(characters, characterId.ToString(CultureInfo.InvariantCulture));
    }

    private static JsonObject ChildObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject child)
            return child;
        parent[key] = child = new JsonObject();
        return child;
    }

    private static long FiletimeFromUnixMs(long unixMs) => checked((unixMs + FiletimeEpochMs) * 10_000);

    private static long ParseFiletime(JsonNode? node)
    {
        var value = node?.ToString();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var filetime) && filetime > 0
            ? filetime
            : 0;
    }

    private static long UnixMs(JsonNode? node)
    {
        var value = node?.ToString();
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number > FiletimeEpochTicks
                ? (number - FiletimeEpochTicks) / 10_000
                : number;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUnixTimeMilliseconds()
            : 0;
    }

    private static IEnumerable<JsonObject> KillmailRecords(JsonObject table)
    {
        var records = new Dictionary<long, JsonObject>();
        var rowsWithoutId = new List<JsonObject>();
        Add(table["pending"]);
        Add(table["records"]);
        return records.Values.Concat(rowsWithoutId);

        void Add(JsonNode? source)
        {
            if (source is JsonObject byKey)
            {
                foreach (var (key, value) in byKey)
                    AddRow(key, value);
            }
            else if (source is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                    AddRow(index.ToString(CultureInfo.InvariantCulture), array[index]);
            }
        }

        void AddRow(string key, JsonNode? value)
        {
            if (value is not JsonObject row)
                return;
            var id = PositiveLong(row["killID"] ?? row["killmailID"]);
            if (id <= 0)
                id = PositiveLong(JsonValue.Create(key));
            if (id > 0)
                records[id] = (JsonObject)row.DeepClone();
            else
                rowsWithoutId.Add((JsonObject)row.DeepClone());
        }
    }
}

internal sealed class MapTelemetry(GameStoreCache cache, Func<long> nowUnixMs, Action<string>? log = null) : ICharacterEntryHook
{
    private readonly object _gate = new();
    private readonly Dictionary<ClientSession, (long CharacterId, Action Closed)> _sessions = new();

    public ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Track(session, characterId);
        RecordLocation(characterId, CurrentSystem(session), recordJump: false);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnCharacterLeavingAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Untrack(session);
        return ValueTask.CompletedTask;
    }

    public JsonObject ReadState() =>
        cache.Read("mapTelemetry", "/").Data as JsonObject ?? MapTelemetryState.Empty();

    public void RecordJump(long characterId, long fromSystemId, long toSystemId)
    {
        Mutate(state => MapTelemetryState.RecordJump(state, characterId, fromSystemId, toSystemId, nowUnixMs()));
    }

    private static long CurrentSystem(ClientSession session) =>
        session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;

    private void Track(ClientSession session, long characterId)
    {
        Untrack(session);
        Action closed = () => Untrack(session);
        lock (_gate)
            _sessions[session] = (characterId, closed);
        session.AttributesChanged += OnAttributesChanged;
        session.Closed += closed;
    }

    private void Untrack(ClientSession session)
    {
        (long CharacterId, Action Closed) tracking;
        lock (_gate)
        {
            if (!_sessions.Remove(session, out tracking))
                return;
        }
        session.AttributesChanged -= OnAttributesChanged;
        session.Closed -= tracking.Closed;
    }

    private void OnAttributesChanged(ClientSession session, IReadOnlyList<string> changed)
    {
        if (!changed.Contains("solarsystemid2", StringComparer.Ordinal) &&
            !changed.Contains("solarsystemid", StringComparer.Ordinal))
            return;
        long characterId;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var tracking))
                return;
            characterId = tracking.CharacterId;
        }
        RecordLocation(characterId, CurrentSystem(session), recordJump: true);
    }

    private void RecordLocation(long characterId, long solarSystemId, bool recordJump)
    {
        if (solarSystemId <= 0)
            return;
        Mutate(state =>
        {
            var previous = MapTelemetryState.LastSolarSystemId(state, characterId);
            if (recordJump && previous > 0 && previous != solarSystemId)
                MapTelemetryState.RecordJump(state, characterId, previous, solarSystemId, nowUnixMs());
            MapTelemetryState.RecordLocation(state, characterId, solarSystemId, nowUnixMs());
        });
    }

    private void Mutate(Action<JsonObject> update)
    {
        lock (_gate)
        {
            var state = ReadState();
            update(state);
            var result = cache.Write("mapTelemetry", "/", state);
            if (!result.Success)
                log?.Invoke($"map telemetry could not be stored: {result.ErrorMsg}");
        }
    }
}
