using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class TargetedParityRpcTests
{
    [Fact]
    public async Task Peace_treaty_uses_corporation_when_alliance_is_zero()
    {
        var session = new ClientSession();
        session.Set("allianceID", new PyInt(0));
        session.Set("corporationID", new PyInt(42));
        long ownerId = 0;
        var services = CoreServices.Create(
            new NoAccounts(),
            () => 0,
            gameRpcCallbacks: new GameRpcCallbacks(ReadPeaceTreaties: (owner, _) =>
            {
                ownerId = owner;
                return ValueTask.FromResult<PeaceTreatySnapshot?>(new PeaceTreatySnapshot([], []));
            }));
        var service = Assert.Single(services, candidate => candidate.Name == "peaceTreatyMgr");

        await service.CallAsync(new ServiceCall
        {
            Method = "GetPeaceTreatiesForSession",
            Args = [],
            Session = session,
        });

        Assert.Equal(42, ownerId);
    }

    [Fact]
    public void Core_services_register_pos_mgr_once_with_the_control_tower_method()
    {
        var services = CoreServices.Create(new NoAccounts(), () => 0);

        var posMgr = Assert.Single(services, service => service.Name == "posMgr");
        Assert.True(posMgr.HasMethod("GetControlTowers"));
        var publicQa = Assert.Single(services, service => service.Name == "publicQaToolsServer");
        Assert.All(["MoveMeTo", "SlashCmd", "CanGiveItemForMultifit"], method =>
            Assert.True(publicQa.HasMethod(method), method));
    }


    [Fact]
    public async Task Public_qa_slash_dispatch_preserves_the_call_and_denies_non_staff()
    {
        var staff = new ClientSession();
        staff.Set("accountRole", new PyLong(1L << 52));
        PublicQaRequest? observed = null;
        var callbackCount = 0;
        var service = (IService)new PublicQaToolsServerService(new PublicQaToolsRpcCallbacks(
            IsStaff: PublicQaToolsServerService.HasStaffAccountRole,
            SlashCmdAsync: (request, _) =>
            {
                callbackCount++;
                observed = request;
                return ValueTask.FromResult<PyValue?>(new PyStr("handled"));
            }));
        var callId = new PyInt(91);
        var result = await service.CallAsync(new ServiceCall
        {
            Method = "SlashCmd",
            Args = [new PyStr("/heal")],
            Session = staff,
            CallId = callId,
        });

        Assert.Equal(new PyStr("handled"), result);
        Assert.Equal(1, callbackCount);
        Assert.Same(staff, observed!.Session);
        Assert.Equal(callId, observed.CallId);
        Assert.Equal([new PyStr("/heal")], observed.Arguments);

        var ordinary = new ClientSession();
        ordinary.Set("accountRole", new PyLong(0));
        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "SlashCmd",
            Args = [new PyStr("/giveitem")],
            Session = ordinary,
        }));
        Assert.Equal(1, callbackCount);
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
