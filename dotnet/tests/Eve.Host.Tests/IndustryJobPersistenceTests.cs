using Eve.Corporations;
using Eve.Characters;
using Eve.Data;
using Eve.Events;
using Eve.Inventory;
using Eve.Market;
using Eve.Scheduler;
using Eve.Services.Core;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The industry install, completion and cancel path over PostgreSQL and the pinned SDE (blueprint 681 makes
/// product 165 from 86 units of material 38; the adjusted price of 38 is seeded at 10 ISK).
/// </summary>
public sealed class IndustryJobPersistenceTests
{
    private const int Blueprint = 681;
    private const int Material = 38;
    private const int Product = 165;
    private const int ManufacturingTimeSeconds = 600;

    [Fact]
    public async Task Manufacturing_charges_consumes_locks_and_delivers_the_product_after_completion()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var blueprint = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Give(owner, Material, 86);
        var balanceBefore = await rig.Balance(owner);

        var jobId = await rig.Install(owner, blueprint, IndustryRules.Manufacturing);

        var high = await rig.HighSecurity();
        var expected = IndustryRules.Total(IndustryRules.CostParts(86 * 10.0, 0, 0.10, high, IndustryRules.Manufacturing));
        var (status, cost, seconds) = await rig.JobRow(jobId);
        Assert.Equal(1, status);
        Assert.Equal(expected, cost);
        Assert.Equal(ManufacturingTimeSeconds, seconds);
        Assert.Equal(balanceBefore - expected, await rig.Balance(owner));
        Assert.Equal(0, await rig.Quantity(owner, Material));
        Assert.True((await rig.Blueprint(blueprint)).BlueprintLocked);

        rig.Now += (ManufacturingTimeSeconds + 1) * 1000L;
        await rig.Complete(owner, jobId);

        Assert.Equal(100, (await rig.JobRow(jobId)).Status);
        Assert.Equal(1, await rig.Quantity(owner, Product));
        Assert.False((await rig.Blueprint(blueprint)).BlueprintLocked);
    }

    [Fact]
    public async Task Material_research_raises_material_efficiency_and_time_research_adds_two_per_run()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var blueprint = await rig.Give(owner, Blueprint, 1, blueprint: true);

        var me = await rig.Install(owner, blueprint, IndustryRules.ResearchMaterial);
        rig.Now += 1_000_000;
        await rig.Complete(owner, me);
        var afterMe = await rig.Blueprint(blueprint);
        Assert.Equal(1, afterMe.MaterialEfficiency);
        Assert.Equal(0, afterMe.TimeEfficiency);

        var te = await rig.Install(owner, blueprint, IndustryRules.ResearchTime);
        rig.Now += 1_000_000;
        await rig.Complete(owner, te);
        var afterTe = await rig.Blueprint(blueprint);
        Assert.Equal(1, afterTe.MaterialEfficiency);
        Assert.Equal(2, afterTe.TimeEfficiency);
    }

    [Fact]
    public async Task Cancel_marks_the_job_cancelled_unlocks_the_blueprint_and_keeps_the_materials_consumed()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var blueprint = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Give(owner, Material, 86);
        var jobId = await rig.Install(owner, blueprint, IndustryRules.Manufacturing);

        await rig.Cancel(owner, jobId);

        Assert.Equal(102, (await rig.JobRow(jobId)).Status);
        Assert.False((await rig.Blueprint(blueprint)).BlueprintLocked);
        Assert.Equal(0, await rig.Quantity(owner, Material));
    }

    [Fact]
    public async Task Insufficient_materials_refuse_the_install_without_charging_or_locking()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var blueprint = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Give(owner, Material, 85);
        var balanceBefore = await rig.Balance(owner);

        await Assert.ThrowsAsync<IndustryJobUserErrorException>(() => rig.Install(owner, blueprint, IndustryRules.Manufacturing));

        Assert.Equal(0, await rig.JobCount());
        Assert.False((await rig.Blueprint(blueprint)).BlueprintLocked);
        Assert.Equal(balanceBefore, await rig.Balance(owner));
        Assert.Equal(85, await rig.Quantity(owner, Material));
    }

    [Fact]
    public async Task A_second_manufacturing_job_is_refused_while_the_only_slot_is_busy()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var first = await rig.Give(owner, Blueprint, 1, blueprint: true);
        var second = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Give(owner, Material, 172);
        await rig.Install(owner, first, IndustryRules.Manufacturing);

        await Assert.ThrowsAsync<IndustryJobUserErrorException>(() => rig.Install(owner, second, IndustryRules.Manufacturing));
        Assert.Equal(1, await rig.JobCount());
    }
}

