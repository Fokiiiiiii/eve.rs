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

    [Fact]
    public async Task A_structure_rig_and_role_bonuses_scale_the_materials_and_the_time_of_its_jobs()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var blueprint = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Give(owner, Material, 86);
        rig.Structure = true;
        rig.Activities = new HashSet<int> { IndustryRules.Manufacturing };
        rig.Bonuses =
        [
            // A rig on the product's group (Clone, group 23): material -2 %, time -20 %.
            new IndustryBonus(IndustryBonusKind.Material, IndustryRules.Manufacturing, 0.98, IndustryBonuses.SourceRig, GroupId: 23, RigTypeId: 1),
            new IndustryBonus(IndustryBonusKind.Time, IndustryRules.Manufacturing, 0.8, IndustryBonuses.SourceRig, GroupId: 23, RigTypeId: 1),
            // A role bonus of an engineering complex: time -15 %, on every product.
            new IndustryBonus(IndustryBonusKind.Time, IndustryRules.Manufacturing, 0.85, IndustryBonuses.SourceHull),
            // A rig on another group (Cruiser, 26): it does not apply to this product.
            new IndustryBonus(IndustryBonusKind.Material, IndustryRules.Manufacturing, 0.5, IndustryBonuses.SourceRig, GroupId: 26, RigTypeId: 2),
        ];

        var jobId = await rig.Install(owner, blueprint, IndustryRules.Manufacturing);

        // 86 x 0.98 = 84.28, rounded up to 85 consumed; 600 s x 0.85 x 0.8 = 408 s.
        var (status, _, seconds) = await rig.JobRow(jobId);
        Assert.Equal(1, status);
        Assert.Equal(408, seconds);
        Assert.Equal(1, await rig.Quantity(owner, Material));
    }

    [Fact]
    public async Task A_rig_for_another_product_group_changes_neither_the_materials_nor_the_time()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var blueprint = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Give(owner, Material, 86);
        rig.Structure = true;
        rig.Activities = new HashSet<int> { IndustryRules.Manufacturing };
        rig.Bonuses =
        [
            new IndustryBonus(IndustryBonusKind.Material, IndustryRules.Manufacturing, 0.5, IndustryBonuses.SourceRig, GroupId: 26, RigTypeId: 2),
            new IndustryBonus(IndustryBonusKind.Time, IndustryRules.Manufacturing, 0.5, IndustryBonuses.SourceRig, GroupId: 26, RigTypeId: 2),
        ];

        var jobId = await rig.Install(owner, blueprint, IndustryRules.Manufacturing);

        var (_, _, seconds) = await rig.JobRow(jobId);
        Assert.Equal(ManufacturingTimeSeconds, seconds);
        Assert.Equal(0, await rig.Quantity(owner, Material));
    }
}

/// <summary>Every system and activity has the same index (the configured fallback, with nothing stored).</summary>
internal sealed class FixedCostIndexes(decimal rate) : IIndustryCostIndexSource
{
    public decimal Fallback => rate;

    public ValueTask<decimal> GetAsync(long solarSystemId, int activityId, CancellationToken cancellationToken) => ValueTask.FromResult(rate);

    public ValueTask<IReadOnlyDictionary<long, decimal>> GetActivityAsync(int activityId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyDictionary<long, decimal>>(new Dictionary<long, decimal>());
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
    private readonly CorporationStore _corporations;

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
        _corporations = corporations;
        var scheduler = new DurableScheduler(source, () => Now, null, _schemas[6]);
        var events = new EventBus(source, scheduler, null, _schemas[7]);
        Scheduler = scheduler;
        ReadyJobs = new IndustryReadyJobs(source, _schemas[8], events, scheduler, () => Now);
        ReadyJobs.Register();
        _prices = new AdjustedPriceStore(source, _schemas[0]);
        var station = _station;
        _operations = new PersistenceIndustryJobOperations(
            source, _schemas[8], _items, _wallets, _skills, sde,
            new AdjustedPriceCache(_prices, null, () => Now), corporations, events, stations,
            (facilityId, stationId, systemId, characterId, token) => ValueTask.FromResult<PersistenceIndustryJobOperations.IndustryFacility?>(
                new PersistenceIndustryJobOperations.IndustryFacility(
                    station.Id, station.SolarSystemId, station.OwnerId, 0.10m, 1d, true, null,
                    Structure ? null : new HashSet<int> { 14, 15 }, Structure ? Activities : null,
                    Structure ? Bonuses : null)),
            new FixedCostIndexes(0m),
            (characterId, token) => ValueTask.FromResult<long?>(InstallerSystem ?? station.SolarSystemId),
            ReadyJobs,
            () => Now);
    }

