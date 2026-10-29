using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed class SolarSystemLpPoolNotifications(CharacterSessions sessions)
{
    public void OnPoolChanged(long solarSystemId, long donorCharacterId, long oldPoints, long newPoints)
    {
        foreach (var session in sessions.InSolarSystem(solarSystemId))
            if (session.CharacterId == donorCharacterId)
                session.AfterResponse(() => session.SendNotification(
                    "OnSolarSystemLPChange", "charid", new PyLong(oldPoints), new PyLong(newPoints)));
            else
                session.SendNotification("OnSolarSystemLPChange", "charid", new PyLong(oldPoints), new PyLong(newPoints));
    }
}
