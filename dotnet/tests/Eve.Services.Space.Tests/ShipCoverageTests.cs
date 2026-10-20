using Eve.Services;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The <c>ship</c> calls the client makes for fitting and hulls (invItemFunctions.py:189-197 for AssembleShip, the fitting window and the undock and
/// drone calls). Each must be answered by a method of the service, or be listed as open. An open call is answered with None by the generator's
/// </summary>
public class ShipCoverageTests
{
    private static readonly string[] ClientCalls =
    [
        "AssembleShip", "GetShipFittingInfo", "Undock", "Board", "LeaveShip", "SafeLogoff", "AbortSafeLogoff", "Eject", "Jettison", "LaunchDrones",
        "LaunchFromShip", "ScoopDrone", "GetDirtTimestamp",
    ];

    private static readonly string[] Open = [];

    [Fact]
    public void Every_ship_call_is_answered_or_listed_as_open()
    {
        IService ship = new ShipService(null!, () => 0);
        var missing = ClientCalls.Where(method => !ship.HasMethod(method)).ToArray();
        Assert.Equal(Open.Order(), missing.Order());
    }
}
