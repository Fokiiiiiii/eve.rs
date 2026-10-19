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
    public async Task Public_qa_rpcs_require_staff_account_roles()
    {
        var staff = new ClientSession();
        staff.Set("accountRole", new PyLong(GameMasterRoles.Qa));
        PublicQaRequest? observedSlash = null;
        var callbackCount = 0;
        var service = (IService)new PublicQaToolsServerService(new PublicQaToolsRpcCallbacks(
            IsStaff: PublicQaToolsServerService.HasStaffAccountRole,
            MoveMeToAsync: (_, _) =>
            {
                callbackCount++;
                return ValueTask.CompletedTask;
            },
            SlashCmdAsync: (request, _) =>
            {
                callbackCount++;
                observedSlash = request;
                return ValueTask.FromResult<PyValue?>(new PyStr("handled"));
            },
            CanGiveItemForMultifitAsync: (_, _) =>
            {
                callbackCount++;
                return ValueTask.FromResult(true);
            }));

        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "MoveMeTo",
            Args = [],
            Session = staff,
        }));
        var callId = new PyInt(91);
        Assert.Equal(new PyStr("handled"), await service.CallAsync(new ServiceCall
        {
            Method = "SlashCmd",
            Args = [new PyStr("/heal")],
            Session = staff,
            CallId = callId,
        }));
        Assert.Same(PyBool.True, await service.CallAsync(new ServiceCall
        {
            Method = "CanGiveItemForMultifit",
            Args = [],
            Session = staff,
        }));
        Assert.Equal(3, callbackCount);
        Assert.Same(staff, observedSlash!.Session);
        Assert.Equal(callId, observedSlash.CallId);
        Assert.Equal([new PyStr("/heal")], observedSlash.Arguments);

        var ordinary = new ClientSession();
        ordinary.Set("accountRole", new PyLong(0));
        ordinary.Set("role", new PyLong(GameMasterRoles.Staff));
        ordinary.Set("rolesAtAll", new PyLong(GameMasterRoles.Staff));
        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "MoveMeTo",
            Args = [],
            Session = ordinary,
        }));
        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "SlashCmd",
            Args = [new PyStr("/giveitem")],
            Session = ordinary,
        }));
        Assert.Same(PyBool.False, await service.CallAsync(new ServiceCall
        {
            Method = "CanGiveItemForMultifit",
            Args = [],
            Session = ordinary,
        }));
        Assert.Equal(3, callbackCount);
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
