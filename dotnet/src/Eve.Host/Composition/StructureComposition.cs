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
    private Eve.Structures.StructureTimers structureTimers = default!;
    private Eve.Structures.StructureFuel structureFuel = default!;
    private StructureCompressionAuthority structureCompression = default!;
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
        return Eve.Structures.StructureJammer.StateAt(
            await structureStore.OnlineSinceAsync(connection, null, (int)solarSystemId, Eve.Structures.StructureServiceIds.CynoJammer, token), now());
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

    private async Task StructureRecords()
    {
        // ST-0: the structures schema owns the records; the game store's table is copied in once (ADR 0003 rule 3: the bridge is the importer, not a read path)
        structureSchema = Env("EVE_STRUCTURES_SCHEMA", Eve.Structures.StructureStore.DefaultSchema);
        await Eve.Structures.StructureStore.MigrateAsync(source, structureSchema);
        structureStore = new Eve.Structures.StructureStore(source, structureSchema);
        var legacy = await ReadGameStoreRawAsync("structures", CancellationToken.None);
        var legacyMoon = await ReadGameStoreRawAsync("moonExtractions", CancellationToken.None);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var copied = await structureStore.ImportAsync(connection, transaction, legacy, now());
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
        // ST-0: the deadlines of the structures are durable jobs; the handler is registered here, before the foundations start
        var rules = await StructureRulesSource.LoadAsync(sde);
        structureTimers = new Eve.Structures.StructureTimers(structureStore, foundations.Scheduler, foundations.Events, rules);
        structureTimers.Register();
        // ST-1 d: a system's structures are in the simulation once a pilot comes into it; the simulation's hits and the records' state changes meet here
        if (sim is not null && spaceSessions is not null)
        {
            var spaceStructures = new Eve.Services.Space.SpaceStructures(sim, spaceSessions, source, structureStore, structureTimers, dogma,
                message => Console.Error.WriteLine($"eve-host: space structures: {message}"));
            spaceStructures.Subscribe(foundations.Events);
        }
        // ST-7: the service fuel is paid from the fuel bay (inventory) at each full hour; the rules are the SDE's service modules
        structureFuel = new Eve.Structures.StructureFuel(structureStore, foundations.Scheduler, structureTimers, ServiceFuelRulesSource.Load(dogma), new InventoryStructureFuelBay(items, dogma));
        structureFuel.Register();
        // E-15: a change of the system's jammer is announced to the pilots in that system (EveJS sovNotifications broadcastCynoJammerChanged)
        foundations.Events.Subscribe("structure.cyno_jammer_notice", [Eve.Structures.StructureTimers.ServiceChangedTopic], async (context, cancellation) =>
        {
            var change = context.Event.Payload<Eve.Structures.StructureServiceChanged>()!;
            if (change.ServiceId != Eve.Structures.StructureServiceIds.CynoJammer)
                return;
            var since = await structureStore.OnlineSinceAsync(context.Connection, context.Transaction, change.SolarSystemId, Eve.Structures.StructureServiceIds.CynoJammer, cancellation);
            var state = Eve.Structures.StructureJammer.StateAt(since, now());
            foreach (var session in characterSessions.InSpace(change.SolarSystemId))
                session.SendNotification("OnCynoJammerChanged", "solarsystemid2", new PyLong(change.SolarSystemId), state is { } value ? new PyLong(value) : PyNone.Instance);
        });
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var scheduled = await structureTimers.EnsureScheduledAsync(connection, transaction);
            await transaction.CommitAsync();
            if (scheduled > 0)
                Console.Error.WriteLine($"eve-host: {scheduled} structure deadline(s) scheduled");
        }
        structureDirectoryReadModel = new PersistenceStructureDirectoryReadModel(ReadGameStoreTableAsync, AffiliationOfAsync, sde, SetStructureDescriptionAsync);
        if (spaceSessions is not null)
            spaceSessions.StructureExistsAsync = async (structureId, cancellationToken) =>
                await structureDirectoryReadModel.GetStructureInfoAsync(structureId, cancellationToken) is { PersistentlyDeployed: true };
        // E-03: a cynosural field is refused in a system its jammer runs in (the state is 0 once the warm-up has passed)
        if (spaceSessions is not null)
            spaceSessions.CynoJammedAsync = CynoJammedAsync;
        structureProfilesRpcBackend = new Eve.Host.PersistenceStructureProfilesRpcBackend(
            ReadGameStoreTableAsync, mailCache, characterSessions, SetStructureProfileAsync);
        structureCompression = new StructureCompressionAuthority(source, structureStore, items, dogma, sde, skills);
        // ST-6a: the moon drill starts a chunk (StartNewExtraction); the fracture and the cancel follow in (d)
        var moonRules = await MoonRulesSource.LoadAsync(sde);
        moonExtractions = new Eve.Structures.MoonExtractions(structureStore, moonExtractionStore, foundations.Scheduler, foundations.Events, moonRules);
        moonExtractions.Register();
        moonExtractions.Subscribe();
        // ST-6a (c, d): an extraction that ended (fractured, decayed, offlined or cancelled) is announced to the corporation and to the pilots docked at the structure
        // (EVE.js sendMoonExtractionClearedToSessions)
        foundations.Events.Subscribe("moon.session_notice", [Eve.Structures.MoonExtractions.EndedTopic], (context, cancellation) =>
        {
            var ended = context.Event.Payload<Eve.Structures.MoonExtractionEnded>()!;
            foreach (var session in characterSessions.OfCorporation(ended.OwnerCorporationId).Concat(characterSessions.InStructure(ended.StructureId)).Distinct())
                session.SendNotification("OnMoonExtractionUpdate", "clientID", PyNone.Instance);
            return Task.CompletedTask;
        });
        structureMoon = new StructureMoonAuthority(source, structureStore, moonExtractions, moonRules);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var open = await moonExtractions.EnsureScheduledAsync(connection, transaction);
            await transaction.CommitAsync();
            if (open > 0)
                Console.Error.WriteLine($"eve-host: {open} open moon extraction(s) have their deadlines");
        }
        structureRpcCallbacks = new Eve.Services.Core.StructureRpcCallbacks
        {
            CompressItemInStructureAsync = async (call, itemId) => await structureCompression.CompressAsync(call, itemId),
            DecompressGasInStructureAsync = async (call, itemId) => await structureCompression.DecompressAsync(call, itemId),
            GetMyGasDecompressionEfficiencyAsync = async call => await structureCompression.EfficiencyAsync(call),
            GetProfilesAsync = structureProfilesRpcBackend.GetProfilesAsync,
            GetProfileSettingsAsync = structureProfilesRpcBackend.GetProfileSettingsAsync,
            GetMyHangarViewStateAsync = (call, structureId) =>
                structureProfilesRpcBackend.GetMyHangarViewStateAsync(structureId, call.Cancellation),
            CreateProfileAsync = (call, corporationId, name, description) =>
                structureProfilesRpcBackend.CreateProfileAsync(corporationId, name, description, call.Cancellation),
            UpdateProfileAsync = (call, corporationId, profileId, name, description) =>
                structureProfilesRpcBackend.UpdateProfileAsync(corporationId, profileId, name, description, call.Cancellation),
            SaveProfileSettingsAsync = (call, corporationId, profileId, settings) =>
                structureProfilesRpcBackend.SaveProfileSettingsAsync(corporationId, profileId, settings, call.Cancellation),
            SetDefaultProfileAsync = (call, corporationId, profileId) =>
                structureProfilesRpcBackend.SetDefaultProfileAsync(corporationId, profileId, call.Cancellation),
            ChangeProfilesAsync = (call, corporationId, structureIds, profileId) =>
                structureProfilesRpcBackend.ChangeProfilesAsync(corporationId, structureIds, profileId, call.Cancellation),
            DeleteProfileAsync = (call, corporationId, profileId) =>
                structureProfilesRpcBackend.DeleteProfileAsync(corporationId, profileId, call.Cancellation),
            DuplicateProfileAsync = (call, corporationId, profileId) =>
                structureProfilesRpcBackend.DuplicateProfileAsync(corporationId, profileId, call.Cancellation),
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
}
