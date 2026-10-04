namespace Eve.Scheduler.Tests;

public class LocalTimersTests
{
    /// <summary>A delay the test finishes by hand.</summary>
    private sealed class Waits
    {
        public List<(TimeSpan After, TaskCompletionSource Done, CancellationToken Token)> Made { get; } = [];

        public Task Delay(TimeSpan after, CancellationToken token)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => done.TrySetCanceled(token));
            Made.Add((after, done, token));
            return done.Task;
        }
    }

    private static async Task Settle(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_timer_runs_its_action_once_when_its_wait_is_over()
    {
        var waits = new Waits();
        using var timers = new LocalTimers(delay: waits.Delay);
        var ran = 0;
        timers.Set("a", TimeSpan.FromSeconds(5), () => ran++);
        Assert.Equal(TimeSpan.FromSeconds(5), waits.Made[0].After);   // the wait started in Set, not when a thread was free
        Assert.True(timers.IsSet("a"));
        Assert.Equal(0, ran);
        waits.Made[0].Done.SetResult();
        await Settle(() => ran == 1);
        Assert.Equal(1, ran);
        Assert.False(timers.IsSet("a"));
        Assert.Equal(0, timers.Count);
    }

    [Fact]
    public async Task Setting_a_key_again_replaces_the_timer_and_cancelling_stops_it()
    {
        var waits = new Waits();
        using var timers = new LocalTimers(delay: waits.Delay);
        var ran = new List<string>();
        timers.Set("k", TimeSpan.FromSeconds(1), () => ran.Add("first"));
        timers.Set("k", TimeSpan.FromSeconds(2), () => ran.Add("second"));
        Assert.True(waits.Made[0].Token.IsCancellationRequested);   // the replaced timer's wait is cancelled
        Assert.Equal(1, timers.Count);
        waits.Made[1].Done.SetResult();
        await Settle(() => ran.Count == 1);
        Assert.Equal(["second"], ran);

        timers.Set("c", TimeSpan.FromSeconds(1), () => ran.Add("cancelled"));
        Assert.True(timers.Cancel("c"));
        Assert.False(timers.Cancel("c"));
        waits.Made[2].Done.TrySetResult();
        await Task.Delay(50);
        Assert.Equal(["second"], ran);
    }

    [Fact]
    public async Task An_action_that_throws_is_logged_and_does_not_stop_the_others()
    {
        var waits = new Waits();
        var log = new List<string>();
        using var timers = new LocalTimers(log.Add, waits.Delay);
        var ran = false;
        timers.Set("bad", TimeSpan.Zero, () => throw new InvalidOperationException("boom"));
        timers.Set("good", TimeSpan.Zero, () => ran = true);
        waits.Made[0].Done.SetResult();
        waits.Made[1].Done.SetResult();
        await Settle(() => ran && log.Count == 1);
        Assert.True(ran);
        Assert.Equal(["the timer bad failed: boom"], log);
    }

    [Fact]
    public async Task Disposing_stops_every_timer_and_the_real_delay_works()
    {
        var ran = 0;
        using (var timers = new LocalTimers())
        {
            timers.Set("later", TimeSpan.FromMinutes(5), () => ran++);
            timers.Set("soon", TimeSpan.FromMilliseconds(20), () => ran += 10);
            await Settle(() => ran == 10);
            Assert.Equal(1, timers.Count);
        }
        await Task.Delay(30);
        Assert.Equal(10, ran);   // the long one never ran
    }

    [Fact]
    public async Task An_infinite_wait_never_runs_and_a_negative_one_runs_at_once()
    {
        var ran = new List<string>();
        using var timers = new LocalTimers();
        timers.Set("never", Timeout.InfiniteTimeSpan, () => ran.Add("never"));
        timers.Set("now", TimeSpan.FromSeconds(-5), () => ran.Add("now"));
        await Settle(() => ran.Contains("now"));
        await Task.Delay(50);
        Assert.Equal(["now"], ran);
        Assert.True(timers.IsSet("never"));
        Assert.True(timers.Cancel("never"));
    }
}
