using Eve.Data;
using Eve.Events;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Wallet;
using Npgsql;

namespace Eve.FactionWarfare.Tests;

/// <summary>A fact that needs PostgreSQL: skipped (not passed) when EVE_PG_URL is not set.</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            Skip = "EVE_PG_URL is not set";
    }
}

/// <summary>A factional warfare store with its own schemas and a wallet, on a clock the test moves.</summary>
public sealed class FwRig : IAsyncDisposable
{
    /// <summary>10:00 UTC on a day boundary: the first downtime after it is 11:00 the same day.</summary>
    public const long Epoch = 1_699_956_000_000;
    public const long Hour = 3_600_000;

    private FwRig(NpgsqlDataSource source, string id)
    {
        Source = source;
        Store = new FwStore(source, "fw_" + id);
        Loyalty = new LoyaltyPointStore(source, "wal_" + id);
        Scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, "sch_" + id);
        Bus = new EventBus(source, Scheduler, null, "evt_" + id);
        Relations = new RelationStore(source, Scheduler, Bus, "rel_" + id);
        Service = new FwService(Store, Loyalty, Relations);
    }

    public NpgsqlDataSource Source { get; }
    public FwStore Store { get; }
    public LoyaltyPointStore Loyalty { get; }
    public DurableScheduler Scheduler { get; }
    public EventBus Bus { get; }
    public RelationStore Relations { get; }
    public string RelationSchema => RelationsSchema;
    public FwService Service { get; }
    public long Now { get; set; } = Epoch;

    private string RelationsSchema => Store.Schema.Replace("fw_", "rel_", StringComparison.Ordinal);

    public static async Task<FwRig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var id = Guid.NewGuid().ToString("N")[..12];
        await FwStore.MigrateAsync(source, "fw_" + id);
        await PgWalletStore.MigrateAsync(source, "wal_" + id);
        await DurableScheduler.MigrateAsync(source, "sch_" + id);
        await EventBus.MigrateAsync(source, "evt_" + id);
        await RelationStore.MigrateAsync(source, "rel_" + id);
        return new FwRig(source, id);
    }

    /// <summary>Gives a character LP from an issuing corporation (a test credit, as a mission or a wallet grant would).</summary>
    public async Task CreditAsync(long characterId, long issuerCorporationId, long amount)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Loyalty.ApplyAsync(connection, transaction, new LoyaltyHolder(LoyaltyHolderKind.Character, characterId), issuerCorporationId, amount,
            "test.credit", "credit:" + Guid.NewGuid().ToString("N"));
        await transaction.CommitAsync();
    }

    public async Task<long> BalanceAsync(long characterId, long issuerCorporationId) =>
        (await Loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, characterId))).FirstOrDefault(b => b.IssuerCorpId == issuerCorporationId)?.Amount ?? 0;

    /// <summary>Occupies every system of a warzone for one faction (the contest of the test needs a share of the warzone).</summary>
    public async Task OccupyWarzoneAsync(long warzoneId, long factionId, long pool = 0)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var systems = FwWarzones.SystemsOf(warzoneId).Select(id => new FwSystem(id, warzoneId, factionId, id == FwWarzones.SystemsOf(warzoneId)[0] ? pool : 0, 0, 0));
        await Store.SeedSystemsAsync(connection, transaction, systems);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using (var connection = await Source.OpenConnectionAsync())
        {
            foreach (var schema in new[] { Store.Schema, "wal_" + Store.Schema[3..], "sch_" + Store.Schema[3..], "evt_" + Store.Schema[3..], RelationsSchema })
                await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
        await Source.DisposeAsync();
    }
}

public sealed class FwDatabaseTests
{
    private const long Character = 90_000_001;
    private const long Corporation = 98_000_001;
    private const long Alliance = 99_000_001;
    private const long Amarr = FwFactions.AmarrEmpire;
    private const long JitaSystem = 30000142;

