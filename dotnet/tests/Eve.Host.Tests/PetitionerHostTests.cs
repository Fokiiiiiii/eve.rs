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
            ("DeletePetition", [new PyLong(123)], machoVersion, PyNone.Instance),
            ("EscalatePetition", [new PyLong(123), new PyLong(2)], machoVersion, PyNone.Instance),
            ("GetCategories", [], machoVersion, new PyList([])),
            ("GetCategoryHierarchicalInfo", [], machoVersion,
                new PyList([new PyDict([]), new PyDict([]), new PyDict([]), new PyDict([])])),
            ("GetCategoryProperties", [new PyInt(1)], machoVersion, new PyList([])),
            ("GetClaimedPetitions", [], machoVersion, new PyList([])),
            ("GetClientPickerInfo", [new PyStr("Jita"), new PyStr("Target")], machoVersion, new PyList([])),
            ("GetLog", [new PyLong(123)], machoVersion, new PyList([])),
            ("GetMyPetitionsEx", [], machoVersion, new PyList([])),
            ("GetPetitionMessages", [new PyLong(123)], machoVersion, new PyList([])),
            ("GetPetitionQueue", [new PyLong(1)], machoVersion, new PyList([])),
            ("AddPetitionRating",
                [new PyLong(123), new PyInt(3), new PyInt(4), new PyInt(5), new PyStr("Helpful"),
                 new PyLong(1_340_000_000_000_000_000)], machoVersion, PyNone.Instance),
            ("CancelPetition", [new PyLong(123)], machoVersion, PyNone.Instance),
            ("ClaimPetition", [new PyLong(123)], machoVersion, new PyInt(0)),
            ("ClosePetition", [new PyLong(123)], machoVersion, PyNone.Instance),
            ("GetQueues", [], machoVersion, new PyList([])),
            ("GetUnreadMessages", [], machoVersion, new PyList([])),
            ("GetUserCatalogCountry", [], machoVersion, new PyBytes("US"u8.ToArray())),
            ("GetZendeskJwtLink", [], machoVersion,
                new PyBytes("https://support.eveonline.com/hc/en-us/requests/new"u8.ToArray())),
            ("GetZendeskJwtLink", [], new PyDict([
                new(new PyStr("machoVersion"), new PyInt(1)),
                new(new PyStr("ticketID"), new PyStr("321")),
            ]), new PyBytes("https://support.eveonline.com/hc/en-us/requests/321"u8.ToArray())),
            ("HasOpenTickets", [], machoVersion, PyBool.False),
            ("IsSerenity", [], machoVersion, PyBool.False),
            ("IsZendeskEnabled", [], machoVersion, PyBool.True),
            ("IsZendeskSwapEnabled", [], machoVersion, PyBool.True),
            ("MarkAsRead", [new PyLong(60_000_001)], machoVersion, PyNone.Instance),
            ("MayPetition", [new PyInt(5), PyNone.Instance], machoVersion, new PyInt(-4)),
            ("PetitioneeChat", [new PyLong(123), new PyStr("reply")], machoVersion, PyNone.Instance),
            ("PetitioneeChat", [new PyLong(123), new PyStr("reply"), PyBool.True], machoVersion, PyNone.Instance),
            ("PetitionerChat", [new PyLong(123), new PyStr("reply")], machoVersion, PyNone.Instance),
            ("PropertyPopulationInfo", [new PyStr("StarbaseLocation"), PyNone.Instance], machoVersion, new PyList([])),
            ("UnClaimPetition", [new PyLong(123)], machoVersion, PyNone.Instance),
            ("UpdatePetitionRating",
                [new PyLong(123), new PyInt(3), new PyInt(4), new PyInt(5), new PyStr("Helpful")],
                machoVersion, PyNone.Instance),
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
