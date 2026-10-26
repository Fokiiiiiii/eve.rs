using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Tournament_ship_list_uses_the_dictionary_field_consumed_by_the_client_through_host_dispatch()
    {
        Start(configure: registry => registry.Register(new TourneyMgrService()));
        var (front, open) = await OpenFrontAsync();

        var result = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 1, "tourneyMgr", "QueryShipList", new PyInt(42_001)));
        var entry = Assert.Single(result.Entries);
        Assert.Equal("allowedShipList", WireText(entry.Key));
        Assert.Empty(Assert.IsType<PyList>(entry.Value).Items);
    }
}