    [PgFact]
    public async Task Corporation_join_is_pending_until_the_downtime_then_active()
    {
        await using var rig = await FwRig.CreateAsync();
        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.JoinAsync(FwStore.CorporationKind, Corporation, Amarr, 0.5, rig.Now));
        var pending = await rig.Service.StatusAsync(FwStore.CorporationKind, Corporation, rig.Now);
        Assert.Equal(new FwStatus(FwService.JoiningStatus, Amarr, rig.Now), pending);
        Assert.Equal(0, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, rig.Now));
        Assert.Empty(await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation));
        Assert.Empty(await rig.Service.CorporationsInFactionAsync(Amarr, rig.Now));

        var downtime = FwRules.NextDowntimeMs(rig.Now);
        Assert.Equal(Amarr, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, downtime));
        Assert.Equal([Amarr], (await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation)).Select(row => row.Object));
        Assert.Equal(new FwStatus(FwService.ActiveStatus, Amarr, downtime), await rig.Service.StatusAsync(FwStore.CorporationKind, Corporation, downtime));
        Assert.Equal(new long[] { Corporation }, await rig.Service.CorporationsInFactionAsync(Amarr, downtime));
    }

    [PgFact]
    public async Task Corporation_leave_is_pending_and_can_be_withdrawn_before_the_downtime()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.Service.JoinAsync(FwStore.CorporationKind, Corporation, Amarr, 0.5, rig.Now);
        var afterJoin = FwRules.NextDowntimeMs(rig.Now);
        Assert.Equal(Amarr, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, afterJoin));
        Assert.Equal([Amarr], (await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation)).Select(row => row.Object));
        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.LeaveAsync(FwStore.CorporationKind, Corporation, afterJoin));
        Assert.Equal(Amarr, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, afterJoin));
        Assert.Equal([Amarr], (await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation)).Select(row => row.Object));
        Assert.Equal(FwService.LeavingStatus, (await rig.Service.StatusAsync(FwStore.CorporationKind, Corporation, afterJoin))!.Status);

        Assert.Equal(EnlistOutcome.NothingToWithdraw, await rig.Service.WithdrawJoinAsync(FwStore.CorporationKind, Corporation, afterJoin));
        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.WithdrawLeaveAsync(FwStore.CorporationKind, Corporation, afterJoin));
        Assert.Equal(FwService.ActiveStatus, (await rig.Service.StatusAsync(FwStore.CorporationKind, Corporation, afterJoin))!.Status);

        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.LeaveAsync(FwStore.CorporationKind, Corporation, afterJoin));
        var afterLeave = FwRules.NextDowntimeMs(afterJoin);
        Assert.Equal(0, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, afterLeave));
        Assert.Empty(await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation));
        Assert.Null(await rig.Service.StatusAsync(FwStore.CorporationKind, Corporation, afterLeave));
    }

    [PgFact]
    public async Task Join_refusals_follow_the_occupier_standing_and_enlistment_rules()
    {
        await using var rig = await FwRig.CreateAsync();
        Assert.Equal(EnlistOutcome.NotAnOccupier, await rig.Service.JoinAsync(FwStore.CorporationKind, Corporation, FwFactions.GuristasPirates, 5, rig.Now));
        Assert.Equal(EnlistOutcome.BelowStanding, await rig.Service.JoinAsync(FwStore.CorporationKind, Corporation, Amarr, -0.5, rig.Now));
        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.JoinAsync(FwStore.CorporationKind, Corporation, Amarr, 0, rig.Now));
        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.JoinAsync(FwStore.CorporationKind, Alliance, Amarr, 0, rig.Now));
        var downtime = FwRules.NextDowntimeMs(rig.Now);
        Assert.Equal(EnlistOutcome.AlreadyEnlisted, await rig.Service.JoinAsync(FwStore.CorporationKind, Corporation, FwFactions.CaldariState, 5, downtime));
        Assert.Equal(EnlistOutcome.NothingToWithdraw, await rig.Service.WithdrawJoinAsync(FwStore.CorporationKind, Corporation, downtime));
    }

    [PgFact]
    public async Task Concurrent_first_joins_for_one_corporation_keep_only_one_pending_faction()
    {
        await using var rig = await FwRig.CreateAsync();
        long corporationId = 98_100_000 + Random.Shared.Next(1_000_000);
        await using var blocker = await rig.Source.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand($"LOCK TABLE {rig.RelationSchema}.relations IN ACCESS EXCLUSIVE MODE", blocker, blockerTransaction))
            await lockTable.ExecuteNonQueryAsync();

        Task<EnlistOutcome> first = Task.Run(() => rig.Service.JoinAsync(FwStore.CorporationKind, corporationId, FwFactions.AmarrEmpire, 0, rig.Now));
        Task<EnlistOutcome> second = Task.Run(() => rig.Service.JoinAsync(FwStore.CorporationKind, corporationId, FwFactions.CaldariState, 0, rig.Now));
        bool bothQueuedAtOwnerOrRelationLocks = false;
        await using (var monitor = await rig.Source.OpenConnectionAsync())
        {
            for (int attempt = 0; attempt < 500; attempt++)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() " +
                    "AND state = 'active' AND wait_event_type = 'Lock' AND (query LIKE $1 OR query LIKE $2)", monitor);
                command.Parameters.AddWithValue("%" + rig.Store.Schema + "%");
                command.Parameters.AddWithValue("%" + rig.RelationSchema + "%");
                if ((long)(await command.ExecuteScalarAsync())! >= 2)
                {
                    bothQueuedAtOwnerOrRelationLocks = true;
                    break;
                }
                await Task.Delay(10);
            }
        }

        await blockerTransaction.CommitAsync();
        EnlistOutcome[] outcomes = await Task.WhenAll(first, second);
        Assert.True(bothQueuedAtOwnerOrRelationLocks, "both concurrent enlistment transactions must reach their owner or relation lock");
        Assert.Equal(1, outcomes.Count(outcome => outcome == EnlistOutcome.Accepted));
        Assert.Equal(1, outcomes.Count(outcome => outcome == EnlistOutcome.AlreadyEnlisted));
        var stored = await rig.Service.EnlistmentAsync(FwStore.CorporationKind, corporationId, rig.Now);
        Assert.Contains(stored.NextFactionId, new[] { FwFactions.AmarrEmpire, FwFactions.CaldariState });
        Assert.Equal(FwRules.NextDowntimeMs(rig.Now), stored.EffectiveMs);
    }

    [PgFact]
    public async Task Alliance_enlistment_is_kept_apart_from_the_corporation_enlistment()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.Service.JoinAsync(FwStore.AllianceKind, Alliance, FwFactions.CaldariState, 0, rig.Now);
        var downtime = FwRules.NextDowntimeMs(rig.Now);
        Assert.Equal(FwFactions.CaldariState, await rig.Service.FactionOfAsync(FwStore.AllianceKind, Alliance, downtime));
        Assert.Equal(0, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, downtime));
        Assert.Equal([FwFactions.CaldariState], (await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Alliance)).Select(row => row.Object));
        Assert.Empty(await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation));
    }

    [PgFact]
    public async Task A_due_corporation_enlistment_is_projected_after_a_service_restart()
    {
        await using var rig = await FwRig.CreateAsync();
        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.JoinAsync(FwStore.CorporationKind, Corporation, Amarr, 0.5, rig.Now));
        var downtime = FwRules.NextDowntimeMs(rig.Now);
        Assert.Empty(await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation));

        var scheduler = new DurableScheduler(rig.Source, () => downtime, new SchedulerOptions { Concurrency = 1 }, "sch_" + rig.Store.Schema[3..]);
        var bus = new EventBus(rig.Source, scheduler, null, "evt_" + rig.Store.Schema[3..]);
        var relations = new RelationStore(rig.Source, scheduler, bus, rig.RelationSchema);
        var restarted = new FwService(new FwStore(rig.Source, rig.Store.Schema), rig.Loyalty, relations);

        Assert.Equal(Amarr, await restarted.FactionOfAsync(FwStore.CorporationKind, Corporation, downtime));
        Assert.Equal([Amarr], (await relations.FromAsync(RelationKinds.FactionMember.Name, Corporation)).Select(row => row.Object));
    }

    [PgFact]
    public async Task Failed_relation_history_write_rolls_back_a_corporation_leave()
    {
        await using var rig = await FwRig.CreateAsync();
        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.GmEnlistAsync(FwStore.CorporationKind, Corporation, Amarr, rig.Now));
        await using (var connection = await rig.Source.OpenConnectionAsync())
            await Sql.ExecuteAsync(connection, null, $"DROP TABLE {rig.RelationSchema}.relation_history", CancellationToken.None);

        await Assert.ThrowsAsync<PostgresException>(() =>
            rig.Service.GmLeaveAsync(FwStore.CorporationKind, Corporation, rig.Now));

        Assert.Equal(Amarr, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, rig.Now));
        Assert.Equal([Amarr], (await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation)).Select(row => row.Object));
    }

    [PgFact]
    public async Task Seeding_keeps_the_stored_state_on_a_second_start()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr, pool: 100);
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, FwFactions.MinmatarRepublic, pool: 0);
        var systems = await rig.Service.OccupiedSystemsAsync();
        Assert.Equal(FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar).Count, systems.Count);
        Assert.All(systems, system => Assert.Equal(Amarr, system.OccupierId));
        Assert.Equal(100, await rig.Service.SolarSystemLpsAsync(FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0]));
    }

    [PgFact]
    public async Task Donation_debits_the_militia_lp_and_adds_what_the_hub_tax_leaves()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr);
        await rig.CreditAsync(Character, FwFactions.MilitiaCorporationOf(Amarr), 1000);
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        var taxRate = await DonationTaxOf(rig);
        var hubLp = (long)(100 - 100 * taxRate);

        var result = await rig.Service.DonateAsync(system, Character, Corporation, Amarr, 100, hubLp, rig.Now);
        Assert.Equal(new DonateResult(DonateOutcome.Accepted, hubLp, 0), result);
        Assert.Equal(900, await rig.BalanceAsync(Character, FwFactions.MilitiaCorporationOf(Amarr)));
        Assert.Equal(hubLp, await rig.Service.SolarSystemLpsAsync(system));
    }

    [PgFact]
    public async Task Donation_quotes_the_tax_of_the_faction_share_of_its_warzone()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr);
        await rig.CreditAsync(Character, FwFactions.MilitiaCorporationOf(Amarr), 1000);
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];

        // Every system of warzone 1 is Amarr: the share is 1 and the tax 0.83.
        Assert.Equal(0.83, await DonationTaxOf(rig), 10);
        var stale = await rig.Service.DonateAsync(system, Character, Corporation, Amarr, 100, 100, rig.Now);
        Assert.Equal(new DonateResult(DonateOutcome.TaxChanged, 0, 83), stale);
        Assert.Equal(1000, await rig.BalanceAsync(Character, FwFactions.MilitiaCorporationOf(Amarr)));
    }

    [PgFact]
    public async Task Donation_refusals_leave_the_pool_and_the_balance_unchanged()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr, pool: 380);
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        await rig.CreditAsync(Character, FwFactions.MilitiaCorporationOf(Amarr), 5);

        Assert.Equal(DonateOutcome.BelowMinimum, (await rig.Service.DonateAsync(system, Character, Corporation, Amarr, 9, 9, rig.Now)).Outcome);
        Assert.Equal(DonateOutcome.NotEnlisted, (await rig.Service.DonateAsync(system, Character, Corporation, 0, 10, 10, rig.Now)).Outcome);
        Assert.Equal(0, FwWarzones.WarzoneOf(JitaSystem));
        Assert.Equal(DonateOutcome.NotOccupied, (await rig.Service.DonateAsync(JitaSystem, Character, Corporation, Amarr, 10, 1, rig.Now)).Outcome);
        Assert.Equal(DonateOutcome.InsufficientLp, (await rig.Service.DonateAsync(system, Character, Corporation, Amarr, 10, 1, rig.Now)).Outcome);
        Assert.Equal(5, await rig.BalanceAsync(Character, FwFactions.MilitiaCorporationOf(Amarr)));
        Assert.Equal(380, await rig.Service.SolarSystemLpsAsync(system));
    }

    [PgFact]
    public async Task Donation_that_would_overfill_the_pool_is_refused_with_the_room_left()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr, pool: 380);
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        await rig.CreditAsync(Character, FwFactions.MilitiaCorporationOf(Amarr), 100);

        var taxRate = await DonationTaxOf(rig);
        var hubLp = (long)(100 - 100 * taxRate);
        var result = await rig.Service.DonateAsync(system, Character, Corporation, Amarr, 100, hubLp, rig.Now);
        Assert.Equal(new DonateResult(DonateOutcome.PoolOverloaded, 380, 4), result);
        Assert.Equal(100, await rig.BalanceAsync(Character, FwFactions.MilitiaCorporationOf(Amarr)));
    }

    private static async Task<double> DonationTaxOf(FwRig rig)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        return await rig.Service.DonationTaxAsync(connection, null, Amarr);
    }

    [PgFact]
    public async Task A_game_master_enlistment_is_in_the_faction_at_once_and_refuses_a_non_occupier_or_a_second_faction()
    {
        await using var rig = await FwRig.CreateAsync();

        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.GmEnlistAsync(FwStore.CorporationKind, Corporation, Amarr, rig.Now));
        Assert.Equal(Amarr, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, rig.Now));
        Assert.Equal(new long[] { Corporation }, await rig.Service.CorporationsInFactionAsync(Amarr, rig.Now));
        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.GmEnlistAsync(FwStore.CorporationKind, Corporation, Amarr, rig.Now));
        Assert.Equal(EnlistOutcome.AlreadyEnlisted, await rig.Service.GmEnlistAsync(FwStore.CorporationKind, Corporation, FwFactions.CaldariState, rig.Now));
        Assert.Equal(EnlistOutcome.NotAnOccupier, await rig.Service.GmEnlistAsync(FwStore.CorporationKind, Alliance, FwFactions.GuristasPirates, rig.Now));
        Assert.Equal(0, await rig.Service.FactionOfAsync(FwStore.AllianceKind, Alliance, rig.Now));
    }

    [PgFact]
    public async Task A_game_master_leave_takes_the_corporation_out_of_its_faction_at_once()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.Service.GmEnlistAsync(FwStore.CorporationKind, Corporation, Amarr, rig.Now);

        Assert.Equal(EnlistOutcome.Accepted, await rig.Service.GmLeaveAsync(FwStore.CorporationKind, Corporation, rig.Now));

        Assert.Equal(0, await rig.Service.FactionOfAsync(FwStore.CorporationKind, Corporation, rig.Now));
        Assert.Empty(await rig.Service.CorporationsInFactionAsync(Amarr, rig.Now));
        Assert.Equal(EnlistOutcome.NothingToWithdraw, await rig.Service.GmLeaveAsync(FwStore.CorporationKind, Corporation, rig.Now));
    }

    [PgFact]
    public async Task A_victory_point_adds_to_an_occupied_system_and_an_unoccupied_system_takes_none()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr, pool: 100);
        var occupied = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];

        Assert.Equal(25, await rig.Service.AddVictoryPointsAsync(occupied, 25, rig.Now));
        Assert.Equal(40, await rig.Service.AddVictoryPointsAsync(occupied, 15, rig.Now));
        Assert.Null(await rig.Service.AddVictoryPointsAsync(JitaSystem, 25, rig.Now));
    }
}
