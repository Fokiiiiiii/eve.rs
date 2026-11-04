using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class CorporationReadRpcHostTests
{
    [Fact]
    public async Task Community_fittings_and_corporate_mining_ledger_use_registered_wire_shapes()
    {
        await using var source = Npgsql.NpgsqlDataSource.Create(
            Eve.Data.PgConnection.ForTest("Host=127.0.0.1;Port=1;Database=unused"));
        var registry = new ServiceRegistry();
        registry.Register(new CorpFittingMgrService(new FittingMgrCalls(null!, null!, () => 1_790_646_377_000)));
        registry.Register(new CorpMiningLedgerService(
            new Eve.ReadSide.ReadSide(source, new Dictionary<string, string>
            {
                [MiningLedgerQueries.SpaceSchema] = "unused",
            }),
            static (_, _, _) => ValueTask.FromResult(false),
            () => 0));
        var dispatcher = new PacketDispatcher(registry);
        var capture = new PacketCapture();
        var session = new ClientSession(capture)
        {
            SessionId = 908,
            ClientId = 909,
            UserId = 910,
        };
        var open = new SessionOpen(908, 909, 910, 0, "127.0.0.1", "EN", "GB");

        async Task<PyValue?> Call(long id, string service, string method, params PyValue[] args)
        {
            capture.Sent.Clear();
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            var request = Packets.CallWithKwargs(open, id, service, method, new PyTuple(args), kwargs);
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
            var replies = capture.Sent.Where(packet =>
                MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp).ToArray();
            return Result(Assert.Single(replies));
        }

        var community = Assert.IsType<PyObject>(await Call(1, "corpFittingMgr", "GetCommunityFittings"));
        var cached = Assert.IsType<PyDict>(
            Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(community.Args).Items[1]).Decoded);
        Assert.Empty(cached.Entries);
        Assert.Empty(Assert.IsType<PyList>(
            await Call(2, "corpMiningLedger", "GetObserversWithMiningEvents")).Items);
        Assert.Empty(Assert.IsType<PyList>(
            await Call(3, "corpMiningLedger", "GetObserverLedger", new PyLong(70000001))).Items);
    }

    private static PyValue? Result(PyValue response)
    {
        var state = ((PyTuple)((PyObject)response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)state[0]).Value);
        var payload = (PyTuple)state[4];
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
