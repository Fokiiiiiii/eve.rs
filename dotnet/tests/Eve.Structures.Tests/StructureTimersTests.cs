using System.Text.Json.Nodes;
using static Eve.Structures.StructureTimes;

namespace Eve.Structures.Tests;

/// <summary>The timeline on PostgreSQL: a change, its deadline job and its announcement commit together, a restart fires a deadline once, and the import copies the game store's rows once.</summary>
public class StructureTimersTests
{
    [PgFact]
    public async Task Anchoring_writes_the_row_its_deadline_job_and_its_announcement_together()
    {
        await using var rig = await Rig.Create();
        await rig.Anchor(1001);
        await rig.Tick();

        var s = await rig.Snapshot(1001);
        Assert.Equal(StructureState.AnchorVulnerable, s.State);
        Assert.Equal(rig.Now + RepairMs, s.StateEndsMs);
        var job = await rig.Job(1001);
        Assert.NotNull(job);
        Assert.Equal(StructureTimers.JobKind, job.Kind);
        Assert.Equal(rig.Now + RepairMs, job.RunAtMs);
        var change = Assert.Single(rig.Changes);
        Assert.Equal(1001, change.StructureId);
        Assert.Equal(StructureState.AnchorVulnerable, change.State);
        Assert.Equal(rig.Now + RepairMs, change.Timer.EndsMs);
    }

    [PgFact]
    public async Task The_deadline_job_advances_the_timeline_and_schedules_the_next_deadline()
    {
        await using var rig = await Rig.Create();
        await rig.Anchor(1001);
        await rig.Tick();
        rig.Now += RepairMs;
        await rig.Tick();

        Assert.Equal(StructureState.Anchoring, (await rig.Snapshot(1001)).State);
        var job = await rig.Job(1001);
        Assert.NotNull(job);
        Assert.Equal(rig.Now + AnchoringMs, job.RunAtMs);
        Assert.Equal(2, rig.Changes.Count);
    }

    [PgFact]
    public async Task A_restart_fires_the_deadline_once_and_a_replayed_handler_changes_nothing()
    {
        await using var rig = await Rig.Create();
        await rig.Anchor(1001);
        await rig.Tick();
        await rig.Restart();

        rig.Now += RepairMs;
        await rig.Tick();
        await rig.Tick();
        await rig.Apply(1001, new TimerDue());
        await rig.Tick();

        var s = await rig.Snapshot(1001);
        Assert.Equal(StructureState.Anchoring, s.State);
        Assert.Equal(rig.Now + AnchoringMs, s.StateEndsMs);
        Assert.Equal(2, rig.Changes.Count);
    }

    [PgFact]
    public async Task A_refused_event_changes_nothing_and_announces_nothing()
    {
        await using var rig = await Rig.Create();
        await rig.Anchor(1001);
        await rig.Tick();
        var before = await rig.Snapshot(1001);

        var t = await rig.Apply(1001, new UnanchorCancelled());
        await rig.Tick();

        Assert.Equal("NOT_UNANCHORING", t!.Refusal);
        Assert.Equal(before, await rig.Snapshot(1001));
        Assert.Single(rig.Changes);
    }

    [PgFact]
    public async Task Unanchoring_takes_seven_days_then_the_structure_leaves_the_system()
    {
        await using var rig = await Rig.Create();
        await rig.Working(1001);
        await rig.Apply(1001, new UnanchorStarted());
        Assert.Equal(rig.Now + UnanchoringMs, (await rig.Snapshot(1001)).UnanchorEndsMs);

        rig.Now += UnanchoringMs - 1;
        await rig.Tick();
        Assert.NotEqual(StructureState.Unanchored, (await rig.Snapshot(1001)).State);

        rig.Now += 1;
        await rig.Tick();
        var gone = await rig.Store.GetAsync(1001);
        Assert.Equal(StructureState.Unanchored, gone!.Snapshot.State);
        Assert.True(gone.Snapshot.DestroyedMs > 0);
        Assert.Equal("unanchored", rig.Changes[^1].Removal);
        var live = await rig.Commit((c, t) => rig.Store.ListAsync(c, t, false));
        Assert.Empty(live);
    }

