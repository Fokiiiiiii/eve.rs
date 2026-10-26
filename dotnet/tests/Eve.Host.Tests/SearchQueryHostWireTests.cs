using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Search_query_returns_group_keyed_result_lists_through_host_with_transport_kwargs()
    {
        var directory = new LookupSearchSources(
            Characters: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Corporations: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Alliances: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Factions: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]));
        Start(strictRpc: true, configure: registry =>
            registry.Register(new SearchQueryService(directory)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "search", "Query",
                new PyTuple([new PyStr("ALP"), new PyList([new PyInt(3), new PyInt(7)])]),
                Py.Dict(("exact", new PyInt(2)), ("machoVersion", new PyInt(1)))))));
        var decoded = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);

        var results = Assert.IsType<PyDict>(Result(decoded));
        Assert.Equal([3L, 7L], results.Entries.Select(entry => Assert.IsType<PyInt>(entry.Key).Value));
        Assert.All(results.Entries, entry => Assert.IsType<PyList>(entry.Value));
    }
}