    public long Now { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>The durable scheduler the readiness jobs wait in (the rig's host, before any restart).</summary>
    public DurableScheduler Scheduler { get; }

    /// <summary>The readiness jobs of the rig's host.</summary>
    public IndustryReadyJobs ReadyJobs { get; }

    /// <summary>A restart of the host: a new scheduler and readiness service over the same schemas, which runs what is due.</summary>
    public async Task<int> RestartAndRunDueAsync()
    {
        var scheduler = new DurableScheduler(_source, () => Now, null, _schemas[6]);
        new IndustryReadyJobs(_source, _schemas[8], new EventBus(_source, scheduler, null, _schemas[7]), scheduler, () => Now).Register();
        return await scheduler.RunDueAsync();
    }

    /// <summary>When true the facility is an industry structure (no NPC station services); otherwise the NPC station with its Factory and Laboratory.</summary>
    public bool Structure { get; set; }

    /// <summary>The activities of a structure (what its online services run); null means every activity.</summary>
    public IReadOnlySet<int>? Activities { get; set; }

    /// <summary>The rig and role bonuses of the structure (used when Structure is set).</summary>
    public IReadOnlyList<IndustryBonus>? Bonuses { get; set; }

    /// <summary>The installer's current solar system; null means the station's own system (zero jumps).</summary>
    public long? InstallerSystem { get; set; }

    /// <summary>A system one stargate jump from the station's system.</summary>
    public async Task<long> NeighbourSystem()
    {
        var gates = await _sde.ReadContainingAsync("mapStargates", $"{{\"solarSystemID\": {_station.SolarSystemId}}}");
        return gates[0].Doc.GetProperty("destination").GetProperty("solarSystemID").GetInt64();
    }

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
        await rig._prices.ReplaceAsync([new AdjustedPriceRow(Material, 10.0, 10.0), new AdjustedPriceRow(16657, 2.0, 2.0), new AdjustedPriceRow(16661, 3.0, 3.0)], rig.Now);
        return rig;
    }

    public async Task<long> Owner(decimal isk = 1_000_000m)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = await _characters.InsertAsync(c, tx, new NewCharacter(1, "Industrialist " + Guid.NewGuid().ToString("N")[..8], 0, 1, 1, 1, 11, 1376, 1000044, null,
            _station.Id, _station.SolarSystemId, _station.ConstellationId, _station.RegionId, null, null));
        await _walletStore.OpenAsync(c, tx, id, isk, 0);
        await tx.CommitAsync();
        return id;
    }

