using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>Authenticated identity used by reward callbacks; campaign state is per user, with character context when present.</summary>
public sealed record LoginRewardPlayer(long UserId, long? CharacterId)
{
    public static LoginRewardPlayer? From(ServiceCall call)
    {
        if (call.Session.UserId is not { } userId || userId <= 0)
            return null;
        var characterId = call.Session.CharacterId is > 0 ? call.Session.CharacterId : null;
        return new LoginRewardPlayer(userId, characterId);
    }
}

/// <summary>The real client-wire campaign state plus the authoritative next-claim facts.</summary>
public sealed record LoginCampaignSnapshot(
    int CampaignId,
    PyValue ClientState,
    int NextRewardIndex,
    long NextRewardTimestampFiletime);

/// <summary>The item and progress values returned by the client loginCampaignManager claim RPC.</summary>
public sealed record LoginCampaignClaimResult(
    PyValue ItemReward,
    PyValue UpdatedItemProgress,
    PyValue ReceivedBucketTypeId,
    PyValue UpdatedBucketProgress);

/// <summary>The exact state used to fence an atomic campaign claim in the persistence callback.</summary>
public sealed record LoginCampaignClaimRequest(
    LoginRewardPlayer Player,
    int CampaignId,
    int ExpectedNextRewardIndex,
    long ExpectedNextRewardTimestampFiletime);

public sealed record SeasonalLoginCampaignReward(int TypeId, long Quantity);

/// <summary>One persisted seasonal claim. ClientCloneGrade is returned unchanged in the claim-history dict.</summary>
public sealed record SeasonalLoginCampaignClaimedDay(int DateOrdinal, int ClientCloneGrade, bool WasOmega);

/// <summary>Active campaign config from the real static catalog and the player's persisted history.</summary>
public sealed record SeasonalLoginCampaignSnapshot(
    int CampaignId,
    DateOnly EndDate,
    TimeOnly TurnoverTimeUtc,
    int RemainingRewardDays,
    bool IsOmega,
    bool RetroClaimEnabled,
    IReadOnlyDictionary<int, SeasonalLoginCampaignReward> AlphaRewards,
    IReadOnlyDictionary<int, SeasonalLoginCampaignReward> OmegaRewards,
    IReadOnlyList<SeasonalLoginCampaignClaimedDay> ClaimHistory);

public enum SeasonalLoginRewardTrack
{
    Alpha,
    Omega
}

public sealed record SeasonalRewardGrant(
    int DayIndex,
    SeasonalLoginRewardTrack Track,
    bool Retroactive,
    int TypeId,
    long Quantity);

/// <summary>Atomic seasonal claim request; the callback must revalidate it against current campaign and claim rows.</summary>
public sealed record SeasonalLoginCampaignClaimRequest(
    LoginRewardPlayer Player,
    int CampaignId,
    int ClaimDateOrdinal,
    IReadOnlyList<SeasonalRewardGrant> Rewards);

/// <summary>
/// Persistence boundary for authoritative login-campaign data and durable grants.
/// Host implementations must commit each claim ledger/progress mutation with its inventory or currency grants.
/// </summary>
public interface ILoginRewardCallbacks
{
    ValueTask<LoginCampaignSnapshot?> GetLoginCampaignStateAsync(
        LoginRewardPlayer player, CancellationToken cancellationToken = default);

    ValueTask<LoginCampaignClaimResult?> TryClaimLoginCampaignRewardAsync(
        LoginCampaignClaimRequest request, CancellationToken cancellationToken = default);

    ValueTask<SeasonalLoginCampaignSnapshot?> GetSeasonalLoginCampaignAsync(
        LoginRewardPlayer player, CancellationToken cancellationToken = default);

