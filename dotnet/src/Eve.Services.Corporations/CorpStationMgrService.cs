using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Services;

namespace Eve.Services.Corporations;

public sealed record CorporationStationPlace(long StationId, long OwnerId, int SolarSystemId);

/// <summary>
/// The client's <c>corpStationMgr</c>, bound to an NPC station (<c>Moniker('corpStationMgr', stationID)</c>). The V24.01 client calls
/// <c>DoStandingCheckForStationService</c> before it opens a station service, and <c>MoveCorpHQHere</c> from the corporation's office
/// window. The other reference methods are not called by the client.
/// </summary>
[Service("corpStationMgr")]
public sealed partial class CorpStationMgrService(
    CorporationStore corporations,
    CharacterSessions? sessions,
    Func<long, long, CancellationToken, ValueTask<double>> readStanding,
    Func<long, CorporationStationPlace?> readStation,
    Func<long, CancellationToken, ValueTask<IReadOnlyDictionary<long, double>>> readStandingRules,
    Func<long> now) : IBoundObjectReleaseHandler
{
    /// <summary>The station each bound object was made for.</summary>
    private readonly ConcurrentDictionary<string, BoundStation> _bound = new(StringComparer.Ordinal);

    /// <summary><c>MachoBindObject(stationID, (method, args, kwargs))</c>: binds the station's corporation door, then makes the nested call.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    public void OnBoundObjectReleased(string oid, ClientSession? session)
    {
        if (!_bound.TryGetValue(oid, out var bound) ||
            session is not null && !ReferenceEquals(bound.Owner, session))
            return;
        _bound.TryRemove(oid, out _);
    }

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var parameters = call.Arg(0) switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        var currentStationId = call.Session.GetInt("stationid") ?? 0;
        if (Integral(parameters) is not { } stationId || stationId <= 0 || stationId != currentStationId ||
            readStation(stationId) is null)
            throw Notify("Corporation services are available only in the current NPC station.");
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = new BoundStation(stationId, call.Session);
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(now())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && Eve.Macho.JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = [.. ArgsOf(nested.Items.Count > 1 ? nested.Items[1] : PyNone.Instance)],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    /// <summary>
    /// <c>DoStandingCheckForStationService(serviceID[, characterID])</c>: refuses the station service when the character's standing
    /// The standing is the higher of the character's own and its corporation's, as the client shows it.
    /// </summary>
    [Method("DoStandingCheckForStationService")]
    public async ValueTask<PyValue> DoStandingCheckForStationService(ServiceCall call)
    {
        var place = BoundPlace(call);
        var rules = await readStandingRules(place.OwnerId, call.Cancellation);
        if (Integral(call.Arg(0)) is not { } serviceId || !rules.TryGetValue(serviceId, out var minimum) || minimum == 0)
            return PyNone.Instance;

        var characterId = call.Session.CharacterId ?? 0;
        var suppliedCharacterId = Integral(call.Arg(1));
        if (characterId <= 0)
            throw Notify("An active pilot is required to check station service standing.");
        if (suppliedCharacterId is > 0 && suppliedCharacterId != characterId)
            throw Notify("Only the active pilot's standings can be checked.");
        if (await readStanding(characterId, place.OwnerId, call.Cancellation) < minimum)
            throw Notify("Your standings are too low to access this service.");
        return PyNone.Instance;
    }

    /// <summary>
    /// <c>MoveCorpHQHere()</c>: the corporation's headquarters move to the NPC station the director is docked in. The CEO, or a
    /// director, may do it. A headquarters already there changes nothing.
    /// </summary>
    [Method("MoveCorpHQHere")]
    public async ValueTask<PyValue> MoveCorpHQHere(ServiceCall call)
    {
        var stationId = call.Session.GetInt("stationid") ?? 0;
        var place = stationId > 0 ? readStation(stationId) : null;
        if (place is null || (Integral(call.Arg(0)) is { } requested && requested != stationId))
            throw Notify("Corporation headquarters can only be moved to your current NPC station.");
        var actor = call.Session.CharacterId ?? throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", new PyStr("not signed in")));
        var corporationId = SessionCorporationId(call.Session);
        await RequireDirectorAsync(actor, corporationId, stationId, call.Cancellation);

        var previous = await corporations.MoveHeadquartersAsync(corporationId, place.StationId, place.SolarSystemId, call.Cancellation);
        if (previous is { } before)
        {
            CorporationNotificationAdapter.CorporationChanged(sessions, call, corporationId, "stationID",
                CorporationWire.Id(before.StationId), CorporationWire.Id(place.StationId));
            CorporationNotificationAdapter.CorporationChanged(sessions, call, corporationId, "solarSystemID",
                new PyInt(before.SolarSystemId), new PyInt(place.SolarSystemId));
        }
        return PyNone.Instance;
    }

    private async Task RequireDirectorAsync(long actor, long corporationId, long stationId, CancellationToken token)
    {
        var corporation = corporationId > 0 ? await corporations.GetAsync(corporationId, token) : null;
        var member = await corporations.MemberAsync(actor, token);
        if (corporation is null || member is null || member.CorporationId != corporationId)
            throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", new PyStr("not a member of the corporation")));
        var ceo = !corporation.CeoRolesSuspended && corporation.CeoId == actor;
        var titles = await corporations.TitlesAsync(corporationId, token);
        var director = (CorporationRoles.Session(corporation, member, stationId, titles).AtAll & CorporationRoles.Director) != 0;
        if (!ceo && !director)
            throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/NotDirector"))));
    }

    private CorporationStationPlace BoundPlace(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var bound) ||
            !ReferenceEquals(bound.Owner, call.Session) || readStation(bound.StationId) is not { } place)
            throw Notify("Corporation services are not available from here.");
        return place;
    }

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));

    private static long SessionCorporationId(ClientSession session)
    {
        var id = session.GetInt("corporationID") ?? 0;
        return id > 0 ? id : session.GetInt("corpid") ?? 0;
    }

    private sealed record BoundStation(long StationId, ClientSession Owner);

    private static long? Integral(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static IReadOnlyList<PyValue> ArgsOf(PyValue value) => value switch
    {
        PyTuple tuple => tuple.Items,
        PyList list => list.Items,
        PyNone => [],
        _ => [value],
    };
}
