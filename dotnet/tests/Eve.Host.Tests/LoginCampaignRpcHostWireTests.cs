using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Login_campaign_state_and_claim_follow_client_wire_contract()
    {
        var now = DateTimeOffset.UtcNow;
        var itemReward = Py.KeyVal(("typeID", new PyInt(49810)), ("quantity", new PyInt(1)));
        var currentProgress = Py.KeyVal(
            ("next_reward_timestamp", new PyLong(now.AddMinutes(-1).UtcDateTime.ToFileTimeUtc())),
            ("num_claimed_rewards", new PyInt(0)),
            ("next_reward_index", new PyInt(1)));
        var currentBuckets = Py.KeyVal(("4", new PyInt(0)));
        var updatedProgress = Py.KeyVal(
            ("next_reward_timestamp", new PyLong(now.AddDays(1).UtcDateTime.ToFileTimeUtc())),
            ("num_claimed_rewards", new PyInt(1)),
            ("next_reward_index", new PyInt(2)));
        var updatedBuckets = Py.KeyVal(("4", new PyInt(1)));
        var state = Py.KeyVal(
            ("static_data", Py.KeyVal(("campaign_id", new PyInt(2187)))),
            ("item_progress", currentProgress),
            ("bucket_progress", currentBuckets));
        var callbacks = new LoginRewardRpcCallbacks
        {
            State = new LoginCampaignSnapshot(
                2187,
                state,
                1,
                now.AddMinutes(-1).UtcDateTime.ToFileTimeUtc()),
            ClaimResult = new LoginCampaignClaimResult(
                itemReward,
                updatedProgress,
                PyNone.Instance,
                updatedBuckets),
        };
        Start(
            configure: registry => registry.Register(new LoginCampaignMgrService(callbacks, () => now)),
            sessionOpened: session =>
            {
                session.UserId = 9001;
                session.CharacterId = 4242;
                callbacks.BeforeSuccessfulClaim = () =>
                    session.SendNotification("OnRedeemingQueueUpdated", "clientID");
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
                "loginCampaignManager",
                method,
                new PyTuple([]),
                MachoVersion()))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(reply.Payload);
            var envelope = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.CallRsp, envelope.Type);
            return Result(decoded)!;
        }

        var stateReply = Assert.IsType<PyObject>(await Call("get_client_campaign_state", 1));
        Assert.Equal(new PyInt(2187), Field(Field(stateReply, "static_data"), "campaign_id"));
        var stateProgress = Field(stateReply, "item_progress");
        Assert.Equal(
            Field(currentProgress, "next_reward_timestamp"),
            Field(stateProgress, "next_reward_timestamp"));
        Assert.Equal(new PyInt(0), Field(stateProgress, "num_claimed_rewards"));
        Assert.Equal(new PyInt(1), Field(stateProgress, "next_reward_index"));
        Assert.Equal(new PyInt(0), Field(Field(stateReply, "bucket_progress"), "4"));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open,
            2,
            "loginCampaignManager",
            "claim_reward",
            new PyTuple([]),
            MachoVersion()))));
        var notification = Assert.IsType<Send>(await front.ReceiveAsync());
        var notificationPacket = Assert.IsType<MachoPacket>(
            MachoPacket.Decode(MarshalCodec.Decode(notification.Payload)));
        Assert.Equal(MachoMessage.Notification, notificationPacket.Type);
        Assert.Equal("OnRedeemingQueueUpdated", notificationPacket.Dest.BroadcastId);
        Assert.Equal("clientID", notificationPacket.Dest.IdType);

        var claimReply = Assert.IsType<Send>(await front.ReceiveAsync());
        var claimDecoded = MarshalCodec.Decode(claimReply.Payload);
        var claimEnvelope = Assert.IsType<MachoPacket>(MachoPacket.Decode(claimDecoded));
        Assert.Equal(MachoMessage.CallRsp, claimEnvelope.Type);
        var result = Assert.IsType<PyTuple>(Result(claimDecoded));
        Assert.Equal(4, result.Items.Count);
        Assert.Equal(new PyInt(49810), Field(result.Items[0], "typeID"));
        Assert.Equal(new PyInt(1), Field(result.Items[0], "quantity"));
        Assert.Equal(new PyInt(1), Field(result.Items[1], "num_claimed_rewards"));
        Assert.Equal(new PyInt(2), Field(result.Items[1], "next_reward_index"));
        Assert.Equal(PyNone.Instance, result.Items[2]);
        Assert.Equal(new PyInt(1), Field(result.Items[3], "4"));
        Assert.Equal(new LoginRewardPlayer(9001, 4242), callbacks.LastPlayer);
        Assert.Equal(new LoginCampaignClaimRequest(
            new LoginRewardPlayer(9001, 4242),
            2187,
            1,
            now.AddMinutes(-1).UtcDateTime.ToFileTimeUtc()), callbacks.LastRequest);

        callbacks.ClaimResult = null;
        Assert.Equal(PyNone.Instance, await Call("claim_reward", 3));
    }

    private static PyValue Field(PyValue value, string name)
    {
        var obj = Assert.IsType<PyObject>(value);
        var dict = Assert.IsType<PyDict>(obj.Args);
        return Assert.Single(dict.Entries, entry => WireString(entry.Key) == name).Value;
    }

    private static string WireString(PyValue value) => value switch
    {
        PyBytes bytes => System.Text.Encoding.ASCII.GetString(bytes.Value.Span),
        PyStr text => text.Value,
        _ => throw new InvalidOperationException($"Expected a string key; got {value.GetType().Name}."),
    };

    private sealed class LoginRewardRpcCallbacks : ILoginRewardCallbacks
    {
        public LoginCampaignSnapshot? State { get; init; }
        public LoginCampaignClaimResult? ClaimResult { get; set; }
        public LoginRewardPlayer? LastPlayer { get; private set; }
        public LoginCampaignClaimRequest? LastRequest { get; private set; }
        public Action? BeforeSuccessfulClaim { get; set; }

        public ValueTask<LoginCampaignSnapshot?> GetLoginCampaignStateAsync(
            LoginRewardPlayer player,
            CancellationToken cancellationToken = default)
        {
            LastPlayer = player;
            return ValueTask.FromResult(State);
        }

        public ValueTask<LoginCampaignClaimResult?> TryClaimLoginCampaignRewardAsync(
            LoginCampaignClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (ClaimResult is not null)
                BeforeSuccessfulClaim?.Invoke();
            return ValueTask.FromResult(ClaimResult);
        }

        public ValueTask<SeasonalLoginCampaignSnapshot?> GetSeasonalLoginCampaignAsync(
            LoginRewardPlayer player,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<SeasonalLoginCampaignSnapshot?>(null);

        public ValueTask<bool> TryClaimSeasonalLoginCampaignAsync(
            SeasonalLoginCampaignClaimRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }
}
