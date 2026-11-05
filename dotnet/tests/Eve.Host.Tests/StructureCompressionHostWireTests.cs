using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Structure_compression_rpcs_use_registered_host_routes_with_persisted_mutations_and_notifications()
    {
        const long character = 20;
        const long structure = 1_000_000_000_001;
        const int veldspar = 1230;
        const int compressedVeldspar = 62516;
        const int cytoserocin = 25268;
        const int compressedCytoserocin = 62396;

        await using var route = await StructureCompressionTests.Route.CreateAsync(
            Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set"));
        await route.AnchorAsync();
        var ore = await route.GiveAsync(veldspar, structure, quantity: 1000);
        var gas = await route.GiveAsync(compressedCytoserocin, structure, quantity: 1000);

        var callbacks = new StructureRpcCallbacks
        {
            CompressItemInStructureAsync = async (call, itemId) => await route.Authority.CompressAsync(call, itemId),
            DecompressGasInStructureAsync = async (call, itemId) => await route.Authority.DecompressAsync(call, itemId),
            GetMyGasDecompressionEfficiencyAsync = async call => await route.Authority.EfficiencyAsync(call),
        };
        Start(
            configure: registry => registry.Register(new StructureRpcService("structureCompressionMgr", callbacks)),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(character));
                session.Set("structureid", new PyLong(structure));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyStr("machoVersion"),
                new PyTuple([new PyInt(0), new PyInt(3396210)])),
        ]);

        async Task<(PyValue? Reply, int Notifications)> Call(long callId, string method, PyValue[] args)
        {
            var payload = Packets.CallWithKwargs(open, callId, "structureCompressionMgr", method, new PyTuple(args), kwargs);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(payload)));
            var notifications = 0;
            while (true)
            {
                var send = Assert.IsType<Send>(await front.ReceiveAsync());
                var packet = MarshalCodec.Decode(send.Payload);
                var state = ((PyTuple)Assert.IsType<PyObject>(packet).Args).Items;
                if (state[0] is PyInt { Value: MachoMessage.Notification })
                {
                    notifications++;
                    continue;
                }
                var reply = Result(packet);
                if (reply is not null)
                    return (reply, notifications);
            }
        }

        var compressed = await Call(1, "CompressItemInStructure", [new PyLong(ore)]);
        Assert.True(compressed.Notifications > 0);
        var compression = Assert.IsType<PyList>(compressed.Reply);
        Assert.Equal(6, compression.Items.Count);
        Assert.Equal(ore, Assert.IsType<PyInt>(compression.Items[0]).Value);
        Assert.Equal(veldspar, Assert.IsType<PyInt>(compression.Items[1]).Value);
        Assert.Equal(compressedVeldspar, Assert.IsType<PyInt>(compression.Items[4]).Value);
        var compressedItemId = Assert.IsType<PyInt>(compression.Items[3]).Value;
        Assert.Null(await route.Items.GetAsync(ore));
        var compressedItem = await route.Items.GetAsync(compressedItemId);
        Assert.Equal((compressedVeldspar, 1000L), (compressedItem!.TypeId, compressedItem.Quantity));

        var decompressed = await Call(2, "DecompressGasInStructure", [new PyLong(gas)]);
        Assert.True(decompressed.Notifications > 0);
        var decomp = Assert.IsType<PyList>(decompressed.Reply);
        Assert.Equal(6, decomp.Items.Count);
        Assert.Equal(gas, Assert.IsType<PyInt>(decomp.Items[0]).Value);
        Assert.Equal(compressedCytoserocin, Assert.IsType<PyInt>(decomp.Items[1]).Value);
        Assert.Equal(cytoserocin, Assert.IsType<PyInt>(decomp.Items[4]).Value);
        Assert.Equal(800, Assert.IsType<PyInt>(decomp.Items[5]).Value);
        Assert.Null(await route.Items.GetAsync(gas));

        var efficiencies = Assert.IsType<PyList>((await Call(3, "GetMyGasDecompressionEfficiency", [])).Reply);
        Assert.Equal(2, efficiencies.Items.Count);
        Assert.Equal(0.8, Assert.IsType<PyReal>(efficiencies.Items[0]).Value);
        Assert.Equal(0.0, Assert.IsType<PyReal>(efficiencies.Items[1]).Value);
    }
}
