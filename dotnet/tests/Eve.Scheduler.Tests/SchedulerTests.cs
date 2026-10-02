using Eve.Data;

namespace Eve.Scheduler.Tests;

public class SchedulerTests
{
    private sealed record Ping(int N, string Who);

    [PgFact]
    public async Task A_job_runs_once_when_it_is_due_with_its_payload_and_is_then_gone()
    {
        await using var rig = await Rig.Create();
        var seen = new List<Ping>();
        rig.Scheduler.Register("t.ping", (ctx, _) => { seen.Add(ctx.Payload<Ping>()!); return Task.CompletedTask; });
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.ping", rig.Now + 500) { Payload = new Ping(7, "x") });

        Assert.Equal(0, await rig.Scheduler.RunDueAsync());
        Assert.Empty(seen);
        rig.Now += 500;
        Assert.Equal(1, await rig.Scheduler.RunDueAsync());
        Assert.Equal([new Ping(7, "x")], seen);
        Assert.Equal(0, await rig.Scheduler.RunDueAsync());
        Assert.Equal(0, await rig.Count());
    }

    [PgFact]
    public async Task A_kind_without_a_handler_in_this_process_is_left_alone()
    {
        await using var rig = await Rig.Create();
        rig.Scheduler.Register("t.known", (_, _) => Task.CompletedTask);
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.other", rig.Now));
        Assert.Equal(0, await rig.Scheduler.RunDueAsync());
        var job = Assert.Single(await rig.Scheduler.ListAsync("t.other"));
        Assert.Equal("pending", job.State);
        Assert.Equal(0, job.Attempts);
    }

    [PgFact]
    public async Task One_waiting_job_per_key_and_the_policy_says_which_time_wins()
    {
        await using var rig = await Rig.Create();
        var t = rig.Now;
        var first = await rig.Scheduler.ScheduleAsync(new JobSpec("t.k", t + 1000) { Key = "a", Payload = new Ping(1, "first") });
        var second = await rig.Scheduler.ScheduleAsync(new JobSpec("t.k", t + 5000) { Key = "a", Payload = new Ping(2, "second") });
        Assert.Equal(first, second);
        var job = (await rig.Scheduler.FindAsync("t.k", "a"))!;
        Assert.Equal(t + 5000, job.RunAtMs);
        Assert.Contains("second", job.PayloadJson);
        Assert.Equal(1, await rig.Count());

        await rig.Scheduler.ScheduleAsync(new JobSpec("t.k", t + 100) { Key = "a", OnDuplicate = OnDuplicate.Keep });
        Assert.Equal(t + 5000, (await rig.Scheduler.FindAsync("t.k", "a"))!.RunAtMs);

        await rig.Scheduler.ScheduleAsync(new JobSpec("t.k", t + 200) { Key = "a", Payload = new Ping(3, "earlier"), OnDuplicate = OnDuplicate.Earlier });
        job = (await rig.Scheduler.FindAsync("t.k", "a"))!;
        Assert.Equal(t + 200, job.RunAtMs);
        Assert.Contains("earlier", job.PayloadJson);

        await rig.Scheduler.ScheduleAsync(new JobSpec("t.k", t + 50) { Key = "a", Payload = new Ping(4, "ignored"), OnDuplicate = OnDuplicate.Later });
        job = (await rig.Scheduler.FindAsync("t.k", "a"))!;
        Assert.Equal(t + 200, job.RunAtMs);
        Assert.Contains("earlier", job.PayloadJson);

        // different kinds and different keys do not collide
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.k", t) { Key = "b" });
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.other", t) { Key = "a" });
        Assert.Equal(3, await rig.Count());
    }

    [PgFact]
    public async Task A_waiting_job_can_be_cancelled_by_its_key()
    {
        await using var rig = await Rig.Create();
        var ran = 0;
        rig.Scheduler.Register("t.c", (_, _) => { ran++; return Task.CompletedTask; });
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.c", rig.Now) { Key = "x" });
        Assert.True(await rig.Scheduler.CancelAsync("t.c", "x"));
        Assert.False(await rig.Scheduler.CancelAsync("t.c", "x"));
        Assert.Equal(0, await rig.Scheduler.RunDueAsync());
        Assert.Equal(0, ran);
    }

