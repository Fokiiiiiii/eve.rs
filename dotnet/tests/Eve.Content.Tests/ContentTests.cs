using System.Text.Json;
using Eve.Data;

namespace Eve.Content.Tests;

public class ContentTests
{
    private const long Pilot = 90000001, Other = 90000002, System = 30000142;

    private static ContentDefinition KillThenReport() => new("mission", "kill-then-report",
    [
        new ObjectiveDef("kill", "counter", new { topic = "npc.killed", where = new { typeId = new[] { 100, 101 } } }, Target: 3),
        new ObjectiveDef("report", "manual"),
    ]) { DurationMs = 60_000 };

    [PgFact]
    public async Task Events_advance_the_objectives_in_order_and_the_content_completes_and_is_announced()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(KillThenReport());
        await rig.Ready();
        var id = (await rig.Start("kill-then-report", Pilot, new { agent = 3009841 })).Id;

        // only npcs of the listed types count, only for this pilot
        await rig.Publish("npc.killed", new { characterId = Pilot, typeId = 100 });
        await rig.Publish("npc.killed", new { characterId = Pilot, typeId = 999 });
        await rig.Publish("npc.killed", new { characterId = Other, typeId = 100 });
        await rig.Publish("npc.killed", new { characterId = Pilot, typeId = 101 });
        var objectives = await rig.Content.ObjectivesOfAsync(id);
        Assert.Equal(("active", 2.0), (objectives[0].State, objectives[0].Progress));
        Assert.Equal("waiting", objectives[1].State);

        await rig.Publish("npc.killed", new { characterId = Pilot, typeId = 100 });
        objectives = await rig.Content.ObjectivesOfAsync(id);
        Assert.Equal(("done", "active"), (objectives[0].State, objectives[1].State));
        Assert.Equal("active", (await rig.Content.GetAsync(id))!.State);