    [PgFact]
    public async Task A_fob_armor_timer_uses_the_24_hour_base_and_normal_exit_schedule()
    {
        const long first = 1101, second = 1102;
        const int fobType = 90_001;
        await using var rig = await Rig.Create();
        rig.Rules.ByType[fobType] = StructureRules.Of(StructureSize.Fob, SecurityBand.LowSec, canBeAbandoned: false);
        rig.Now = rig.Now / Day * Day;

        foreach (var id in new[] { first, second })
        {
            await rig.Commit((connection, transaction) => rig.Timers.DeployFobAsync(connection, transaction,
                new NewStructure(id, fobType, 30_000_142, 0, 0, 0, 0, 98_000_001, 0, "Test FOB", "",
                    ReinforceWeekday: NoReinforceWeekday, ReinforceHour: 3)));
            await rig.Apply(id, new FobExposed());
        }

        var start = rig.Now;
        rig.Roll = 0;
        await rig.Apply(first, new Damaged(StructureLayer.Shield, Depleted: true, PausesRepair: false));
        await rig.Tick();
        var earliest = (await rig.Snapshot(first)).StateEndsMs;
        Assert.Equal(start + Day, earliest);
        Assert.Equal(earliest, (await rig.Job(first))!.RunAtMs);
        Assert.Equal(earliest, rig.Changes[^1].Timer.EndsMs);

        rig.Roll = 0.999999;
        await rig.Apply(second, new Damaged(StructureLayer.Shield, Depleted: true, PausesRepair: false));
        await rig.Tick();
        var latest = (await rig.Snapshot(second)).StateEndsMs;
        Assert.InRange(latest, start + Day, start + Day + 6 * Hour);
        Assert.Equal(latest, (await rig.Job(second))!.RunAtMs);
        Assert.Equal(latest, rig.Changes[^1].Timer.EndsMs);
    }

    [PgFact]
    public async Task A_flex_hull_timer_uses_the_one_hour_base_and_persists_its_exit_deadline()
    {
        const long structureId = 1103;
        const int flexType = 35_841;
        await using var rig = await Rig.Create();
        rig.Rules.ByType[flexType] = StructureRules.Of(StructureSize.Flex, SecurityBand.NullSec);
        await rig.Working(structureId, flexType);
        rig.Now = rig.Now / Day * Day;
        rig.Roll = 0;

        var snapshot = await rig.Snapshot(structureId);
        await rig.Commit(async (connection, transaction) =>
        {
            await rig.Store.SaveAsync(connection, transaction, snapshot with { ReinforceWeekday = NoReinforceWeekday, ReinforceHour = 1 });
            return true;
        });
        await rig.Apply(structureId, new Damaged(StructureLayer.Shield, Depleted: true, PausesRepair: false));
        rig.Now += 1_000;
        var hullDamageAt = rig.Now;
        await rig.Apply(structureId, new Damaged(StructureLayer.Armor, Depleted: true, PausesRepair: false));
        await rig.Tick();

        var reinforced = await rig.Snapshot(structureId);
        Assert.Equal(StructureState.HullReinforce, reinforced.State);
        Assert.Equal(hullDamageAt + Hour, reinforced.StateEndsMs);
        Assert.Equal(reinforced.StateEndsMs, (await rig.Job(structureId))!.RunAtMs);
        Assert.Equal(reinforced.StateEndsMs, rig.Changes[^1].Timer.EndsMs);
    }

    [PgFact]
    public async Task Low_power_abandonment_uses_a_persisted_random_deadline_across_restart()
    {
        const long structureId = 1104;
        await using var rig = await Rig.Create();
        await rig.Working(structureId);
        var lowPowerAt = rig.Now;
        rig.Roll = 1d - 1d / (Day + 1);
        await rig.Commit((connection, transaction) => rig.Timers.FuelOutAsync(connection, transaction, structureId));
        await rig.Tick();

        var deadline = lowPowerAt + 8 * Day;
        Assert.Equal(UpkeepState.LowPower, (await rig.Snapshot(structureId)).Upkeep);
        Assert.Equal(deadline, (await rig.Job(structureId))!.RunAtMs);
        var legacy = (await rig.Store.ReadLegacyTableAsync())[structureId.ToString()];
        Assert.Equal(deadline, legacy!["abandonAt"]!.GetValue<long>());

        await rig.Restart();
        Assert.Equal(deadline, (await rig.Job(structureId))!.RunAtMs);
        Assert.Equal(deadline, (await rig.Store.ReadLegacyTableAsync())[structureId.ToString()]!["abandonAt"]!.GetValue<long>());
        rig.Now = deadline - 1;
        await rig.Tick();
        Assert.Equal(UpkeepState.LowPower, (await rig.Snapshot(structureId)).Upkeep);
        rig.Now++;
        await rig.Tick();
        Assert.Equal(UpkeepState.Abandoned, (await rig.Snapshot(structureId)).Upkeep);
        Assert.Null(await rig.Job(structureId));
    }

    [PgFact]
    public async Task A_queued_reinforce_time_waits_until_shield_vulnerability_is_fully_repaired()
    {
        const long structureId = 1105;
        await using var rig = await Rig.Create();
        await rig.Working(structureId);
        await rig.Apply(structureId, new ReinforceTimeRequested(2, 10));
        var pendingAt = (await rig.Snapshot(structureId)).PendingEffectiveMs;

        rig.Now = pendingAt - RepairMs + 1;
        await rig.Apply(structureId, new Damaged(StructureLayer.Shield, Depleted: false, PausesRepair: false));
        var damaged = await rig.Snapshot(structureId);
        Assert.Equal(pendingAt + 1, damaged.StateEndsMs);
        Assert.Equal(damaged.StateEndsMs, (await rig.Job(structureId))!.RunAtMs);

        rig.Now = pendingAt;
        await rig.Tick();
        var stillPending = await rig.Snapshot(structureId);
        Assert.Equal(StructureTimes.DefaultReinforceWeekday, stillPending.ReinforceWeekday);
        Assert.Equal(pendingAt, stillPending.PendingEffectiveMs);
        Assert.Equal(damaged.StateEndsMs, (await rig.Job(structureId))!.RunAtMs);

        rig.Now = damaged.StateEndsMs;
        await rig.Tick();
        var applied = await rig.Snapshot(structureId);
        Assert.Equal((2, 10), (applied.ReinforceWeekday, applied.ReinforceHour));
        Assert.Equal(0, applied.PendingEffectiveMs);
        Assert.Equal(StructureState.ShieldVulnerable, applied.State);
        Assert.True(rig.Changes[^1].Repaired);
    }

