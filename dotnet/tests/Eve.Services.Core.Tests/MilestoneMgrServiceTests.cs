using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class MilestoneMgrServiceTests
{
    [Fact]
    public async Task Login_and_claim_routes_return_none_when_milestone_runtime_is_unwired()
    {
        var service = (IService)new MilestoneMgrService();
        Assert.True(service.HasMethod("ProcessCharacterLogon"));
        Assert.True(service.HasMethod("ClaimRewards"));
        var session = new ClientSession { CharacterId = 90000001 };
        Assert.Same(PyNone.Instance, await Call(service, session, "ProcessCharacterLogon"));
        Assert.Same(PyNone.Instance, await Call(service, session, "ClaimRewards", new PyInt(0)));
    }

    [Fact]
    public async Task Milestone_routes_forward_to_the_future_runtime_callback()
    {
        var calls = new List<string>();
        var service = (IService)new MilestoneMgrService((call, _) =>
        {
            calls.Add(call.Method);
            return ValueTask.FromResult<PyValue?>(null);
        });
        var session = new ClientSession { CharacterId = 90000002 };
        await Call(service, session, "ProcessCharacterLogon");
        await Call(service, session, "ClaimRewards", new PyInt(11));
        Assert.Equal(["ProcessCharacterLogon", "ClaimRewards"], calls);
    }

    private static Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session }).AsTask();
}