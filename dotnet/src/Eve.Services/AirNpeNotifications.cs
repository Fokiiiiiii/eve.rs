using Eve.Marshal;

namespace Eve.Services;

/// <summary>The single wire format for AIR NPE state changes from login and NPE RPCs.</summary>
public static class AirNpeNotifications
{
    /// <summary>A null state means the persistence transition did not change anything and emits nothing.</summary>
    public static void NotifyStateChanged(ClientSession session, long characterId, int? state)
    {
        if (state is { } changedState)
        {
            session.SendNotification("OnAirNpeStateChanged", "clientID", new PyInt(characterId), new PyInt(changedState));
        }
    }
}
