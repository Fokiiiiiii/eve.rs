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
    private async Task CorporationMigrations()
    {
        // Codex-owned corporation registration; keep combat/NPC construction in its own block below.
        corporationSchema = Env("EVE_CORPORATIONS_SCHEMA", Eve.Corporations.CorporationStore.DefaultSchema);
        await Eve.Corporations.CorporationStore.MigrateAsync(source, corporationSchema);
        await Eve.Corporations.CorporationOfficeStore.MigrateAsync(source, corporationSchema);
    }

    async Task SetCorporationBlueprintLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long corporationId,
        long itemId,
        long typeId,
        long locationId,
        bool locked,
        CancellationToken cancellationToken)
    {
        var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        var type = item is null ? null : await itemTypes.GetAsync(item.TypeId, cancellationToken);
        if (item is null || item.OwnerId != corporationId || item.TypeId != typeId ||
            item.LocationId != locationId || !Eve.Inventory.ItemFlags.IsCorporationHangar(item.Flag) ||
            !item.Singleton || item.BlueprintCopy || type?.GroupId != 9)
            throw new Eve.Corporations.CorporationError("CORPORATION_VOTE_BLUEPRINT_NOT_LOCKABLE");
        await items.SetBlueprintLockedAsync(connection, transaction, corporationId, itemId, locked, cancellationToken);
    }

    private async Task CorporationStores()
    {
        corporations = new Eve.Corporations.CorporationStore(source, characters, wallets, economyWallets, skills, now, corporationSchema,
            setBlueprintLock: SetCorporationBlueprintLockAsync);
        await corporations.SeedNpcAsync(sde);
        foundations.Scheduler.Register("corporation.votes.resolve",
            async (_, token) => await corporations.ResolveAllExpiredVotesAsync(token));
        await foundations.Scheduler.EnsureRecurringAsync(
            "corporation.votes.resolve", "resolve", TimeSpan.FromMinutes(1));
        corporationLogin = new Eve.Services.Corporations.CorporationLoginHook(corporations, characters);
        marketSchema = Env("EVE_MARKET_SCHEMA", Eve.Market.MarketStore.DefaultSchema);
        await Eve.Market.MarketStore.MigrateAsync(source, marketSchema);
        market = new Eve.Market.MarketStore(source, marketSchema);
        marketCatalog = await Eve.Market.MarketCatalog.LoadAsync(sde);
    }

    private async Task MarketSeed()
    {
        if (Env("EVE_MARKET_SEED", "auto") is "skip" or "off")
        {
            // CI and development: no fetch from CCP's ESI (minutes, and a load on it); the market starts with no NPC orders
            Console.Error.WriteLine("eve-host: native market v4 seed skipped (EVE_MARKET_SEED=skip)");
        }
        else if (!await market.HasSeedV4Async(build))
        {
            var typeIds = (await sde.ReadTypeSummariesAsync()).Where(type => type.TypeId > 0).Select(type => (long)type.TypeId).ToHashSet();
            var npcStationIds = stations.All.Select(station => station.Id).ToHashSet();
            var regionIds = stations.All.Select(station => (long)station.RegionId).Distinct().ToArray();
            var snapshotPath = Path.Combine(Path.GetTempPath(), $"eve.rs-market-seed-v4-{Guid.NewGuid():N}.jsonl");
            try
            {
                await using var snapshotFile = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var esi = new HttpClient();
                var captured = await Eve.Market.MarketSeedV4.CaptureAsync(
                    esi, snapshotFile, regionIds, typeIds, npcStationIds, build, DateTimeOffset.FromUnixTimeMilliseconds(now()));
                await snapshotFile.FlushAsync();
                snapshotFile.Position = 0;
                var fingerprint = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(snapshotFile)).ToLowerInvariant();
                snapshotFile.Position = 0;
                var snapshot = Eve.Market.MarketSeedV4.Read(snapshotFile);
                var imported = await market.SeedNpcV4Async(
                    Eve.Market.MarketSeedV4.ToSeedsAsync(snapshot.Orders, stations),
                    fingerprint, checked((int)build), captured.CapturedAtUtc.ToUnixTimeMilliseconds(), now);
                if (imported == 0)
                {
                    Console.Error.WriteLine("eve-host: matching market v4 seed was imported by another host instance");
                }
                else
                {
                    var verified = await snapshot.Completion;
                    if (verified.RetainedOrderCount != captured.RetainedOrderCount || imported != captured.RetainedOrderCount)
                        throw new InvalidDataException("Market Seed v4 snapshot/import counts do not match.");
                    Console.Error.WriteLine($"eve-host: native market v4 seeded {imported} TQ NPC-station orders");
                }
            }
            finally
            {
                if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
            }
        }
        else
        {
            Console.Error.WriteLine($"eve-host: native market v4 seed already loaded for SDE build {build}");
        }
        marketTopology = await Eve.Market.MarketTopology.LoadAsync(sde);
        marketTrading = new Eve.Market.MarketTrading(source, market, characters, corporations, skills, items, economyWallets, stations, marketTopology, now);
    }

    private async Task Alliances()
    {
        allianceAffiliationNotify = spaceSessions is null
            ? static (_, _) => Task.CompletedTask
            : spaceSessions.AffiliationChangedAsync;
        allianceDue = new Eve.Services.Corporations.AllianceApplicationDueWorker(
            corporations, characters, foundations.Scheduler, foundations.Relations, foundations.Events,
            allianceAffiliationNotify, characterSessions);
        await allianceDue.RegisterAsync();
        allianceRegistry = new Eve.Services.Corporations.AllianceRegistryService(
            corporations, characters, now,
            scheduleAllianceApplication: allianceDue.ScheduleAcceptedAsync,
            wakeAllianceApplicationScheduler: allianceDue.Wake,
            relations: foundations.Relations,
            characterSessions: characterSessions,
            readSovSnapshot: ReadAllianceSovSnapshotAsync);
    }

    private async Task<Eve.Services.Corporations.AllianceSovSnapshot?> ReadAllianceSovSnapshotAsync(
        long allianceId,CancellationToken token)
    {
        var snapshot = await sovereigntyReadModel.ReadSnapshotAsync(token);
        var claims = snapshot.ClaimsBySolarSystemId.ToDictionary(
            pair => pair.Key,pair => pair.Value.AllianceId);
        var hubs = snapshot.HubsBySolarSystemId.ToDictionary(
            pair => pair.Key,pair => (pair.Value.AllianceId,pair.Value.HubId));
        var now = corporations.VoteNowFiletime;
        var nextVulnerability = snapshot.StructuresBySolarSystemId.Values
            .SelectMany(structures => structures)
            .Where(structure => structure.AllianceId == allianceId &&
                structure.VulnerableEndTimeFileTime is { } end && end > now)
            .Select(structure => structure.VulnerableEndTimeFileTime!.Value)
            .DefaultIfEmpty(0)
            .Min();
        return new Eve.Services.Corporations.AllianceSovSnapshot(
            claims,hubs,nextVulnerability > 0 ? nextVulnerability : null);
    }

    private async Task CorporationOffices()
    {
        corporationOfficeStore = new Eve.Corporations.CorporationOfficeStore(source, corporationSchema);
        officeManager = new Eve.Services.Corporations.OfficeManagerService(
            corporationOfficeStore, corporations, characters, items,
            economyWallets,
            id => stations.Get(id) is { } station
                ? new Eve.Corporations.CorporationOfficeStation(
                    station.Id, station.TypeId, station.OwnerId, station.SolarSystemId, station.Security)
                : null,
            now);
        skinCosmeticsCatalog = await ShipCosmeticsCatalog.LoadAsync(sde);
    }
}
