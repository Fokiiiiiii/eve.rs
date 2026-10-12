using Eve.Data;
using Eve.Scheduler;

namespace Eve.Events.Tests;

public class EventBusTests
{
    private sealed record Killed(long Ship, int Type);

    private static DomainEventHandler Collect(List<string> into) => (ctx, _) => { into.Add(ctx.Event.Topic + ":" + ctx.Event.Key); return Task.CompletedTask; };

    [Fact]
    public void Transaction_completion_registration_is_available_to_moon_subscribers()
    {
        Assert.NotNull(typeof(Eve.Events.EventContext).GetMethod("AfterCommit"));
        Assert.NotNull(typeof(Eve.Events.EventContext).GetMethod("AfterCompletion"));
    }

    [PgFact]
    public async Task Commit_callbacks_see_committed_rows_and_cleanup_runs_after_commit_or_rollback()
    {
        await using var rig = await Rig.Create();
        var fail = true;
        var published = 0;
        var released = 0;
        rig.Bus.Subscribe("completion", ["moon.test"], async (ctx, token) =>
        {
            ctx.AfterCompletion(() => released++);
            ctx.AfterCommit(async _ =>
            {
                Assert.Equal(["saved"], await rig.Effects());
                published++;
            });
            await Sql.ExecuteAsync(ctx.Connection, ctx.Transaction, $"INSERT INTO {rig.EventsSchema}.effects VALUES ('saved')", token);
            if (fail) throw new InvalidOperationException("before commit");
        });
        await rig.Bus.PumpAsync();
        await rig.Publish("moon.test");
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Empty(await rig.Effects());
        Assert.Equal((0, 1), (published, released));
        fail = false;
        rig.Now += 1000;
        Assert.Equal(1, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal((1, 2), (published, released));
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal((1, 2), (published, released));
    }

    [PgFact]
    public async Task A_failed_live_callback_preserves_committed_state_cursor_and_cleanup()
    {
        await using var rig = await Rig.Create();
        var completed = 0;
        rig.Bus.Subscribe("live", ["moon.test"], async (ctx, token) =>
        {
            ctx.AfterCommit(_ => throw new IOException("client disconnected"));
            ctx.AfterCompletion(() => completed++);
            await Sql.ExecuteAsync(ctx.Connection, ctx.Transaction, $"INSERT INTO {rig.EventsSchema}.effects VALUES ('saved')", token);
        });
        await rig.Bus.PumpAsync();
        await rig.Publish("moon.test");
        Assert.Equal(1, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(["saved"], await rig.Effects());
        Assert.Equal(1, completed);
        Assert.Contains(rig.Log, message => message.Contains("client disconnected", StringComparison.Ordinal));
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(1, completed);
    }

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
    public async Task An_event_published_in_a_transaction_that_rolls_back_never_exists()
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
        Assert.Equal(0, await rig.Bus.CountAsync());
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);

        await rig.Publish("a.b", null, "k");
        Assert.Equal(1, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(["a.b:k"], seen);
    }

    [PgFact]
    public async Task The_transactional_publish_helper_commits_with_the_change_and_rolls_back_with_it()
    {
        await using var rig = await Rig.Create();
        var seen = new List<string>();
        rig.Bus.Subscribe("s", ["*"], Collect(seen));
        await rig.Bus.PumpAsync();

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Bus.PublishInTransactionAsync(connection, transaction, "mining.ore_mined", null, "rolled-back");
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Empty(seen);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Bus.PublishInTransactionAsync(connection, transaction, "mining.ore_mined", null, "committed");
            // not delivered while the publishing transaction is open
            Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
            await transaction.CommitAsync();
        }
        rig.Bus.Wake();
        Assert.Equal(1, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(["mining.ore_mined:committed"], seen);
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
    public async Task A_publisher_that_is_still_open_holds_delivery_back_and_a_late_commit_is_never_skipped()
    {
        await using var rig = await Rig.Create();
        var seen = new List<string>();
        rig.Bus.Subscribe("s", ["*"], Collect(seen));
        await rig.Bus.PumpAsync();

        // slow publishes first (so it has the lower id) but commits last; fast publishes and commits in between
        await using var slowConnection = await rig.Source.OpenConnectionAsync();
        await using var slow = await slowConnection.BeginTransactionAsync();
        await rig.Bus.PublishAsync(slowConnection, slow, "t", null, "slow");
        await rig.Publish("t", null, "fast");
        var held = await rig.Bus.PumpAsync();
        Assert.Equal(0, held.Delivered);            // a publisher is still open: nothing is delivered past it
        Assert.Equal(rig.Now + 50, held.RetryAtMs); // and the pump says when to look again
        Assert.Empty(seen);

        await slow.CommitAsync();
        Assert.Equal(2, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(["t:slow", "t:fast"], seen);   // in the order they were published; the late one was not skipped
        Assert.Equal(0, (await rig.Bus.PumpAsync()).Delivered);
    }

    [PgFact]
    public async Task A_long_transaction_elsewhere_in_the_database_does_not_delay_delivery()
    {
        await using var rig = await Rig.Create();
        var seen = new List<string>();
        rig.Bus.Subscribe("s", ["*"], Collect(seen));
        await rig.Bus.PumpAsync();

        // some other work holds a transaction open (with a transaction id, which is what holds back a database-wide horizon)
        await using var other = await rig.Source.OpenConnectionAsync();
        await using var otherTransaction = await other.BeginTransactionAsync();
        await Sql.ScalarAsync(other, otherTransaction, "SELECT pg_current_xact_id()", CancellationToken.None);

        await rig.Publish("t", null, "1");
        Assert.Equal(1, (await rig.Bus.PumpAsync()).Delivered);
        Assert.Equal(["t:1"], seen);
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
        Assert.Equal(1, (await rig.Bus.PumpAsync()).Delivered);   // a is delivered; its handler publishes b
        Assert.Equal(1, (await rig.Bus.PumpAsync()).Delivered);   // and the next pass delivers b
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

    [PgFact]
    public async Task The_pump_loop_delivers_without_being_called_and_stops()
    {
        await using var rig = await Rig.Create(new EventBusOptions { PollMs = 20 });
        var got = new TaskCompletionSource<string>();
        rig.Bus.Subscribe("s", ["x"], (ctx, _) => { got.TrySetResult(ctx.Event.Key!); return Task.CompletedTask; });
        await rig.Bus.PumpAsync();   // the subscriber starts here
        await rig.Bus.StartAsync();
        await rig.Bus.PublishAsync("x", null, "k");
        Assert.Equal("k", await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await rig.Bus.StopAsync();
        Assert.Empty(rig.Log);
    }
}
