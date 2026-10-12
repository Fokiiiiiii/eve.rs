using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Services;

namespace Eve.Services.Corporations;

/// <summary>An NPC station as the corporation door needs it: its id, its owner (the corporation that runs it) and its solar system.</summary>
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
    Func<long> now)
{
    /// <summary>The station each bound object was made for.</summary>
    private readonly ConcurrentDictionary<string, long> _bound = new(StringComparer.Ordinal);

    /// <summary><c>MachoBindObject(stationID, (method, args, kwargs))</c>: binds the station's corporation door, then makes the nested call.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var parameters = call.Arg(0) switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        if (Integral(parameters) is not { } stationId || readStation(stationId) is null)
            throw Notify("Corporation services are available in an NPC station.");
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = stationId;
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(now())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && nested.Items[0] is PyStr method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method.Value,
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
    /// toward the station's owner is below the service's minimum in the static data (the SDE's <c>stationStandingsRestrictions</c>).
    /// The standing is the higher of the character's own and its corporation's, as the client shows it.
    /// </summary>
    [Method("DoStandingCheckForStationService")]
    public async ValueTask<PyValue> DoStandingCheckForStationService(ServiceCall call)
    {
        var place = BoundPlace(call);
        var rules = await readStandingRules(place.OwnerId, call.Cancellation);
        if (Integral(call.Arg(0)) is not { } serviceId || !rules.TryGetValue(serviceId, out var minimum) || minimum == 0)
            return PyNone.Instance;
        var given = Integral(call.Arg(1)) ?? 0;
        var character = given > 0 ? given : call.Session.CharacterId ?? 0;
        if (character > 0 && await readStanding(character, place.OwnerId, call.Cancellation) < minimum)
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
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var stationId) || readStation(stationId) is not { } place)
            throw Notify("Corporation services are not available from here.");
        return place;
    }

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));

    private static long SessionCorporationId(ClientSession session)
    {
        var id = session.GetInt("corporationID") ?? 0;
        return id > 0 ? id : session.GetInt("corpid") ?? 0;
    }

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
