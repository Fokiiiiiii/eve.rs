using System.Collections.Concurrent;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

[Service("activityMgr")]
public sealed partial class ActivityMgrService
{
    private const int DefaultPopState = 1;
    private readonly ConcurrentDictionary<long, CharacterState> _states = new();

    [Method("GetActivityBadgeStatuses")]
    public ValueTask<PyValue> GetActivityBadgeStatuses(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var state = State(call.Session);
        PyValue result;
        lock (state.Gate)
            result = new PyList([.. state.SeenActivityIds.Order().Select(WireId)]);
        call.Session.SendNotification("OnSystemActivityUpdated", "None");
        return ValueTask.FromResult(result);
    }

    [Method("ActivitySeen")]
    public ValueTask<PyValue> ActivitySeen(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var activityId = call.Args.Count > 0 ? Integer(call.Args[0]) : 0;
        if (activityId <= 0)
            return ValueTask.FromResult<PyValue>(PyBool.False);
        var state = State(call.Session);
        lock (state.Gate)
            state.SeenActivityIds.Add(activityId);
        return ValueTask.FromResult<PyValue>(PyBool.True);
    }

    [Method("GetActivitiesPopState")]
    public ValueTask<PyValue> GetActivitiesPopState(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var state = State(call.Session);
        lock (state.Gate)
            return ValueTask.FromResult<PyValue>(new PyInt(state.PopState));
    }

    [Method("SetActivitiesPopState")]
    public ValueTask<PyValue> SetActivitiesPopState(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var value = call.Args.Count > 0 ? Integer(call.Args[0]) : DefaultPopState;
        var state = State(call.Session);
        lock (state.Gate)
            state.PopState = value == 0 ? 0 : 1;
        return ValueTask.FromResult<PyValue>(PyBool.True);
    }

    private CharacterState State(ClientSession session) =>
        _states.GetOrAdd(session.CharacterId ?? 0, static _ => new CharacterState());

    private static long Integer(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0,
    };

    private static PyValue WireId(long value) => value <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);

    private sealed class CharacterState
    {
        public object Gate { get; } = new();
        public HashSet<long> SeenActivityIds { get; } = [];
        public int PopState { get; set; } = DefaultPopState;
    }
}
