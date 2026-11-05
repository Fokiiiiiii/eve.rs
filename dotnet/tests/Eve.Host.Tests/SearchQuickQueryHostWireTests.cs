using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Search_quick_query_returns_client_id_list_through_host_with_transport_kwargs()
    {
        var directory = new LookupSearchSources(
            Characters: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Corporations: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([
                new(301, "Alpha Corporation", TickerName: "ALP"),
                new(302, "Alpha NPC Corporation", TickerName: "ALP", IsNpc: true),
            ]),
            Alliances: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Factions: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]));
        Start(strictRpc: true, configure: registry =>
            registry.Register(new SearchQueryService(directory)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = Py.Dict(
            ("hideNPC", PyBool.True),
            ("onlyAltName", PyBool.True),
            ("exact", new PyInt(2)),
            ("machoVersion", new PyInt(1)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "search", "QuickQuery",
                new PyTuple([new PyStr("ALP"), new PyList([new PyInt(3), new PyInt(3)])]),
                kwargs))));

        var response = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(response.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
        Assert.Equal([new PyInt(301)], Assert.IsType<PyList>(Result(decoded)).Items);
    }
}
