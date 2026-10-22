using Eve.Marshal;
using Eve.Services;
using Eve.Services.Planets;

namespace Eve.Host.Tests;

public sealed class PlanetMgrAuthorizationTests
{
    [Fact]
    public async Task Planet_GM_Rpcs_refuse_a_session_without_the_server_set_GML_role()
    {
        var service = new PlanetMgrService(
            null!, null!, null!, null!, null!, null!, null!,
            () => 0,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            "inventory");
        var methods = new (string Name, Func<ServiceCall, ValueTask<PyValue>> Invoke)[]
        {
            ("GMForceInstallProgram", service.GMForceInstallProgram),
            ("GMAddCommodity", service.GMAddCommodity),
            ("GMConvertCommandCenter", service.GMConvertCommandCenter),
            ("GMGetSynchedServerState", service.GMGetSynchedServerState),
        };

        foreach (var (name, invoke) in methods)
        {
            var call = new ServiceCall
            {
                Method = name,
                Args = [],
                Session = new ClientSession(),
            };
            await Assert.ThrowsAsync<MachoWrappedException>(async () => await invoke(call));
        }
    }
}
