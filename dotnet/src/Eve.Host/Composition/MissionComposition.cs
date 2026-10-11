using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Events;
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
    /// <summary>The offset of a mission dungeon from the pilot's position when it starts (metres): the rule of the eve.rs mission runtime.</summary>
    public const double MissionDungeonOffsetMetres = 20_000;

    /// <summary>The wave director of the dungeons (set by the space composition, which runs first).</summary>
    private Eve.Services.Space.Waves.DungeonWaveDirector? dungeonWavesDirector;

    /// <summary>The kill missions' runtime (A-X1), built with the agent missions.</summary>
    private MissionDungeonRuntime? missionDungeons;

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
        agentMissionAnnouncer = new Eve.Services.Core.AgentMissionAnnouncer(agentMissionStore, characterSessions);
        // the faction of each NPC corporation (SDE npcCorporations.factionID): the access rule reads the faction standing
        var npcFactions = (await sde.ReadAsync("npcCorporations", cancellationToken: CancellationToken.None))
            .Where(row => row.Doc.TryGetProperty("factionID", out var faction) && faction.ValueKind == System.Text.Json.JsonValueKind.Number)
            .ToDictionary(row => row.Key, row => row.Doc.GetProperty("factionID").GetInt64());
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
                // the game store's record, with the agents store's offers and accepted missions laid over it (AG-2 / A5)
                var state = await ReadGameStoreTableAsync("missionRuntimeState", ct);
                var character = (state["charactersByID"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
                var stored = await agentMissionStore.ReadAsync(characterId, ct);
                return character is null && stored.Count == 0 ? null : Eve.Services.Core.AgentMissionStoreRecords.Overlay(character, stored);
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
            rpcCallbacks: agentMissionRpcCallbacks,
            dialogue: new Eve.Services.Core.AgentDialogueSources(
                agentMissionStore,
                // the standings are the relations rows (subject: the faction, the corporation or the agent; object: the character);
                // a missing row is 0, the reference's value for an unknown standing
                async ValueTask<Eve.Services.Core.AgentStandings> (long characterId, long agentId, long corporationId, long factionId, CancellationToken ct) =>
                    new Eve.Services.Core.AgentStandings(
                        await foundations.Relations.ValueAsync(Eve.Relations.RelationKinds.Standing.Name, factionId, characterId, 0, cancellationToken: ct),
                        await foundations.Relations.ValueAsync(Eve.Relations.RelationKinds.Standing.Name, corporationId, characterId, 0, cancellationToken: ct),
                        await foundations.Relations.ValueAsync(Eve.Relations.RelationKinds.Standing.Name, agentId, characterId, 0, cancellationToken: ct)),
                // the trained level of a skill, read in its own transaction
                async ValueTask<int> (long characterId, int skillTypeId, CancellationToken ct) =>
                {
                    await using var connection = await source.OpenConnectionAsync(ct);
                    await using var transaction = await connection.BeginTransactionAsync(ct);
                    var level = await skills.LevelAsync(connection, transaction, characterId, skillTypeId, ct);
                    await transaction.CommitAsync(ct);
                    return level;
                },
                corporationId => npcFactions.GetValueOrDefault(corporationId),
                // the offer is the next step of the agent's epic arc (EpicArcOffers: the official arc sequence, the held-back sites kept
                // out by owner decision), else the basic kill mission of a level 4 or 5 security agent (BasicKillOffers). Other basic
                // agents have no official mission link and offer nothing.
                async ValueTask<Eve.Agents.AgentMissionContent?> (AgentMissionAgentData agent, long characterId, CancellationToken ct) =>
                {
                    var completed = await agentMissionStore.ReadCompletedAsync(characterId, ct);
                    var failed = await agentMissionStore.ReadFailedAsync(characterId, ct);
                    var now = DateTimeOffset.UtcNow;
                    var completedAt = completed.GroupBy(item => item.MissionId).ToDictionary(group => group.Key, group => group.Max(item => item.CompletedAt));
                    if ((Eve.Services.Core.EpicArcOffers.Pick(agentMissionStatic, agent.AgentId, completed, now, failed) ??
                            Eve.Services.Core.BasicKillOffers.Pick(agentMissionStatic, agent, completedAt, now,
                                dungeon => dungeonWavesDirector?.StartRefusalOf(dungeon) is null)) is not { } missionId ||
                        agentMissionStatic.Mission(missionId) is not { } mission)
                        return null;
                    return new Eve.Agents.AgentMissionContent(mission.MissionId, mission.NameId, mission.NameEn, "", false);
                },
                () => DateTimeOffset.UtcNow,
                // a talk step completes when the pilot talks to the next agent, in the station or in space in the agent's system (A-X3)
                async (characterId, agent, atLocation, ct) =>
                {
                    if (missionDungeons is null) return;
                    var together = agent.IsInSpace
                        ? agent.SolarSystemId is { } system && spaceSessions is not null && spaceSessions.IsPilotInSystem(characterId, (int)system)
                        : atLocation;
                    await missionDungeons.OnConversationAsync(characterId, agent.AgentId, together, ct);
                }));
        await AttachMissionDungeonsAsync();
    }

    /// <summary>
    /// The kill missions run their dungeons: a durable sweep starts the dungeon of an accepted kill mission when the pilot is in space in the
    /// agent's system, and the dungeon's outcome (content.dungeon.*) decides the mission. Registered before the bus starts.
    /// </summary>
    private async Task AttachMissionDungeonsAsync()
    {
        if (dungeonWavesDirector is null)
        {
            Console.Error.WriteLine("eve-host: missions: kill missions cannot run dungeons: the wave director is not composed");
            return;
        }
        var host = new SpaceMissionDungeonHost(
            (characterId, systemId) => spaceSessions is not null && spaceSessions.IsPilotInSystem(characterId, systemId),
            StartMissionDungeonAsync,
            CreditMissionIskAsync);
        missionDungeons = new MissionDungeonRuntime(
            agentMissionStore,
            agentMissionStatic,
            host,
            agentId => agentMissionAgentById.TryGetValue(agentId, out var agent) && agent.SolarSystemId is { } system
                ? new MissionAgentPlace(system, agent.CorporationId)
                : null,
            () => DateTimeOffset.UtcNow);
        var runtime = missionDungeons;
        foundations.Scheduler.Register(MissionDungeonSweepJob, (context, token) => runtime.SweepAsync(token));
        await foundations.Scheduler.EnsureRecurringAsync(MissionDungeonSweepJob, "sweep", TimeSpan.FromSeconds(10));
        foreach (var (topic, outcome) in new[] { ("content.dungeon.completed", "completed"), ("content.dungeon.failed", "failed"),
                     ("content.dungeon.abandoned", "abandoned"), ("content.dungeon.expired", "expired") })
        {
            var subscriber = "mission.dungeons." + outcome;
            foundations.Events.Subscribe(subscriber, [topic],
                (EventContext context, CancellationToken token) => MissionDungeonOutcomeAsync(runtime, outcome, context, token));
        }
    }

    private const string MissionDungeonSweepJob = "mission.dungeons.sweep";

    /// <summary>The SDE group of the agents in space (type 30887 "Tevis Jak's Epithal" is group 517).</summary>
    private const int AgentInSpaceGroupId = 517;

    /// <summary>The agents in space of a system go into the scene when a pilot enters it (placement: InSpaceAgentPlacement).</summary>
    private void PutInSpaceAgents(int solarSystemId)
    {
        if (spaceContainers is null || solarSystems.Get(solarSystemId) is not { } system) return;
        foreach (var agent in agentMissionAgentRows)
        {
            if (!agent.IsInSpace || agent.SolarSystemId != solarSystemId || agent.TypeId is not { } typeId) continue;
            var position = Eve.Services.Core.InSpaceAgentPlacement.Position(agent.AgentId, system.X, system.Y, system.Z);
            spaceContainers.PutAgentInSpace(solarSystemId, agent.AgentId, (int)typeId, AgentInSpaceGroupId, (int)agent.CorporationId, position);
        }
    }

    private static async Task MissionDungeonOutcomeAsync(MissionDungeonRuntime runtime, string outcome, EventContext context, CancellationToken token)
    {
        using var document = JsonDocument.Parse(context.Event.PayloadJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("owner", out var ownerElement) || !ownerElement.TryGetInt64(out var owner) ||
            !root.TryGetProperty("instanceId", out var instanceElement) || !instanceElement.TryGetInt64(out var instanceId) ||
            !root.TryGetProperty("definition", out var definitionElement) || definitionElement.ValueKind != JsonValueKind.String)
            return;
        await runtime.OnDungeonOutcomeAsync(outcome, owner, definitionElement.GetString()!, instanceId, token);
    }

    /// <summary>
    /// Starts the mission dungeon in front of the pilot: the origin is the pilot's position offset by <see cref="MissionDungeonOffsetMetres"/> along x.
    /// A pilot without a known position is refused by name.
    /// </summary>
    private async Task<string?> StartMissionDungeonAsync(long characterId, int solarSystemId, long dungeonId, CancellationToken cancellationToken)
    {
        if (dungeonWavesDirector is null || spaceSessions is null) return "the space is not running";
        var session = FleetSessionForCharacter(characterId);
        if (session is null) return "the pilot is not logged in";
        var shipId = spaceSessions.ShipIdOf(session);
        if (shipId is not > 0) return "the pilot has no ship in space";
        var position = await spaceSessions.PositionOfAsync(session, shipId.Value, cancellationToken);
        if (position is not { } at) return "the pilot's position is not known";
        var origin = new[] { at.X + MissionDungeonOffsetMetres, at.Y, at.Z };
        var start = await dungeonWavesDirector.StartAsync(characterId, solarSystemId, origin, dungeonId, cancellationToken);
        return start.Started ? null : start.Refusal ?? "the dungeon did not start";
    }

    /// <summary>The ISK of a mission reward: one idempotent wallet adjustment, in its own transaction.</summary>
    private async Task CreditMissionIskAsync(long characterId, long amount, long counterpartyId, int refTypeId, string reason, string operationKey, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await economyWallets.AdjustAsync(connection, transaction, characterId, amount, counterpartyId, refTypeId, reason,
            now(), operationKey, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>The host's side of <see cref="IMissionDungeonHost"/>: the space's pilot presence, the wave director's start and the wallet.</summary>
internal sealed class SpaceMissionDungeonHost(
    Func<long, int, bool> isPilotInSystem,
    Func<long, int, long, CancellationToken, Task<string?>> startDungeon,
    Func<long, long, long, int, string, string, CancellationToken, Task> creditIsk) : IMissionDungeonHost
{
    public bool IsPilotInSystem(long characterId, int solarSystemId) => isPilotInSystem(characterId, solarSystemId);

    public Task<string?> StartDungeonAsync(long characterId, int solarSystemId, long dungeonId, CancellationToken cancellationToken) =>
        startDungeon(characterId, solarSystemId, dungeonId, cancellationToken);

    public Task CreditIskAsync(long characterId, long amount, long counterpartyId, int refTypeId, string reason, string operationKey, CancellationToken cancellationToken) =>
        creditIsk(characterId, amount, counterpartyId, refTypeId, reason, operationKey, cancellationToken);
}
