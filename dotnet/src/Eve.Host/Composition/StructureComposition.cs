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

    private async Task StructureRecords()
    {
        // ST-0: the structures schema owns the records; the game store's table is copied in once (ADR 0003 rule 3: the bridge is the importer, not a read path)
        structureSchema = Env("EVE_STRUCTURES_SCHEMA", Eve.Structures.StructureStore.DefaultSchema);
        await Eve.Structures.StructureStore.MigrateAsync(source, structureSchema);
        structureStore = new Eve.Structures.StructureStore(source, structureSchema);
        var legacy = await ReadGameStoreRawAsync("structures", CancellationToken.None);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var copied = await structureStore.ImportAsync(connection, transaction, legacy, now());
        await transaction.CommitAsync();
        if (copied.Count > 0)
            Console.Error.WriteLine($"eve-host: {copied.Count} structure(s) copied from the game store into the {structureSchema} schema");
    }

    private async Task Structures()
    {
        // ST-0: the deadlines of the structures are durable jobs; the handler is registered here, before the foundations start
        var rules = await StructureRulesSource.LoadAsync(sde);
        structureTimers = new Eve.Structures.StructureTimers(structureStore, foundations.Scheduler, foundations.Events, rules);
        structureTimers.Register();
        // ST-7: the service fuel is paid from the fuel bay (inventory) at each full hour; the rules are the SDE's service modules
        structureFuel = new Eve.Structures.StructureFuel(structureStore, foundations.Scheduler, structureTimers, ServiceFuelRulesSource.Load(dogma), new InventoryStructureFuelBay(items, dogma));
        structureFuel.Register();
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
        structureProfilesRpcBackend = new Eve.Host.PersistenceStructureProfilesRpcBackend(
            ReadGameStoreTableAsync, mailCache, characterSessions, SetStructureProfileAsync);
        structureRpcCallbacks = new Eve.Services.Core.StructureRpcCallbacks
        {
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
