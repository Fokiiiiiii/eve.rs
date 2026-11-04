using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

[Service("activityMgr")]
public sealed partial class ActivityMgrService(IActivityMgrStateStore stateStore)
{
    private const int DefaultPopState = 1;

    public ActivityMgrService() : this(new InMemoryActivityMgrStateStore())
    {
    }

    [Method("GetActivityBadgeStatuses")]
    public async ValueTask<PyValue> GetActivityBadgeStatuses(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var state = await ReadState(call.Session, call.Cancellation)
            ?? throw new InvalidOperationException("The activity state is unavailable for this character.");
        var result = new PyList([.. state.SeenActivityIds.Order().Select(WireId)]);
        call.Session.SendNotification("OnSystemActivityUpdated", "None");
        return result;
    }

    [Method("ActivitySeen")]
    public async ValueTask<PyValue> ActivitySeen(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var activityId = call.Args.Count > 0 ? Integer(call.Args[0], 0) : 0;
        if (activityId <= 0)
            return PyBool.False;
        var result = await stateStore.UpdateAsync(
            CharacterId(call.Session),
            state => state with { SeenActivityIds = new HashSet<long>(state.SeenActivityIds) { activityId } },
            call.Cancellation);
        return result is null ? PyBool.False : PyBool.True;
    }

    [Method("GetActivitiesPopState")]
    public async ValueTask<PyValue> GetActivitiesPopState(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var state = await ReadState(call.Session, call.Cancellation)
            ?? throw new InvalidOperationException("The activity state is unavailable for this character.");
        return new PyInt(state.PopState);
    }

    [Method("SetActivitiesPopState")]
    public async ValueTask<PyValue> SetActivitiesPopState(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var value = call.Args.Count > 0 ? Integer(call.Args[0], DefaultPopState) : DefaultPopState;
        var result = await stateStore.UpdateAsync(
            CharacterId(call.Session),
            state => state with { PopState = value == 0 ? 0 : 1 },
            call.Cancellation);
        return result is null ? PyBool.False : PyBool.True;
    }

    private ValueTask<ActivityMgrState?> ReadState(ClientSession session, CancellationToken cancellationToken) =>
        stateStore.ReadAsync(CharacterId(session), cancellationToken);

    private static long CharacterId(ClientSession session) => session.CharacterId ?? 0;

    private static long Integer(PyValue value, long fallback) => value switch
    {
        PyBool boolean => boolean.Value ? 1 : 0,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyNone => fallback,
        _ => fallback,
    };

    private static PyValue WireId(long value) => value <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);
}
