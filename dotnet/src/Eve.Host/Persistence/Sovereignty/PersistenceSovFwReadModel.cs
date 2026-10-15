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

public sealed class PersistenceSovFwReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf)
{
    public async ValueTask<IReadOnlyList<Eve.Host.DevelopmentIndexRecord>> ReadAllDevelopmentIndicesAsync(
        CancellationToken cancellationToken)
    {
        var state = await readTable("sovereignty", cancellationToken);
        var systems = state["systems"] as JsonObject;
        if (systems is null)
            return [];

        var records = new List<Eve.Host.DevelopmentIndexRecord>(systems.Count);
        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system)
                continue;
            var systemId = Positive(Integer(system["solarSystemID"]));
            if (systemId <= 0 &&
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out systemId))
                continue;
            if (systemId <= 0)
                continue;

            var indices = system["devIndices"] as JsonObject;
            records.Add(new(
                systemId,
                Positive(Integer(indices?["militaryPoints"])),
                Positive(Integer(indices?["industrialPoints"])),
                Positive(Integer(indices?["claimedForDays"] ?? indices?["claimedFor"]))));
        }
        return records.OrderBy(record => record.SolarSystemId).ToArray();
    }

    public async ValueTask<Eve.Services.Core.SovFwReadSnapshot> ReadSnapshotAsync(
        Eve.Services.ServiceCall call) =>
        await ReadSnapshotAsync(call.Cancellation);

    public async ValueTask<bool> IsOnLocalFuelAccessGroupAsync(Eve.Services.ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0)
            return false;
        var systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
        if (systemId <= 0)
            return false;
        var snapshot = await ReadSnapshotAsync(call.Cancellation);
        if (!snapshot.FuelAccessGroupIdsBySolarSystemId.TryGetValue(systemId, out var groupId) ||
            groupId is not { } actualGroupId || actualGroupId <= 0)
            return false;
        var groups = await readTable("accessGroups", call.Cancellation);
        var affiliation = await affiliationOf(characterId, call.Cancellation);
        return PersistenceStructureDirectoryReadModel.AccessGroupMatchRank(
            actualGroupId, (characterId, affiliation), groups) > 0;
    }

    public async ValueTask<Eve.Services.Core.SovFwReadSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var state = await readTable("sovereignty", cancellationToken);
        var systems = state["systems"] as JsonObject;
        if (systems is null || systems.Count == 0)
            return Eve.Services.Core.SovFwReadSnapshot.Empty;

        var claims = new Dictionary<long, Eve.Services.Core.SovClaimRecord>();
        var hubs = new Dictionary<long, Eve.Services.Core.SovHubRecord>();
        var structures = new Dictionary<long, IReadOnlyList<Eve.Services.Core.SovStructureRecord>>();
        var occupations = new List<Eve.Services.Core.FwOccupationRecord>();
        var fuelGroups = new Dictionary<long, long?>();

        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system)
                continue;
            var systemId = Positive(Integer(system["solarSystemID"]));
            if (systemId <= 0 && !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out systemId))
                continue;
            if (systemId <= 0)
                continue;

            var claimStructureId = NullablePositive(system["claimStructureID"]);
            var corporationId = NullablePositive(system["corporationID"]);
            var allianceId = NullablePositive(system["allianceID"]);
            if (claimStructureId is not null || corporationId is not null || allianceId is not null)
                claims[systemId] = new(claimStructureId, corporationId, allianceId);

            var hubId = NullablePositive(system["infrastructureHubID"]);
            var claimTime = NullablePositive(system["claimTime"]);
            if (hubId is not null)
                hubs[systemId] = new(hubId, corporationId, allianceId, claimTime);

            var groupId = NullablePositive(system["fuelAccessGroupID"]);
            if (system.ContainsKey("fuelAccessGroupID"))
                fuelGroups[systemId] = groupId;

            var systemStructures = (system["structures"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(structure => ReadStructure(structure, systemId))
                .Where(structure => structure is not null)
                .Cast<Eve.Services.Core.SovStructureRecord>()
                .OrderBy(structure => structure.ItemId)
                .ToArray();
            if (systemStructures.Length > 0)
                structures[systemId] = systemStructures;

            var warzoneId = Positive(Integer(system["warzoneID"] ?? system["warZoneID"]));
            var occupierId = Positive(Integer(system["factionID"]));
            if (occupierId <= 0)
                occupierId = Positive(Integer(system["ownerFactionID"]));
            if (occupierId <= 0)
                occupierId = Positive(Integer(system["occupierID"] ?? system["fwOccupierID"]));
            if (warzoneId > 0 && occupierId > 0)
                occupations.Add(new(warzoneId, systemId, occupierId));
        }

        return new Eve.Services.Core.SovFwReadSnapshot(claims, hubs, structures, occupations, fuelGroups);
    }


    /// <summary>The Cynosural Suppression upgrade (SDE 81619; EveJS TYPE_CYNO_SUPPRESSION_UPGRADE).</summary>
    private const long CynoSuppressionUpgradeTypeId = 81619;

    /// <summary>
    /// E-03: whether the system runs its Cynosural Suppression upgrade (EveJS isCynoSuppressionUpgradeOnline): the system has a hub and the hub
    /// has the upgrade installed and online.
    /// shortcut: reads the stored online flag only; EveJS also needs the upgrade power state (sovModernState computeHubUpgradeSnapshot, the local power and
    /// workforce budget), which is not ported; upgrade with the sov upgrade budget.
    /// </summary>
    public async ValueTask<bool> IsCynoSuppressionOnlineAsync(long solarSystemId, CancellationToken cancellationToken)
    {
        if (solarSystemId <= 0)
            return false;
        var state = await readTable("sovereignty", cancellationToken);
        if (state["systems"] is not JsonObject systems)
            return false;
        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system)
                continue;
            var systemId = Positive(Integer(system["solarSystemID"]));
            if (systemId <= 0 &&
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out systemId))
                continue;
            if (systemId != solarSystemId)
                continue;
            if (NullablePositive(system["infrastructureHubID"]) is null)
                return false;
            return system["hubUpgradeConfiguration"] is JsonArray configured && configured.OfType<JsonObject>().Any(upgrade =>
                Positive(Integer(upgrade["typeID"])) == CynoSuppressionUpgradeTypeId &&
                upgrade["online"] is JsonValue flag && flag.TryGetValue(out bool isOnline) && isOnline);
        }
        return false;
    }

    public async ValueTask<IReadOnlyList<long>> ReadAllianceSystemsAsync(
        long allianceId,
        CancellationToken cancellationToken)
    {
        if (allianceId <= 0)
            return Array.Empty<long>();
        var snapshot = await ReadSnapshotAsync(cancellationToken);
        return snapshot.ClaimsBySolarSystemId
            .Where(pair => pair.Value.AllianceId == allianceId)
            .Select(pair => pair.Key)
            .OrderBy(systemId => systemId)
            .ToArray();
    }


    public async ValueTask<PyValue> ReadCurrentMapDataAsync(
        long locationId,
        IReadOnlyList<Eve.Sde.SdeRow> staticSystems,
        CancellationToken cancellationToken)
    {
        var columns = new[]
        {
            "locationID", "solarSystemID", "constellationID", "regionID", "ownerID",
            "allianceID", "corporationID", "claimStructureID", "infrastructureHubID",
            "stationID", "claimTime",
        };
        if (locationId <= 0)
            return Py.Rowset(columns, []);

        static long StaticId(System.Text.Json.JsonElement doc, string field) =>
            doc.TryGetProperty(field, out var value) && value.TryGetInt64(out var number) ? number : 0;

        static PyValue Id(JsonObject? row, string field, long fallback = 0)
        {
            var value = Positive(Integer(row?[field]));
            if (value <= 0)
                value = fallback;
            return value > 0 ? new PyInt(value) : PyNone.Instance;
        }

        static PyValue CurrentRow(JsonObject? row, long systemId, long constellationId, long regionId)
        {
            var allianceId = Positive(Integer(row?["allianceID"]));
            return Py.Tuple(
                Id(row, "locationID", systemId),
                Id(row, "solarSystemID", systemId),
                Id(row, "constellationID", constellationId),
                Id(row, "regionID", regionId),
                Id(row, "ownerID", allianceId),
                Id(row, "allianceID", allianceId),
                Id(row, "corporationID"),
                Id(row, "claimStructureID"),
                Id(row, "infrastructureHubID"),
                Id(row, "stationID"),
                new PyLong(Math.Max(0, Integer(row?["claimTime"]))));
        }

        var state = await readTable("sovereignty", cancellationToken);
        var systems = state["systems"] as JsonObject;
        var result = new List<PyValue>();
        foreach (var system in staticSystems.OrderBy(row => row.Key))
        {
            var constellationId = StaticId(system.Doc, "constellationID");
            var regionId = StaticId(system.Doc, "regionID");
            if (system.Key != locationId && constellationId != locationId && regionId != locationId)
                continue;

            var stored = systems?[system.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            if (stored?["currentData"] is JsonArray { Count: > 0 } currentRows)
            {
                foreach (var current in currentRows.OfType<JsonObject>())
                    result.Add(CurrentRow(current, system.Key, constellationId, regionId));
            }
            else
            {
                result.Add(CurrentRow(stored, system.Key, constellationId, regionId));
            }
        }

        return Py.Rowset(columns, result);
    }

    public async ValueTask<PyValue> ReadRecentSovActivityAsync(CancellationToken cancellationToken)
    {
        var columns = new[] { "solarSystemID", "ownerID", "oldOwnerID", "stationID", "changeTime" };
        var state = await readTable("sovereignty", cancellationToken);
        var systems = state["systems"] as JsonObject;
        if (systems is null)
            return Py.Rowset(columns, []);

        var changes = new List<(long SystemId, long OwnerId, long OldOwnerId, long StationId, long ChangeTime)>();
        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system || system["recentActivity"] is not JsonArray activityRows)
                continue;
            var storedSystemId = Positive(Integer(system["solarSystemID"]));
            if (storedSystemId <= 0 && long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedSystemId))
                storedSystemId = Positive(parsedSystemId);

            foreach (var activity in activityRows.OfType<JsonObject>())
            {
                var systemId = Positive(Integer(activity["solarSystemID"]));
                if (systemId <= 0)
                    systemId = storedSystemId;
                changes.Add((
                    systemId,
                    Positive(Integer(activity["ownerID"])),
                    Positive(Integer(activity["oldOwnerID"])),
                    Positive(Integer(activity["stationID"])),
                    Math.Max(0, Integer(activity["changeTime"]))));
            }
        }

        return Py.Rowset(columns, changes
            .OrderByDescending(change => change.ChangeTime)
            .ThenBy(change => change.SystemId)
            .Select(change => Py.Tuple(
                change.SystemId > 0 ? new PyInt(change.SystemId) : PyNone.Instance,
                change.OwnerId > 0 ? new PyInt(change.OwnerId) : PyNone.Instance,
                change.OldOwnerId > 0 ? new PyInt(change.OldOwnerId) : PyNone.Instance,
                change.StationId > 0 ? new PyInt(change.StationId) : PyNone.Instance,
                new PyLong(change.ChangeTime))));
    }

    private static Eve.Services.Core.SovStructureRecord? ReadStructure(JsonObject value, long systemId)
    {
        var itemId = Positive(Integer(value["itemID"] ?? value["structureID"]));
        var typeId = Positive(Integer(value["typeID"]));
        if (itemId <= 0 || typeId <= 0)
            return null;
        return new(
            itemId,
            typeId,
            NullablePositive(value["ownerID"]),
            NullablePositive(value["corporationID"] ?? value["ownerCorpID"]),
            NullablePositive(value["allianceID"]),
            Positive(Integer(value["solarSystemID"])) is var storedSystem && storedSystem > 0 ? storedSystem : systemId,
            NullableInt(value["campaignEventType"]),
            NullablePositive(value["campaignStartTimeFileTime"] ?? value["campaignStartTime"]),
            NumberMap(value["campaignScoresByTeam"] ?? value["campaignScores"]),
            NullablePositive(value["vulnerableStartTimeFileTime"] ?? value["vulnerableStartTime"]),
            NullablePositive(value["vulnerableEndTimeFileTime"] ?? value["vulnerableEndTime"]),
            Number(value["defenseMultiplier"], 1),
            Truthy(value["isCapital"]));
    }

    private static IReadOnlyDictionary<long, double>? NumberMap(JsonNode? value)
    {
        if (value is not JsonObject values)
            return null;
        var parsed = new Dictionary<long, double>();
        foreach (var (key, node) in values)
        {
            if (!long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
                continue;
            var number = Number(node, double.NaN);
            if (double.IsFinite(number))
                parsed[id] = number;
        }
        return parsed;
    }

    private static long? NullablePositive(JsonNode? value)
    {
        var number = Integer(value);
        return number > 0 ? number : null;
    }

    private static int? NullableInt(JsonNode? value)
    {
        var number = Integer(value, int.MinValue);
        return number == int.MinValue ? null : checked((int)number);
    }

    private static long Positive(long value) => value > 0 ? value : 0;

    private static long Integer(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static double Number(JsonNode? value, double fallback) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : fallback;

    private static bool Truthy(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean))
            return boolean;
        if (double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number != 0;
        return false;
    }
}
