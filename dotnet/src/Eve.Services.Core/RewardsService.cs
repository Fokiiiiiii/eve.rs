using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>Empty read-side contract for reward data not yet backed by persistence.</summary>
public sealed class RewardsService : IService
{
    public string Name => "rewardMgr";

    public bool HasMethod(string method) => method is
        "GetDelayedRewardsByGroupIDs" or
        "GetRewardLPLogs";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetDelayedRewardsByGroupIDs" => ValueTask.FromResult<PyValue>(Py.Dict()),
        "GetRewardLPLogs" => ValueTask.FromResult<PyValue>(new PyList([])),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };
}

/// <summary>Read-only login campaign RPCs; no campaign is advertised without campaign data.</summary>
public sealed class LoginCampaignMgrService : IService
{
    public string Name => "loginCampaignManager";

    public bool HasMethod(string method) => method is
        "GetActiveCampaigns" or
        "GetCampaignData" or
        "GetPlayerProgress" or
        "get_client_campaign_state";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetActiveCampaigns" => ValueTask.FromResult<PyValue>(new PyList([])),
        "GetCampaignData" => ValueTask.FromResult<PyValue>(Py.Dict()),
        "GetPlayerProgress" => ValueTask.FromResult<PyValue>(Py.Dict()),
        // EVE.js returns None when the login campaign is disabled. No campaign store/config
        // is wired here, so do not synthesize the reference server's sample reward track.
        "get_client_campaign_state" => ValueTask.FromResult<PyValue>(PyNone.Instance),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };
}

/// <summary>Read-only seasonal login campaign RPCs.</summary>
public sealed class SeasonalLoginCampaignMgrService : IService
{
    public string Name => "seasonalLoginCampaignManager";

    public bool HasMethod(string method) => method is
        "GetActiveCampaigns" or
        "GetCampaignData" or
        "GetPlayerProgress" or
        "get_active_campaign";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetActiveCampaigns" => ValueTask.FromResult<PyValue>(new PyList([])),
        "GetCampaignData" => ValueTask.FromResult<PyValue>(new PyList([])),
        "GetPlayerProgress" => ValueTask.FromResult<PyValue>(Py.Dict()),
        "get_active_campaign" => ValueTask.FromResult<PyValue>(PyTuple.Of(
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance)),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };
}
