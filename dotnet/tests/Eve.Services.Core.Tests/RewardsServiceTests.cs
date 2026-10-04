using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class RewardsServiceTests
{
    [Theory]
    [InlineData("rewardMgr", "GetDelayedRewardsByGroupIDs", "dict")]
    [InlineData("rewardMgr", "GetRewardLPLogs", "list")]
    [InlineData("loginCampaignManager", "GetActiveCampaigns", "list")]
    [InlineData("loginCampaignManager", "GetCampaignData", "dict")]
    [InlineData("loginCampaignManager", "GetPlayerProgress", "dict")]
    [InlineData("seasonalLoginCampaignManager", "GetActiveCampaigns", "list")]
    [InlineData("seasonalLoginCampaignManager", "GetCampaignData", "list")]
    [InlineData("seasonalLoginCampaignManager", "GetPlayerProgress", "dict")]
    public async Task Empty_read_rpcs_return_the_reference_wire_container(
        string serviceName, string method, string shape)
    {
        var service = Service(serviceName);
        Assert.Equal(serviceName, service.Name);
        Assert.True(service.HasMethod(method));

        var result = await service.CallAsync(Call(method,
            method == "GetDelayedRewardsByGroupIDs" ? new PyList([]) : null));
        PyValue expected = shape == "dict" ? Py.Dict() : new PyList([]);

        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(result));
    }

    [Fact]
    public async Task Login_campaign_state_does_not_claim_a_synthetic_reward_track()
    {
        var service = new LoginCampaignMgrService();
        Assert.True(service.HasMethod("get_client_campaign_state"));

        var result = await service.CallAsync(Call("get_client_campaign_state"));

        Assert.IsType<PyNone>(result);
        Assert.Equal(MarshalCodec.Encode(PyNone.Instance), MarshalCodec.Encode(result));
    }

    [Fact]
    public async Task Seasonal_campaign_without_an_active_campaign_returns_four_none_values()
    {
        var service = new SeasonalLoginCampaignMgrService();
        Assert.True(service.HasMethod("get_active_campaign"));

        var result = await service.CallAsync(Call("get_active_campaign"));
        var expected = PyTuple.Of(PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance);

        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(result));
    }

    [Fact]
    public void Claim_and_mutation_rpcs_are_not_exposed()
    {
        Assert.False(new RewardsService().HasMethod("ClaimReward"));
        Assert.False(new LoginCampaignMgrService().HasMethod("claim_reward"));
        Assert.False(new SeasonalLoginCampaignMgrService().HasMethod("claim_reward"));
    }

    private static IService Service(string name) => name switch
    {
        "rewardMgr" => new RewardsService(),
        "loginCampaignManager" => new LoginCampaignMgrService(),
        "seasonalLoginCampaignManager" => new SeasonalLoginCampaignMgrService(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static ServiceCall Call(string method, PyValue? argument = null) => new()
    {
        Method = method,
        Args = argument is null ? [] : [argument],
        Session = new ClientSession(new Sink()),
    };

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) => throw new InvalidOperationException("Read RPCs must not send packets.");
    }
}
