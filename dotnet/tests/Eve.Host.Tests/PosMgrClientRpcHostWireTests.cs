using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task PosMgr_missing_client_methods_are_dispatched_through_the_registered_host_service()
    {
        var received = new List<(string Method, IReadOnlyList<PyValue> Args)>();
        Start(posMgrOperation: call =>
        {
            received.Add((call.Method, call.Args));
            return ValueTask.FromResult<PyValue>(new PyStr(call.Method));
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        (string Method, PyValue[] Args)[] calls =
        [
            ("AnchorOrbital", [new PyLong(9_871_005_101)]),
            ("ChangeStructureProvisionType", [new PyLong(9_871_005_101), new PyLong(9_871_005_102), PyNone.Instance]),
            ("GetConsumptionFactor", [new PyLong(9_871_005_101)]),
            ("GetMoonForTower", [new PyLong(9_871_005_101)]),
            ("GetMoonProcessInfoForTower", [new PyLong(9_871_005_101)]),
            ("GetSiloCapacityByItemID", [new PyLong(9_871_005_102)]),
            ("GetStarbasePermissions", [new PyLong(9_871_005_101)]),
            ("GetTowerNotificationSettings", [new PyLong(9_871_005_101)]),
            ("GetTowerSentrySettings", [new PyLong(9_871_005_101)]),
            ("LinkResourceForTower", [new PyLong(9_871_005_101), new PyList([])]),
            ("OnlineOrbital", [new PyLong(9_871_005_101)]),
            ("RunMoonProcessCycleforTower", [new PyLong(9_871_005_101)]),
            ("SetStarbasePermissions", [new PyLong(9_871_005_101), new PyDict([]), new PyList([])]),
            ("SetTowerNotifications", [new PyLong(9_871_005_101), PyBool.True, PyBool.False]),
            ("SetTowerPassword", [new PyLong(9_871_005_101), new PyBytes("secret"u8.ToArray()), PyBool.True, PyBool.False]),
            ("SetTowerSentrySettings", [new PyLong(9_871_005_101), PyNone.Instance, PyNone.Instance, PyBool.False, PyBool.True, PyBool.False]),
            ("UnanchorOrbital", [new PyLong(9_871_005_101)]),
        ];

        uint callId = 1;
        foreach (var (method, args) in calls)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, callId++, "posMgr", method, new PyTuple(args), MachoVersion()))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Envelope(reply).Type);
            Assert.Equal(method, T(Result(MarshalCodec.Decode(reply.Payload))));
        }

        Assert.Equal(calls.Select(call => call.Method), received.Select(call => call.Method));
        Assert.Equal(calls.Select(call => call.Args), received.Select(call => call.Args));
    }
}
