using System.Collections.Concurrent;

namespace Eve.Scheduler;

/// <summary>
/// The server's one in-memory timer service, for waits that belong to something that lives in this process only: a pilot's undock protection, a module's reload, the end of a
/// skill queue for a character who is logged in. A timer has a key; setting it again replaces it, cancelling it stops it, and a timer whose time has come runs its action once (an
/// action that throws is logged and does not stop anything else). Nothing is written anywhere: when the process stops, the timers are gone, which is right for these waits (the state they
/// belong to is gone too, or is worked out again at login). A wait that must survive a restart, or be seen by another process, is a job of the <see cref="DurableScheduler"/>
/// instead. Features do not make <c>System.Threading.Timer</c>s or <c>Task.Delay</c> loops of their own; tests give the delay a fake.
/// </summary>
public sealed class LocalTimers : IDisposable
{
    private sealed class Entry(CancellationTokenSource source)
    {
        public CancellationTokenSource Source { get; } = source;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;

    /// <param name="log">Told when an action throws.</param>
    /// <param name="delay">How a wait is made (<see cref="Task.Delay(TimeSpan, CancellationToken)"/> by default); a test passes one it controls.</param>
    public LocalTimers(Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _log = log;
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
    }

    /// <summary>How many timers are waiting.</summary>
    public int Count => _entries.Count;

    public bool IsSet(string key) => _entries.ContainsKey(key);

    /// <summary>Runs <paramref name="action"/> after <paramref name="after"/>, replacing the timer of the same key. The wait starts now, not when a thread is free.</summary>
    public void Set(string key, TimeSpan after, Func<Task> action)
    {
        var entry = new Entry(new CancellationTokenSource());
        // Timeout.InfiniteTimeSpan is a timer that never runs (until it is replaced or cancelled); any other negative wait is no wait
        var wait = _delay(after == Timeout.InfiniteTimeSpan || after >= TimeSpan.Zero ? after : TimeSpan.Zero, entry.Source.Token);
        if (_entries.TryGetValue(key, out var old))
        {
            old.Source.Cancel();
        }
        _entries[key] = entry;
        _ = Task.Run(async () =>
        {
            try
            {
                await wait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // only the timer that is still the one of its key runs (a replaced or cancelled one has been taken out)
            if (!_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
            {
                return;
            }
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _log?.Invoke($"the timer {key} failed: {error.Message}");
            }
            finally
            {
                entry.Source.Dispose();
            }
        });
    }

    /// <summary><see cref="Set(string, TimeSpan, Func{Task})"/> for an action that is not asynchronous.</summary>
    public void Set(string key, TimeSpan after, Action action) => Set(key, after, () =>
    {
        action();
        return Task.CompletedTask;
    });

    /// <summary>Stops the timer of a key. Returns whether there was one.</summary>
    public bool Cancel(string key)
    {
        if (!_entries.TryRemove(key, out var entry))
        {
            return false;
        }
        entry.Source.Cancel();
        return true;
    }

    /// <summary>Stops every timer.</summary>
    public void Dispose()
    {
        foreach (var key in _entries.Keys.ToArray())
        {
            Cancel(key);
        }
    }
}
