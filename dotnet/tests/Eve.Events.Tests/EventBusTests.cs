using Eve.Data;
using Eve.Scheduler;

namespace Eve.Events.Tests;

public class EventBusTests
{
    private sealed record Killed(long Ship, int Type);

    private static DomainEventHandler Collect(List<string> into) => (ctx, _) => { into.Add(ctx.Event.Topic + ":" + ctx.Event.Key); return Task.CompletedTask; };

    [PgFact]
    public async Task An_event_is_delivered_in_order_to_a_subscriber_with_its_payload()
    {
        await using var rig = await Rig.Create();
        var seen = new List<Killed>();
        rig.Bus.Subscribe("kills", ["npc.killed"], (ctx, _) => { seen.Add(ctx.Event.Payload<Killed>()!); return Task.CompletedTask; });
        await rig.Bus.PumpAsync();   // creates the subscriber at "now"
        await rig.Publish("npc.killed", new Killed(1, 10));
        await rig.Publish("npc.killed", new Killed(2, 20));
        await rig.Publish("npc.killed", new Killed(3, 30));
        var result = await rig.Bus.PumpAsync();
        Assert.Equal(3, result.Delivered);
        Assert.Equal([new Killed(1, 10), new Killed(2, 20), new Killed(3, 30)], seen);
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
    }

