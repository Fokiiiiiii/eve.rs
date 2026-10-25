using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class PetitionerHostWireTests
{
    private const long NowMs = 1_790_646_377_000;

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public async Task Petitioner_client_methods_answer_through_registered_host_wire()
    {
        var registry = new ServiceRegistry();
        foreach (var service in CoreServices.Create(new NoAccounts(), () => NowMs))
            registry.Register(service);

        var dispatcher = new PacketDispatcher(registry, () => NowMs);
        var sink = new Sink();
        var session = new ClientSession(sink);
        var open = new SessionOpen((ulong)NowMs * 15, 42_000_042, 42, 0, "127.0.0.1", "EN", "US");

        async Task<PyValue> Call(string method, PyValue[]? args = null, PyValue? kwargs = null)
        {
            sink.Packets.Clear();
            var wireCall = kwargs is null
                ? Packets.Call(open, 1, "petitioner", method, args ?? [])
                : Packets.CallWithKwargs(open, 1, "petitioner", method, new PyTuple(args ?? []), kwargs);
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(wireCall)), session);
            return CallResult(Assert.Single(sink.Packets));
        }

        var machoVersion = new PyDict([new(new PyStr("machoVersion"), new PyInt(3396210))]);
        Assert.Empty(Assert.IsType<PyList>(await Call("GetQueues", kwargs: machoVersion)).Items);
        Assert.Empty(Assert.IsType<PyList>(await Call("GetUnreadMessages", kwargs: machoVersion)).Items);
        Assert.Equal(new PyStr("US"), await Call("GetUserCatalogCountry", kwargs: machoVersion));
        Assert.Equal(PyBool.False, await Call("HasOpenTickets", kwargs: machoVersion));
        Assert.Equal(PyBool.False, await Call("IsSerenity", kwargs: machoVersion));
        Assert.Equal(PyBool.True, await Call("IsZendeskEnabled", kwargs: machoVersion));
        Assert.Equal(PyBool.True, await Call("IsZendeskSwapEnabled", kwargs: machoVersion));

        Assert.Equal(new PyStr("https://support.eveonline.com/hc/en-us/requests/new"),
            await Call("GetZendeskJwtLink", kwargs: machoVersion));
        var ticketKwargs = new PyDict([
            new(new PyStr("ticketID"), new PyStr("321")),
            new(new PyStr("machoVersion"), new PyInt(3396210)),
        ]);
        Assert.Equal(new PyStr("https://support.eveonline.com/hc/en-us/requests/321"),
            await Call("GetZendeskJwtLink", kwargs: ticketKwargs));
        var invalidTicketKwargs = new PyDict([
            new(new PyStr("ticketID"), new PyStr("not-a-ticket")),
            new(new PyStr("machoVersion"), new PyInt(3396210)),
        ]);
        Assert.Equal(new PyStr("https://support.eveonline.com/hc/en-us/requests/new"),
            await Call("GetZendeskJwtLink", kwargs: invalidTicketKwargs));

        Assert.Equal(new PyInt(-4), await Call("MayPetition",
            [new PyInt(5), PyNone.Instance], machoVersion));
        Assert.IsType<PyNone>(await Call("MarkAsRead", [new PyLong(60000001)], machoVersion));
        Assert.IsType<PyNone>(await Call("PetitioneeChat",
            [new PyLong(43000001), new PyStr("reply"), PyBool.True], machoVersion));
        Assert.IsType<PyNone>(await Call("PetitioneeChat",
            [new PyLong(43000001), new PyStr("reply")], machoVersion));
    }

    private static PyValue CallResult(PyValue response)
    {
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)fields[0]).Value);
        var payload = Assert.IsType<PyTuple>(fields[4]);
        return Assert.IsType<PySubStream>(payload.Items[0]).Decoded!;
    }
}
