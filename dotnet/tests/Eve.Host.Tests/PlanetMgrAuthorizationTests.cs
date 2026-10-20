using Eve.Marshal;
using Eve.Services;
using Eve.Services.Planets;

namespace Eve.Host.Tests;

public sealed class PlanetMgrAuthorizationTests
{
    [Fact]
    public async Task Planet_GM_RPC_refuses_a_session_without_the_server_set_GML_role()
    {
        var service = new PlanetMgrService(
            null!, null!, null!, null!, null!, null!, null!,
            () => 0,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            "inventory");
        var call = new ServiceCall
        {
            Method = "GMGetSynchedServerState",
            Args = [],
            Session = new ClientSession(),
        };

        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.GMGetSynchedServerState(call));
    }
}
