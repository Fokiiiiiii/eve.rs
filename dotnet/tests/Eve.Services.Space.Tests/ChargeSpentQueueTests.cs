namespace Eve.Services.Space.Tests;

/// <summary>The rounds a gun in space has spent are written in the order they were told, for each session on a lane of its own, and a flush waits for what came before it.</summary>
public class ChargeSpentQueueTests
{
    private sealed class Store
    {
        public List<(long Character, long Module, long Rounds)> Written { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Gate { get; set; }

        public int FailuresLeft { get; set; }

        public async Task WriteAsync(long character, long module, long rounds)
        {
            Started.TrySetResult();
            if (Gate is { } gate)
            {
                await gate.Task;
            }
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new InvalidOperationException("the database is away");
            }
            lock (Written)
            {
                Written.Add((character, module, rounds));
            }
        }
    }

    [Fact]

    public async Task What_is_told_is_written_in_the_order_it_was_told_and_a_flush_waits_for_it()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Gate = gate };
        var queue = new ChargeSpentQueue(store.WriteAsync, null);

        queue.Enqueue(1, 10, 400, 90);
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Hold the lane on the first write so both reports for each gun are in the same pending batch.
        for (var round = 79L; round >= 78; round--)
        {
            queue.Enqueue(1, 10, 100, round);
            queue.Enqueue(1, 10, 200, round);
            queue.Enqueue(1, 10, 300, round);
        }
        var flushed = queue.FlushAsync(1);
        await Task.Delay(50);
        Assert.False(flushed.IsCompleted);

        gate.SetResult();
        await flushed;

        Assert.Equal(
            [(10L, 400L, 90L), (10L, 100L, 78L), (10L, 200L, 78L), (10L, 300L, 78L)],
            store.Written.ToArray());
    }

    [Fact]
    public async Task A_flush_is_not_answered_before_the_database_has_what_came_before_it()
    {
        var store = new Store { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var queue = new ChargeSpentQueue(store.WriteAsync, null);
        queue.Enqueue(1, 10, 100, 5);

        var flushed = queue.FlushAsync(1);
        await Task.Delay(200);
        Assert.False(flushed.IsCompleted);
        Assert.Empty(store.Written);

        store.Gate.SetResult();
        await flushed;
        Assert.Equal([(10L, 100L, 5L)], store.Written.ToArray());
    }

    [Fact]
    public async Task Enqueueing_never_waits_for_the_database_and_each_session_has_a_lane_of_its_own()
    {
        var store = new Store { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var queue = new ChargeSpentQueue(store.WriteAsync, null);
        var told = Task.Run(() =>
        {
            for (var round = 100L; round > 0; round--)
            {
                queue.Enqueue(1, 10, 100, round);
                queue.Enqueue(2, 20, 200, round);
            }
        });
        await told.WaitAsync(TimeSpan.FromSeconds(5)); // a count that waited on the database would time out here

        store.Gate.SetResult();
        await Task.WhenAll(queue.FlushAsync(1), queue.FlushAsync(2));
        // the two sessions each end at their own last count; neither's flush waited for the other's order
        Assert.Equal(1L, store.Written.Last(w => w.Module == 100).Rounds);
        Assert.Equal(1L, store.Written.Last(w => w.Module == 200).Rounds);
        Assert.True(store.Written.Where(w => w.Module == 100).Select(w => w.Rounds).SequenceEqual(store.Written.Where(w => w.Module == 100).Select(w => w.Rounds).OrderByDescending(r => r)));
    }

    [Fact]
    public async Task A_write_that_fails_is_tried_again_and_a_closed_lane_flushes_and_lets_go()
    {
        var store = new Store { FailuresLeft = 2 };
        var queue = new ChargeSpentQueue(store.WriteAsync, null);
        queue.Enqueue(1, 10, 100, 3);

        await queue.CloseAsync(1);

        Assert.Equal([(10L, 100L, 3L)], store.Written.ToArray());
        // the session comes back to space: a new lane takes the counts
        queue.Enqueue(1, 10, 100, 2);
        await queue.FlushAsync(1);
        Assert.Equal(2L, store.Written.Last().Rounds);
        // a flush or a close of a session that has no lane is nothing
        await queue.FlushAsync(99);
        await queue.CloseAsync(99);
    }

    [Fact]
    public async Task Stopping_the_host_writes_what_every_session_has_spent()
    {
        var store = new Store();
        var queue = new ChargeSpentQueue(store.WriteAsync, null);
        queue.Enqueue(1, 10, 100, 4);
        queue.Enqueue(2, 20, 200, 6);

        await queue.CloseAllAsync();

        Assert.Equal([100L, 200L], store.Written.Select(w => w.Module).Order().ToArray());
    }
}
