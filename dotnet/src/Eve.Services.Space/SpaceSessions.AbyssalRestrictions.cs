using Eve.Macho;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    internal const string AbyssalWarpBlockedMessage = "You cannot warp out of Abyssal Deadspace.";
    internal const string AbyssalFleetWarpBlockedMessage = "You cannot warp to a fleet member inside Abyssal Deadspace.";
    internal const string AbyssalShipSwapBlockedMessage = "You cannot eject, board, launch, or store ships inside Abyssal Deadspace.";

    public bool IsInAbyssalRun(ClientSession session) =>
        _pilots.TryGetValue((ulong)session.SessionId, out var pilot) && pilot.SimulationScene.InstanceId > 0 &&
        session.CharacterId == pilot.CharacterId && session.GetInt("shipid") == pilot.ShipId;

    private void AssertAbyssalShipSwapAllowed(ClientSession session)
    {
        if (IsInAbyssalRun(session)) throw Notify(AbyssalShipSwapBlockedMessage);
    }

    private static string? AbyssalRestrictedAction(SimAction? action) => action switch
    {
        SimAction.Cloak => "ABYSSAL_CLOAK_RESTRICTED",
        SimAction.CynoField => "CYNOSURAL_FIELD_NOT_ALLOWED_IN_ABYSSAL_DEADSPACE",
        SimAction.Charged charged => AbyssalRestrictedAction(charged.Action),
        _ => null,
    };
}
