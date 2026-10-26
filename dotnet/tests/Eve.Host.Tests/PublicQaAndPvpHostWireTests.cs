using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Public_qa_calls_keep_the_client_wire_shapes_through_host_dispatch()
    {
        Start(sessionOpened: session => session.Set("accountRole", new PyLong(0)));
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 1, "publicQaToolsServer", "MoveMeTo", new PyStr("Jita")));
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 2, "publicQaToolsServer", "SlashCmd", new PyStr("/heal")));
        Assert.Same(PyBool.False, await HostRpcAsync(
            front, open, 3, "publicQaToolsServer", "CanGiveItemForMultifit"));
    }

    [Fact]
    public async Task Abyssal_pvp_end_gate_rejects_without_an_active_origin_trace_through_host()
    {
        Start(configure: registry => registry.Register(new PvpFilamentMgrService()));
        var (front, open) = await OpenFrontAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "pvpFilamentMgr", "AbyssalPVPEndGateActivation",
                Py.Tuple(new PyLong(9001)), TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
        Assert.Contains("DeniedTargetAttemptFailed", Encoding.ASCII.GetString(send.Payload.Span));
    }

    [Fact]
    public async Task Delayed_incursion_rewards_return_the_rows_consumed_by_the_journal_through_host_dispatch()
    {
        IReadOnlyList<long> requested = [];
        var service = new RewardsService((groupIds, _) =>
        {
            requested = groupIds;
            return Task.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long> { [9001] = 250_000 });
        });
        Start(configure: registry => registry.Register(service));
        var (front, open) = await OpenFrontAsync();

        var rewards = Assert.IsType<PyDict>(await HostRpcAsync(
            front, open, 1, "rewardMgr", "GetDelayedRewardsByGroupIDs",
            new PyList([new PyInt(9001), new PyInt(9002)])));
        Assert.Equal([9001L, 9002L], requested);
        var entry = Assert.Single(rewards.Entries);
        Assert.Equal(9001, WireInteger(entry.Key));
        var rows = Assert.IsType<PyList>(entry.Value);
        Assert.Equal(250_000, WireInteger(WireField(Assert.Single(rows.Items), "rewardQuantity")));

        var lpLogs = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 2, "rewardMgr", "GetRewardLPLogs"));
        Assert.Empty(lpLogs.Items);
    }
}
