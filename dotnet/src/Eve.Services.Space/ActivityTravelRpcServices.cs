using System.Globalization;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

public sealed record SpaceRpcItem(long ItemId, long OwnerId, int TypeId, int GroupId, long Quantity);

public sealed record WormholePolarization(long EndAtUnixMs, int DurationSeconds);

public sealed class PvpFilamentMgrService(
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? dispatch = null) : IService
{
    public string Name => "pvpFilamentMgr";

    public bool HasMethod(string method) => method is
        "AbyssalPVPEndGateActivation" or "GetActiveEvents" or "GetAllEvents" or
        "GetCharacterStatistics" or "GetLeaderboard" or "GetMostRecentEvent" or
        "GetNextEventDate" or "JoinPVPQueue" or "LeavePVPQueue";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        if (dispatch is not null && await dispatch(call, call.Cancellation).ConfigureAwait(false) is { } response)
            return response;

        switch (call.Method)
        {
            case "GetAllEvents":
            case "GetActiveEvents":
                return new PyDict([]);
            case "GetMostRecentEvent":
            case "GetNextEventDate":
            case "LeavePVPQueue":
                return PyNone.Instance;
            case "GetLeaderboard":
                call.Session.SendNotification("OnPVPFilamentsLeaderboard", "clientID", Leaderboard(call));
                return PyNone.Instance;
            case "GetCharacterStatistics":
                call.Session.SendNotification("OnPVPFilamentsCharacterStatistics", "clientID", CharacterStatistics(call));
                return PyNone.Instance;
            case "JoinPVPQueue":
                throw MachoErrors.ThrowUserError("PVPJumpError", ("errors", ErrorList(33)));
            case "AbyssalPVPEndGateActivation":
                throw MachoErrors.ThrowUserError("DeniedTargetAttemptFailed");
            default:
                return PyNone.Instance;
        }
    }

    private static PyDict Leaderboard(ServiceCall call) => Dict(
        ("matchTypeID", new PyInt(TravelRpcWire.Integer(call.Arg(0)))),
        ("scheduleID", new PyInt(TravelRpcWire.Integer(call.Arg(1)))),
        ("entries", new PyList([])));

    private static PyDict CharacterStatistics(ServiceCall call) => Dict(
        ("matchTypeID", new PyInt(TravelRpcWire.Integer(call.Arg(0)))),
        ("scheduleID", new PyInt(TravelRpcWire.Integer(call.Arg(1)))),
        ("statistics", Dict(
            ("rank", new PyInt(0)),
            ("wins", new PyInt(0)),
            ("losses", new PyInt(0)),
            ("draws", new PyInt(0)))));

    private static PyDict Dict(params (string Key, PyValue Value)[] pairs) => new(
        [.. pairs.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Key), pair.Value))]);

    private static PyList ErrorList(int code) => PyList.Of(
        PyList.Of(new PyInt(code), new PyList([])));
}

public sealed class RandomJumpMgrService(
    Func<long, CancellationToken, ValueTask<SpaceRpcItem?>>? readItem = null,
    Func<ServiceCall, SpaceRpcItem, CancellationToken, ValueTask<PyValue?>>? activate = null) : IService
{
    private static readonly HashSet<int> RandomJumpKeyGroups = [4041, 4087];

    public string Name => "randomJumpMgr";

    public bool HasMethod(string method) => method == "ActivateRandomJumpFilament";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        var itemId = TravelRpcWire.Integer(call.Arg(0));
        var characterId = TravelRpcWire.CharacterId(call.Session);
        List<PyValue> errors = [];
        SpaceRpcItem? item = null;

        if (itemId <= 0)
        {
            errors.Add(Error(6));
        }
        else
        {
            item = readItem is null ? null : await readItem(itemId, call.Cancellation).ConfigureAwait(false);
            if (item is null)
            {
                errors.Add(Error(6));
            }
            else
            {
                if (!RandomJumpKeyGroups.Contains(item.GroupId))
                    errors.Add(Error(2, characterId > 0 ? [new PyInt(characterId)] : []));
                if (characterId > 0 && item.OwnerId != characterId)
                    errors.Add(Error(3, [new PyInt(characterId)]));
                if (item.Quantity < 1)
                    errors.Add(Error(11));
            }
        }

        if (errors.Count > 0)
            throw RandomJumpError(errors);

        if (item is not null && activate is not null &&
            await activate(call, item, call.Cancellation).ConfigureAwait(false) is { } response)
            return response;

        throw RandomJumpError([Error(22)]);
    }

    private static PyList Error(int code, PyValue[]? args = null) =>
        PyList.Of(new PyInt(code), new PyList(args ?? []));

    private static MachoWrappedException RandomJumpError(IReadOnlyList<PyValue> errors) =>
        MachoErrors.ThrowUserError("RandomJumpError", ("errors", new PyList(errors.ToArray())));
}

