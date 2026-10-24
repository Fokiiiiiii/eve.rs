using Eve.Services;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The client calls these ship methods from eveMisc.py, shipConfigSvc.py, menusvc.py and cleanShipButton.py.
/// Every method must be answered or explicitly listed as open.
/// </summary>
public class ShipCoverageTests
{
    private static readonly string[] ClientCalls =
    [
        "AbortSafeLogoff", "AssembleShip", "Board", "ConfigureShip", "Drop", "Eject", "GetDirtTimestamp",
        "GetShipConfiguration", "GetShipFittingInfo", "Jettison", "LaunchDrones", "LaunchFromContainer",
        "LaunchFromShip", "LeaveShip", "ResetDirtTimestamp", "SafeLogoff", "ScoopDrone", "Undock",
    ];

    private static readonly string[] Open =
    [
        "ConfigureShip", "GetShipConfiguration", "LaunchFromContainer", "ResetDirtTimestamp",
    ];

    [Fact]
    public void Every_ship_call_is_answered_or_listed_as_open()
    {
        IService ship = new ShipService(null!, () => 0);
        var missing = ClientCalls.Where(method => !ship.HasMethod(method)).ToArray();
        Assert.Equal(Open.Order(), missing.Order());
    }
}
