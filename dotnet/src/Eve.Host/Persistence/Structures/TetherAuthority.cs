using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using Eve.Inventory;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>ST-4 authorization reads the existing structure owner and docking policy; SDE supplies all geometry.</summary>
public sealed class TetherAuthority(
    NpgsqlDataSource source, StructureStore structures, DogmaStatic statics,
    IStructureDirectoryReadCallbacks directory,
    Func<long, long, CancellationToken, Task<bool>> dockingAllowed,
    Func<ClientSession, int?> shipTypeOf,
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long, CancellationToken, Task<long?>> corporationFaction,
    CharacterShips ships, PgItemStore items, PgModuleStateStore conditions, GameStoreCache cache,
    Func<ClientSession, long, bool> canRepairTetheredShip)
{
    private const int RadiusAttribute = 162;
    private const int TetherRangeAttribute = 2268;
    private const string RestrictionsTable = "structureTetherRestrictions";
    private readonly ConcurrentDictionary<long, TetherGeometry> _geometry = new();

    public async Task<IReadOnlyList<SimTetherCandidate>> ReadTetherCandidatesAsync(ClientSession session, CancellationToken token)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("solarsystemid2") is not { } system
            || shipTypeOf(session) is not { } shipTypeId)
            return [];
        await statics.EnsureTypesAsync([shipTypeId], token);
        var shipType = statics.Type(shipTypeId);
        if (shipType is null || shipType.CategoryId != statics.CategoryId("Ship")) return [];
        var ids = await directory.GetStructureIdsInSolarSystemAsync(system, token);
        if (ids.Count == 0) return [];
        var sovereignty = await readTable("sovereignty", token);
        var systemRow = (sovereignty["systems"] as JsonObject)?[system.ToString(CultureInfo.InvariantCulture)] as JsonObject;
        var occupier = FwOccupier(systemRow);
        if (occupier < 0) return [];
        var pilotFaction = session.GetInt("warfactionid") ?? 0;
        if (FwEnemy(pilotFaction, occupier)) return [];
        await using var connection = await source.OpenConnectionAsync(token);
        var records = (await structures.ListInSystemAsync(connection, null, checked((int)system), token))
            .Where(r => ids.Contains(r.Snapshot.StructureId)).ToArray();
        await statics.EnsureTypesAsync(records.Select(r => r.TypeId), token);
        var candidates = new List<SimTetherCandidate>();
        foreach (var record in records)
        {
            if (!Eligible(record.Snapshot) || statics.Type(record.TypeId) is not { } type
                || type.CategoryId != statics.CategoryId("Structure")
                || !type.Attributes.TryGetValue(TetherRangeAttribute, out var range) || !double.IsFinite(range) || range <= 0
                || !type.Attributes.TryGetValue(RadiusAttribute, out var radius) || !double.IsFinite(radius) || radius <= 0)
                continue;
            if (FwEnemy(await corporationFaction(record.OwnerCorporationId, token) ?? 0, occupier)
                || !await dockingAllowed(characterId, record.Snapshot.StructureId, token))
                continue;
            _geometry[record.Snapshot.StructureId] = new TetherGeometry(radius, range);
            candidates.Add(new SimTetherCandidate(record.Snapshot.StructureId, range));
        }
        return candidates;
    }

    public async Task<TetherGeometry?> ReadTetherGeometryAsync(long structureId, CancellationToken token)
    {
        if (_geometry.TryGetValue(structureId, out var geometry)) return geometry;
        if (await structures.GetAsync(structureId, token) is not { } record) return null;
        await statics.EnsureTypesAsync([record.TypeId], token);
        if (statics.Type(record.TypeId) is not { } type
            || !type.Attributes.TryGetValue(TetherRangeAttribute, out var range) || !double.IsFinite(range) || range <= 0
            || !type.Attributes.TryGetValue(RadiusAttribute, out var radius) || !double.IsFinite(radius) || radius <= 0)
            return null;
        return _geometry[structureId] = new TetherGeometry(radius, range);
    }

    public async Task<long> ReadTetherBlockUntilAsync(long characterId, CancellationToken token)
    {
        await cache.LoadAsync(RestrictionsTable, token);
        var result = cache.Read(RestrictionsTable, $"/restrictions/{characterId}");
        if (result.Data is not JsonObject row) return 0;
        return row["factionalWarfareBlocked"]?.ToString() == "true" ? long.MaxValue : Number(row["tetherDelayUntilMs"]);
    }

    public async Task WriteTetherBlockUntilAsync(long characterId, long untilMs, CancellationToken token)
    {
        await cache.LoadAsync(RestrictionsTable, token);
        var existing = cache.Read(RestrictionsTable, $"/restrictions/{characterId}").Data as JsonObject;
        var row = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        row["characterID"] = characterId;
        row["tetherDelayUntilMs"] = untilMs;
        var result = cache.Write(RestrictionsTable, $"/restrictions/{characterId}", row);
        if (!result.Success) throw new InvalidOperationException($"Cannot persist tether delay: {result.ErrorMsg}");
        await cache.FlushTableAsync(RestrictionsTable, token);
    }

    public async Task<bool> RepairTetheredItemsAsync(ClientSession session, long expectedShipId, CancellationToken token)
    {
        if (expectedShipId <= 0 || !canRepairTetheredShip(session, expectedShipId)) return false;
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") != expectedShipId
            || await ships.LoadAsync(characterId, token) is not { } snapshot || snapshot.Ship.Id != expectedShipId)
            return false;
        if (session.CharacterId != characterId || session.GetInt("shipid") != expectedShipId
            || !canRepairTetheredShip(session, expectedShipId)) return false;
        var healed = await ships.HealAsync(characterId, expectedShipId, token);
        if (healed is null || !canRepairTetheredShip(session, expectedShipId)) return false;
        var repaired = healed.Modules.Count > 0;
        var bay = (await items.InLocationAsync(expectedShipId, characterId, token))
            .Where(i => i.Flag == CharacterShips.DroneBayFlag).ToArray();
        await statics.EnsureTypesAsync(bay.Select(i => i.TypeId), token);
        var drones = bay.Where(i => statics.Type(i.TypeId)?.CategoryId == statics.CategoryId("Drone")).ToArray();
        var moduleConditions = await conditions.ModuleConditionsAsync(drones.Select(i => i.Id), token);
        var mended = new Dictionary<long, ModuleCondition>();
        foreach (var drone in drones)
        {
            var condition = await conditions.ShipConditionAsync(drone.Id, token);
            if (!canRepairTetheredShip(session, expectedShipId) || session.GetInt("shipid") != expectedShipId) return repaired;
            if (condition != ShipCondition.Full)
            {
                await conditions.SetShipConditionAsync(drone.Id, ShipCondition.Full, token);
                repaired = true;
            }
            if (moduleConditions.TryGetValue(drone.Id, out var module) && module != ModuleCondition.Mended)
            {
                mended[drone.Id] = ModuleCondition.Mended;
                repaired = true;
            }
        }
        if (!canRepairTetheredShip(session, expectedShipId) || session.GetInt("shipid") != expectedShipId) return repaired;
        await conditions.SetModuleConditionsAsync(mended, token);
        return repaired;
    }

    public static bool Eligible(StructureSnapshot snapshot) =>
        snapshot.CoreInstalled && snapshot.DestroyedMs == 0 && snapshot.Upkeep is UpkeepState.FullPower or UpkeepState.LowPower
        && snapshot.State is StructureState.OnliningVulnerable or StructureState.ShieldVulnerable
            or StructureState.ArmorReinforce or StructureState.ArmorVulnerable or StructureState.HullReinforce
            or StructureState.HullVulnerable or StructureState.FobInvulnerable;

    // V24.01 facwarCommon: occupation enemies and the opposing insurgency factions.
    public static bool FwEnemy(long faction, long occupier) => faction switch
    {
        500001 => occupier is 500004 or 500010,
        500004 => occupier is 500001 or 500010,
        500003 => occupier is 500002 or 500011,
        500002 => occupier is 500003 or 500011,
        500010 => occupier is 500001 or 500004,
        500011 => occupier is 500003 or 500002,
        _ => false,
    };
    private static long FwOccupier(JsonObject? system) =>
        Number(system?["warzoneID"] ?? system?["warZoneID"]) > 0
            ? new[] { "factionID", "ownerFactionID", "occupierID", "fwOccupierID" }.Select(key => Number(system?[key])).FirstOrDefault(value => value > 0, -1) : 0;
    private static long Number(JsonNode? node) => long.TryParse(node?.ToString(), CultureInfo.InvariantCulture, out var value) ? value : 0;
}
