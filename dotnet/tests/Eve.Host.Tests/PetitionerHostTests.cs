using Eve.Marshal;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Petitioner_client_calls_return_reference_shapes_through_host_registration()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync(); // the initial state

        var machoVersion = new PyDict([new(new PyStr("machoVersion"), new PyInt(1))]);
        var calls = new (string Method, PyValue[] Args, PyValue Kwargs, PyValue Expected)[]
        {
            ("CreatePetition",
                [new PyStr("subject"), new PyStr("body"), new PyInt(1), PyNone.Instance, new PyInt(42),
                 new PyList([]), new PyList([]), new PyList([])],
                machoVersion, PyBool.False),
            ("CreatePetition",
                [new PyStr("subject"), new PyStr("body"), new PyInt(1), PyNone.Instance, new PyInt(42)],
                new PyDict([
                    new(new PyStr("machoVersion"), new PyInt(1)),
                    new(new PyStr("chatLog"), new PyList([])),
                    new(new PyStr("combatLog"), new PyList([])),
                ]), PyBool.False),
            ("DeletePetition", [new PyInt(123)], machoVersion, PyNone.Instance),
            ("EscalatePetition", [new PyInt(123), new PyInt(2)], machoVersion, PyNone.Instance),
            ("GetCategories", [], machoVersion, new PyList([])),
            ("GetCategoryHierarchicalInfo", [], machoVersion,
                new PyList([new PyDict([]), new PyDict([]), new PyDict([]), new PyDict([])])),
            ("GetCategoryProperties", [new PyInt(1)], machoVersion, new PyList([])),
            ("GetClaimedPetitions", [], machoVersion, new PyList([])),
            ("GetClientPickerInfo", [new PyStr("Jita"), new PyStr("Target")], machoVersion, new PyList([])),
            ("GetLog", [new PyInt(123)], machoVersion, new PyList([])),
            ("GetMyPetitionsEx", [], machoVersion, new PyList([])),
            ("GetPetitionMessages", [new PyInt(123)], machoVersion, new PyList([])),
            ("GetPetitionQueue", [new PyInt(1)], machoVersion, new PyList([])),
        };

        long callId = 1;
        foreach (var call in calls)
        {
            var request = Packets.CallWithKwargs(
                open, callId++, "petitioner", call.Method, new PyTuple(call.Args), call.Kwargs);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(call.Expected, Result(MarshalCodec.Decode(reply.Payload)));
        }
    }
}
