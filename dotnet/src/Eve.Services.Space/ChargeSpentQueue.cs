using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Eve.Services.Space;

/// <summary>
/// Keeps the rounds a turret in space has spent. The simulation tells the host after every shot how many rounds a gun has left; this writes them to the stack of the
/// charge, for each session on a lane of its own and in the order they were told, so a later count is never overwritten by an earlier one and a message never waits on
/// the database. What is told in a burst is written once (the last count of each module). A lane is flushed (every count told so far is written) before something reads
/// or moves what the guns hold: the ship docks, dies or leaves, a gun is started, and the host stops.
/// </summary>
internal sealed class ChargeSpentQueue(Func<long, long, long, Task> persist, Action<string>? log)
{
    /// <summary>How long a flush waits for the database before it gives up and says so (the counts are not lost: the lane goes on).</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(15);

    private const int Attempts = 4;

    private readonly record struct Entry(long CharacterId, long ModuleId, long RoundsLeft, TaskCompletionSource? Flushed);

    private sealed class Lane
    {
        public Channel<Entry> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true });

        public Task Worker { get; set; } = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<ulong, Lane> _lanes = new();

    /// <summary>Queues the count of one module of a session. Never waits.</summary>
    public void Enqueue(ulong session, long characterId, long moduleId, long roundsLeft)
    {
        var entry = new Entry(characterId, moduleId, roundsLeft, null);
        // a lane that was closed between the lookup and the write is already out of the table (CloseAsync takes it out first), so the next look makes a new one
        var spin = new SpinWait();
        while (!LaneOf(session).Channel.Writer.TryWrite(entry))
        {
            spin.SpinOnce();
        }
    }

    private Lane LaneOf(ulong session) => _lanes.GetOrAdd(session, _ =>
    {
        var lane = new Lane();
        lane.Worker = Task.Run(() => RunAsync(lane));
        return lane;
    });

    /// <summary>Completes when every count queued for the session before the call is written (at once when there are none).</summary>
    public async Task FlushAsync(ulong session)
    {
        if (!_lanes.TryGetValue(session, out var lane))
        {
            return;
        }
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!lane.Channel.Writer.TryWrite(new Entry(0, 0, 0, flushed)))
        {
            await lane.Worker.WaitAsync(FlushTimeout).ConfigureAwait(false); // closed: what it held is written when its worker ends
            return;
        }
        await WaitAsync(flushed.Task, session).ConfigureAwait(false);
    }

    /// <summary>Flushes the session and lets its lane go (a session that leaves space for good): the next count it queues makes a new lane.</summary>
    public async Task CloseAsync(ulong session)
    {
        if (!_lanes.TryRemove(session, out var lane))
        {
            return;
        }
        lane.Channel.Writer.TryComplete();
        await WaitAsync(lane.Worker, session).ConfigureAwait(false);
    }

    /// <summary>Flushes and closes every lane: the host stops.</summary>
    public async Task CloseAllAsync()
    {
        foreach (var session in _lanes.Keys.ToArray())
        {
            await CloseAsync(session).ConfigureAwait(false);
        }
    }

    private async Task WaitAsync(Task task, ulong session)
    {
        try
        {
            await task.WaitAsync(FlushTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log?.Invoke($"the rounds spent by session {session} were not written within {FlushTimeout.TotalSeconds:0} seconds; they are written when the database answers");
        }
    }

    private async Task RunAsync(Lane lane)
    {
        var reader = lane.Channel.Reader;
        var batch = new List<Entry>();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            // everything told so far, up to a flush: the flush is answered when what comes before it is written
            while (reader.TryRead(out var entry))
            {
                batch.Add(entry);
                if (entry.Flushed is not null)
                {
                    break;
                }
            }
            // the last count of each module in the order the modules were first told
            var last = new Dictionary<(long, long), long>();
            var order = new List<(long Character, long Module)>();
            foreach (var entry in batch.Where(e => e.Flushed is null))
            {
                var key = (entry.CharacterId, entry.ModuleId);
                if (!last.ContainsKey(key))
                {
                    order.Add(key);
                }
                last[key] = entry.RoundsLeft;
            }
            foreach (var key in order)
            {
                await WriteAsync(key.Character, key.Module, last[key]).ConfigureAwait(false);
            }
            foreach (var entry in batch)
            {
                entry.Flushed?.TrySetResult();
            }
        }
    }

    private async Task WriteAsync(long characterId, long moduleId, long roundsLeft)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await persist(characterId, moduleId, roundsLeft).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (attempt < Attempts)
            {
                log?.Invoke($"the rounds of module {moduleId} could not be written (try {attempt}): {error.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                log?.Invoke($"the rounds of module {moduleId} could not be written: {error}");
                return;
            }
        }
    }
}
