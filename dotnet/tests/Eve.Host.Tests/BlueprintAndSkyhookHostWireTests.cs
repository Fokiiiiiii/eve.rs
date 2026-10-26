using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Blueprint_and_skyhook_reads_keep_client_shapes_through_registered_host_routes()
    {
        const long characterId = 90000002;
        const long corporationId = 98000002;
        var blueprint = new BlueprintRecord(7001, 200, characterId, 60000001, 4, 4,
            false, null, 0, 0, 30000142, false);
        var blueprintService = new BlueprintManagerService(
            (itemId, _) => ValueTask.FromResult<BlueprintRecord?>(itemId == blueprint.ItemId ? blueprint : null),
            (ownerId, _) => ValueTask.FromResult<IReadOnlyList<BlueprintRecord>>(
                ownerId == characterId ? [blueprint] : []),
            (_, _) => ValueTask.FromResult<IReadOnlyDictionary<long, long>>(
                new Dictionary<long, long> { [blueprint.ItemId] = 7002 }),
            (_, _, _, _) => ValueTask.FromResult<Eve.Corporations.SessionRoles?>(null));

        var skyhook = new SkyhookResourceSnapshot(8001, 81080, 30000142, 4000001, corporationId,
            9900001, true, 250, "1.0.0", [], null, 213);
        var resources = new SkyhookResourceService(
            (_, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(skyhook),
            (ownerId, _) => ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>(
                ownerId == corporationId ? [skyhook] : []),
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _, _, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(null));
        Start(strictRpc: true,
            configure: registry =>
            {
                registry.Register(blueprintService);
                registry.Register(new SkyhookResourceMgrService(resources));
            },
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("corpid", new PyLong(corporationId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        long callId = 0;
        async Task<PyValue> Call(string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, service, method, new PyTuple(args), kwargs))));
            var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(response));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(response)!;
        }

        PyValue Field(PyValue value, string name)
        {
            var fields = value switch
            {
                PyDict dict => dict,
                PyObject { Args: PyDict dict } => dict,
                _ => throw new InvalidOperationException("expected a KeyVal or dictionary"),
            };
            return fields.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;
        }

        Assert.Equal(new PyInt(500), Field(await Call("blueprintManager", "GetLimits"), "maxBlueprintResults"));
        var item = await Call("blueprintManager", "GetBlueprintData", new PyLong(blueprint.ItemId));
        Assert.Equal(new PyInt(blueprint.TypeId), Field(item, "typeID"));
        Assert.Equal(new PyInt(-1), Field(item, "quantity"));
        Assert.Equal(new PyInt(7002), Field(item, "jobID"));

        var byOwner = Assert.IsType<PyTuple>(await Call(
            "blueprintManager", "GetBlueprintDataByOwner", new PyLong(characterId), PyNone.Instance));
        Assert.Single(Assert.IsType<PyList>(byOwner.Items[0]).Items);
        Assert.Equal(new PyInt(1), Assert.IsType<PyDict>(byOwner.Items[1]).Entries[0].Value);
        var byFacility = Assert.IsType<PyTuple>(await Call(
            "blueprintManager", "GetBlueprintDataByOwner", new PyLong(characterId), new PyLong(9001)));
        Assert.Empty(Assert.IsType<PyList>(byFacility.Items[0]).Items);

        var skyhooks = Assert.IsType<PyList>(
            await Call("colonyResourcesMgr", "GetMyCorporationSkyhooks"));
        var row = Assert.Single(skyhooks.Items);
        Assert.Equal(new PyInt(skyhook.SkyhookId), Field(row, "skyhookID"));
        Assert.Equal(new PyInt(skyhook.SolarSystemId), Field(row, "solarSystemID"));
        Assert.Equal(new PyInt(213), Field(row, "state"));
    }
}
