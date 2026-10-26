using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Superweapon_activations_return_success_only_after_the_registered_host_callback_accepts()
    {
        var pointRequest = (SuperWeaponPointRequest?)null;
        var slashRequest = (SuperWeaponSlashRequest?)null;
        Start(configure: registry => registry.Register(new SuperWeaponMgrService(new SuperWeaponRpcCallbacks(
            (request, _) =>
            {
                pointRequest = request;
                return ValueTask.FromResult(true);
            },
            (request, _) =>
            {
                slashRequest = request;
                return ValueTask.FromResult(true);
            }))));
        var (front, open) = await OpenFrontAsync();
        var point = PyTuple.Of(new PyInt(10), new PyInt(20), new PyInt(30));
        var start = PyTuple.Of(new PyInt(1), new PyInt(2), new PyInt(3));
        var end = PyTuple.Of(new PyReal(4), new PyReal(5), new PyReal(6));

        Assert.Equal(new PyInt(1),
            await HostRpcAsync(front, open, 1, "superWeaponMgr", "ActivateSinglePointTargetedModule",
                new PyInt(4001), new PyInt(9001), point));
        Assert.Equal(new PyInt(1),
            await HostRpcAsync(front, open, 2, "superWeaponMgr", "ActivateSlashModule",
                new PyInt(4002), start, end));

        Assert.Equal(new SuperWeaponPointRequest(4001, 9001, point, pointRequest!.Session), pointRequest);
        Assert.Equal(new SuperWeaponSlashRequest(4002, start, end, slashRequest!.Session), slashRequest);
    }
}
