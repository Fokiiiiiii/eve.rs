using Eve.Host;
using Eve.Marshal;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Dynamic_item_info_without_a_valid_item_returns_none_through_host_dispatch()
    {
        Start(configure: registry => registry.Register(new DynamicItemRpcService(null!, null!, null!, null!, null)));
        var (front, open) = await OpenFrontAsync();

        var result = await HostRpcAsync(
            front, open, 1, "dynamicItemService", "GetDynamicItemInfo", new PyInt(0));

        Assert.Same(PyNone.Instance, result);
    }
}
