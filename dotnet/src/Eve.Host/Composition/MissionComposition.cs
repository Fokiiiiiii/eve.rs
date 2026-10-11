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

internal sealed partial class CharacterServicesComposition
{
    async Task<JsonObject?> ReadCharacterGameStoreRowAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0) return null;
        var charactersState = await ReadGameStoreTableAsync("characters", cancellationToken);
        return charactersState[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
    }

    async Task<(JsonObject? MissionRecord, JsonObject? DungeonState, bool? InActiveDungeon)> ReadMissionTrackerContextAsync(
        long characterId, long agentId, ClientSession session, CancellationToken cancellationToken)
    {
        var missionRecord = await agentMissionService.ReadMissionRecordAsync(characterId, agentId, cancellationToken);
        if (missionRecord is null) return (null, null, false);
        var dungeonState = await ReadGameStoreTableAsync("dungeonRuntimeState", cancellationToken);
        if (spaceSessions is null) return (missionRecord, dungeonState, false);
        var shipId = spaceSessions.ShipIdOf(session);
        if (shipId is not > 0) return (missionRecord, dungeonState, null);
        var position = await spaceSessions.PositionOfAsync(session, shipId.Value, cancellationToken);
        var currentSystemId = position?.SolarSystemId ??
            session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
        var active = MissionTrackerGameStoreProjection.IsInActiveMissionDungeon(
            missionRecord,
            dungeonState,
            characterId,
            currentSystemId,
            shipId.Value,
            position?.X,
            position?.Y,
            position?.Z,
            spaceSessions.IsWarping(session));
        return (missionRecord, dungeonState, active);
    }

    async Task<AirNpeCharacterState?> ReadAirNpeCharacterStateAsync(long characterId, CancellationToken cancellationToken)
    {
        var character = await characters.GetAsync(characterId, cancellationToken);
        return character is null ? null : new AirNpeCharacterState(character.TutorialEntryMode, character.AirNpeState);
    }

    async ValueTask<Eve.Services.Core.ShipStanceChange?> SetShipStanceAsync(
        long shipId, int stanceId, long characterId, CancellationToken cancellationToken)
    {
        var ship = await items.GetAsync(shipId, cancellationToken);
        if (ship is null)
            return new(false, ErrorCode: "SHIP_NOT_FOUND");
        if (ship.OwnerId != characterId)
            return new(false, ErrorCode: "SHIP_NOT_OWNED");
        if (!Eve.Services.Core.ShipStanceMgrService.StanceModifiersByShipType.TryGetValue(ship.TypeId, out var stanceTypes))
            return new(false, ErrorCode: "SHIP_STANCE_UNSUPPORTED");
        if (!stanceTypes.TryGetValue(stanceId, out var modifierTypeId))
            return new(false, ErrorCode: "SHIP_STANCE_INVALID");

        await dogma.EnsureTypesAsync([ship.TypeId], cancellationToken);
        var cooldownMs = dogma.Type(ship.TypeId)?.Attributes.GetValueOrDefault(1985, 0d) ?? 0d;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var lockedShip = await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken);
        if (lockedShip is null)
            return new(false, ErrorCode: "SHIP_NOT_FOUND");
        if (lockedShip.OwnerId != characterId)
            return new(false, ErrorCode: "SHIP_NOT_OWNED");
        if (lockedShip.TypeId != ship.TypeId)
            return new(false, ErrorCode: "SHIP_NOT_FOUND");

        var modifiers = await items.LockInLocationAsync(
            connection, transaction, shipId, characterId, Eve.Inventory.ItemFlags.HiddenModifiers,
            stanceTypes.Values.ToArray(), cancellationToken);
        var currentStanceId = modifiers.OrderBy(item => item.Id)
            .Select(item => stanceTypes.FirstOrDefault(pair => pair.Value == item.TypeId).Key)
            .FirstOrDefault(id => id > 0);
        if (currentStanceId <= 0)
            currentStanceId = 1;

        var exactTarget = modifiers.Count == 1 && modifiers[0].TypeId == modifierTypeId;
        var changed = currentStanceId != stanceId;
        var lastSwitchMs = modifiers.Select(ParseShipStanceSwitchTime)
            .Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max();
        if (!changed && exactTarget)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(true, currentStanceId);
        }

        var nowMs = now();
        if (changed && cooldownMs <= 0)
            return new(false, ErrorCode: "SHIP_STANCE_COOLDOWN_UNAVAILABLE");
        if (changed && lastSwitchMs > 0 && nowMs < lastSwitchMs + cooldownMs)
            return new(false, ErrorCode: "SHIP_STANCE_COOLDOWN");

        foreach (var modifier in modifiers)
            await items.DeleteAsync(connection, transaction, modifier.Id, cancellationToken);
        var switchedAt = changed ? nowMs : lastSwitchMs > 0 ? lastSwitchMs : (long?)null;
        var customInfo = switchedAt is { } timestamp
            ? $"shipStance:{stanceId}:switchedAtMs:{timestamp}"
            : $"shipStance:{stanceId}";
        await items.CreateAsync(connection, transaction,
            new Eve.Inventory.NewItem(modifierTypeId, characterId, shipId,
                Eve.Inventory.ItemFlags.HiddenModifiers, CustomInfo: customInfo), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, currentStanceId, Changed: changed);
    }

    static long? ParseShipStanceSwitchTime(Eve.Inventory.Item item)
    {
        var parts = item.CustomInfo?.Split(':');
        return parts is { Length: 4 } && parts[0] == "shipStance" && parts[2] == "switchedAtMs" &&
               long.TryParse(parts[3], System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var timestamp) && timestamp >= 0
            ? timestamp
            : null;
    }

    async ValueTask<JsonObject?> ReadKillmailAsync(long killId, CancellationToken cancellationToken)
    {
        var state = await ReadGameStoreTableAsync("killmails", cancellationToken);
        var key = killId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var record = (state["records"] as JsonObject)?[key] as JsonObject;
        return record?.DeepClone() as JsonObject;
    }

    private async Task AgentMissions()
    {
        agentMissionAgentById = agentMissionAgentRows.ToDictionary(agent => agent.AgentId);
        // The official mission build (sde_mission). Nothing is read from the EveJS authority tables.
        agentMissionStatic = await MissionStaticCatalog.LoadAsync(new SdeReader(source, MissionStaticCatalog.DefaultSchema));
        if (agentMissionStatic.IsLoaded)
        {
            Console.Error.WriteLine($"eve-host: missions: build {agentMissionStatic.Build}, {agentMissionStatic.Missions.Count} missions, {agentMissionStatic.EpicArcs.Count} epic arcs");
            var levelsByProvenance = string.Join(", ", agentMissionStatic.LevelTags.All.GroupBy(tag => tag.Provenance).OrderBy(group => group.Key).Select(group => $"{group.Key} {group.Count()}"));
            Console.Error.WriteLine($"eve-host: missions: level tags {agentMissionStatic.LevelTags.Count} ({levelsByProvenance})");
            var arcsByAvailability = string.Join(", ", agentMissionStatic.EpicArcs.GroupBy(arc => agentMissionStatic.ArcAvailability(arc.ArcId)).OrderBy(group => group.Key).Select(group => $"{group.Key} {group.Count()}"));
            Console.Error.WriteLine($"eve-host: missions: epic arcs {agentMissionStatic.EpicArcs.Count} ({arcsByAvailability}); offered {agentMissionStatic.OfferableEpicArcs.Count}" + (agentMissionStatic.IncludesUnavailableArcs ? " (EVE_MISSION_INCLUDE_UNAVAILABLE_ARCS: every arc is offered)" : " (only in-client-build arcs; EVE_MISSION_INCLUDE_UNAVAILABLE_ARCS=1 offers the rest)"));
            foreach (var (area, reason) in agentMissionStatic.Gaps())
                Console.Error.WriteLine($"eve-host: missions: not answered, {area}: {reason}");
        }
        else
        {
            Console.Error.WriteLine($"eve-host: missions: {agentMissionStatic.UnavailableReason}");
        }
        agentMissionRpcCallbacks = new AgentMissionRpcCallbacks(
            ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>(agentMissionAgentRows),
            ReadDisabledMissions: _ => ValueTask.FromResult(agentMissionStatic.DisabledMissionIds),
            ReadPlausibleMissionIds: (agentId, _) => ValueTask.FromResult(
                agentMissionStatic.PlausibleMissionIds(agentId)),
            ReadEpicArcMessages: _ => ValueTask.FromResult<JsonObject?>(agentMissionStatic.EpicArcMessageMaps),
            ReadSolarSystemOfAgent: (agentId, _) => ValueTask.FromResult<long?>(
                agentMissionAgentById.TryGetValue(agentId, out var agent) ? agent.SolarSystemId : null));
        agentMissionService = new AgentMissionService(
            async (characterId, ct) =>
            {
                var state = await ReadGameStoreTableAsync("missionRuntimeState", ct);
                return (state["charactersByID"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            },
            async (characterId, bookmarkIds, ct) =>
            {
                var state = await ReadGameStoreTableAsync("bookmarks", ct);
                var records = state["records"] as JsonObject;
                var bookmarks = new Dictionary<long, JsonObject>();
                foreach (var bookmarkId in bookmarkIds.Distinct())
                {
                    if (records?[bookmarkId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject row)
                        bookmarks[bookmarkId] = row;
                }
                return bookmarks;
            },
            async (characterId, ct) =>
            {
                var state = await ReadGameStoreTableAsync("researchRuntimeState", ct);
                return (state["charactersByID"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonArray;
            },
            (characterId, agentId, session) =>
            {
                var stationId = session.GetInt("stationid");
                return stationId is { } station &&
                    (!agentLocationById.TryGetValue(agentId, out var agentStationId) || station == agentStationId);
            },
            rpcCallbacks: agentMissionRpcCallbacks);
    }
}
