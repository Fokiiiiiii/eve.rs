using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Slash;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Slash_client_calls_keep_the_string_reply_through_the_host_route()
    {
        var commands = new[]
        {
            "/structure state 10000001 3",
            "/structure timer 10000001 300",
            "/structure unanchor 10000001",
            "/unspawn 10000001",
            "/entity deploy 42 100",
            "/create 42 10000001",
            "/load me 42",
            "/fit me 42",
            "/unload me 10000001",
            "/heal 10000001",
            "/spawnN 42 4000 10000001",
            "/loop 42 /fit me 10000001",
            "/tr me 30000142",
        };
        Start(
            strictRpc: true,
            configure: registry => registry.Register(new SlashService(new ShipHealthCommands(null!, null!, () => FixedNowMs))),
            sessionOpened: session => session.Set("accountRole", new PyLong(GameMasterRoles.Gml)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        long callId = 0;

        foreach (var command in commands)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "slash", "SlashCmd", new PyTuple([new PyStr(command)]), kwargs))));
            var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(response));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            Assert.IsType<PyBytes>(Result(response));
        }
    }

    [Fact]
    public async Task Interference_client_consumers_receive_bands_and_local_state_over_the_host_route()
    {
        const long systemId = 30000142;
        var nowFiletime = Py.FileTimeFromUnixMs(FixedNowMs);
        var table = new System.Text.Json.Nodes.JsonObject
        {
            ["systems"] = new System.Text.Json.Nodes.JsonObject
            {
                [systemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["interferenceLevel"] = 8000,
                        ["lastDecayTickTimestamp"] = nowFiletime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                ["30000143"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["interferenceLevel"] = 4000,
                    ["lastDecayTickTimestamp"] = nowFiletime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                ["30000144"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["interferenceLevel"] = 0,
                    ["lastDecayTickTimestamp"] = nowFiletime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
        };
        Start(
            strictRpc: true,
            gameRpcCallbacks: new GameRpcCallbacks(
                ReadInterferenceTableAsync: _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(table)),
            sessionOpened: session => session.Set("solarsystemid2", new PyLong(systemId)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        long callId = 0;

        async Task<PyValue> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "solarsystemInterferenceMgr", method, new PyTuple(args), kwargs))));
            var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(response));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(response)!;
        }

        var bands = Assert.IsType<PyDict>(await Call("GetAllInterferenceBands"));
        Assert.Equal(2, bands.Entries.Count);
        Assert.Contains(bands.Entries, row => row.Key == new PyInt(systemId) && Encoding.UTF8.GetString(Assert.IsType<PyBytes>(row.Value).Value.Span) == "H");
        Assert.Contains(bands.Entries, row => row.Key == new PyInt(30000143) && Encoding.UTF8.GetString(Assert.IsType<PyBytes>(row.Value).Value.Span) == "M");

        var state = Assert.IsType<PyObjectEx>(await Call("GetLocalInterferenceState"));
        var header = Assert.IsType<PyTuple>(state.Header);
        Assert.Equal(
            new PyToken("solarsysteminterference.solarsystemInterferenceState.SolarsystemInterferenceState"),
            header.Items[0]);
        var fields = Assert.IsType<PyTuple>(header.Items[1]);
        Assert.Equal(6, fields.Items.Count);
        Assert.Equal(new PyInt(8000), fields.Items[0]);
        Assert.Equal(new PyLong(nowFiletime), fields.Items[1]);
    }

    [Fact]
    public async Task Sovereignty_client_calls_reach_the_registered_service_and_keep_reference_shapes()
    {
        const long systemId = 30000142;
        const long hubId = 1020002;
        const long characterId = 90000002;
        const long corporationId = 98000002;
        const long allianceId = 99000002;
        const long fuelGroupId = 99000077;
        const long acquireSkyhookId = 810801;
        const long destroySkyhookId = 810802;
        var hub = new SovHubRecord(hubId, corporationId, allianceId, DateTime.UtcNow.AddHours(-1).ToFileTimeUtc());
        var snapshot = new SovFwReadSnapshot(
            new Dictionary<long, SovClaimRecord> { [systemId] = new(1020001, corporationId, allianceId) },
            new Dictionary<long, SovHubRecord> { [systemId] = hub },
            new Dictionary<long, IReadOnlyList<SovStructureRecord>>
            {
                [systemId] = [new(1020001, 32226, null, corporationId, allianceId, systemId)],
            },
            [],
            new Dictionary<long, long?> { [systemId] = fuelGroupId });
        long? acquiredAccessGroup = null;
        var hubWrites = new SovHubWriteOperations(
            (_, _, _) => ValueTask.FromResult(true),
            (_, _, groupId) => ValueTask.FromResult<PyValue>(groupId is { } id ? new PyInt(id) : PyNone.Instance),
            (_, _, _, _) => ValueTask.FromResult<PyValue>(PyNone.Instance),
            (_, _, _) => ValueTask.FromResult<PyValue>(PyNone.Instance),
            (_, _, _) => ValueTask.FromResult<PyValue>(PyNone.Instance));
        var skyhookWrites = new SovSkyhookWriteOperations(
            (_, _, _, ids) => ValueTask.FromResult<IReadOnlyList<long>>(ids.Contains(destroySkyhookId) ? [destroySkyhookId] : []),
            (_, _, _, ids, accessGroup) =>
            {
                acquiredAccessGroup = accessGroup;
                return ValueTask.FromResult<IReadOnlyList<long>>(ids.Contains(acquireSkyhookId) ? [acquireSkyhookId] : []);
            });
        Start(
            strictRpc: true,
            configure: registry =>
            {
                foreach (var service in SovFwService.Create(
                    _ => ValueTask.FromResult(snapshot),
                    _ => ValueTask.FromResult(true),
                    hubWrites,
                    skyhookWrites))
                    registry.Register(service);
            },
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("corpid", new PyLong(corporationId));
                session.Set("allianceid", new PyLong(allianceId));
                session.Set("solarsystemid2", new PyLong(systemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        long callId = 0;

        async Task<PyValue> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "sovMgr", method, new PyTuple(args), kwargs))));
            var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(response));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(response)!;
        }

        var structures = Assert.IsType<PyList>(await Call("GetSovStructuresInfoForLocalSolarSystem"));
        var structure = Assert.IsType<PyObject>(Assert.Single(structures.Items));
        Assert.Equal(new PyInt(1020001), Field(structure, "itemID"));

        var hubInfo = Assert.IsType<PyObjectEx>(await Call("GetInfrastructureHubInfo", new PyLong(systemId)));
        var hubHeader = Assert.IsType<PyTuple>(hubInfo.Header);
        Assert.Equal(new PyToken("sovereignty.data_types.SovHubInfo"), hubHeader.Items[0]);
        Assert.Equal(hubId, Assert.IsType<PyInt>(Assert.IsType<PyList>(hubHeader.Items[1]).Items[0]).Value);

        Assert.Equal(new PyInt(fuelGroupId), await Call("GetSovHubFuelAccessGroup", new PyLong(systemId)));
        Assert.Equal(
            new PyList([new PyInt(acquireSkyhookId)]),
            await Call("AcquireSkyhooks", new PyList([new PyLong(acquireSkyhookId)]), new PyLong(fuelGroupId)));
        Assert.Equal(fuelGroupId, acquiredAccessGroup);
        Assert.Equal(
            new PyList([new PyInt(destroySkyhookId)]),
            await Call("DestroySkyhooks", new PyList([new PyLong(destroySkyhookId)])));
    }

}
