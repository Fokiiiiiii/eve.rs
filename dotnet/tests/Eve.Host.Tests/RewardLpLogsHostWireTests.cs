using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Incursion_lp_logs_return_the_row_list_consumed_by_the_client_through_host_dispatch()
    {
        Start(configure: registry => registry.Register(new RewardsService()));
        var (front, open) = await OpenFrontAsync();

        Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 1, "rewardMgr", "GetRewardLPLogs"));
    }
}