    [PgFact]
    public async Task The_import_copies_the_legacy_rows_once_and_reads_back_in_the_legacy_shape()
    {
        await using var rig = await Rig.Create();
        var table = new JsonObject
        {
            ["1001"] = new JsonObject
            {
                ["structureID"] = 1001, ["typeID"] = 35832, ["solarSystemID"] = 30000142, ["ownerCorpID"] = 98000001,
                ["itemName"] = "Imported Astrahus", ["position"] = new JsonObject { ["x"] = 1.5, ["y"] = -2.0, ["z"] = 3.0 },
                ["state"] = StructureState.ShieldVulnerable, ["upkeepState"] = UpkeepState.FullPower, ["hasQuantumCore"] = true,
                ["reinforceWeekday"] = 2, ["reinforceHour"] = 4, ["services"] = new JsonObject { ["1"] = ServiceState.Online },
            },
            ["1002"] = new JsonObject
            {
                ["structureID"] = 1002, ["typeID"] = 35833, ["solarSystemID"] = 30000142, ["ownerCorpID"] = 98000001,
                ["itemName"] = "Second", ["state"] = StructureState.ShieldVulnerable, ["upkeepState"] = UpkeepState.FullPower,
            },
        };

        var first = await rig.Commit((c, t) => rig.Store.ImportAsync(c, t, table, rig.Now));
        var second = await rig.Commit((c, t) => rig.Store.ImportAsync(c, t, table, rig.Now));

        Assert.Equal(new long[] { 1001, 1002 }, first);
        Assert.Empty(second);
        var s = await rig.Snapshot(1001);
        Assert.Equal(StructureState.ShieldVulnerable, s.State);
        Assert.True(s.CoreInstalled);
        Assert.Equal(1, s.OnlineServices);
        var legacy = (await rig.Store.ReadLegacyTableAsync())["1001"]!.AsObject();
        Assert.Equal("Imported Astrahus", legacy["itemName"]!.GetValue<string>());
        Assert.Equal(StructureState.ShieldVulnerable, legacy["state"]!.GetValue<int>());
        Assert.Equal(1.5, legacy["position"]!["x"]!.GetValue<double>());
        Assert.Equal(ServiceState.Online, legacy["serviceStates"]!["1"]!.GetValue<int>());
    }

    [PgFact]
    public async Task A_system_query_returns_only_its_live_structures_in_id_order()
    {
        await using var rig = await Rig.Create();
        var table = new JsonObject();
        foreach (var (id, system) in new[] { (1004, 30000142), (1003, 30000142), (1002, 30000143), (1001, 30000142) })
            table[id.ToString()] = new JsonObject
            {
                ["structureID"] = id, ["typeID"] = 35832, ["solarSystemID"] = system,
                ["ownerCorpID"] = 98000001, ["state"] = StructureState.ShieldVulnerable,
                ["upkeepState"] = UpkeepState.FullPower,
            };
        await rig.Commit((c, t) => rig.Store.ImportAsync(c, t, table, rig.Now));
        var gone = await rig.Snapshot(1003);
        await rig.Commit(async (c, t) =>
        {
            await rig.Store.SaveAsync(c, t, gone with { DestroyedMs = rig.Now });
            return true;
        });

        var live = await rig.Commit((c, t) => rig.Store.ListInSystemAsync(c, t, 30000142));
        Assert.Equal(new long[] { 1001, 1004 }, live.Select(r => r.Snapshot.StructureId));
        var empty = await rig.Commit((c, t) => rig.Store.ListInSystemAsync(c, t, 30000144));
        Assert.Empty(empty);
    }

    [PgFact]
    public async Task A_description_and_a_profile_are_written_to_the_structure_and_read_back_in_the_legacy_shape()
    {
        await using var rig = await Rig.Create();
        await rig.Anchor(1001);
        await rig.Tick();
        await rig.Commit(async (c, t) => { await rig.Store.SetDescriptionAsync(c, t, 1001, "a new description"); return 0; });
        await rig.Commit(async (c, t) => { await rig.Store.SetProfileAsync(c, t, new long[] { 1001 }, 77); return 0; });

        var row = (await rig.Store.ReadLegacyTableAsync())["1001"]!.AsObject();
        Assert.Equal("a new description", row["description"]!.GetValue<string>());
        Assert.Equal(77, row["profileID"]!.GetValue<long>());
    }
}
