using Eve.Marshal;

namespace Eve.Services;

/// <summary>
/// Re-applies the session location of a character that has been moved to a station while it stays in the game (a clone jump, a
/// pod death). It sets the attributes the character selection sets for a docked pilot (<c>CharUnboundMgrService.SelectCharacterID</c>),
/// in one session change, so the client's station and location handlers see the same order as on entry. Only the attributes that
/// change are announced.
/// </summary>
public static class CharacterSessionRelocation
{
    public static IReadOnlyList<SessionChange> Docked(ClientSession session, long stationId, int solarSystemId, int constellationId, int regionId, long? shipId) =>
        session.Change(
            ("stationid", new PyInt(stationId)),
            ("stationid2", new PyInt(stationId)),
            ("structureid", PyNone.Instance),
            ("solarsystemid", PyNone.Instance),
            ("solarsystemid2", new PyInt(solarSystemId)),
            ("constellationid", new PyInt(constellationId)),
            ("regionid", new PyInt(regionId)),
            ("shipid", shipId is { } ship ? new PyLong(ship) : PyNone.Instance),
            ("locationid", new PyInt(stationId)));
}
