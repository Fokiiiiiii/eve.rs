using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Activity_mgr_persists_checkbox_values_through_composed_host_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var activityMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("activityMgr"));

        StartComposed(registry => registry.Register(activityMgr), session => session.CharacterId = character);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var notifications = new List<string>();
        long callId = 0;
        async Task<PyValue> Exchange(string method, PyValue[] args)
        {
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "activityMgr", method, new PyTuple(args), kwargs))));
            while (true)
            {
                var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var packet = MachoPacket.Decode(response);
                if (packet?.Type == MachoMessage.Notification)
                {
                    notifications.Add(packet.Dest.BroadcastId ?? string.Empty);
                    continue;
                }
                return response;
            }
        }

        async Task<PyValue?> Call(string method, params PyValue[] args) => Result(await Exchange(method, args));

        Assert.Equal(PyBool.True, await Call("ActivitySeen", new PyInt(42)));
        Assert.Equal(PyBool.True, await Call("ActivitySeen", new PyLong(4_294_967_297)));
        Assert.Equal(PyBool.False, await Call("ActivitySeen", PyNone.Instance));
        Assert.Equal(new PyList([new PyInt(42), new PyLong(4_294_967_297)]),
            await Call("GetActivityBadgeStatuses"));
        Assert.Equal(["OnSystemActivityUpdated"], notifications);
        notifications.Clear();

        Assert.Equal(PyBool.True, await Call("SetActivitiesPopState", PyBool.False));
        Assert.Equal(new PyInt(0), await Call("GetActivitiesPopState"));
        Assert.Equal(PyBool.True, await Call("SetActivitiesPopState", PyNone.Instance));
        Assert.Equal(new PyInt(1), await Call("GetActivitiesPopState"));
        Assert.Empty(notifications);
    }
}
