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
    private string structureSchema = "";
    private Eve.Structures.StructureStore structureStore = default!;
    private Eve.Structures.StructurePilotStore structurePilots = default!;
    private StructureControlReadModel structureControlReadModel = default!;
    private readonly ConnectedSessions connected;
    private StructureControlBackend? structureControl;
    private StructureDeliveryBackend structureDelivery = default!;
    private Eve.Structures.StructureTimers structureTimers = default!;
    private Eve.Structures.StructureFuel structureFuel = default!;
    private StructureCompressionAuthority structureCompression = default!;
    private StructureDeploymentAuthority structureDeployment = default!;
    private StructureAnchorAuthority structureAnchor = default!;
    private Eve.Services.Space.SpaceStructures? spaceStructures;
    private Eve.Structures.MoonExtractionStore moonExtractionStore = default!;
    private Eve.Structures.MoonExtractions moonExtractions = default!;
    private StructureMoonAuthority structureMoon = default!;

    /// <summary>The structures as the directory and profile readers see them: the legacy shape of the structures schema.</summary>
    Task<JsonObject> ReadStructuresAsync(CancellationToken token) =>
        (structureStore ?? throw new InvalidOperationException("the structures are read before the structure store is open")).ReadLegacyTableAsync(token);

    private Task SetStructureDescriptionAsync(long structureId, string description, CancellationToken token) =>
        WithStructureConnectionAsync((connection, transaction) =>
            structureStore.SetDescriptionAsync(connection, transaction, structureId, description, token), token);

    private Task SetStructureProfileAsync(IReadOnlyCollection<long> structureIds, long profileId, CancellationToken token) =>
        WithStructureConnectionAsync((connection, transaction) =>
            structureStore.SetProfileAsync(connection, transaction, structureIds, profileId, token), token);

    private async Task WithStructureConnectionAsync(Func<NpgsqlConnection, NpgsqlTransaction?, Task> work, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await work(connection, null);
    }

    /// <summary>E-03: the cyno jammer state of a system for <c>GetCynoJammerState</c>: null without a jammer, 0 once it runs, else the FILETIME its warm-up ends at.</summary>
    private async ValueTask<long?> CynoJammerOnlineSimTimeAsync(long solarSystemId, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return await JammerStateAsync(connection, null, solarSystemId, token);
    }

    /// <summary>

    /// getCynoJammerActivationTimeMs, which asks isCynoSuppressionUpgradeOnline).
    /// </summary>
    private async Task<long?> JammerStateAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long solarSystemId, CancellationToken token)
    {
        var since = await structureStore.OnlineSinceAsync(connection, transaction, (int)solarSystemId, Eve.Structures.StructureServiceIds.CynoJammer, token);
        if (since is null || !await sovereigntyReadModel.IsCynoSuppressionOnlineAsync(solarSystemId, token))
            return null;
        return Eve.Structures.StructureJammer.StateAt(since, now());
    }

    /// <summary>Whether standard cynosural fields are refused in the system (its jammer runs: the state is 0).</summary>
    private async ValueTask<bool> CynoJammedAsync(long solarSystemId, CancellationToken token) => await CynoJammerOnlineSimTimeAsync(solarSystemId, token) == 0;

    /// <summary>The structure a cyno beacon jump names, as the beacon travel checks it: its type, its system and its Cyno Beacon service (null when it is gone).</summary>
    private async ValueTask<Eve.Services.Space.StructureBeaconInfo?> StructureBeaconAsync(long structureId, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        if (await structureStore.GetAsync(connection, null, structureId, false, token) is not { } record || record.Snapshot.DestroyedMs > 0)
            return null;
        var services = await structureStore.ServicesAsync(connection, null, structureId, token);
        return new Eve.Services.Space.StructureBeaconInfo(record.TypeId, record.SolarSystemId,
            services.GetValueOrDefault(Eve.Structures.StructureServiceIds.CynoBeacon) == Eve.Structures.ServiceState.Online);
    }

    /// <summary>

    /// (the rule of <c>StructureDirectoryService.CanDock</c>, on the access the directory reads for the character's corporation and alliance).
    /// </summary>
    private async Task<bool> StructureDockingAllowedAsync(long characterId, long structureId, CancellationToken token)
    {
        var affiliation = await AffiliationOfAsync(characterId, token);
        var corporationId = affiliation.Corporation ?? 0;
        var allianceId = affiliation.Alliance ?? 0;
        await using (var connection = await source.OpenConnectionAsync(token))
        {
            var services = await structureStore.ServicesAsync(connection, null, structureId, token);
            if (services.GetValueOrDefault(Eve.Structures.StructureServiceIds.Docking) != Eve.Structures.ServiceState.Online)
                return false;
        }
        var snapshots = await structureDirectoryReadModel.GetDockingAccessSnapshotsAsync(characterId, corporationId, allianceId, [structureId], token);
        return snapshots.FirstOrDefault(snapshot => snapshot.StructureId == structureId) is { } access
            && Eve.Services.Core.StructureDirectoryService.CanDock(access, structureId, characterId, corporationId, allianceId);
    }

    private StructureControlBackend RequireStructureControl() => structureControl
        ?? throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Space is not available for structure control.")));

    private async ValueTask<long?> ReadStructurePilotAsync(long structureId, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return (await structurePilots.GetByStructureAsync(connection, null, structureId, token))?.CharacterId;
    }

    private async ValueTask DeliverStructureItemsAsync(ServiceCall call, long structureId, long recipientId, IReadOnlyList<long> itemIds)
    {
        await mailSendWriter.MutationGate.WaitAsync(call.Cancellation);
        try
        {
            await mailCache.FlushTableAsync("notifications", call.Cancellation);
            await structureDelivery.DeliverAsync(call, structureId, recipientId, itemIds);
        }
        finally { mailSendWriter.MutationGate.Release(); }
    }

    private async ValueTask<StructureDeliveryBackend.DeliveryInboxCommit> PersistStructureDeliveryInboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        StructureDeliveryBackend.DeliveryInboxNotification notification, CancellationToken token)
    {
        var recipientKey = notification.RecipientCharacterID.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rowKey = RowCodec.Explode("notifications", new JsonObject
        {
            ["boxes"] = new JsonObject { [recipientKey] = new JsonObject() }
        }).Keys.Single(key => key != "boxes");
        var saved = await mailStore.ReadRowAsync(connection, transaction, "notifications", rowKey, token);
        var box = saved is null ? new JsonObject() : JsonNode.Parse(saved)!.AsObject();
        var rows = box["byID"] as JsonObject;
        if (rows is null) box["byID"] = rows = new JsonObject();
        var boxes = (mailCache.Read("notifications", null).Data as JsonObject)?["boxes"] as JsonObject;
        var maximum = boxes?.SelectMany(pair => (pair.Value as JsonObject)?["byID"] is JsonObject entries
            ? entries.Select(entry => long.TryParse(entry.Key, out var id) ? id : 0) : []).DefaultIfEmpty(0).Max() ?? 0;
        var notificationId = checked(maximum + 1);
        rows[notificationId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = notification.ToRecord(notificationId);
        await mailStore.ApplyChangesAsync(connection, transaction, "notifications",
            [new RowUpsert(rowKey, box.ToJsonString())], [], mailStoreOwner.Fence, token);
        return new StructureDeliveryBackend.DeliveryInboxCommit(notificationId, args =>
        {
            var updated = mailCache.Write("notifications", $"/boxes/{recipientKey}", box);
            if (!updated.Success) throw new InvalidOperationException("The committed structure delivery inbox could not update its cache.");
            characterSessions.Of(notification.RecipientCharacterID)?.SendNotification("OnNotificationReceived", "clientID", args);
            return ValueTask.CompletedTask;
        });
    }

    private async ValueTask<double?> CargoDepositSurfaceDistanceAsync(ServiceCall call, long shipId, long structureId, CancellationToken token)
    {
        if (spaceSessions is null || sim is null || !spaceSessions.IsInSpace(call.Session) ||
            spaceSessions.IsStructureObserver(call.Session) || spaceSessions.IsWarping(call.Session) ||
            spaceSessions.ShipIdOf(call.Session) != shipId || call.Session.CharacterId is not { } characterId)
            return null;
        var state = await crimewatchRuntime.StateOfAsync(characterId, token);
        var nowMs = now();
        if (state.HasWeaponsTimer(nowMs) || state.IsCriminal(nowMs)) return null;
        var reply = await sim.CommandAsync((ulong)call.Session.SessionId, SimCommands.Measure, [structureId, shipId], token);
        return reply.Accepted ? reply.Distance : null;
    }
    /// <summary>The ship's place in its system as the simulation has it (null without a simulation: the anchor is then refused as not in space).</summary>
    private Task<Eve.Services.Space.WarpCoordinates?> ShipPositionForAnchorAsync(ClientSession session, long shipId, CancellationToken token) =>
        spaceSessions is null ? Task.FromResult<Eve.Services.Space.WarpCoordinates?>(null) : spaceSessions.PositionOfAsync(session, shipId, token);

    /// <summary>The corporation's creation time (Unix milliseconds) for the seven-day anchoring rule; null when the corporation is not in the store.</summary>
    private async Task<long?> CorporationCreatedMsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long corporationId, CancellationToken token) =>
        await Sql.ScalarAsync(connection, transaction, $"SELECT created_ms FROM {corporationSchema}.corporations WHERE id = $1", token, corporationId) is long created ? created : null;

    private async Task StructureRecords()
    {

        structureSchema = Env("EVE_STRUCTURES_SCHEMA", Eve.Structures.StructureStore.DefaultSchema);
        await Eve.Structures.StructureStore.MigrateAsync(source, structureSchema);
        structureStore = new Eve.Structures.StructureStore(source, structureSchema);
        structurePilots = new Eve.Structures.StructurePilotStore(structureStore);
        var legacy = await ReadGameStoreRawAsync("structures", CancellationToken.None);
        var legacyMoon = await ReadGameStoreRawAsync("moonExtractions", CancellationToken.None);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var copied = await structureStore.ImportAsync(connection, transaction, legacy, now());
        await structurePilots.ClearOnRestartAsync(connection, transaction);
        // ST-6a: the moon extractions and resource profiles move over in the same transaction (the same importer pattern)
        moonExtractionStore = new Eve.Structures.MoonExtractionStore(source, structureSchema);
        var moonCopied = await moonExtractionStore.ImportAsync(connection, transaction, legacyMoon);
        await transaction.CommitAsync();
        if (copied.Count > 0)
            Console.Error.WriteLine($"eve-host: {copied.Count} structure(s) copied from the game store into the {structureSchema} schema");
        if (moonCopied > 0)
            Console.Error.WriteLine($"eve-host: {moonCopied} moon extraction row(s) copied from the game store into the {structureSchema} schema");
    }

    private async Task Structures()
    {

        structureControlReadModel = new StructureControlReadModel(source, structureStore, structurePilots, items, modules, dogma,
            (characterId, structureId, setting, token) => structureDirectoryReadModel.CharacterHasSettingAsync(characterId, structureId, setting, token));
        SetupStructureModules();
        var rules = await StructureRulesSource.LoadAsync(sde);
        var refineryTypeIds = await PersistenceMarketStructureDirectory.ReadRefineryTypeIdsAsync(sde);
        var unanchorDrop = CreateStructureUnanchorDropAuthority();
        var destructionLoot = CreateStructureDestructionLootAuthority();
        structureTimers = new Eve.Structures.StructureTimers(structureStore, foundations.Scheduler, foundations.Events, rules,
            completeUnanchor: unanchorDrop.CompleteAsync, completeDestruction: destructionLoot.CompleteAsync);
        structureTimers.Register();

        if (sim is not null && spaceSessions is not null)
        {
            spaceStructures = new Eve.Services.Space.SpaceStructures(sim, spaceSessions, source, structureStore, structureTimers, dogma,
                message => Console.Error.WriteLine($"eve-host: space structures: {message}"));
            spaceStructures.Subscribe(foundations.Events);
            spaceSessions.PlaceStructuresAsync = RestoreStructuresAndCynoBeaconsAsync;


            spaceSessions.IsSovereigntyHubAsync = async (systemId, targetId, token) =>
                Eve.Services.Space.SpaceSovHubs.HubOf(await sovereigntyReadModel.ReadSnapshotAsync(token), systemId) is { } hub && hub.HubId == targetId

                || (entosisCaptureService is not null && await entosisCaptureService.IsCommandNodeAsync(targetId, systemId, token));

            if (spaceContainers is not null)
            {
                await Eve.Services.Space.Entosis.EntosisCaptureService.MigrateAsync(source);
                var entosisCaptures = new Eve.Services.Space.Entosis.EntosisCaptureService(
                    source, foundations.Scheduler, () => foundations.Scheduler.Now, EntosisHubReadingAsync, EntosisHandoverAsync,
                    log: message => Console.Error.WriteLine($"eve-host: entosis: {message}"), events: foundations.Events,
                    constellationSystems: EntosisConstellationSystemsAsync, constellationOf: EntosisConstellationOfAsync,
                    destroyHub: EntosisHubDestroyedAsync);
                entosisCaptures.Register();
                spaceContainers.EntosisCaptures = entosisCaptures;
                entosisCaptureService = entosisCaptures;
            }
            spaceSovHubs = new Eve.Services.Space.SpaceSovHubs(sim, spaceSessions, dogma,
                async (systemId, token) => Eve.Services.Space.SpaceSovHubs.HubOf(await sovereigntyReadModel.ReadSnapshotAsync(token), systemId),
                message => Console.Error.WriteLine($"eve-host: space sovereignty hubs: {message}"));

            spaceSovHubs.LiveCommandNodesOf = async (systemId, token) =>
                entosisCaptureService is null ? [] : (await entosisCaptureService.LiveNodesAsync(systemId, token)).Select(node => node.NodeId).ToList();
            foundations.Events.Subscribe("eve.host.space.command-nodes",
                [
                    Eve.Services.Space.Entosis.EntosisCaptureService.NodeSpawnedTopic,
                    Eve.Services.Space.Entosis.EntosisCaptureService.NodeCapturedTopic,
                    Eve.Services.Space.Entosis.EntosisCaptureService.StageEndedTopic,
                    Eve.Services.Space.Entosis.EntosisCaptureService.HubDestroyedTopic,
                ],
                async (context, cancellation) =>
                {
                    if (context.Event.Topic == Eve.Services.Space.Entosis.EntosisCaptureService.NodeSpawnedTopic)
                    {
                        var spawned = context.Event.Payload<Eve.Services.Space.Entosis.CommandNodeSpawned>()!;
                        await spaceSovHubs.PlaceCommandNodeAsync(spawned.NodeId, spawned.SolarSystemId, cancellation);
                    }
                    else if (context.Event.Topic == Eve.Services.Space.Entosis.EntosisCaptureService.NodeCapturedTopic)
                    {
                        spaceSovHubs.RemoveCommandNode(context.Event.Payload<Eve.Services.Space.Entosis.CommandNodeCaptured>()!.NodeId);
                    }
                    else if (context.Event.Topic == Eve.Services.Space.Entosis.EntosisCaptureService.HubDestroyedTopic)
                    {
                        spaceSovHubs.RemoveHub(context.Event.Payload<Eve.Services.Space.Entosis.HubDestroyed>()!.HubId);
                    }
                    else
                    {
                        foreach (var nodeId in context.Event.Payload<Eve.Services.Space.Entosis.CommandStageEnded>()!.RemovedNodeIds)
                        {
                            spaceSovHubs.RemoveCommandNode(nodeId);
                        }
                    }
                });
        }

        structureFuel = new Eve.Structures.StructureFuel(structureStore, foundations.Scheduler, structureTimers, await ServiceFuelRulesSource.LoadAsync(dogma,sde), new InventoryStructureFuelBay(items, dogma, foundations.Events));
        structureFuel.Register();
        SetupStructureCynoBeaconRuntime();

        new Eve.Host.Persistence.Structures.StructureCynoJammerNotifications(JammerStateAsync, characterSessions).Subscribe(foundations.Events);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var scheduled = await structureTimers.EnsureScheduledAsync(connection, transaction);
            await transaction.CommitAsync();
            if (scheduled > 0)
                Console.Error.WriteLine($"eve-host: {scheduled} structure deadline(s) scheduled");
        }
        structureDirectoryReadModel = new PersistenceStructureDirectoryReadModel(
            ReadGameStoreTableAsync, AffiliationOfAsync, sde, SetStructureDescriptionAsync, CynoJammedAsync,
            async (characterId, corporationId, token) =>
            {
                var corporation = await corporations.GetAsync(corporationId, token);
                var member = await corporations.MemberAsync(characterId, token);
                if (corporation is null || corporation.IsNpc || member?.CorporationId != corporationId)
                    return false;
                var roles = Eve.Corporations.CorporationRoles.Session(
                    corporation, member, null, await corporations.TitlesAsync(corporationId, token));
                return (roles.CorpRole & Eve.Corporations.CorporationRoles.StationManager) != 0;
            },
            async (characterId, corporationId, token) =>
            {
                var corporation = await corporations.GetAsync(corporationId, token);
                var member = await corporations.MemberAsync(characterId, token);
                if (corporation is null || corporation.IsNpc || member?.CorporationId != corporationId)
                    return false;
                var roles = Eve.Corporations.CorporationRoles.Session(
                    corporation, member, null, await corporations.TitlesAsync(corporationId, token));
                return (roles.CorpRole & (Eve.Corporations.CorporationRoles.StationManager |
                    Eve.Corporations.CorporationRoles.BrandManager)) != 0;
            });
        marketTrading.StructureDirectory = new PersistenceMarketStructureDirectory(
            structureStore,structureDirectoryReadModel,marketTopology,rules,refineryTypeIds);
        if (spaceSessions is not null)
            spaceSessions.StructureExistsAsync = async (structureId, cancellationToken) =>
                await structureDirectoryReadModel.GetStructureInfoAsync(structureId, cancellationToken) is { PersistentlyDeployed: true };
        if (spaceSessions is not null)
            spaceSessions.StructureDockingAllowedAsync = StructureDockingAllowedAsync;
        if (spaceSessions is not null)
        {
            var tether = new TetherAuthority(source, structureStore, dogma, structureDirectoryReadModel,
                StructureDockingAllowedAsync, spaceSessions.ShipTypeIdOf, ReadGameStoreTableAsync,
                async (corporationId, token) => Eve.Services.Corporations.CorporationWire.FactionId(await corporations.GetAsync(corporationId, token)),
                ships, items, modules, mailCache, spaceSessions.CanRepairTetheredShip);
            spaceSessions.ReadTetherCandidatesAsync = tether.ReadTetherCandidatesAsync;
            spaceSessions.ReadTetherGeometryAsync = tether.ReadTetherGeometryAsync;
            spaceSessions.RepairTetheredItemsAsync = tether.RepairTetheredItemsAsync;
            spaceSessions.ReadTetherBlockUntilAsync = tether.ReadTetherBlockUntilAsync;
            spaceSessions.WriteTetherBlockUntilAsync = tether.WriteTetherBlockUntilAsync;
            foundations.Events.Subscribe("structure.tether_policy", [Eve.Structures.StructureTimers.StateChangedTopic, Eve.Structures.StructureTimers.ServiceChangedTopic],
                (_, token) => spaceSessions.RefreshTetherPoliciesAsync(token));
        }
        // E-03: a cynosural field is refused in a system its jammer runs in (the state is 0 once the warm-up has passed)
        if (spaceSessions is not null)
            spaceSessions.CynoJammedAsync = CynoJammedAsync;
        structureProfilesRpcBackend = new Eve.Host.PersistenceStructureProfilesRpcBackend(
            ReadGameStoreTableAsync, mailCache, characterSessions, SetStructureProfileAsync);
        Eve.Host.StructureNotifications.Subscribe(foundations.Events, structureStore, characterSessions,
            structureProfilesRpcBackend.GetMyHangarViewStateAsync);
        // the corporation office source reads its rental and the corporation hangar access when a pilot compresses (the office store is set later in the start-up)
        structureCompression = new StructureCompressionAuthority(source, structureStore, items, dogma, sde, skills,
            Eve.Host.HostInventoryServiceFactory.HangarAccessOf(corporations),
            async (connection, transaction, corporationId, stationId, token) =>
                await corporationOfficeStore.GetAsync(connection, transaction, corporationId, stationId, false, token) is not null,
            characterSessions);

        structureDeployment = new StructureDeploymentAuthority(source, structureStore, structureTimers, foundations.Scheduler, foundations.Events);

        structureAnchor = new StructureAnchorAuthority(source, structureStore, structureTimers, rules, foundations.Scheduler, foundations.Events, items, dogma,
            CorporationCreatedMsAsync, ShipPositionForAnchorAsync, session => spaceSessions is not null && spaceSessions.IsWarping(session),
            async structureId => { if (spaceStructures is not null) await spaceStructures.PlaceAsync(structureId); },
            profiles: structureProfilesRpcBackend.GetProfilesAsync,
            sov: ReadStructureAnchorSovereigntyAsync,
            anchorWithDuration: (connection, transaction, structure, anchoringMs, ct) =>
                structureTimers.AnchorAsync(connection, transaction, structure with { AnchoringMs = anchoringMs }, ct),
            solarSystemOf: session => spaceSessions?.SolarSystemOf(session),
            shipIdOf: session => spaceSessions?.ShipIdOf(session));
        // ST-6a: the moon drill starts a chunk (StartNewExtraction); the fracture and the cancel follow in (d)
        var moonRules = await MoonRulesSource.LoadAsync(sde);
        var moonRigRules = new MoonRigReadModel(items, dogma, moonRules, structureStore);
        moonExtractions = new Eve.Structures.MoonExtractions(structureStore, moonExtractionStore, foundations.Scheduler, foundations.Events, moonRules,
            moonRigRules.ReadAsync);
        moonExtractions.Register();
        moonExtractions.Subscribe();
        if (spaceContainers is not null)
        {
            new SpaceMoonFields(moonExtractionStore, structureStore, items, new PgSpaceItemStore(source, spaceSchema),
                dogma, moonRules, spaceContainers).Subscribe(foundations.Events);
            spaceContainers.SubscribeUnanchorDrops(foundations.Events);
        }
        // ST-6a (c, d): an extraction that ended (fractured, decayed, offlined or cancelled) is announced to the corporation and to the pilots docked at the structure

        foundations.Events.Subscribe("moon.session_notice", [Eve.Structures.MoonExtractions.EndedTopic], (context, cancellation) =>
        {
            var ended = context.Event.Payload<Eve.Structures.MoonExtractionEnded>()!;
            foreach (var session in characterSessions.OfCorporation(ended.OwnerCorporationId).Concat(characterSessions.InStructure(ended.StructureId)).Distinct())
                session.SendNotification("OnMoonExtractionUpdate", "clientID", PyNone.Instance);
            return Task.CompletedTask;
        });
        structureMoon = new StructureMoonAuthority(source, structureStore, moonExtractions, moonRules, structurePilots,
            (characterId, structureId, token) => structureDirectoryReadModel.CharacterHasServiceAsync(
                characterId, structureId, Eve.Structures.StructureServiceIds.MoonMining, token));
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var open = await moonExtractions.EnsureScheduledAsync(connection, transaction);
            await transaction.CommitAsync();
            if (open > 0)
                Console.Error.WriteLine($"eve-host: {open} open moon extraction(s) have their deadlines");
        }
        if (spaceSessions is not null)
        {
            structureControl = new StructureControlBackend(source, structureStore, structurePilots, items, dogma, spaceSessions,
                structureDirectoryReadModel.CharacterHasSettingAsync, connected,
                message => Console.Error.WriteLine($"eve-host: structure control: {message}"));
            foundations.Events.Subscribe("structure.control_owner", [Eve.Structures.StructureTimers.StateChangedTopic],
                (_, token) => structureControl.ReconcileAsync(token));
        }
        structureDelivery = new StructureDeliveryBackend(source, items, structureStore, rules, dogma, characters,
            characterSessions, foundations.Events, structureDirectoryReadModel.GetStructureInfoAsync,
            CargoDepositSurfaceDistanceAsync, PersistStructureDeliveryInboxAsync, now);
        structureRpcCallbacks = new Eve.Services.Core.StructureRpcCallbacks
        {
            GetStructurePilotAsync = (call, structureId) => structureControl is not null
                ? structureControl.GetStructurePilotAsync(call, structureId)
                : ReadStructurePilotAsync(structureId, call.Cancellation),
            TakeControlAsync = (call, structureId) => RequireStructureControl().TakeControlAsync(call, structureId),
            BoardStructureAsync = (call, structureId) => RequireStructureControl().BoardStructureAsync(call, structureId),
            EjectFromStructureAsync = (call, shipId) => RequireStructureControl().EjectFromStructureAsync(call, shipId),
            ReleaseControlAsync = call => RequireStructureControl().ReleaseControlAsync(call),
            DeliverAsync = DeliverStructureItemsAsync,
            DropOffItemsAsync = structureDelivery.DropOffItemsAsync,
            CompressItemInStructureAsync = async (call, itemId) => await structureCompression.CompressAsync(call, itemId),
            DecompressGasInStructureAsync = async (call, itemId) => await structureCompression.DecompressAsync(call, itemId),
            GetMyGasDecompressionEfficiencyAsync = async call => await structureCompression.EfficiencyAsync(call),
            GetProfilesAsync = structureProfilesRpcBackend.GetProfilesAsync,
            GetProfileSettingsAsync = structureProfilesRpcBackend.GetProfileSettingsAsync,
            GetMyHangarViewStateAsync = (call, structureId) =>
                structureProfilesRpcBackend.GetMyHangarViewStateAsync(structureId, call.Cancellation),
            UndockAsync = (call, structureId, shipId, ignoreContraband) => spaceSessions is null
                ? throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Space is not available right now. Try again in a moment.")))
                : new ValueTask(spaceSessions.UndockStructureAsync(call.Session, structureId, shipId, call.Cancellation, ignoreContraband)),
            DockAsync = (call, structureId, shipId) => spaceSessions is null
                ? throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Space is not available right now. Try again in a moment.")))
                : new ValueTask(spaceSessions.DockStructureAsync(call.Session, structureId, shipId, call.Cancellation)),
            CreateProfileAsync = (call, corporationId, name, description) =>
                structureProfilesRpcBackend.CreateProfileAsync(corporationId, name, description, call.Cancellation),
            UpdateProfileAsync = (call, corporationId, profileId, name, description) =>
                structureProfilesRpcBackend.UpdateProfileAsync(corporationId, profileId, name, description, call.Cancellation),
            SaveProfileSettingsAsync = async (call, corporationId, profileId, settings) =>
            {
                await structureProfilesRpcBackend.SaveProfileSettingsAsync(corporationId, profileId, settings, call.Cancellation);
                if (spaceSessions is not null) await spaceSessions.RefreshTetherPoliciesAsync(call.Cancellation);
                if (structureControl is not null) await structureControl.ReconcileAsync(call.Cancellation);
            },
            SetDefaultProfileAsync = async (call, corporationId, profileId) =>
            {
                await structureProfilesRpcBackend.SetDefaultProfileAsync(corporationId, profileId, call.Cancellation);
                if (spaceSessions is not null) await spaceSessions.RefreshTetherPoliciesAsync(call.Cancellation);
                if (structureControl is not null) await structureControl.ReconcileAsync(call.Cancellation);
            },
            ChangeProfilesAsync = async (call, corporationId, structureIds, profileId) =>
            {
                await structureProfilesRpcBackend.ChangeProfilesAsync(corporationId, structureIds, profileId, call.Cancellation);
                if (spaceSessions is not null) await spaceSessions.RefreshTetherPoliciesAsync(call.Cancellation);
                if (structureControl is not null) await structureControl.ReconcileAsync(call.Cancellation);
            },
            DeleteProfileAsync = async (call, corporationId, profileId) =>
            {
                await structureProfilesRpcBackend.DeleteProfileAsync(corporationId, profileId, call.Cancellation);
                if (spaceSessions is not null) await spaceSessions.RefreshTetherPoliciesAsync(call.Cancellation);
                if (structureControl is not null) await structureControl.ReconcileAsync(call.Cancellation);
            },
            DuplicateProfileAsync = (call, corporationId, profileId) =>
                structureProfilesRpcBackend.DuplicateProfileAsync(corporationId, profileId, call.Cancellation),
            RenameStructureAsync = (call, structureId, name) => new ValueTask(structureDeployment.RenameAsync(call, structureId, name, call.Cancellation)),
            AnchorAsync = (call, request) => new ValueTask(structureAnchor.AnchorAsync(call, request, call.Cancellation)),
            UnanchorAsync = (call, structureId) => new ValueTask(structureDeployment.UnanchorAsync(call, structureId, call.Cancellation)),
            CancelUnanchorAsync = (call, structureId) => new ValueTask(structureDeployment.CancelUnanchorAsync(call, structureId, call.Cancellation)),
            UpdateReinforceTimesFromClientAsync = (call, structureIds, weekday, hour) =>
                new ValueTask(structureDeployment.UpdateReinforceTimesAsync(call, structureIds, weekday, hour, call.Cancellation)),
        };
        structureAssetSafetyReadModel = new PersistenceStructureAssetSafetyReadModel(
            source, items, new PgSpaceItemStore(source, spaceSchema), economyWallets, sde,
            ReadGameStoreTableAsync, AffiliationOfAsync, now, inventorySchema, corporationSchema,
            assetSafetySchema, structureDirectoryReadModel,
            message => Console.Error.WriteLine($"eve-host: asset safety: {message}"));
        foundations.Scheduler.Register(
            PersistenceStructureAssetSafetyReadModel.DueJobKind,
            (_, cancellation) => structureAssetSafetyReadModel.ProcessDueAutomaticDeliveriesAsync(cancellation).AsTask());
        await foundations.Scheduler.EnsureRecurringAsync(
            PersistenceStructureAssetSafetyReadModel.DueJobKind,
            "host-asset-safety",
            System.TimeSpan.FromMinutes(5));
        structureSettingsService = new Eve.Services.Core.StructureSettingsService(structureDirectoryReadModel);
        sovereigntyReadModel = new PersistenceSovFwReadModel(ReadGameStoreTableAsync, AffiliationOfAsync);
    }

    private void NotifyAssetSafetyOwner(
        ClientSession actor,
        long ownerId,
        bool corporation,
        string notification,
        IReadOnlyList<PyValue> arguments)
    {
        foreach (var recipient in connected.Snapshot())
        {
            var recipientOwnerId = corporation ? recipient.GetInt("corpid") : recipient.CharacterId;
            if (recipientOwnerId != ownerId)
                continue;

            var args = arguments.ToArray();
            if (ReferenceEquals(recipient, actor))
                recipient.AfterResponse(() => recipient.SendNotification(notification, "clientID", args));
            else
                recipient.SendNotification(notification, "clientID", args);
        }
    }
}
