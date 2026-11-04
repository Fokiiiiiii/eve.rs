using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_fuel_jettison_moniker_binds_and_dispatches_selected_item_ids_through_host_route()
    {
        long? observedStructure = null;
        IReadOnlyList<long>? observedItems = null;
        var service = new StructureMonikerService(
            (call, structureId, itemIds) =>
            {
                observedStructure = structureId;
                observedItems = itemIds;
                Assert.Equal("JettisonStructureFuel", call.Method);
                return ValueTask.CompletedTask;
            },
            () => FixedNowMs);
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open,
            1,
            "structure",
            "MachoBindObject",
            Py.Tuple(
                new PyLong(7001),
                Py.Tuple(new PyStr("JettisonStructureFuel"), Py.Tuple(new PyList([new PyLong(801), new PyLong(802)])))),
            kwargs))));

        var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var result = Assert.IsType<PyTuple>(Result(response));
        Assert.Equal(PyNone.Instance, result.Items[1]);
        Assert.Equal(7001, observedStructure);
        Assert.Equal([801L, 802L], observedItems);
    }
}
