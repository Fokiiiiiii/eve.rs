using System.Text;
using Eve.Characters;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Inventory;
using Eve.Ships;
using Eve.Skills;
using Eve.Sde;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Invbroker_structure_inventory_capacity_and_contents_keep_client_wire_shapes()
    {
        const long characterId = 90000001;
        const long stationId = 60000001;
        const long structureId = 60000002;
        const long structureOwnerId = 98000001;
        const int structureTypeId = 35832;
        long? resolvedStructureId = null;
        await using var source = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Username=unused;Pooling=false").Build();
        var items = new PgItemStore(source);
        var statics = new DogmaStatic([], [],
            [new TypeDef(structureTypeId, 1657, 65, new Dictionary<int, double>(), [], "Astrahus")],
            categoryOfGroup: new Dictionary<int, int> { [1657] = 65 },
            categoryNames: new Dictionary<int, string> { [65] = "Structure" });
        var ships = new CharacterShips(source, statics, new PgCharacterStore(source), items,
            new PgSkillStore(source), new PgModuleStateStore(source));
        var station = new Station(stationId, 1531, "Test Station", 0, "", null, "", 0, 0,
            30000142, "", 0, "", 0, "", 0, 0, 0);
        var service = new InvBrokerService(items, new ItemTypeCatalog(new SdeReader(source)),
            new StationCatalog([station]), ships, statics, () => FixedNowMs,
            controlledStructureInventory: (_, requestedId, _) =>
            {
                resolvedStructureId = requestedId;
                return ValueTask.FromResult<ControlledStructureInventory?>(requestedId == structureId
                    ? new ControlledStructureInventory(
                        new Item(structureId, structureTypeId, structureOwnerId, 30000142, 0, 1, true, null), [])
                    : null);
            });

        Start(configure: registry => registry.Register(service), strictRpc: true, sessionOpened: session =>
        {
            session.Set("charid", new PyInt(characterId));
            session.Set("stationid", new PyInt(stationId));
            session.Set("structureid", new PyInt(structureId));
            session.Set("shipid", new PyInt(structureId));
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "invbroker", "GetInventory", new PyTuple([new PyInt(InvBrokerService.ContainerStructure)]), MachoVersion()))));
        var structureReply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(structureReply.Payload))).Type);
        var structureBound = Assert.IsType<PySubStruct>(Result(MarshalCodec.Decode(structureReply.Payload)));
        var structureBoundData = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(structureBound.Value).Decoded);
        var structureObjectId = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(structureBoundData.Items[0]).Value.Span);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 2, structureObjectId, "List", new PyTuple([]), MachoVersion()))));
        var structureListReply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(structureListReply.Payload))).Type);
        Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(structureListReply.Payload))).Items);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 3, structureObjectId, "GetCapacity", new PyTuple([]), MachoVersion()))));
        var structureCapacityReply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(structureCapacityReply.Payload))).Type);
        Assert.IsType<PyObject>(Result(MarshalCodec.Decode(structureCapacityReply.Payload)));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 4, "invbroker", "GetInventory", new PyTuple([new PyInt(InvBrokerService.ContainerHangar)]), MachoVersion()))));
        var stationBindReply = Assert.IsType<Send>(await front.ReceiveAsync());
        var stationBound = Assert.IsType<PySubStruct>(Result(MarshalCodec.Decode(stationBindReply.Payload)));
        var boundData = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(stationBound.Value).Decoded);
        var objectId = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundData.Items[0]).Value.Span);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 5, objectId, "GetCapacity", new PyTuple([]), MachoVersion()))));
        var capacityReply = Assert.IsType<Send>(await front.ReceiveAsync());
        var capacity = Assert.IsType<PyObject>(Result(MarshalCodec.Decode(capacityReply.Payload)));
        Assert.Equal("util.KeyVal", Encoding.UTF8.GetString(Assert.IsType<PyBytes>(capacity.Name).Value.Span));
        var values = Assert.IsType<PyDict>(capacity.Args).Entries;
        Assert.Contains(values, pair => Assert.IsType<PyStr>(pair.Key).Value == "capacity"
            && pair.Value == new PyReal(9_000_000_000_000_000d));
        Assert.Contains(values, pair => Assert.IsType<PyStr>(pair.Key).Value == "used"
            && pair.Value == new PyReal(0));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 6, "invbroker", "GetContainerContents", new PyTuple([new PyInt(structureId), new PyInt(structureId)]), MachoVersion()))));
        var contentsReply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(structureId, resolvedStructureId);
        var rowset = Assert.IsType<PyObject>(Result(MarshalCodec.Decode(contentsReply.Payload)));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", Encoding.UTF8.GetString(Assert.IsType<PyBytes>(rowset.Name).Value.Span));
        var lines = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyDict>(rowset.Args).Entries,
            pair => Assert.IsType<PyStr>(pair.Key).Value == "lines").Value);
        Assert.Empty(lines.Items);
    }
}
