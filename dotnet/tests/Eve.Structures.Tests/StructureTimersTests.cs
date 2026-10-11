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
}
