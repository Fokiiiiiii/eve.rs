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
    public async Task Corporation_hangar_stack_all_uses_division_owner_and_sends_item_changes_over_host_wire()
    {
        const long characterId = 90000001;
        const long corporationId = 98000001;
        const long stationId = 60000001;
        const int divisionFlag = ItemFlags.CorpSAG1;
        var allowed = true;
        var accessCalls = new List<(long Character, long Corporation, long Station, int Flag, CorporationHangarOperation Operation)>();
        var stackCalls = new List<(long Character, long Container, int? Flag, long Owner)>();
        var survivorBefore = new Item(40000001, 34, corporationId, stationId, divisionFlag, 5, false, null);
        var survivorAfter = survivorBefore with { Quantity = 8 };
        var consumed = new Item(40000002, 34, corporationId, stationId, divisionFlag, 3, false, null);
        IReadOnlyList<ItemChanged> nextChanges =
        [
            new(survivorAfter, stationId, divisionFlag, 5),
            new(consumed with { LocationId = CharacterShips.JunkLocation }, stationId, divisionFlag, 3),
        ];

        await using var source = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Username=unused;Pooling=false").Build();
        var items = new PgItemStore(source);
        var statics = new DogmaStatic([], [], []);
        var ships = new CharacterShips(source, statics, new PgCharacterStore(source), items,
            new PgSkillStore(source), new PgModuleStateStore(source));
        var service = new InvBrokerService(
            items,
            new ItemTypeCatalog(new SdeReader(source)),
            new StationCatalog([]),
            ships,
            statics,
            () => FixedNowMs,
            corporationHangarAccess: (character, corporation, station, flag, operation, _) =>
            {
                accessCalls.Add((character, corporation, station, flag, operation));
                return ValueTask.FromResult(allowed && operation == CorporationHangarOperation.Take);
            },
            stackAll: (character, container, flag, owner, _) =>
            {
                stackCalls.Add((character, container, flag, owner));
                return Task.FromResult(nextChanges);
            });

        Start(
            configure: registry => registry.Register(service),
            strictRpc: true,
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(characterId));
                session.Set("corpid", new PyInt(corporationId));
                session.Set("stationid", new PyInt(stationId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "invbroker", "GetInventory", new PyTuple([new PyInt(InvBrokerService.ContainerHangar)]), MachoVersion()))));
        var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
        var bound = Assert.IsType<PySubStruct>(Result(MarshalCodec.Decode(bindReply.Payload)));
        var boundData = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
        var objectId = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundData.Items[0]).Value.Span);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 2, objectId, "StackAll", new PyTuple([new PyInt(divisionFlag)]), MachoVersion()))));

        var first = await ItemChangeAsync(front);
        var second = await ItemChangeAsync(front);
        Assert.Equal("OnItemChange", first.Name);
        Assert.Equal("OnItemChange", second.Name);
        AssertPackedItem(first.Args.Items[0], survivorAfter);
        AssertPackedItem(second.Args.Items[0], consumed with { LocationId = CharacterShips.JunkLocation });
        Assert.Equal(
            new[] { new KeyValuePair<PyValue, PyValue>(new PyInt(9), new PyInt(5)) },
            Assert.IsType<PyDict>(first.Args.Items[1]).Entries);
        Assert.Equal(
            new[] { new KeyValuePair<PyValue, PyValue>(new PyInt(3), new PyInt(stationId)) },
            Assert.IsType<PyDict>(second.Args.Items[1]).Entries);

        var response = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload))).Type);
        Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(response.Payload)));
        Assert.Equal([(characterId, stationId, (int?)divisionFlag, corporationId)], stackCalls);
        Assert.Equal([(characterId, corporationId, stationId, divisionFlag, CorporationHangarOperation.Take)], accessCalls);

        nextChanges = [];
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 3, objectId, "StackAll", new PyTuple([new PyInt(divisionFlag)]), MachoVersion()))));
        var emptyResponse = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(emptyResponse.Payload))).Type);
        Assert.IsType<PyNone>(Result(MarshalCodec.Decode(emptyResponse.Payload)));
        Assert.Equal(2, stackCalls.Count);
        Assert.Equal(2, accessCalls.Count);

        allowed = false;
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 4, objectId, "StackAll", new PyTuple([new PyInt(divisionFlag)]), MachoVersion()))));
        var denied = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(denied.Payload))).Type);
        Assert.Equal(2, stackCalls.Count);
        Assert.Equal(3, accessCalls.Count);
        Assert.Equal(CorporationHangarOperation.Take, accessCalls[2].Operation);
    }

    private static void AssertPackedItem(PyValue value, Item expected)
    {
        var row = Assert.IsType<PyPackedRow>(value);
        var fields = row.Fields();
        Assert.Equal(InventoryWire.Columns, fields.Select(field => field.Key));
        PyValue[] expectedValues =
        [
            new PyInt(expected.Id), new PyInt(expected.TypeId), new PyInt(expected.OwnerId), new PyInt(expected.LocationId),
            new PyInt(expected.Flag), new PyInt(expected.Quantity), new PyInt(0), new PyInt(0), new PyBytes(ReadOnlyMemory<byte>.Empty),
            new PyInt(expected.Quantity < 0 ? 1 : expected.Quantity),
            new PyInt(expected.Quantity < 0 ? -expected.Quantity : expected.LocationId is >= 30_000_000 and < 40_000_000 ? 1 : 0),
        ];
        Assert.Equal(expectedValues, fields.Select(field => field.Value));
    }

    private static async Task<(string Name, PyTuple Args)> ItemChangeAsync(TestFront front)
    {
        var sent = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(sent.Payload)));
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal("clientID", packet.Dest.IdType);
        var payload = Assert.IsType<PyTuple>(packet.Payload);
        var bytes = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(payload.Items[0]).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.Decode(bytes.Value));
        var notificationArgs = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(decoded.Items[1]).Items[1]);
        return (packet.Dest.BroadcastId!, notificationArgs);
    }
}
