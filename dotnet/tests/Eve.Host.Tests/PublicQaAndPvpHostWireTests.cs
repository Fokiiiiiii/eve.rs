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
    }

    [Fact]
    public async Task Pvp_filament_reads_and_notifications_keep_v24_shapes_over_host_wire()
    {
        Start(configure: registry => registry.Register(new PvpFilamentMgrService()));
        var (front, open) = await OpenFrontAsync();

        Assert.Empty(Assert.IsType<PyDict>(await HostRpcAsync(
            front, open, 1, "pvpFilamentMgr", "GetAllEvents")).Entries);
        Assert.Empty(Assert.IsType<PyDict>(await HostRpcAsync(
            front, open, 2, "pvpFilamentMgr", "GetActiveEvents")).Entries);
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 3, "pvpFilamentMgr", "GetMostRecentEvent"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 4, "pvpFilamentMgr", "GetNextEventDate"));

        void AssertDictionary(PyDict expected, PyDict actual)
        {
            Assert.Equal(expected.Entries.Count, actual.Entries.Count);
            foreach (var expectedEntry in expected.Entries)
            {
                var actualValue = Assert.Single(actual.Entries,
                    entry => entry.Key.Equals(expectedEntry.Key)).Value;
                if (expectedEntry.Value is PyDict expectedNested)
                    AssertDictionary(expectedNested, Assert.IsType<PyDict>(actualValue));
                else
                    Assert.Equal(expectedEntry.Value, actualValue);
            }
        }

        async Task AssertNotificationAsync(long callId, string name, PyDict expected)
        {
            var call = name == "OnPVPFilamentsLeaderboard" ? "GetLeaderboard" : "GetCharacterStatistics";
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "pvpFilamentMgr", call,
                    Py.Tuple(new PyInt(7), new PyInt(9)), TransportKwargs()))));

            var notificationRaw = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var notification = Assert.IsType<PyObject>(notificationRaw);
            Assert.Equal(ClientNotification.ClassName, Encoding.UTF8.GetString(Assert.IsType<PyBytes>(notification.Name).Value.Span));
            var packetItems = Assert.IsType<PyTuple>(notification.Args).Items;
            var destination = Assert.IsType<PyObject>(packetItems[2]);
            var destinationItems = Assert.IsType<PyTuple>(destination.Args).Items;
            Assert.Equal(name, Encoding.UTF8.GetString(Assert.IsType<PyBytes>(destinationItems[1]).Value.Span));
            Assert.Equal("clientID", Assert.IsType<PyStr>(destinationItems[3]).Value);
            var envelope = Assert.IsType<PyTuple>(packetItems[4]).Items[0];
            var payload = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(envelope).Items[1]);
            var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(payload.Value));
            var eventArgs = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(decoded.Items[1]).Items[1]);
            AssertDictionary(expected, Assert.IsType<PyDict>(Assert.Single(eventArgs.Items)));

            var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
            Assert.Same(PyNone.Instance, Result(response));
        }

        await AssertNotificationAsync(5, "OnPVPFilamentsLeaderboard", new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("matchTypeID")), new PyInt(7)),
            new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("scheduleID")), new PyInt(9)),
            new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("entries")), new PyList([])),
        ]));
        await AssertNotificationAsync(6, "OnPVPFilamentsCharacterStatistics", new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("matchTypeID")), new PyInt(7)),
            new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("scheduleID")), new PyInt(9)),
            new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("statistics")), new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("rank")), new PyInt(0)),
                new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("wins")), new PyInt(0)),
                new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("losses")), new PyInt(0)),
                new KeyValuePair<PyValue, PyValue>(new PyBytes(Encoding.UTF8.GetBytes("draws")), new PyInt(0)),
            ])),
        ]));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 7, "pvpFilamentMgr", "JoinPVPQueue",
                Py.Tuple(new PyInt(44), new PyInt(55)), TransportKwargs()))));
        var error = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(error.Payload))).Type);
        Assert.Contains("PVPJumpError", System.Text.Encoding.ASCII.GetString(error.Payload.Span));
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 8, "pvpFilamentMgr", "LeavePVPQueue", new PyInt(44), new PyInt(55)));
    }
}
