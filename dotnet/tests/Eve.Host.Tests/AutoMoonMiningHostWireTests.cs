using Eve.Marshal;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Auto_moon_mining_wire_replies_keep_missing_id_error_and_empty_cycle_shape()
    {
        Start(configure: registry => registry.Register(new AutoMoonMiningRpcService()));
        var (front, open) = await OpenFrontAsync();

        var body = await NsHostRpcErrorAsync(front, open, 1,
            "autoMoonMining", "RequestMiningDetails", new PyTuple([new PyInt(30_000_142), new PyInt(0)]));
        Assert.Contains("CustomInfo", body);
        Assert.Contains("Auto moon miner details require a structure ID.", body);

        var cycle = Assert.IsType<PyDict>(await HostRpcAsync(front, open, 2,
            "autoMoonMining", "RequestMiningCycleOutput", new PyInt(30_000_142), new PyInt(0)));
        Assert.Empty(cycle.Entries);
    }
}
