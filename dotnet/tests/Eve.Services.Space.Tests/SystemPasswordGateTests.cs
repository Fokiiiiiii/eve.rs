namespace Eve.Services.Space.Tests;

public sealed class SystemPasswordGateTests
{
    private const int SystemId = 30_000_142;

    [Fact]
    public async Task Saved_password_changes_in_one_system_run_one_at_a_time_in_arrival_order_and_other_systems_are_not_held()
    {
        var sim = new FakeSim();
        using var space = new SpaceSessions(sim, null!, null!, null!, null!, () => 0);
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = space.RunInSystemPasswordGateAsync(SystemId, async () =>
        {
            order.Enqueue("first start");
            firstEntered.SetResult();
            await releaseFirst.Task;
            order.Enqueue("first end");
            return 1;
        }, CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = space.RunInSystemPasswordGateAsync(SystemId, () =>
        {
            order.Enqueue("second start");
            return Task.FromResult(2);
        }, CancellationToken.None);

        // another system is not held by this system's change
        Assert.Equal(7, await space.RunInSystemPasswordGateAsync(SystemId + 1, () => Task.FromResult(7), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        // the second change waits while the first holds the system's gate
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        Assert.Equal(new[] { "first start" }, order.ToArray());

        releaseFirst.SetResult();
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "first start", "first end", "second start" }, order.ToArray());
    }
}
