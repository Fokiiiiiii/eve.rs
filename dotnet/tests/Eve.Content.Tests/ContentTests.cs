using System.Text.Json;
using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

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

    [PgFact]
    public async Task Restart_refuses_active_content_when_its_saved_objective_type_is_not_registered()
    {
        await using var rig = await Rig.Create();
        rig.Content.RegisterObjectiveType(new TopicObjective("legacy-objective"));
        rig.Content.Define(new ContentDefinition("mission", "legacy-objective-run",
            [new ObjectiveDef("legacy-step", "legacy-objective", new { topic = "legacy.progress" })]));
        await rig.Ready();
        var id = (await rig.Start("legacy-objective-run", Pilot)).Id;

        var (scheduler, bus, restored) = Restart(rig);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => restored.AttachAsync());

        Assert.Contains(id.ToString(), error.Message);
        Assert.Contains("legacy-objective", error.Message);
        Assert.Equal("active", (await restored.GetAsync(id))!.State);
        _ = scheduler;
        _ = bus;
    }

    [PgFact]
    public async Task Restart_refuses_active_content_when_its_saved_action_is_not_registered()
    {
        await using var rig = await Rig.Create();
        rig.Content.RegisterAction("legacy-action", (_, _, _) => Task.CompletedTask);
        rig.Content.Define(new ContentDefinition("mission", "legacy-action-run", [new ObjectiveDef("wait", "manual")])
        {
            OnFail = [new ActionRef("legacy-action")],
        });
        await rig.Ready();
        var id = (await rig.Start("legacy-action-run", Pilot)).Id;

        var (scheduler, bus, restored) = Restart(rig);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => restored.AttachAsync());

        Assert.Contains(id.ToString(), error.Message);
        Assert.Contains("legacy-action", error.Message);
        Assert.Equal("active", (await restored.GetAsync(id))!.State);
        _ = scheduler;
        _ = bus;
    }

    [PgFact]
    public async Task Definitions_reject_objective_targets_that_are_not_finite_and_positive()
    {
        await using var rig = await Rig.Create();

        foreach (var (index, target) in new[]
        {
            (0, double.NaN),
            (1, double.PositiveInfinity),
            (2, double.NegativeInfinity),
            (3, 0d),
            (4, -1d),
        })
        {
            var definition = new ContentDefinition("mission", $"invalid-target-{index}",
                [new ObjectiveDef("progress", "manual", Target: target)]);
            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Content.Define(definition));
        }
    }

    [PgFact]
    public async Task Progress_rejects_invalid_deltas_without_mutating_the_objective()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(new ContentDefinition("mission", "finite-progress", [new ObjectiveDef("progress", "manual", Target: 3)]));
        await rig.Ready();
        var id = (await rig.Start("finite-progress", Pilot)).Id;

        foreach (var delta in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                rig.Commit((connection, transaction) => rig.Content.ProgressAsync(connection, transaction, id, "progress", delta)));
        }

        var objective = Assert.Single(await rig.Content.ObjectivesOfAsync(id));
        Assert.Equal((0d, "active"), (objective.Progress, objective.State));
        Assert.True(await rig.Commit((connection, transaction) => rig.Content.ProgressAsync(connection, transaction, id, "progress", 1.5)));
        Assert.Equal(1.5, (await rig.Content.ObjectivesOfAsync(id))[0].Progress);
    }

    [PgFact]
    public async Task Objective_progress_constraints_reject_nonfinite_values_in_postgres()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(new ContentDefinition("mission", "database-finite-progress", [new ObjectiveDef("progress", "manual")]));
        await rig.Ready();
        var id = (await rig.Start("database-finite-progress", Pilot)).Id;
        await using var connection = await rig.Source.OpenConnectionAsync();

        await using var target = new NpgsqlCommand($"UPDATE {rig.ContentSchema}.objectives SET target = @value WHERE instance_id = @id", connection);
        target.Parameters.AddWithValue("value", double.PositiveInfinity);
        target.Parameters.AddWithValue("id", id);
        var targetError = await Assert.ThrowsAsync<PostgresException>(() => target.ExecuteNonQueryAsync());
        Assert.Equal("objectives_target_finite_positive", targetError.ConstraintName);

        await using var progress = new NpgsqlCommand($"UPDATE {rig.ContentSchema}.objectives SET progress = @value WHERE instance_id = @id", connection);
        progress.Parameters.AddWithValue("value", double.NaN);
        progress.Parameters.AddWithValue("id", id);
        var progressError = await Assert.ThrowsAsync<PostgresException>(() => progress.ExecuteNonQueryAsync());
        Assert.Equal("objectives_progress_finite_nonnegative", progressError.ConstraintName);
    }

    [PgFact]
    public async Task Objective_event_effects_cannot_complete_content_with_infinite_progress()
    {
        await using var rig = await Rig.Create();
        rig.Content.RegisterObjectiveType(new TopicObjective("infinite-objective", double.PositiveInfinity));
        rig.Content.Define(new ContentDefinition("mission", "infinite-objective-run",
            [new ObjectiveDef("infinite-step", "infinite-objective", new { topic = "infinite.progress" })]));
        await rig.Ready();
        var id = (await rig.Start("infinite-objective-run", Pilot)).Id;

        await rig.Publish("infinite.progress", new { characterId = Pilot });

        Assert.Equal("active", (await rig.Content.GetAsync(id))!.State);
        Assert.Equal((0d, "active"), ((await rig.Content.ObjectivesOfAsync(id))[0].Progress, (await rig.Content.ObjectivesOfAsync(id))[0].State));
        Assert.DoesNotContain("content.mission.completed", rig.Seen);
    }

    [PgFact]
    public async Task Parallel_content_with_only_optional_objectives_completes_at_start()
    {
        await using var rig = await Rig.Create();
        var completedActions = new List<string>();
        rig.Content.RegisterAction("record-completion", (_, _, _) =>
        {
            completedActions.Add("ran");
            return Task.CompletedTask;
        });
        rig.Content.Define(new ContentDefinition("incursion", "optional-only",
            [new ObjectiveDef("bonus", "manual", Required: false)])
        {
            Order = ObjectiveOrder.Parallel,
            DurationMs = 5_000,
            OnComplete = [new ActionRef("record-completion")],
        });
        await rig.Ready();

        var id = (await rig.Start("optional-only", System)).Id;
        await rig.Drain();

        Assert.Equal("completed", (await rig.Content.GetAsync(id))!.State);
        Assert.Equal("skipped", Assert.Single(await rig.Content.ObjectivesOfAsync(id)).State);
        Assert.Empty(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));
        Assert.Equal(["content.incursion.started", "content.incursion.completed"], rig.Seen);
        Assert.Equal(["ran"], completedActions);
    }

    [PgFact]
    public async Task Restart_completes_saved_parallel_content_with_no_required_objectives()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(new ContentDefinition("incursion", "legacy-optional-only",
            [new ObjectiveDef("bonus", "manual", Required: false)]) { Order = ObjectiveOrder.Parallel });
        await rig.Ready();
        var id = (await rig.Start("legacy-optional-only", System)).Id;
        await rig.Commit(async (connection, transaction) =>
        {
            await Sql.ExecuteAsync(connection, transaction,
                $"UPDATE {rig.ContentSchema}.instances SET state = 'active', finished_ms = NULL, result = NULL WHERE id = $1",
                CancellationToken.None, id);
            await Sql.ExecuteAsync(connection, transaction,
                $"UPDATE {rig.ContentSchema}.objectives SET state = 'active', done_ms = NULL WHERE instance_id = $1",
                CancellationToken.None, id);
            return true;
        });

        var (scheduler, bus, restored) = Restart(rig);
        await restored.AttachAsync();
        var completions = new List<string>();
        bus.Subscribe("restored-content-completions", ["content.incursion.completed"], (context, _) =>
        {
            completions.Add(context.Event.Topic);
            return Task.CompletedTask;
        });
        await bus.PumpAsync();
        await scheduler.RunDueAsync();
        await bus.PumpAsync();

        Assert.Equal("completed", (await restored.GetAsync(id))!.State);
        Assert.Equal("skipped", Assert.Single(await restored.ObjectivesOfAsync(id)).State);
        Assert.Equal(["content.incursion.completed"], completions);
    }

    [PgFact]
    public async Task Active_content_can_be_read_by_kind_across_all_owners()
    {
        await using var rig = await Rig.Create();
        rig.Content.Define(KillThenReport());
        await rig.Ready();
        var first = await rig.Start("kill-then-report", Pilot);
        var second = await rig.Start("kill-then-report", Other);

        var active = await rig.Content.ActiveByKindAsync("mission");

        Assert.Equal(new[] { first.Id, second.Id }.Order(), active.Select(instance => instance.Id).Order());
        Assert.Equal(new[] { Pilot, Other }.Order(), active.Select(instance => instance.Owner).Order());
        Assert.Empty(await rig.Content.ActiveByKindAsync("missing-kind"));
    }

    private static (DurableScheduler Scheduler, EventBus Bus, ContentRuntime Content) Restart(Rig rig)
    {
        var scheduler = new DurableScheduler(rig.Source, () => rig.Now, new SchedulerOptions { Concurrency = 1 }, rig.SchedulerSchema);
        var bus = new EventBus(rig.Source, scheduler, null, rig.EventsSchema);
        return (scheduler, bus, new ContentRuntime(rig.Source, scheduler, bus, rig.ContentSchema));
    }

    private sealed class TopicObjective(string name, double delta = 1) : ObjectiveType
    {
        public override string Name => name;

        public override string? TopicOf(JsonElement parameters) => parameters.GetProperty("topic").GetString();

        public override ObjectiveEffect Evaluate(ObjectiveState objective, DomainEvent @event) => ObjectiveEffect.Advance(delta);
    }
}
