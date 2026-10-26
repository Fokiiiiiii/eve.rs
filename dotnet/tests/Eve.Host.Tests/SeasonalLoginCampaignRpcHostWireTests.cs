using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Seasonal_login_campaign_reads_and_claim_follow_client_wire_contract()
    {
        var now = DateTimeOffset.UtcNow;
        var callbacks = new SeasonalRewardCallbacks();
        Start(
            configure: registry => registry.Register(new SeasonalLoginCampaignMgrService(callbacks, () => now)),
            sessionOpened: session =>
            {
                session.UserId = 9001;
                session.CharacterId = 4242;
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Call(string method, uint callId)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                callId,
                "seasonalLoginCampaignManager",
                method,
                new PyTuple([]),
                MachoVersion()))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(reply.Payload);
            var envelope = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.CallRsp, envelope.Type);
            return Result(decoded)!;
        }

        var inactive = Assert.IsType<PyTuple>(await Call("get_active_campaign", 1));
        Assert.Equal(4, inactive.Items.Count);
        Assert.All(inactive.Items, item => Assert.Same(PyNone.Instance, item));
        Assert.Empty(Assert.IsType<PyDict>(await Call("get_claim_history", 2)).Entries);
        Assert.Equal(PyBool.False, await Call("claim_reward", 3));

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var yesterdayOrdinal = today.AddDays(-1).DayNumber + 1;
        callbacks.State = new SeasonalLoginCampaignSnapshot(
            2356,
            today.AddDays(1),
            TimeOnly.MinValue,
            3,
            false,
            true,
            new Dictionary<int, SeasonalLoginCampaignReward>
            {
                [1] = new(49810, 1),
            },
            new Dictionary<int, SeasonalLoginCampaignReward>(),
            [new SeasonalLoginCampaignClaimedDay(yesterdayOrdinal, 0, false)]);

        var active = Assert.IsType<PyTuple>(await Call("get_active_campaign", 4));
        Assert.Equal(new PyInt(2356), active.Items[0]);
        Assert.Equal(new PyInt(today.AddDays(1).DayNumber + 1), active.Items[1]);
        var turnoverTime = Assert.IsType<PyBytes>(active.Items[2]);
        Assert.Equal("00:00:00", System.Text.Encoding.ASCII.GetString(turnoverTime.Value.Span));
        Assert.Equal(new PyInt(3), active.Items[3]);

        var history = Assert.IsType<PyDict>(await Call("get_claim_history", 5));
        var historyEntry = Assert.Single(history.Entries);
        Assert.Equal(new PyInt(yesterdayOrdinal), historyEntry.Key);
        Assert.Equal(new PyInt(0), historyEntry.Value);

        Assert.Equal(PyBool.True, await Call("claim_reward", 6));
        Assert.Equal(new LoginRewardPlayer(9001, 4242), callbacks.LastPlayer);
        Assert.Equal(new LoginRewardPlayer(9001, 4242), callbacks.LastClaim?.Player);
        Assert.Equal(2356, callbacks.LastClaim?.CampaignId);
        Assert.Equal(today.DayNumber + 1, callbacks.LastClaim?.ClaimDateOrdinal);
        var reward = Assert.Single(callbacks.LastClaim!.Rewards);
        Assert.Equal(new SeasonalRewardGrant(1, SeasonalLoginRewardTrack.Alpha, false, 49810, 1), reward);

        callbacks.ClaimSucceeds = false;
        Assert.Equal(PyBool.False, await Call("claim_reward", 7));
    }

    private sealed class SeasonalRewardCallbacks : ILoginRewardCallbacks
    {
        public SeasonalLoginCampaignSnapshot? State { get; set; }
        public bool ClaimSucceeds { get; set; } = true;
        public LoginRewardPlayer? LastPlayer { get; private set; }
        public SeasonalLoginCampaignClaimRequest? LastClaim { get; private set; }

        public ValueTask<LoginCampaignSnapshot?> GetLoginCampaignStateAsync(
            LoginRewardPlayer player,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<LoginCampaignSnapshot?>(null);

        public ValueTask<LoginCampaignClaimResult?> TryClaimLoginCampaignRewardAsync(
            LoginCampaignClaimRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<LoginCampaignClaimResult?>(null);

        public ValueTask<SeasonalLoginCampaignSnapshot?> GetSeasonalLoginCampaignAsync(
            LoginRewardPlayer player,
            CancellationToken cancellationToken = default)
        {
            LastPlayer = player;
            return ValueTask.FromResult(State);
        }

        public ValueTask<bool> TryClaimSeasonalLoginCampaignAsync(
            SeasonalLoginCampaignClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            LastClaim = request;
            return ValueTask.FromResult(ClaimSucceeds);
        }
    }
}