public sealed class WarpVectorMgrService(
    Func<long, CancellationToken, ValueTask<SpaceRpcItem?>>? readItem = null,
    Func<ServiceCall, SpaceRpcItem, CancellationToken, ValueTask<PyValue?>>? useVector = null) : IService
{
    private const int WarpVectorGroupId = 4599;
    private const string DestinationUnavailable = "No verified warp vector destination is available.";

    public string Name => "warpVectorMgr";

    public bool HasMethod(string method) => method == "UseVector";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        var itemId = TravelRpcWire.Integer(call.Arg(0));
        var characterId = TravelRpcWire.CharacterId(call.Session);
        var item = itemId > 0 && readItem is not null
            ? await readItem(itemId, call.Cancellation).ConfigureAwait(false)
            : null;

        if (itemId <= 0 || item is null)
            throw Notify("Warp vector item not found.");
        if (item.GroupId != WarpVectorGroupId)
            throw Notify("The selected item is not warp vector data.");
        if (characterId > 0 && item.OwnerId != characterId)
            throw Notify("You do not own this warp vector data.");
        if (item.Quantity < 1)
            throw Notify("Warp vector data is not available.");

        if (useVector is not null &&
            await useVector(call, item, call.Cancellation).ConfigureAwait(false) is { } response)
            return response;

        throw Notify(DestinationUnavailable);
    }

    private static MachoWrappedException Notify(string text) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}

// The simulation's Jump command accepts Stargates only; actual wormhole entity and mass handling is supplied by this full-transition callback.
public sealed class WormholeMgrService(
    SpaceSessions? spaceSessions,
    Func<long, long, CancellationToken, ValueTask<WormholePolarization?>>? readPolarization = null,
    Func<ServiceCall, long, CancellationToken, ValueTask<PyValue?>>? jump = null,
    Func<ServiceCall, bool>? isPilotWarpLandingPending = null) : IService
{
    public string Name => "wormholeMgr";

    public bool HasMethod(string method) => method is "GetWormholePolarization" or "WormholeJump";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (call.Method == "GetWormholePolarization")
        {
            var endpointId = TravelRpcWire.Integer(call.Arg(0));
            var characterId = TravelRpcWire.CharacterId(call.Session);
            if (endpointId <= 0 || characterId <= 0 || readPolarization is null)
                return PyNone.Instance;

            var polarization = await readPolarization(endpointId, characterId, call.Cancellation).ConfigureAwait(false);
            return polarization is null
                ? PyNone.Instance
                : PyList.Of(
                    new PyLong(Py.FileTimeFromUnixMs(polarization.EndAtUnixMs)),
                    new PyInt(Math.Max(0, polarization.DurationSeconds)));
        }

        if (call.Method != "WormholeJump")
            return PyNone.Instance;

        var sourceEndpointId = TravelRpcWire.Integer(call.Arg(0));
        var session = call.Session;
        var character = TravelRpcWire.CharacterId(session);
        if (sourceEndpointId <= 0 || character <= 0)
            return PyNone.Instance;

        if (isPilotWarpLandingPending?.Invoke(call) == true)
        {
            session.SendNotification("OnWormholeJumpCancel", "clientID");
            return PyNone.Instance;
        }

        if (jump is null || (spaceSessions is not null && spaceSessions.ShipIdOf(session) is not > 0))
            return PyNone.Instance;

        var result = await jump(call, sourceEndpointId, call.Cancellation).ConfigureAwait(false);
        return result switch
        {
            null => PyNone.Instance,
            PyTuple tuple => new PyList(tuple.Items),
            _ => result,
        };
    }
}

internal static class TravelRpcWire
{
    public static long CharacterId(ClientSession session) =>
        session.CharacterId ?? session.GetInt("characterID") ?? session.GetInt("characterId") ?? 0;

    public static long Integer(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyBool boolean => boolean.Value ? 1 : 0,
        PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value < 9_223_372_036_854_775_808d =>
            (long)Math.Truncate(number.Value),
        PyStr text when double.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number) && number >= long.MinValue && number < 9_223_372_036_854_775_808d =>
            (long)Math.Truncate(number),
        _ => 0,
    };
}