        Assert.True(await rig.Commit((c, t) => rig.Content.ProgressAsync(c, t, id, "report")));
        await rig.Drain();
        var instance = (await rig.Content.GetAsync(id))!;
        Assert.Equal("completed", instance.State);
        Assert.Equal(3009841, instance.Data<JsonElement>().GetProperty("agent").GetInt64());
        Assert.Equal(["content.mission.started", "content.mission.objective_completed", "content.mission.objective_completed", "content.mission.completed"], rig.Seen);
        Assert.Empty(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));   // the deadline job went with the content
        Assert.Empty(await rig.Content.ActiveOfAsync("mission", Pilot));
    }

    [PgFact]
    public async Task Parallel_content_completes_when_every_required_objective_is_done_and_skips_the_optional_ones()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(new ContentDefinition("incursion", "site",
        [
            new ObjectiveDef("a", "counter", new { topic = "x.a", scopeField = "systemId" }),
            new ObjectiveDef("b", "counter", new { topic = "x.b", scopeField = "systemId" }),
            new ObjectiveDef("bonus", "counter", new { topic = "x.bonus", scopeField = "systemId" }, Required: false),
        ]) { Order = ObjectiveOrder.Parallel });
        await rig.Ready();
        var id = (await rig.Start("site", System)).Id;
        Assert.All(await rig.Content.ObjectivesOfAsync(id), o => Assert.Equal("active", o.State));
        await rig.Publish("x.b", new { systemId = System });
        Assert.Equal("active", (await rig.Content.GetAsync(id))!.State);
        await rig.Publish("x.a", new { systemId = System });
        Assert.Equal("completed", (await rig.Content.GetAsync(id))!.State);
        Assert.Equal(["done", "done", "skipped"], (await rig.Content.ObjectivesOfAsync(id)).Select(o => o.State));
    }

    [PgFact]
    public async Task An_objective_can_name_the_instance_so_an_anomalys_spawn_counts_only_its_own_kills()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(new ContentDefinition("anomaly", "combat-site",
            [new ObjectiveDef("clear", "counter", new { topic = "npc.killed", scopeField = "solarSystemId", where = new { site = "$instance" }, amountField = "count" }, Target: 4)]));
        await rig.Ready();
        var first = (await rig.Start("combat-site", System, uniqueKey: "belt-1")).Id;
        var second = (await rig.Start("combat-site", System, uniqueKey: "belt-2")).Id;
        await rig.Publish("npc.killed", new { solarSystemId = System, site = first, count = 3 });
        await rig.Publish("npc.killed", new { solarSystemId = System, site = second, count = 1 });
        Assert.Equal(3, (await rig.Content.ObjectivesOfAsync(first))[0].Progress);
        Assert.Equal(1, (await rig.Content.ObjectivesOfAsync(second))[0].Progress);
        await rig.Publish("npc.killed", new { solarSystemId = System, site = first, count = 9 });   // more than needed is capped at the target
        Assert.Equal("completed", (await rig.Content.GetAsync(first))!.State);
        Assert.Equal("active", (await rig.Content.GetAsync(second))!.State);
    }

    [PgFact]
    public async Task Content_expires_when_its_time_is_up_runs_its_failure_actions_and_announces_it()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(KillThenReport() with { OnFail = [new ActionRef("emit", new { topic = "mission.penalty", payload = new { characterId = Pilot, standing = -0.5 } })] });
        var penalties = new List<double>();
        await rig.Ready();
        rig.Bus.Subscribe("penalty", ["mission.penalty"], (ctx, _) => { penalties.Add(ctx.Event.Json().GetProperty("standing").GetDouble()); return Task.CompletedTask; });
        await rig.Bus.PumpAsync();
        var id = (await rig.Start("kill-then-report", Pilot)).Id;

        rig.Now += 59_000;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("active", (await rig.Content.GetAsync(id))!.State);
        rig.Now += 1_000;
        await rig.Scheduler.RunDueAsync();
        await rig.Drain();
        var instance = (await rig.Content.GetAsync(id))!;
        Assert.Equal(("expired", "time ran out"), (instance.State, instance.Result));
        Assert.Equal([-0.5], penalties);
        Assert.Contains("content.mission.expired", rig.Seen);
        Assert.All(await rig.Content.ObjectivesOfAsync(id), o => Assert.Equal("failed", o.State));
    }

    [PgFact]
    public async Task A_timer_objective_completes_by_the_scheduler_and_a_forbid_objective_fails_the_content()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(new ContentDefinition("mission", "wait", [new ObjectiveDef("wait", "timer", new { delayMs = 5000 }), new ObjectiveDef("go", "manual")]));
        rig.Content.Define(new ContentDefinition("mission", "careful",
            [new ObjectiveDef("deliver", "manual"), new ObjectiveDef("keep-ship", "forbid", new { topic = "ship.lost" }, Required: false)]) { Order = ObjectiveOrder.Parallel });
        await rig.Ready();
        var waiting = (await rig.Start("wait", Pilot)).Id;
        var careful = (await rig.Start("careful", Pilot)).Id;

        rig.Now += 5000;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(["done", "active"], (await rig.Content.ObjectivesOfAsync(waiting)).Select(o => o.State));

        await rig.Publish("ship.lost", new { characterId = Other });
        Assert.Equal("active", (await rig.Content.GetAsync(careful))!.State);
        await rig.Publish("ship.lost", new { characterId = Pilot });
        var failed = (await rig.Content.GetAsync(careful))!;
        Assert.Equal(("failed", "objective:keep-ship"), (failed.State, failed.Result));
    }

    [PgFact]
    public async Task A_unique_key_allows_one_active_instance_and_abandoning_cancels_the_deadline()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(KillThenReport());
        await rig.Ready();
        var first = await rig.Start("kill-then-report", Pilot, uniqueKey: "agent-1");
        var again = await rig.Start("kill-then-report", Pilot, uniqueKey: "agent-1");
        Assert.True(first.Created);
        Assert.Equal((first.Id, false), (again.Id, again.Created));
        Assert.Single(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));

        Assert.True(await rig.Commit((c, t) => rig.Content.AbandonAsync(c, t, first.Id, "declined")));
        Assert.False(await rig.Commit((c, t) => rig.Content.AbandonAsync(c, t, first.Id)));
        Assert.Empty(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));
        Assert.Equal("abandoned", (await rig.Content.GetAsync(first.Id))!.State);
        var next = await rig.Start("kill-then-report", Pilot, uniqueKey: "agent-1");   // free again
        Assert.True(next.Created);
    }

    [PgFact]
    public async Task A_start_in_a_transaction_that_rolls_back_leaves_nothing_and_bad_definitions_are_refused_at_attach()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(KillThenReport());
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Content.StartAsync(connection, transaction, "kill-then-report", Pilot);
            await transaction.RollbackAsync();
        }
        Assert.Empty(await rig.Content.ActiveOfAsync("mission", Pilot));
        Assert.Empty(await rig.Scheduler.ListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Start("nope", Pilot));

        rig.Content.Define(new ContentDefinition("mission", "bad-type", [new ObjectiveDef("x", "teleport")]));
        Assert.Contains("teleport", (await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Content.AttachAsync())).Message);
    }

    [PgFact]
    public async Task Running_content_survives_a_restart_because_its_state_is_in_the_database()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(KillThenReport());
        await rig.Ready();
        var id = (await rig.Start("kill-then-report", Pilot)).Id;
        await rig.Publish("npc.killed", new { characterId = Pilot, typeId = 100 });

        // a new process: same database, new runtime objects; the definition is not even registered this time, the instance carries its own copy
        var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, rig.SchedulerSchema);
        var bus = new Eve.Events.EventBus(rig.Source, scheduler, null, rig.EventsSchema);
        var content = new ContentRuntime(rig.Source, scheduler, bus, rig.ContentSchema);
        await content.AttachAsync();
        await bus.PumpAsync();
        await bus.PublishAsync("npc.killed", new { characterId = Pilot, typeId = 101 });
        await bus.PumpAsync();
        Assert.Equal(2, (await content.ObjectivesOfAsync(id))[0].Progress);
    }
}