internal sealed class IndustryRig : IAsyncDisposable
{
    private const int BlueprintType = 681;
    private const int Material = 38;
    private readonly string[] _schemas;
    private readonly NpgsqlDataSource _source;
    private readonly Eve.Universe.Station _station;
    private readonly PgItemStore _items;
    private readonly WalletTransactions _wallets;
    private readonly PgWalletStore _walletStore;
    private readonly PgCharacterStore _characters;
    private readonly PgSkillStore _skills;
    private readonly SdeReader _sde;
    private readonly AdjustedPriceStore _prices;
    private readonly PersistenceIndustryJobOperations _operations;

    private IndustryRig(NpgsqlDataSource source, SdeReader sde, StationCatalog stations)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        _schemas = ["ij_mk_" + id, "ij_ch_" + id, "ij_wa_" + id, "ij_in_" + id, "ij_sk_" + id,
                    "ij_co_" + id, "ij_sc_" + id, "ij_ev_" + id, "ij_id_" + id];
        _source = source;
        _sde = sde;
        _station = stations.All.First();
        _characters = new PgCharacterStore(source, _schemas[1]);
        _walletStore = new PgWalletStore(source, _schemas[2]);
        _wallets = new WalletTransactions(source, _schemas[2]);
        _items = new PgItemStore(source, _schemas[3]);
        _skills = new PgSkillStore(source, _schemas[4]);
        var corporations = new CorporationStore(source, _characters, _walletStore, _wallets, _skills, () => Now, _schemas[5]);
        var scheduler = new DurableScheduler(source, () => Now, null, _schemas[6]);
        var events = new EventBus(source, scheduler, null, _schemas[7]);
        _prices = new AdjustedPriceStore(source, _schemas[0]);
        var station = _station;
        _operations = new PersistenceIndustryJobOperations(
            source, _schemas[8], _items, _wallets, _skills, sde,
            new AdjustedPriceCache(_prices, null, () => Now), corporations, events, stations,
            (facilityId, stationId, systemId, characterId, token) => ValueTask.FromResult<PersistenceIndustryJobOperations.IndustryFacility?>(
                new PersistenceIndustryJobOperations.IndustryFacility(
                    station.Id, station.SolarSystemId, station.OwnerId, 0.10m, 1d, true, null, new HashSet<int> { 14, 15 })),
            (systemId, activityId, token) => ValueTask.FromResult(0m),
            () => Now);
    }

    public long Now { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<IndustryRig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var stations = await StationCatalog.LoadAsync(sde);
        var rig = new IndustryRig(source, sde, stations);
        await MarketStore.MigrateAsync(source, rig._schemas[0]);
        await PgCharacterStore.MigrateAsync(source, rig._schemas[1]);
        await PgWalletStore.MigrateAsync(source, rig._schemas[2]);
        await PgItemStore.MigrateAsync(source, rig._schemas[3]);
        await PgSkillStore.MigrateAsync(source, rig._schemas[4]);
        await CorporationStore.MigrateAsync(source, rig._schemas[5]);
        await DurableScheduler.MigrateAsync(source, rig._schemas[6]);
        await EventBus.MigrateAsync(source, rig._schemas[7]);
        await Migrations.ApplyAsync(source, rig._schemas[8], [IndustryJobsSchema.Jobs(rig._schemas[3])]);
        await rig._prices.ReplaceAsync([new AdjustedPriceRow(Material, 10.0, 10.0)], rig.Now);
        return rig;
    }

    public async Task<long> Owner(decimal isk = 1_000_000m)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = await _characters.InsertAsync(c, tx, new NewCharacter(1, "Industrialist", 0, 1, 1, 1, 11, 1376, 1000044, null,
            _station.Id, _station.SolarSystemId, _station.ConstellationId, _station.RegionId, null, null));
        await _walletStore.OpenAsync(c, tx, id, isk, 0);
        await tx.CommitAsync();
        return id;
    }

    public async Task<long> Give(long owner, int typeId, long quantity, bool blueprint = false)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = blueprint
            ? await _items.CreateAsync(c, tx, new NewItem(typeId, owner, _station.Id, ItemFlags.Hangar, 1, true,
                BlueprintCopy: false, BlueprintRuns: -1, MaterialEfficiency: 0, TimeEfficiency: 0))
            : await _items.CreateAsync(c, tx, new NewItem(typeId, owner, _station.Id, ItemFlags.Hangar, quantity));
        await tx.CommitAsync();
        return id;
    }

    public async Task<long> Install(long owner, long blueprintId, int activityId)
    {
        var job = new IndustryJobInstallSubmission(
            activityId, blueprintId, BlueprintType, false, _station.Id, ItemFlags.Hangar,
            _station.Id, owner, _station.SolarSystemId, _station.Id,
            new IndustryJobLocation(_station.Id, ItemFlags.Hangar), new IndustryJobLocation(_station.Id, ItemFlags.Hangar),
            1, 1, null, null, null, new IndustryJobWallet(owner, 1000));
        var request = new IndustryJobInstallRequest(
            new IndustryJobOperationContext(new IndustryJobActor(owner, 0, 0, 1000), "op-" + Guid.NewGuid().ToString("N")), job);
        return await _operations.InstallJobAsync(request, default);
    }

    public Task Complete(long owner, long jobId) =>
        _operations.CompleteJobAsync(new IndustryJobAction(
            new IndustryJobOperationContext(new IndustryJobActor(owner, 0, 0, 1000), "complete-" + jobId), jobId, _station.SolarSystemId), default).AsTask();

    public Task Cancel(long owner, long jobId) =>
        _operations.CancelJobAsync(new IndustryJobAction(
            new IndustryJobOperationContext(new IndustryJobActor(owner, 0, 0, 1000), "cancel-" + jobId), jobId, _station.SolarSystemId), default).AsTask();

    public async Task<decimal> Balance(long owner) => await _wallets.AccountBalanceAsync(new WalletAccount(owner, 1000)) ?? 0m;

    public async Task<bool> HighSecurity()
    {
        var rows = await _sde.ReadAsync("mapSolarSystems", [_station.SolarSystemId]);
        var status = rows[0].Doc.GetProperty("securityStatus").GetDouble();
        return Math.Round(status, 1, MidpointRounding.AwayFromZero) >= 0.5;
    }

    public async Task<Item> Blueprint(long itemId)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var item = await _items.LockAndGetAsync(c, tx, itemId);
        await tx.CommitAsync();
        return item!;
    }

    public async Task<long> Quantity(long owner, int typeId)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var items = await _items.LockInLocationAsync(c, tx, _station.Id, owner, ItemFlags.Hangar, [typeId]);
        await tx.CommitAsync();
        return items.Where(item => item.TypeId == typeId).Sum(item => item.Quantity);
    }

    public async Task<(long Status, long Cost, long Seconds)> JobRow(long jobId)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT status, cost_isk, time_in_seconds FROM {_schemas[8]}.jobs WHERE job_id = $1", c);
        command.Parameters.Add(new NpgsqlParameter { Value = jobId });
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2));
    }

    public async Task<long> JobCount()
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {_schemas[8]}.jobs", c);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await using var command = _source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await _source.DisposeAsync();
    }
}