    [PgFact]
    public async Task A_failing_job_is_retried_with_a_back_off_then_parked_and_can_be_revived()
    {
        await using var rig = await Rig.Create();
        var tries = 0;
        var succeed = false;
        rig.Scheduler.Register("t.f", (_, _) => { tries++; return succeed ? Task.CompletedTask : throw new InvalidOperationException("nope"); });
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.f", rig.Now) { MaxAttempts = 3 });

        Assert.Equal(1, await rig.Scheduler.RunDueAsync());
        var job = Assert.Single(await rig.Scheduler.ListAsync("t.f"));
        Assert.Equal(("pending", 1, "nope"), (job.State, job.Attempts, job.LastError));
        Assert.Equal(rig.Now + 1000, job.RunAtMs);   // the first back-off
        Assert.Equal(0, await rig.Scheduler.RunDueAsync());

        rig.Now += 1000;
        Assert.Equal(1, await rig.Scheduler.RunDueAsync());
        job = Assert.Single(await rig.Scheduler.ListAsync("t.f"));
        Assert.Equal(rig.Now + 2000, job.RunAtMs);   // doubled

        rig.Now += 2000;
        Assert.Equal(1, await rig.Scheduler.RunDueAsync());
        job = Assert.Single(await rig.Scheduler.ListAsync("t.f"));
        Assert.Equal(("failed", 3), (job.State, job.Attempts));
        rig.Now += 1_000_000;
        Assert.Equal(0, await rig.Scheduler.RunDueAsync());
        Assert.Equal(3, tries);

        succeed = true;
        Assert.True(await rig.Scheduler.RetryAsync(job.Id));
        Assert.Equal(1, await rig.Scheduler.RunDueAsync());
        Assert.Equal(0, await rig.Count());
    }

    [PgFact]
    public async Task A_recurring_job_runs_every_interval_and_skips_the_runs_it_missed()
    {
        await using var rig = await Rig.Create();
        var times = new List<long>();
        rig.Scheduler.Register("t.r", (ctx, _) => { times.Add(ctx.ScheduledForMs); return Task.CompletedTask; });
        var t = rig.Now;
        await rig.Scheduler.EnsureRecurringAsync("t.r", "tick", TimeSpan.FromMilliseconds(1000));
        // registering it again changes nothing
        await rig.Scheduler.EnsureRecurringAsync("t.r", "tick", TimeSpan.FromMilliseconds(1000));
        Assert.Equal(1, await rig.Count());

        rig.Now = t + 1000;
        await rig.Scheduler.RunDueAsync();
        rig.Now = t + 2000;
        await rig.Scheduler.RunDueAsync();
        // the server was away for 10 seconds: one run, not ten
        rig.Now = t + 12_500;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal([t + 1000, t + 2000, t + 3000], times);
        Assert.Equal(t + 13_000, (await rig.Scheduler.FindAsync("t.r", "tick"))!.RunAtMs);
    }

    [PgFact]
    public async Task A_job_can_ask_to_run_again_with_a_new_payload_and_a_job_can_schedule_a_job()
    {
        await using var rig = await Rig.Create();
        var steps = new List<string>();
        rig.Scheduler.Register("t.sm", async (ctx, _) =>
        {
            var p = ctx.Payload<Ping>()!;
            steps.Add($"sm{p.N}");
            if (p.N < 3)
            {
                ctx.RunAgainAt(ctx.NowMs, new Ping(p.N + 1, p.Who));
            }
            else
            {
                await ctx.ScheduleAsync(new JobSpec("t.after", ctx.NowMs));
            }
        });
        rig.Scheduler.Register("t.after", (_, _) => { steps.Add("after"); return Task.CompletedTask; });
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.sm", rig.Now) { Key = "s", Payload = new Ping(1, "w") });
        Assert.Equal(4, await rig.Scheduler.RunDueAsync());
        Assert.Equal(["sm1", "sm2", "sm3", "after"], steps);
        Assert.Equal(0, await rig.Count());
    }

