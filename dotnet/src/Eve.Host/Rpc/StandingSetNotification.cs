using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

internal static class StandingSetNotification
{
    public static void Send(ClientSession? session, long characterId, long ownerId, double standing) =>
        session?.SendNotification("OnStandingSet", "clientID",
            new PyInt(ownerId), new PyLong(characterId), new PyReal(standing));
}
