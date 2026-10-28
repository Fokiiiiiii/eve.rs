using System.Collections.Concurrent;

namespace Eve.Services.Core;

public sealed record ActivityMgrState(IReadOnlySet<long> SeenActivityIds, int PopState)
{
    public static ActivityMgrState Default => new(new HashSet<long>(), 1);
}

public interface IActivityMgrStateStore
{
    ValueTask<ActivityMgrState?> ReadAsync(long characterId, CancellationToken cancellationToken);
    ValueTask<ActivityMgrState?> UpdateAsync(
        long characterId,
        Func<ActivityMgrState, ActivityMgrState> update,
        CancellationToken cancellationToken);
}

public sealed class InMemoryActivityMgrStateStore : IActivityMgrStateStore
{
    private readonly ConcurrentDictionary<long, StoredState> _states = new();

    public ValueTask<ActivityMgrState?> ReadAsync(long characterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _states.GetOrAdd(characterId, static _ => new StoredState());
        lock (state.Gate)
            return ValueTask.FromResult<ActivityMgrState?>(Copy(state.Value));
    }

    public ValueTask<ActivityMgrState?> UpdateAsync(
        long characterId,
        Func<ActivityMgrState, ActivityMgrState> update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _states.GetOrAdd(characterId, static _ => new StoredState());
        lock (state.Gate)
        {
            state.Value = Copy(update(Copy(state.Value)));
            return ValueTask.FromResult<ActivityMgrState?>(Copy(state.Value));
        }
    }

    private static ActivityMgrState Copy(ActivityMgrState state) =>
        new(new HashSet<long>(state.SeenActivityIds), state.PopState);

    private sealed class StoredState
    {
        public object Gate { get; } = new();
        public ActivityMgrState Value { get; set; } = ActivityMgrState.Default;
    }
}