    [PgFact]
    public async Task A_job_scheduled_in_a_transaction_exists_exactly_when_the_transaction_commits()
    {
        await using var rig = await Rig.Create();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Scheduler.ScheduleAsync(connection, transaction, new JobSpec("t.tx", rig.Now));
            await rig.Scheduler.CancelAsync(connection, transaction, "t.tx", "nothing");
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await rig.Count());
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Scheduler.ScheduleAsync(connection, transaction, new JobSpec("t.tx", rig.Now));
            await transaction.CommitAsync();
        }
        Assert.Equal(1, await rig.Count());
    }

    [PgFact]
    public async Task What_a_handler_writes_is_committed_with_the_job_and_rolled_back_when_it_fails()
    {
        await using var rig = await Rig.Create();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await Sql.ExecuteAsync(connection, null, $"CREATE TABLE {rig.Schema}.effects (note text)", CancellationToken.None);
        }
        var fail = true;
        rig.Scheduler.Register("t.w", async (ctx, token) =>
        {
            await Sql.ExecuteAsync(ctx.Connection, ctx.Transaction, $"INSERT INTO {rig.Schema}.effects VALUES ('done')", token);
            if (fail)
            {
                throw new InvalidOperationException("after the write");
            }
        });
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.w", rig.Now));
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(0L, await Effects(rig));
        fail = false;
        rig.Now += 5000;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(1L, await Effects(rig));
        Assert.Equal(0, await rig.Count());

        static async Task<long> Effects(Rig rig)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            return (long)(await Sql.ScalarAsync(connection, null, $"SELECT count(*) FROM {rig.Schema}.effects", CancellationToken.None))!;
        }
    }

    [PgFact]
    public async Task A_claim_that_lapsed_is_taken_over_and_one_that_lapsed_on_every_attempt_is_parked()
    {
        await using var rig = await Rig.Create();
        var ran = 0;
        rig.Scheduler.Register("t.l", (_, _) => { ran++; return Task.CompletedTask; });
        var id = await rig.Scheduler.ScheduleAsync(new JobSpec("t.l", rig.Now) { MaxAttempts = 2 });
        // a runner claimed it and died
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await Sql.ExecuteAsync(connection, null,
                $"UPDATE {rig.Schema}.jobs SET state='running', attempts=1, lease_owner='dead', lease_until_ms=$1 WHERE id=$2", CancellationToken.None, rig.Now + 10_000, id);
        }
        Assert.Equal(0, await rig.Scheduler.RunDueAsync());   // the lease still holds
        rig.Now += 10_001;
        Assert.Equal(1, await rig.Scheduler.RunDueAsync());   // taken over, second and last try
        Assert.Equal(1, ran);

        var doomed = await rig.Scheduler.ScheduleAsync(new JobSpec("t.l", rig.Now) { MaxAttempts = 1 });
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await Sql.ExecuteAsync(connection, null,
                $"UPDATE {rig.Schema}.jobs SET state='running', attempts=1, lease_owner='dead', lease_until_ms=$1 WHERE id=$2", CancellationToken.None, rig.Now - 1, doomed);
        }
        Assert.Equal(0, await rig.Scheduler.RunDueAsync());
        Assert.Equal(1, ran);
        Assert.Equal("failed", Assert.Single(await rig.Scheduler.ListAsync("t.l")).State);
    }

    [PgFact]
    public async Task Two_runners_on_the_same_jobs_run_each_job_once()
    {
        await using var rig = await Rig.Create(new SchedulerOptions { Concurrency = 4, BatchSize = 5 });
        var other = rig.Make();
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        JobHandler handler = async (ctx, _) =>
        {
            await Task.Delay(2);
            counts.AddOrUpdate(ctx.Payload<Ping>()!.N, 1, (_, n) => n + 1);
        };
        rig.Scheduler.Register("t.m", handler);
        other.Register("t.m", handler);
        for (var i = 0; i < 60; i++)
        {
            await rig.Scheduler.ScheduleAsync(new JobSpec("t.m", rig.Now) { Payload = new Ping(i, "") });
        }
        await Task.WhenAll(rig.Scheduler.RunDueAsync(), other.RunDueAsync(), rig.Scheduler.RunDueAsync());
        Assert.Equal(60, counts.Count);
        Assert.All(counts.Values, n => Assert.Equal(1, n));
        Assert.Equal(0, await rig.Count());
    }

    [PgFact]
    public async Task A_job_that_is_replaced_while_it_runs_leaves_the_replacement_waiting()
    {
        await using var rig = await Rig.Create();
        var runs = 0;
        rig.Scheduler.Register("t.s", async (ctx, _) =>
        {
            runs++;
            if (runs == 1)
            {
                // someone reschedules the same thing while this run is in progress
                await rig.Make().ScheduleAsync(new JobSpec("t.s", ctx.NowMs + 9000) { Key = "k" });
            }
            ctx.RunAgainAt(ctx.NowMs + 100);
        });
        await rig.Scheduler.ScheduleAsync(new JobSpec("t.s", rig.Now) { Key = "k" });
        Assert.Equal(1, await rig.Scheduler.RunDueAsync());
        var job = Assert.Single(await rig.Scheduler.ListAsync("t.s"));
        Assert.Equal(rig.Now + 9000, job.RunAtMs);
    }

    [PgFact]
    public async Task The_background_runner_picks_up_work_scheduled_in_this_process_and_stops()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var rig = await Rig.Create(new SchedulerOptions { Concurrency = 2, MaxPollMs = 50 });
        var done = new TaskCompletionSource();
        var real = new DurableScheduler(rig.Source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new SchedulerOptions { MaxPollMs = 50 }, rig.Schema);
        real.Register("t.bg", (_, _) => { done.TrySetResult(); return Task.CompletedTask; });
        real.Start();
        await real.ScheduleAsync(new JobSpec("t.bg", now + 150));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await real.StopAsync();
        Assert.Equal(0, await rig.Count());
    }
}