    [PgFact]
    public async Task An_event_published_in_a_transaction_that_rolls_back_never_exists_and_neither_does_its_pump_job()
    {
        await using var rig = await Rig.Create();
        var seen = new List<string>();
        rig.Bus.Subscribe("s", ["*"], Collect(seen));
        await rig.Bus.PumpAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Bus.PublishAsync(connection, transaction, "a.b", null, "k");
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await rig.JobCount());
        Assert.Equal(0, await rig.Bus.CountAsync());
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);

        await rig.Publish("a.b", null, "k");
        Assert.Equal(1, await rig.JobCount());   // the pump job came with the event
        await rig.Scheduler.RunDueAsync();       // and the scheduler runs it
        Assert.Equal(["a.b:k"], seen);
    }

    [PgFact]
    public async Task Topics_are_exact_names_prefixes_or_everything()
    {
        await using var rig = await Rig.Create();
        var exact = new List<string>();
        var prefix = new List<string>();
        var all = new List<string>();
        rig.Bus.Subscribe("exact", ["npc.killed"], Collect(exact));
        rig.Bus.Subscribe("prefix", ["npc.*", "mission.completed"], Collect(prefix));
        rig.Bus.Subscribe("all", ["*"], Collect(all));
        await rig.Bus.PumpAsync();
        foreach (var topic in new[] { "npc.killed", "npc.spawned", "mission.completed", "mission.started", "npcx" })
        {
            await rig.Publish(topic, null, "k");
        }
        await rig.Bus.PumpAsync();
        Assert.Equal(["npc.killed:k"], exact);
        Assert.Equal(["npc.killed:k", "npc.spawned:k", "mission.completed:k"], prefix);
        Assert.Equal(5, all.Count);
    }

    [PgFact]
    public async Task A_new_subscriber_starts_now_or_at_the_beginning_as_asked()
    {
        await using var rig = await Rig.Create();
        await rig.Publish("old", null, "1");
        await rig.Publish("old", null, "2");
        var now = new List<string>();
        var beginning = new List<string>();
        rig.Bus.Subscribe("now", ["*"], Collect(now));
        rig.Bus.Subscribe("beginning", ["*"], Collect(beginning), StartAt.Beginning);
        await rig.Bus.PumpAsync();
        await rig.Publish("new", null, "3");
        await rig.Bus.PumpAsync();
        Assert.Equal(["new:3"], now);
        Assert.Equal(["old:1", "old:2", "new:3"], beginning);
    }

    [PgFact]
    public async Task A_handlers_writes_are_committed_with_its_cursor_so_a_failure_leaves_neither_and_the_retry_runs_once()
    {
        await using var rig = await Rig.Create();
        var fail = true;
        rig.Bus.Subscribe("w", ["x"], async (ctx, token) =>
        {
            await Sql.ExecuteAsync(ctx.Connection, ctx.Transaction, $"INSERT INTO {rig.EventsSchema}.effects VALUES ('wrote')", token);
            if (fail)
            {
                throw new InvalidOperationException("after the write");
            }
        });
        await rig.Bus.PumpAsync();
        await rig.Publish("x");
        var first = await rig.Bus.PumpAsync();
        Assert.Equal(0, first.Delivered);
        Assert.Equal(rig.Now + 1000, first.RetryAtMs);          // back-off
        Assert.Empty(await rig.Effects());                      // the write was rolled back with the failed try
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered); // still waiting out the back-off

        fail = false;
        rig.Now += 1000;
        Assert.Equal(1, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(["wrote"], await rig.Effects());           // once
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(0, (await rig.Bus.StatusAsync()).Single().Failures);
    }

    [PgFact]
    public async Task An_event_that_keeps_failing_becomes_a_dead_letter_and_the_subscriber_goes_on()
    {
        await using var rig = await Rig.Create();   // MaxAttempts = 3
        var seen = new List<string>();
        rig.Bus.Subscribe("p", ["*"], (ctx, _) =>
        {
            if (ctx.Event.Key == "poison")
            {
                throw new InvalidOperationException("cannot");
            }
            seen.Add(ctx.Event.Key!);
            return Task.CompletedTask;
        });
        await rig.Bus.PumpAsync();
        await rig.Publish("t", null, "a");
        await rig.Publish("t", null, "poison");
        await rig.Publish("t", null, "b");
        for (var i = 0; i < 4; i++)
        {
            await rig.Bus.PumpAsync();
            rig.Now += 10_000;
        }
        Assert.Equal(["a", "b"], seen);
        var dead = Assert.Single(await rig.Bus.DeadLettersAsync("p"));
        Assert.Equal(("t", "poison", "cannot"), (dead.Topic, dead.Key, dead.Error));
        Assert.Equal(0, (await rig.Bus.StatusAsync()).Single().Failures);
    }

    [PgFact]
    public async Task A_failing_subscriber_does_not_hold_the_others_back()
    {
        await using var rig = await Rig.Create();
        var good = new List<string>();
        rig.Bus.Subscribe("bad", ["*"], (_, _) => throw new InvalidOperationException("always"));
        rig.Bus.Subscribe("good", ["*"], Collect(good));
        await rig.Bus.PumpAsync();
        await rig.Publish("t", null, "1");
        await rig.Publish("t", null, "2");
        var result = await rig.Bus.PumpAsync();
        Assert.Equal(["t:1", "t:2"], good);
        Assert.Equal(2, result.Delivered);
        Assert.NotNull(result.RetryAtMs);
    }

    [PgFact]
    public async Task A_transaction_that_commits_late_is_waited_for_and_never_skipped()
    {
        await using var rig = await Rig.Create();
        var seen = new List<string>();
        rig.Bus.Subscribe("s", ["*"], Collect(seen));
        await rig.Bus.PumpAsync();

        // slow takes its transaction id first but publishes (and commits) last; fast publishes and commits in between
        await using var slowConnection = await rig.Source.OpenConnectionAsync();
        await using var slow = await slowConnection.BeginTransactionAsync();
        await Sql.ScalarAsync(slowConnection, slow, "SELECT pg_current_xact_id()", CancellationToken.None);
        await rig.Publish("t", null, "fast");
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);   // not yet: an older transaction is still running
        await rig.Bus.PublishAsync(slowConnection, slow, "t", null, "slow");
        await slow.CommitAsync();
        Assert.Equal(2, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(["t:slow", "t:fast"], seen.OrderBy(x => x).Reverse().ToList());   // both arrived; nothing was skipped
        Assert.Equal(2, seen.Count);
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
    }

    [PgFact]
    public async Task A_handler_can_publish_and_the_new_event_reaches_another_subscriber()
    {
        await using var rig = await Rig.Create();
        var done = new List<string>();
        rig.Bus.Subscribe("first", ["a"], (ctx, _) => ctx.PublishAsync("b", null, ctx.Event.Key));
        rig.Bus.Subscribe("second", ["b"], Collect(done));
        await rig.Bus.PumpAsync();
        await rig.Publish("a", null, "k");
        await rig.Scheduler.RunDueAsync();   // the pump job delivers a, publishing b and its own pump job, which runs next
        Assert.Equal(["b:k"], done);
    }

    [PgFact]
    public async Task Events_every_subscriber_has_read_are_purged_after_the_retention_and_unread_ones_are_kept()
    {
        await using var rig = await Rig.Create(new EventBusOptions { RetentionMs = 1000, HardRetentionMs = 100_000 });
        rig.Bus.Subscribe("s", ["read"], Collect([]));
        await rig.Bus.PumpAsync();
        await rig.Publish("read", null, "1");
        await rig.Bus.PumpAsync();
        await rig.Publish("read", null, "2");   // published but not yet pumped
        Assert.Equal(0, await rig.Bus.PurgeAsync());   // too young
        rig.Now += 2000;
        Assert.Equal(1, await rig.Bus.PurgeAsync());   // only the one that was read
        Assert.Equal(1, await rig.Bus.CountAsync());
        rig.Now += 200_000;
        Assert.Equal(1, await rig.Bus.PurgeAsync());   // the hard limit takes the rest
    }

    [PgFact]
    public async Task Two_pumps_on_the_same_log_deliver_each_event_once_per_subscriber()
    {
        await using var rig = await Rig.Create();
        var (_, other) = rig.Make();
        var count = 0;
        DomainEventHandler handler = async (ctx, token) =>
        {
            await Sql.ExecuteAsync(ctx.Connection, ctx.Transaction, $"INSERT INTO {rig.EventsSchema}.effects VALUES ('e')", token);
            Interlocked.Increment(ref count);
        };
        rig.Bus.Subscribe("one", ["*"], handler);
        other.Subscribe("one", ["*"], handler);
        await rig.Bus.PumpAsync();
        for (var i = 0; i < 40; i++)
        {
            await rig.Publish("t", null, i.ToString());
        }
        await Task.WhenAll(rig.Bus.PumpAsync(), other.PumpAsync(), rig.Bus.PumpAsync());
        await rig.Bus.PumpAsync();
        Assert.Equal(40, count);
        Assert.Equal(40, (await rig.Effects()).Count);
    }
}