    ValueTask<bool> TryClaimSeasonalLoginCampaignAsync(
        SeasonalLoginCampaignClaimRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The local host has no configured login campaigns; return the client's no-campaign state.</summary>
public sealed class EmptyLoginRewardCallbacks : ILoginRewardCallbacks
{
    public ValueTask<LoginCampaignSnapshot?> GetLoginCampaignStateAsync(
        LoginRewardPlayer player, CancellationToken cancellationToken = default) => ValueTask.FromResult<LoginCampaignSnapshot?>(null);

    public ValueTask<LoginCampaignClaimResult?> TryClaimLoginCampaignRewardAsync(
        LoginCampaignClaimRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<LoginCampaignClaimResult?>(null);

    public ValueTask<SeasonalLoginCampaignSnapshot?> GetSeasonalLoginCampaignAsync(
        LoginRewardPlayer player, CancellationToken cancellationToken = default) => ValueTask.FromResult<SeasonalLoginCampaignSnapshot?>(null);

    public ValueTask<bool> TryClaimSeasonalLoginCampaignAsync(
        SeasonalLoginCampaignClaimRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}

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

/// <summary>Login campaign read and claim RPCs; no campaign is advertised without campaign data.</summary>
public sealed class LoginCampaignMgrService : IService
{
    private readonly ILoginRewardCallbacks _callbacks;
    private readonly Func<DateTimeOffset> _utcNow;

    public LoginCampaignMgrService(
        ILoginRewardCallbacks callbacks,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        _callbacks = callbacks;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name => "loginCampaignManager";

    public bool HasMethod(string method) => method is
        "GetActiveCampaigns" or
        "GetCampaignData" or
        "GetPlayerProgress" or
        "get_client_campaign_state" or
        "claim_reward";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetActiveCampaigns" => ValueTask.FromResult<PyValue>(new PyList([])),
        "GetCampaignData" or "GetPlayerProgress" => ValueTask.FromResult<PyValue>(new PyDict([])),
        "get_client_campaign_state" => GetClientCampaignStateAsync(call),
        "claim_reward" => ClaimRewardAsync(call),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };

    private async ValueTask<PyValue> GetClientCampaignStateAsync(ServiceCall call)
    {
        if (LoginRewardPlayer.From(call) is not { } player)
            return PyNone.Instance;

        var state = await _callbacks.GetLoginCampaignStateAsync(player, call.Cancellation);
        return state is { CampaignId: > 0 } ? state.ClientState : PyNone.Instance;
    }

    private async ValueTask<PyValue> ClaimRewardAsync(ServiceCall call)
    {
        if (LoginRewardPlayer.From(call) is not { } player)
            return PyNone.Instance;

        var state = await _callbacks.GetLoginCampaignStateAsync(player, call.Cancellation);
        if (state is not { CampaignId: > 0, NextRewardIndex: > 0 })
            return PyNone.Instance;

        // EVE wallclock values are FILETIME ticks; the client only enables the next claim after this timestamp.
        if (state.NextRewardTimestampFiletime >= _utcNow().UtcDateTime.ToFileTimeUtc())
            return PyNone.Instance;

        var request = new LoginCampaignClaimRequest(
            player,
            state.CampaignId,
            state.NextRewardIndex,
            state.NextRewardTimestampFiletime);
        var claimed = await _callbacks.TryClaimLoginCampaignRewardAsync(request, call.Cancellation);
        return claimed is null
            ? PyNone.Instance
            : PyTuple.Of(
                claimed.ItemReward,
                claimed.UpdatedItemProgress,
                claimed.ReceivedBucketTypeId,
                claimed.UpdatedBucketProgress);
    }
}

/// <summary>Seasonal login campaign state and reward-claim RPCs.</summary>
public sealed class SeasonalLoginCampaignMgrService : IService
{
    private readonly ILoginRewardCallbacks _callbacks;
    private readonly Func<DateTimeOffset> _utcNow;

    public SeasonalLoginCampaignMgrService(
        ILoginRewardCallbacks callbacks,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        _callbacks = callbacks;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name => "seasonalLoginCampaignManager";

    public bool HasMethod(string method) => method is
        "GetActiveCampaigns" or
        "GetCampaignData" or
        "GetPlayerProgress" or
        "get_active_campaign" or
        "get_claim_history" or
        "claim_reward";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetActiveCampaigns" or "GetCampaignData" => ValueTask.FromResult<PyValue>(new PyList([])),
        "GetPlayerProgress" => ValueTask.FromResult<PyValue>(new PyDict([])),
        "get_active_campaign" => GetActiveCampaignAsync(call),
        "get_claim_history" => GetClaimHistoryAsync(call),
        "claim_reward" => ClaimRewardAsync(call),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };

    private async ValueTask<PyValue> GetActiveCampaignAsync(ServiceCall call)
    {
        var state = await GetStateAsync(call);
        return state is not { CampaignId: > 0 }
            ? PyTuple.Of(PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance)
            : PyTuple.Of(
                new PyInt(state.CampaignId),
                new PyInt(state.EndDate.DayNumber + 1),
                new PyStr(state.TurnoverTimeUtc.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
                new PyInt(state.RemainingRewardDays));
    }

    private async ValueTask<PyValue> GetClaimHistoryAsync(ServiceCall call)
    {
        var state = await GetStateAsync(call);
        if (state is not { CampaignId: > 0 })
            return Py.Dict();

        return new PyDict([
            .. state.ClaimHistory.Select(claim => new KeyValuePair<PyValue, PyValue>(
                new PyInt(claim.DateOrdinal),
                new PyInt(claim.ClientCloneGrade)))
        ]);
    }

    private async ValueTask<PyValue> ClaimRewardAsync(ServiceCall call)
    {
        var player = LoginRewardPlayer.From(call);
        var state = await GetStateAsync(call);
        if (player is null || state is not { CampaignId: > 0 })
            return PyBool.False;

        var rewardDate = DateOnly.FromDateTime(_utcNow().UtcDateTime - state.TurnoverTimeUtc.ToTimeSpan());
        if (rewardDate > state.EndDate)
            return PyBool.False;

        var claimDateOrdinal = rewardDate.DayNumber + 1;
        var todaysClaim = state.ClaimHistory.FirstOrDefault(claim => claim.DateOrdinal == claimDateOrdinal);
        var priorClaims = state.ClaimHistory
            .Where(claim => claim.DateOrdinal < claimDateOrdinal)
            .OrderBy(claim => claim.DateOrdinal)
            .ToArray();
        var retroactiveOmegaAvailable = state.IsOmega &&
            state.RetroClaimEnabled &&
            (todaysClaim is { WasOmega: false } || priorClaims.Any(claim => !claim.WasOmega));

        if (todaysClaim is not null && !retroactiveOmegaAvailable)
            return PyBool.False;

        var grants = new List<SeasonalRewardGrant>();
        if (todaysClaim is null)
        {
            AddGrant(grants, state.AlphaRewards, priorClaims.Length, SeasonalLoginRewardTrack.Alpha, retroactive: false);
            if (state.IsOmega)
                AddGrant(grants, state.OmegaRewards, priorClaims.Length, SeasonalLoginRewardTrack.Omega, retroactive: false);
        }
        else if (state.IsOmega && !todaysClaim.WasOmega)
        {
            AddGrant(grants, state.OmegaRewards, priorClaims.Length, SeasonalLoginRewardTrack.Omega, retroactive: false);
        }

        if (state.IsOmega && state.RetroClaimEnabled)
        {
            for (var dayIndex = 0; dayIndex < priorClaims.Length; dayIndex++)
            {
                if (!priorClaims[dayIndex].WasOmega)
                    AddGrant(grants, state.OmegaRewards, dayIndex, SeasonalLoginRewardTrack.Omega, retroactive: true);
            }
        }

        if (grants.Count == 0)
            return PyBool.False;

        var request = new SeasonalLoginCampaignClaimRequest(
            player,
            state.CampaignId,
            claimDateOrdinal,
            grants);
        var claimed = await _callbacks.TryClaimSeasonalLoginCampaignAsync(request, call.Cancellation);
        return claimed ? PyBool.True : PyBool.False;
    }

    private async ValueTask<SeasonalLoginCampaignSnapshot?> GetStateAsync(ServiceCall call)
    {
        if (LoginRewardPlayer.From(call) is not { } player)
            return null;
        return await _callbacks.GetSeasonalLoginCampaignAsync(player, call.Cancellation);
    }

    private static void AddGrant(
        ICollection<SeasonalRewardGrant> grants,
        IReadOnlyDictionary<int, SeasonalLoginCampaignReward> catalog,
        int dayIndex,
        SeasonalLoginRewardTrack track,
        bool retroactive)
    {
        if (!catalog.TryGetValue(dayIndex, out var reward) || reward.TypeId <= 0 || reward.Quantity <= 0)
            return;
        grants.Add(new SeasonalRewardGrant(dayIndex, track, retroactive, reward.TypeId, reward.Quantity));
    }
}
