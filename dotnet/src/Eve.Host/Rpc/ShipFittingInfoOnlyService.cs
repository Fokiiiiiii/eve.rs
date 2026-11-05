using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Host;

internal sealed class ShipFittingInfoOnlyService(ShipService ship) : IService
{
    public string Name => "ship";

    public bool HasMethod(string method) => method == nameof(ShipService.GetShipFittingInfo);

    public ValueTask<PyValue> CallAsync(ServiceCall call) => ship.GetShipFittingInfo(call);
}
