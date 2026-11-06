using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class RewardsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("GetDelayedRewardsByGroupIDs", "dict")]
    [InlineData("GetRewardLPLogs", "list")]
    public async Task Reward_manager_non_campaign_reads_keep_their_wire_shape(string method, string shape)
    {
        var service = new RewardsService();
        var result = await service.CallAsync(Call(method, new PyList([])));
        PyValue expected = shape == "dict" ? Py.Dict() : new PyList([]);
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(result));
    }

    [Theory]
    [InlineData("loginCampaignManager", "GetActiveCampaigns", "list")]
    [InlineData("loginCampaignManager", "GetCampaignData", "dict")]
    [InlineData("loginCampaignManager", "GetPlayerProgress", "dict")]
    [InlineData("seasonalLoginCampaignManager", "GetActiveCampaigns", "list")]
    [InlineData("seasonalLoginCampaignManager", "GetCampaignData", "list")]
    [InlineData("seasonalLoginCampaignManager", "GetPlayerProgress", "dict")]
    public async Task Campaign_catalog_rpcs_keep_the_reference_empty_shapes(string serviceName, string method, string shape)
    {
        var callbacks = new EmptyLoginRewardCallbacks();
        IService service = serviceName == "loginCampaignManager"
            ? new LoginCampaignMgrService(callbacks, () => Now)
            : new SeasonalLoginCampaignMgrService(callbacks, () => Now);

        Assert.True(service.HasMethod(method));
        var result = await service.CallAsync(Call(method));
        PyValue expected = shape == "list" ? new PyList([]) : new PyDict([]);
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(result));
    }

    [Fact]
    public async Task Login_campaign_state_returns_the_backend_snapshot_without_inventing_data()
    {
        var backend = new Backend { LoginState = LoginState(Time(Now.AddMinutes(-1))) };
        var service = new LoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("get_client_campaign_state"));

        Assert.Equal(MarshalCodec.Encode(backend.LoginState!.ClientState), MarshalCodec.Encode(result));
        Assert.True(service.HasMethod("claim_reward"));
    }

    [Fact]
    public void Login_campaign_manager_requires_authoritative_callbacks()
    {
        Assert.Throws<ArgumentNullException>(() => new LoginCampaignMgrService(null!));
    }

    [Fact]
    public async Task Login_campaign_claim_waits_until_the_authoritative_next_reward_time()
    {
        var backend = new Backend { LoginState = LoginState(Time(Now.AddSeconds(1))) };
        var service = new LoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.IsType<PyNone>(result);
        Assert.Equal(0, backend.LoginClaimCalls);
    }

    [Fact]
    public async Task Login_campaign_claim_returns_the_reference_tuple_after_atomic_grant_callback()
    {
        var claimResult = new LoginCampaignClaimResult(
            Py.KeyVal(("typeID", new PyInt(34)), ("qty", new PyInt(3))),
            Py.KeyVal(("next_reward_index", new PyInt(4))),
            new PyInt(9001),
            Py.KeyVal(("progress", new PyInt(1))));
        var backend = new Backend
        {
            LoginState = LoginState(Time(Now.AddMinutes(-1))),
            LoginClaimResult = claimResult
        };
        var service = new LoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        var expected = PyTuple.Of(
            claimResult.ItemReward,
            claimResult.UpdatedItemProgress,
            claimResult.ReceivedBucketTypeId,
            claimResult.UpdatedBucketProgress);
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(result));
        Assert.Equal(1, backend.LoginClaimCalls);
        Assert.Equal(123L, backend.LastLoginClaim!.Player.UserId);
        Assert.Equal(456L, backend.LastLoginClaim.Player.CharacterId);
        Assert.Equal(41, backend.LastLoginClaim.CampaignId);
        Assert.Equal(3, backend.LastLoginClaim.ExpectedNextRewardIndex);
        Assert.Equal(Time(Now.AddMinutes(-1)), backend.LastLoginClaim.ExpectedNextRewardTimestampFiletime);
    }

    [Fact]
    public async Task Login_campaign_claim_returns_none_when_atomic_grant_is_rejected()
    {
        var backend = new Backend { LoginState = LoginState(Time(Now.AddMinutes(-1))) };
        var service = new LoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.IsType<PyNone>(result);
        Assert.Equal(1, backend.LoginClaimCalls);
    }

    [Fact]
    public async Task Missing_login_campaign_is_none_and_cannot_be_claimed()
    {
        var backend = new Backend();
        var service = new LoginCampaignMgrService(backend, () => Now);

        var state = await service.CallAsync(Call("get_client_campaign_state"));
        var claim = await service.CallAsync(Call("claim_reward"));

        Assert.IsType<PyNone>(state);
        Assert.IsType<PyNone>(claim);
        Assert.Equal(0, backend.LoginClaimCalls);
    }

    [Fact]
    public async Task Seasonal_campaign_state_and_claim_history_use_authoritative_callback_values()
    {
        var backend = new Backend { SeasonalState = SeasonalState() };
        var service = new SeasonalLoginCampaignMgrService(backend, () => Now);

        var active = await service.CallAsync(Call("get_active_campaign"));
        var history = await service.CallAsync(Call("get_claim_history"));
        var expectedActive = PyTuple.Of(
            new PyInt(77),
            new PyInt(Ordinal(new DateOnly(2026, 10, 10))),
            new PyStr("08:00:00"),
            new PyInt(4));
        var expectedHistory = new PyDict([
            new(new PyInt(Ordinal(new DateOnly(2026, 10, 3))), new PyInt(5)),
            new(new PyInt(Ordinal(new DateOnly(2026, 10, 4))), new PyInt(9))
        ]);

        Assert.Equal(MarshalCodec.Encode(expectedActive), MarshalCodec.Encode(active));
        Assert.Equal(MarshalCodec.Encode(expectedHistory), MarshalCodec.Encode(history));
    }

    [Fact]
    public void Seasonal_login_campaign_manager_requires_authoritative_callbacks()
    {
        Assert.Throws<ArgumentNullException>(() => new SeasonalLoginCampaignMgrService(null!));
    }

    [Fact]
    public async Task Seasonal_claim_grants_current_tracks_and_only_missing_omega_days()
    {
        var backend = new Backend { SeasonalState = SeasonalState() };
        var service = new SeasonalLoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.Equal(MarshalCodec.Encode(PyBool.True), MarshalCodec.Encode(result));
        Assert.Equal(1, backend.SeasonalClaimCalls);
        Assert.Equal(123L, backend.LastSeasonalClaim!.Player.UserId);
        Assert.Equal(77, backend.LastSeasonalClaim.CampaignId);
        Assert.Equal(Ordinal(new DateOnly(2026, 10, 5)), backend.LastSeasonalClaim.ClaimDateOrdinal);
        Assert.Contains(new SeasonalRewardGrant(2, SeasonalLoginRewardTrack.Alpha, false, 102, 12), backend.LastSeasonalClaim.Rewards);
        Assert.Contains(new SeasonalRewardGrant(2, SeasonalLoginRewardTrack.Omega, false, 202, 2), backend.LastSeasonalClaim.Rewards);
        Assert.Contains(new SeasonalRewardGrant(0, SeasonalLoginRewardTrack.Omega, true, 200, 2), backend.LastSeasonalClaim.Rewards);
        Assert.DoesNotContain(backend.LastSeasonalClaim.Rewards, reward =>
            reward.DayIndex == 1 && reward.Track == SeasonalLoginRewardTrack.Omega);
    }

    [Fact]
    public async Task Seasonal_turnover_uses_the_previous_utc_reward_date_before_dateline()
    {
        var beforeTurnover = new DateTimeOffset(2026, 10, 5, 7, 59, 0, TimeSpan.Zero);
        var state = SeasonalState() with
        {
            ClaimHistory = [new(Ordinal(new DateOnly(2026, 10, 3)), 5, false)]
        };
        var backend = new Backend { SeasonalState = state };
        var service = new SeasonalLoginCampaignMgrService(backend, () => beforeTurnover);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.Equal(MarshalCodec.Encode(PyBool.True), MarshalCodec.Encode(result));
        Assert.Equal(Ordinal(new DateOnly(2026, 10, 4)), backend.LastSeasonalClaim!.ClaimDateOrdinal);
    }

    [Fact]
    public async Task Seasonal_claim_rejects_a_repeat_today_without_omega_retro_eligibility()
    {
        var state = SeasonalState() with
        {
            IsOmega = false,
            RetroClaimEnabled = false,
            ClaimHistory =
            [
                new(Ordinal(new DateOnly(2026, 10, 5)), 5, false)
            ]
        };
        var backend = new Backend { SeasonalState = state };
        var service = new SeasonalLoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.Equal(MarshalCodec.Encode(PyBool.False), MarshalCodec.Encode(result));
        Assert.Equal(0, backend.SeasonalClaimCalls);
    }

    [Fact]
    public async Task Seasonal_claim_returns_false_when_host_transaction_does_not_commit()
    {
        var backend = new Backend { SeasonalState = SeasonalState(), SeasonalClaimResult = false };
        var service = new SeasonalLoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.Equal(MarshalCodec.Encode(PyBool.False), MarshalCodec.Encode(result));
        Assert.Equal(1, backend.SeasonalClaimCalls);
    }

    [Fact]
    public async Task Seasonal_campaign_without_authoritative_active_data_returns_no_campaign_and_no_claim()
    {
        var backend = new Backend();
        var service = new SeasonalLoginCampaignMgrService(backend, () => Now);

        var active = await service.CallAsync(Call("get_active_campaign"));
        var claim = await service.CallAsync(Call("claim_reward"));

        Assert.Equal(MarshalCodec.Encode(PyTuple.Of(
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance)), MarshalCodec.Encode(active));
        Assert.Equal(MarshalCodec.Encode(PyBool.False), MarshalCodec.Encode(claim));
        Assert.Equal(0, backend.SeasonalClaimCalls);
    }

    [Fact]
    public async Task Seasonal_claim_ignores_future_history_when_selecting_reward_days()
    {
        var state = SeasonalState() with
        {
            ClaimHistory =
            [
                new(Ordinal(new DateOnly(2026, 10, 3)), 5, false),
                new(Ordinal(new DateOnly(2026, 10, 6)), 5, false)
            ]
        };
        var backend = new Backend { SeasonalState = state };
        var service = new SeasonalLoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.Equal(MarshalCodec.Encode(PyBool.True), MarshalCodec.Encode(result));
        Assert.Contains(new SeasonalRewardGrant(1, SeasonalLoginRewardTrack.Alpha, false, 101, 11), backend.LastSeasonalClaim!.Rewards);
        Assert.Contains(new SeasonalRewardGrant(1, SeasonalLoginRewardTrack.Omega, false, 201, 2), backend.LastSeasonalClaim.Rewards);
        Assert.Contains(new SeasonalRewardGrant(0, SeasonalLoginRewardTrack.Omega, true, 200, 2), backend.LastSeasonalClaim.Rewards);
        Assert.DoesNotContain(backend.LastSeasonalClaim.Rewards, reward => reward.DayIndex == 2);
        Assert.DoesNotContain(backend.LastSeasonalClaim.Rewards, reward => reward.Retroactive && reward.DayIndex == 1);
    }

    [Fact]
    public async Task Seasonal_claim_after_campaign_end_is_rejected_before_host_claim()
    {
        var backend = new Backend
        {
            SeasonalState = SeasonalState() with { EndDate = new DateOnly(2026, 10, 4) }
        };
        var service = new SeasonalLoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.Equal(MarshalCodec.Encode(PyBool.False), MarshalCodec.Encode(result));
        Assert.Equal(0, backend.SeasonalClaimCalls);
    }

    [Fact]
    public async Task Login_campaign_claim_is_not_ready_at_the_exact_reward_timestamp()
    {
        var backend = new Backend { LoginState = LoginState(Time(Now)) };
        var service = new LoginCampaignMgrService(backend, () => Now);

        var result = await service.CallAsync(Call("claim_reward"));

        Assert.IsType<PyNone>(result);
        Assert.Equal(0, backend.LoginClaimCalls);
    }

    private static LoginCampaignSnapshot LoginState(long nextRewardTimestamp) =>
        new(41,
            Py.KeyVal(
                ("static_data", Py.KeyVal(("campaign_id", new PyInt(41)))),
                ("item_progress", Py.KeyVal(
                    ("num_claimed_rewards", new PyInt(2)),
                    ("next_reward_index", new PyInt(3)),
                    ("next_reward_timestamp", new PyLong(nextRewardTimestamp))))),
            3, nextRewardTimestamp);

    private static SeasonalLoginCampaignSnapshot SeasonalState()
    {
        var alpha = new Dictionary<int, SeasonalLoginCampaignReward>
        {
            [0] = new(100, 10),
            [1] = new(101, 11),
            [2] = new(102, 12)
        };
        var omega = new Dictionary<int, SeasonalLoginCampaignReward>
        {
            [0] = new(200, 2),
            [1] = new(201, 2),
            [2] = new(202, 2)
        };
        return new(77,
            new DateOnly(2026, 10, 10),
            new TimeOnly(8, 0),
            4,
            true,
            true,
            alpha,
            omega,
            [
                new(Ordinal(new DateOnly(2026, 10, 3)), 5, false),
                new(Ordinal(new DateOnly(2026, 10, 4)), 9, true)
            ]);
    }

    private static int Ordinal(DateOnly date) => date.DayNumber + 1;

    private static long Time(DateTimeOffset value) => value.UtcDateTime.ToFileTimeUtc();

    private static ServiceCall Call(string method, PyValue? argument = null) => new()
    {
        Method = method,
        Args = argument is null ? [] : [argument],
        Session = new ClientSession { UserId = 123, CharacterId = 456 }
    };

    private sealed class Backend : ILoginRewardCallbacks
    {
        public LoginCampaignSnapshot? LoginState { get; init; }
        public SeasonalLoginCampaignSnapshot? SeasonalState { get; init; }
        public LoginCampaignClaimResult? LoginClaimResult { get; init; }
        public int LoginClaimCalls { get; private set; }
        public LoginCampaignClaimRequest? LastLoginClaim { get; private set; }
        public int SeasonalClaimCalls { get; private set; }
        public bool SeasonalClaimResult { get; init; } = true;
        public SeasonalLoginCampaignClaimRequest? LastSeasonalClaim { get; private set; }

        public ValueTask<LoginCampaignSnapshot?> GetLoginCampaignStateAsync(
            LoginRewardPlayer player, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LoginState);

        public ValueTask<LoginCampaignClaimResult?> TryClaimLoginCampaignRewardAsync(
            LoginCampaignClaimRequest request, CancellationToken cancellationToken = default)
        {
            LoginClaimCalls++;
            LastLoginClaim = request;
            return ValueTask.FromResult(LoginClaimResult);
        }

        public ValueTask<SeasonalLoginCampaignSnapshot?> GetSeasonalLoginCampaignAsync(
            LoginRewardPlayer player, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SeasonalState);

        public ValueTask<bool> TryClaimSeasonalLoginCampaignAsync(
            SeasonalLoginCampaignClaimRequest request, CancellationToken cancellationToken = default)
        {
            SeasonalClaimCalls++;
            LastSeasonalClaim = request;
            return ValueTask.FromResult(SeasonalClaimResult);
        }
    }
}
