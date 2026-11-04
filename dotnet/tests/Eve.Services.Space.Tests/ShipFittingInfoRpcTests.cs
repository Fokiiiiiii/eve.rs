using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Xunit;

namespace Eve.Services.Space.Tests;

public sealed class ShipFittingInfoRpcTests
{
    [Fact]
    public async Task Unknown_fitting_returns_the_reference_four_value_tuple()
    {
        var service = new ShipService(null!, () => 0);

        var result = Assert.IsType<PyTuple>(await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetShipFittingInfo",
            Args = [new PyLong(456)],
            Session = new ClientSession(),
        }));

        Assert.Equal(4, result.Items.Count);
        Assert.Equal(new PyStr("Fitting 456"), result.Items[0]);
        Assert.Equal(new PyStr("res:/UI/Texture/classes/Fitting/tabFittings.png"), result.Items[1]);
        Assert.Equal(new PyInt(0), result.Items[2]);
        Assert.IsType<PyNone>(result.Items[3]);
        Assert.True(((IService)service).HasMethod("GetShipFittingInfo"));
    }

    [Fact]
    public async Task Fitting_backend_populates_the_same_four_tuple()
    {
        var service = new ShipService(null!, () => 0, getShipFittingInfo: (fittingId, _) =>
            ValueTask.FromResult<ShipFittingInfo?>(fittingId == 456
                ? new ShipFittingInfo("Punisher setup", "res:/fits/punisher.png", 587, new PyInt(2))
                : null));

        var result = Assert.IsType<PyTuple>(await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetShipFittingInfo",
            Args = [new PyLong(456)],
            Session = new ClientSession(),
        }));

        Assert.Equal(new PyStr("Punisher setup"), result.Items[0]);
        Assert.Equal(new PyStr("res:/fits/punisher.png"), result.Items[1]);
        Assert.Equal(new PyInt(587), result.Items[2]);
        Assert.Equal(new PyInt(2), result.Items[3]);
    }
}