    public async Task<long> Give(long owner, int typeId, long quantity, bool blueprint = false, bool copy = false, int runs = -1, int me = 0, int te = 0)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = blueprint
            ? await _items.CreateAsync(c, tx, new NewItem(typeId, owner, _station.Id, ItemFlags.Hangar, 1, true,
                BlueprintCopy: copy, BlueprintRuns: runs, MaterialEfficiency: me, TimeEfficiency: te))
            : await _items.CreateAsync(c, tx, new NewItem(typeId, owner, _station.Id, ItemFlags.Hangar, quantity));
        await tx.CommitAsync();
        return id;
    }

    public async Task<long> Install(long owner, long blueprintId, int activityId, int blueprintType = BlueprintType, bool copy = false,
        int runs = 1, int licensedRuns = 1, int? productTypeId = null, int? optionalTypeId = null)
    {
        var job = new IndustryJobInstallSubmission(
            activityId, blueprintId, blueprintType, copy, _station.Id, ItemFlags.Hangar,
            _station.Id, owner, _station.SolarSystemId, _station.Id,
            new IndustryJobLocation(_station.Id, ItemFlags.Hangar), new IndustryJobLocation(_station.Id, ItemFlags.Hangar),
            runs, licensedRuns, productTypeId, optionalTypeId, null, new IndustryJobWallet(owner, 1000));
        var request = new IndustryJobInstallRequest(
            new IndustryJobOperationContext(new IndustryJobActor(owner, 0, 0, 1000), "op-" + Guid.NewGuid().ToString("N")), job);
        return await _operations.InstallJobAsync(request, default);
    }

    /// <summary>A corporation founded by the character (who becomes its director); its wallet is open and empty.</summary>
    public async Task<long> Corporation(long founder) =>
        (await _corporations.CreateAsync(founder, new NewCorporation("Industry Corp", "INDC", "", "", 0m), "found-" + Guid.NewGuid().ToString("N"))).Id;

    /// <summary>A character who is a member of the corporation with the given role bits (a non-director member).</summary>
    public async Task<long> Member(long corporation, long roles)
    {
        var character = await Owner();
        await using var c = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schemas[5]}.members (character_id, corporation_id, started_ms, roles) VALUES ($1, $2, 0, $3)", c);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        command.Parameters.Add(new NpgsqlParameter { Value = corporation });
        command.Parameters.Add(new NpgsqlParameter { Value = roles });
        await command.ExecuteNonQueryAsync();
        return character;
    }

    /// <summary>Pays ISK into the corporation's wallet (the account it pays jobs from).</summary>
    public async Task Fund(long corporation, decimal isk)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await _wallets.AdjustAsync(c, tx, corporation, isk, 1000125, 39, "test funding", Now, "fund-" + Guid.NewGuid().ToString("N"));
        await tx.CommitAsync();
    }

    /// <summary>A corporation job installed by a member: the member is the installer, the corporation owns the blueprint and pays.</summary>
    public async Task<long> InstallForCorporation(long member, long corporation, long blueprintId, int activityId, int runs = 1,
        int outputFlag = 115)
    {
        var job = new IndustryJobInstallSubmission(
            activityId, blueprintId, BlueprintType, false, _station.Id, ItemFlags.Hangar,
            _station.Id, corporation, _station.SolarSystemId, _station.Id,
            new IndustryJobLocation(_station.Id, ItemFlags.Hangar), new IndustryJobLocation(_station.Id, outputFlag),
            runs, 1, null, null, null, new IndustryJobWallet(corporation, 1000));
        var request = new IndustryJobInstallRequest(
            new IndustryJobOperationContext(new IndustryJobActor(member, corporation, 0, 1000), "op-" + Guid.NewGuid().ToString("N")), job);
        return await _operations.InstallJobAsync(request, default);
    }

    public Task Complete(long owner, long jobId) =>
        _operations.CompleteJobAsync(new IndustryJobAction(
            new IndustryJobOperationContext(new IndustryJobActor(owner, 0, 0, 1000), "complete-" + jobId), jobId, _station.SolarSystemId), default).AsTask();

    public Task Cancel(long owner, long jobId) =>
        _operations.CancelJobAsync(new IndustryJobAction(
            new IndustryJobOperationContext(new IndustryJobActor(owner, 0, 0, 1000), "cancel-" + jobId), jobId, _station.SolarSystemId), default).AsTask();

    /// <summary>The items of a type in the station hangar that the owner holds (the outputs and inputs of the jobs).</summary>
    public async Task<List<Item>> Items(long owner, int typeId)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var items = await _items.LockInLocationAsync(c, tx, _station.Id, owner, ItemFlags.Hangar, [typeId]);
        await tx.CommitAsync();
        return items.Where(item => item.TypeId == typeId).ToList();
    }

    /// <summary>Grants skills at the given levels (the skill points are the level's minimum).</summary>
    public async Task Grant(long owner, params (int TypeId, int Level)[] skills)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await _skills.GrantAsync(c, tx, owner, skills.Select(skill => new CharacterSkill(skill.TypeId, skill.Level, 0)).ToArray());
        await tx.CommitAsync();
    }

    /// <summary>The job's stored activity snapshot, as JSON.</summary>
    public async Task<string> SnapshotJson(long jobId)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT activity_snapshot::text FROM {_schemas[8]}.jobs WHERE job_id = $1", c);
        command.Parameters.Add(new NpgsqlParameter { Value = jobId });
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The job's probability, as stored at install.</summary>
    public async Task<double> Probability(long jobId)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT probability FROM {_schemas[8]}.jobs WHERE job_id = $1", c);
        command.Parameters.Add(new NpgsqlParameter { Value = jobId });
        return (double)(await command.ExecuteScalarAsync())!;
    }

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

    /// <summary>The quantity of a type that the owner holds at the station in one flag (a corporation hangar division or deliveries).</summary>
    public async Task<long> QuantityAt(long owner, int typeId, int flag)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var items = await _items.LockInLocationAsync(c, tx, _station.Id, owner, flag, [typeId]);
        await tx.CommitAsync();
        return items.Where(item => item.TypeId == typeId).Sum(item => item.Quantity);
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

    public async Task<long> JobOwner(long jobId)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT owner_id FROM {_schemas[8]}.jobs WHERE job_id = $1", c);
        command.Parameters.Add(new NpgsqlParameter { Value = jobId });
        return (long)(await command.ExecuteScalarAsync())!;
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
