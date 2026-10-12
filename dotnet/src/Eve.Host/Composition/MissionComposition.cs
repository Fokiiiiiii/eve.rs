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
        await LoadClientMissionMessagesAsync();
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
                agentMissionAgentById.TryGetValue(agentId, out var agent) ? agent.SolarSystemId : null),
            ReadHeldMission: ReadHeldMissionAsync);
        // the R&D agents: the science fields from the SDE, and the datacore purchase in the store's transaction (AG-4 / A6)
        var researchPort = new PersistenceAgentResearch(source, agentResearchProjects, economyWallets, items, itemTypes, characterSessions,
            await PersistenceAgentResearch.LoadFieldsAsync(sde, CancellationToken.None), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // the locator agents: the pilot by name, the place from the online session and the SDE, the fee and the result (AG-4 / A7)
        var locatorPort = new PersistenceAgentLocator(source, agentLocates, characters, characterSessions, solarSystems, economyWallets,
            foundations.Events, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // the located notification: written by the subscriber, keyed by the event so a retry writes it once (the outbox row is the fee's)
        foundations.Events.Subscribe("agents.locate-notice", [LocateDelivered.Topic], async (context, ct) =>
        {
            var located = context.Event.Payload<LocateDelivered>()!;
            await mailSendWriter!.WriteLocateNotificationAsync(located.CharacterId, located.AgentId, located.Data, $"locate:{context.Event.Id}", ct);
        });
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
                                dungeon => dungeonWavesDirector?.StartRefusalOf(dungeon) is null) ??
                            Eve.Services.Core.BasicKillOffers.PickCourier(agentMissionStatic, agent, completedAt, now) ??
                            Eve.Services.Core.BasicKillOffers.PickMining(agentMissionStatic, agent, completedAt, now,
                                dungeon => dungeonWavesDirector?.StartRefusalOf(dungeon) is null, IsMiningMission)) is not { } missionId ||
                        agentMissionStatic.Mission(missionId) is not { } mission)
                        return null;
                    // the mission type label is the agent's kind (encounter, epicArc): the journal shows no mission without one
                    return new Eve.Agents.AgentMissionContent(mission.MissionId, mission.NameId, mission.NameEn, agent.MissionKind, agent.ImportantMission);
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
                }, researchPort, locatorPort,
                async (characterId, agentId, token) => { if (missionDungeons is { } dungeons) await dungeons.OnAcceptedAsync(characterId, agentId, token); },
                async (characterId, agentId, token) => { if (missionDungeons is { } dungeons) await dungeons.OnQuitAsync(characterId, agentId, token); }));
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
            CreditMissionIskAsync,
            CompleteMissionAsync,
            GrantObjectiveItemAsync,
            HeldQuantityAsync,
            GrantCargoAsync,
            TakeCargoAsync);
        missionDungeons = new MissionDungeonRuntime(
            agentMissionStore,
            agentMissionStatic,
            host,
            agentId => agentMissionAgentById.TryGetValue(agentId, out var agent) && agent.SolarSystemId is { } system
                ? new MissionAgentPlace(system, agent.CorporationId, agent.BaseLocationId)
                : null,
            () => DateTimeOffset.UtcNow,
            (missionId, characterId, place, level) => stargateGraph is { } gates
                ? MissionCourierRules.Destination(missionId, characterId, place.BaseStationId!.Value, (int)place.SolarSystemId, level, gates, stations.All)
                : null,
            IsTradeMission);
        var runtime = missionDungeons;
        if (spaceSessions is not null) spaceSessions.PilotDocked = (characterId, stationId, token) => runtime.OnDockedAsync(characterId, stationId, token);
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

    /// <summary>The localization IDs of the missions' texts from the client data (see LoadClientMissionMessagesAsync).</summary>
    private IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>> clientMissionMessages = new Dictionary<long, IReadOnlyDictionary<string, long>>();

    /// <summary>The client content template of each mission (see ClientMissionTemplates).</summary>
    private IReadOnlyDictionary<long, string> clientMissionTemplates = new Dictionary<long, string>();

    /// <summary>Whether the mission is a storyline trade mission of the client: handed in at its agent like a fetch (DEVIATIONS X13).</summary>
    private bool IsTradeMission(long missionId) => clientMissionTemplates.GetValueOrDefault(missionId) == ClientMissionTemplates.TradeTemplate;

    /// <summary>Whether the mission is a basic mining mission of the client: a dungeon whose ore is handed in at its agent.</summary>
    private bool IsMiningMission(long missionId) => clientMissionTemplates.GetValueOrDefault(missionId) == ClientMissionTemplates.MiningTemplate;

    /// <summary>
    /// Loads the client's mission text IDs and content templates (the embedded client data, or the export named by EVE_CLIENT_SDE_DIR where it holds
    /// the file) into the schema <c>client_sde</c> (replaced each start). The briefing then carries its text ID.
    /// </summary>
    private async Task LoadClientMissionMessagesAsync()
    {
        var directory = Environment.GetEnvironmentVariable(ClientStaticData.DirectoryVariable);
        var (messages, skipped) = ClientMissionMessages.Parse(ClientMissionMessages.Open(directory));
        clientMissionTemplates = ClientMissionTemplates.Parse(ClientMissionMessages.Open(directory));
        var missionIds = new List<long>();
        var keys = new List<string>();
        var ids = new List<long>();
        foreach (var (missionId, texts) in messages)
            foreach (var (key, id) in texts)
            {
                missionIds.Add(missionId);
                keys.Add(key);
                ids.Add(id);
            }
        await using var connection = await source.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await using (var setup = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS client_sde;
            CREATE TABLE IF NOT EXISTS client_sde.mission_messages (
                mission_id bigint NOT NULL, message_key text NOT NULL, message_id bigint NOT NULL, PRIMARY KEY (mission_id, message_key));
            DELETE FROM client_sde.mission_messages;
            INSERT INTO client_sde.mission_messages SELECT * FROM unnest(@m::bigint[], @k::text[], @v::bigint[]);
            """, connection, transaction))
        {
            setup.Parameters.Add(new NpgsqlParameter("m", missionIds.ToArray()));
            setup.Parameters.Add(new NpgsqlParameter("k", keys.ToArray()));
            setup.Parameters.Add(new NpgsqlParameter("v", ids.ToArray()));
            await setup.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        clientMissionMessages = messages;
        Console.Error.WriteLine($"eve-host: missions: client texts {messages.Count} missions, {ids.Count} message IDs ({skipped} rows not read)");
    }

    /// <summary>The held mission of the character with the bound agent (offered or accepted), with its briefing text ID.</summary>
    private async ValueTask<Eve.Services.Core.HeldMissionView?> ReadHeldMissionAsync(long characterId, long agentId, CancellationToken cancellationToken)
    {
        var record = await agentMissionStore.ReadAsync(characterId, agentId, cancellationToken);
        if (record is null || record.State is not (Eve.Agents.AgentMissionState.Offered or Eve.Agents.AgentMissionState.Accepted or Eve.Agents.AgentMissionState.Completed)) return null;
        if (agentMissionStatic.Mission(record.Content.MissionId) is not { } mission) return null;
        long? briefing = clientMissionMessages.TryGetValue(mission.MissionId, out var texts) && texts.TryGetValue(ClientMissionMessages.BriefingKey, out var id) ? id : null;
        return new Eve.Services.Core.HeldMissionView(mission, record, briefing, await FetchObjectiveAsync(characterId, record, cancellationToken),
            await CourierObjectiveAsync(characterId, record, cancellationToken));
    }

    /// <summary>
    /// The cargo of a courier mission: the SDE's item and its volume, the pilot's holding, the pickup (the agent's station) and the destination of the
    /// same seed the runtime uses. Null when the mission carries no courier objective, or no destination is in range.
    /// </summary>
    private async ValueTask<Eve.Services.Core.CourierObjectiveView?> CourierObjectiveAsync(long characterId, Eve.Agents.AgentMissionRecord record, CancellationToken cancellationToken)
    {
        if (IsTradeMission(record.Content.MissionId)) return null;
        if (agentMissionStatic.Mission(record.Content.MissionId)?.Courier is not { ObjectiveTypeId: { } typeId, ObjectiveQuantity: > 0 } courier) return null;
        if (!agentMissionAgentById.TryGetValue(record.AgentId, out var agent) || agent.BaseLocationId is not { } pickup || agent.SolarSystemId is not { } system) return null;
        var level = agentMissionStatic.Level(record.Content.MissionId).Level;
        if (stargateGraph is not { } gates || Eve.Services.Core.MissionCourierRules.Destination(record.Content.MissionId, characterId, pickup, (int)system, level, gates, stations.All) is not { } destination) return null;
        var type = dogma.Type((int)typeId);
        var volume = type is not null && dogma.AttributeId("volume") is { } attribute ? type.Attributes.GetValueOrDefault(attribute, 0) : 0;
        var held = await HeldQuantityAsync(characterId, (int)typeId, cancellationToken);
        return new Eve.Services.Core.CourierObjectiveView((int)typeId, courier.ObjectiveQuantity, volume, held >= courier.ObjectiveQuantity, pickup, destination.Id, agent.CorporationId);
    }

    /// <summary>
    /// The objective item of a fetch step: the SDE's type and volume, the pilot's holding, and the agent's station and corporation (where the item is
    /// handed in). Null when the mission fetches nothing or the agent has no station.
    /// </summary>
    private async ValueTask<Eve.Services.Core.FetchObjectiveView?> FetchObjectiveAsync(long characterId, Eve.Agents.AgentMissionRecord record, CancellationToken cancellationToken)
    {
        var fetched = IsTradeMission(record.Content.MissionId)
            ? agentMissionStatic.Mission(record.Content.MissionId)?.Courier is { ObjectiveTypeId: { } tradeType, ObjectiveQuantity: > 0 } trade ? (tradeType, trade.ObjectiveQuantity) : ((long, long)?)null
            : agentMissionStatic.Mission(record.Content.MissionId)?.Kill is { ObjectiveTypeId: { } killType, ObjectiveQuantity: > 0 } kill ? (killType, kill.ObjectiveQuantity) : ((long, long)?)null;
        if (fetched is not { } item) return null;
        var (typeId, quantity) = item;
        if (!agentMissionAgentById.TryGetValue(record.AgentId, out var agent) || agent.BaseLocationId is not { } station) return null;
        var type = dogma.Type((int)typeId);
        var volume = type is not null && dogma.AttributeId("volume") is { } attribute ? type.Attributes.GetValueOrDefault(attribute, 0) : 0;
        var held = await HeldQuantityAsync(characterId, (int)typeId, cancellationToken);
        return new Eve.Services.Core.FetchObjectiveView((int)typeId, quantity, volume, held, station, agent.CorporationId);
    }

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

    /// <summary>
    /// A mission's completion, its consumed objective items and its standings in one transaction (the mission store, the items and the relations
    /// store share the database): the completion is journalled, the items leave the pilot, the standings change, and the session is told after the
    /// commit. False when the mission was not accepted or the items are short.
    /// </summary>
    private Task<bool> CompleteMissionAsync(long characterId, long agentId, DateTimeOffset now, string reference,
        IReadOnlyList<MissionStandingGain> standings, IReadOnlyList<(int TypeId, long Quantity, string? Tag)> consume, CancellationToken cancellationToken) =>
        CompleteInTransactionAsync(source, agentMissionStore, foundations.Relations, items, characterId, agentId, now, reference, standings, consume, cancellationToken);

    /// <summary>
    /// The completion, the consumption and the standing writes in one transaction: a write that fails rolls all of them back, so the mission stays
    /// accepted. The commit notice follows the commit.
    /// </summary>
    internal static async Task<bool> CompleteInTransactionAsync(NpgsqlDataSource source, Eve.Agents.AgentMissionStore agentMissionStore,
        Eve.Relations.RelationStore relations, Eve.Inventory.PgItemStore items, long characterId, long agentId, DateTimeOffset now, string reference,
        IReadOnlyList<MissionStandingGain> standings, IReadOnlyList<(int TypeId, long Quantity, string? Tag)> consume, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // the stacks are locked before anything is written: a second turn-in waits here and then finds them gone (or short)
        var takes = new List<(Eve.Inventory.Item Stack, long Taken)>();
        foreach (var (typeId, quantity, tag) in consume)
        {
            var left = quantity;
            // a courier cargo is consumed by its tag only: the other stacks of the type belong to the pilot and stay
            foreach (var stack in (await items.LockOwnedOfTypeAsync(connection, transaction, characterId, typeId, cancellationToken))
                         .Where(candidate => tag is null || candidate.CustomInfo == tag))
            {
                if (left == 0) break;
                var taken = Math.Min(left, stack.Quantity);
                takes.Add((stack, taken));
                left -= taken;
            }
            if (left > 0) return false;
        }
        Eve.Agents.AgentMissionChange? change;
        try
        {
            change = await agentMissionStore.CompleteInAsync(connection, transaction, characterId, agentId, now, reference, cancellationToken);
        }
        catch (Eve.Agents.AgentMissionRefusedException)
        {
            return false;
        }
        if (change is null) return false;
        // the objective items leave with the completion
        foreach (var (stack, taken) in takes)
        {
            if (taken == stack.Quantity) await items.DeleteAsync(connection, transaction, stack.Id, cancellationToken);
            else await items.SetQuantityAsync(connection, transaction, stack.Id, stack.Quantity - taken, cancellationToken);
        }
        // read in the transaction: a diminishing gain depends on the standing the pilot has when the completion is written
        foreach (var gain in standings)
        {
            var kind = Eve.Relations.RelationKinds.Standing.Name;
            var current = await relations.ValueAsync(kind, gain.EntityId, characterId, 0, connection, transaction, cancellationToken);
            await relations.AdjustAsync(connection, transaction, kind, gain.EntityId, characterId, gain.DeltaFrom(current),
                "Agent mission standing", cancellationToken: cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        agentMissionStore.Announce(change);
        return true;
    }

    /// <summary>The objective items of a fetch step: the pilot's holding of the type is topped up to the quantity in the hangar of the station.</summary>
    private async Task GrantObjectiveItemAsync(long characterId, int typeId, long quantity, long stationId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var held = await HeldQuantityAsync(characterId, typeId, cancellationToken);
        if (held < quantity)
            await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(typeId, characterId, stationId, Eve.Inventory.ItemFlags.Hangar, quantity - held), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The custom info that tags a courier's cargo with its mission, so the take-back removes only that cargo.</summary>
    private static string CourierCargoTag(long missionId) => MissionCourierRules.CargoTag(missionId);

    /// <summary>A courier's cargo: the quantity (singletons when the data says so) created into the hangar of the station, tagged with the mission.</summary>
    private async Task GrantCargoAsync(long characterId, long missionId, int typeId, long quantity, bool singleton, long stationId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await items.CreateAsync(connection, transaction,
            new Eve.Inventory.NewItem(typeId, characterId, stationId, Eve.Inventory.ItemFlags.Hangar, quantity, Singleton: singleton,
                CustomInfo: CourierCargoTag(missionId)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Takes a courier's cargo back: the stacks of the type tagged with the mission are removed, in one transaction (the agent takes it back).</summary>
    private async Task TakeCargoAsync(long characterId, long missionId, int typeId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var stack in await items.LockOwnedOfTypeAsync(connection, transaction, characterId, typeId, cancellationToken))
        {
            if (stack.CustomInfo == CourierCargoTag(missionId)) await items.DeleteAsync(connection, transaction, stack.Id, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The quantity of the type the pilot holds (all owned stacks).</summary>
    private async Task<long> HeldQuantityAsync(long characterId, int typeId, CancellationToken cancellationToken) =>
        (await items.OwnedByAsync(characterId, cancellationToken)).Where(item => item.TypeId == typeId).Sum(item => item.Quantity);

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
    Func<long, long, long, int, string, string, CancellationToken, Task> creditIsk,
    Func<long, long, DateTimeOffset, string, IReadOnlyList<MissionStandingGain>, IReadOnlyList<(int TypeId, long Quantity, string? Tag)>, CancellationToken, Task<bool>> completeMission,
    Func<long, int, long, long, CancellationToken, Task> grantItem,
    Func<long, int, CancellationToken, Task<long>> heldQuantity,
    Func<long, long, int, long, bool, long, CancellationToken, Task> grantCargo,
    Func<long, long, int, CancellationToken, Task> takeCargo) : IMissionDungeonHost
{
    public bool IsPilotInSystem(long characterId, int solarSystemId) => isPilotInSystem(characterId, solarSystemId);

    public Task<string?> StartDungeonAsync(long characterId, int solarSystemId, long dungeonId, CancellationToken cancellationToken) =>
        startDungeon(characterId, solarSystemId, dungeonId, cancellationToken);

    public Task CreditIskAsync(long characterId, long amount, long counterpartyId, int refTypeId, string reason, string operationKey, CancellationToken cancellationToken) =>
        creditIsk(characterId, amount, counterpartyId, refTypeId, reason, operationKey, cancellationToken);

    public Task<bool> CompleteMissionAsync(long characterId, long agentId, DateTimeOffset now, string reference,
        IReadOnlyList<MissionStandingGain> standings, IReadOnlyList<(int TypeId, long Quantity, string? Tag)> consume, CancellationToken cancellationToken) =>
        completeMission(characterId, agentId, now, reference, standings, consume, cancellationToken);

    public Task GrantObjectiveItemAsync(long characterId, int typeId, long quantity, long stationId, CancellationToken cancellationToken) =>
        grantItem(characterId, typeId, quantity, stationId, cancellationToken);

    public Task<long> HeldQuantityAsync(long characterId, int typeId, CancellationToken cancellationToken) =>
        heldQuantity(characterId, typeId, cancellationToken);

    public Task GrantCargoAsync(long characterId, long missionId, int typeId, long quantity, bool singleton, long stationId, CancellationToken cancellationToken) =>
        grantCargo(characterId, missionId, typeId, quantity, singleton, stationId, cancellationToken);

    public Task TakeCargoAsync(long characterId, long missionId, int typeId, CancellationToken cancellationToken) =>
        takeCargo(characterId, missionId, typeId, cancellationToken);
}
