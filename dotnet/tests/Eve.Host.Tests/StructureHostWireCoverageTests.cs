using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_settings_bridge_and_vulnerability_calls_keep_the_client_wire_contract()
    {
        var callbacks = new TestStructureSettingsReadCallbacks();
        var reinforcementBatches = new List<(long[] Ids, int Weekday, int Hour)>();
        var linkedStructureService = new StructureTravelRpcService(
            "structureJumpBridgeMgr",
            linkedStructure: (structureId, _) => ValueTask.FromResult<(long StructureId, long SolarSystemId)?>(
                structureId == 80000001 ? (80000002, 30000142) : null));
        var vulnerabilityService = new StructureRpcService(
            "structureVulnerability",
            new StructureRpcCallbacks
            {
                UpdateReinforceTimesFromClientAsync = (_, ids, weekday, hour) =>
                {
                    reinforcementBatches.Add((ids.ToArray(), weekday, hour));
                    return ValueTask.CompletedTask;
                },
            });

        Start(
            configure: registry =>
            {
                registry.Register(new StructureSettingsService(callbacks));
                registry.Register(linkedStructureService);
                registry.Register(vulnerabilityService);
            },
            sessionOpened: session => session.Set("charid", new PyInt(90000001)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<Send> Call(string service, string method, PyValue[] args, uint correlationId, bool withMachoVersion = false)
        {
            PyValue kwargs = withMachoVersion
                ? new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])
                : PyNone.Instance;
            var request = Packets.CallWithKwargs(open, correlationId, service, method, new PyTuple(args), kwargs);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        static int PacketType(Send send) =>
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type;

        async Task<PyValue> Success(string service, string method, PyValue[] args, uint correlationId, bool withMachoVersion = false)
        {
            var response = await Call(service, method, args, correlationId, withMachoVersion);
            Assert.Equal((int)MachoMessage.CallRsp, PacketType(response));
            return Result(MarshalCodec.Decode(response.Payload))!;
        }

        var linkedReply = Assert.IsType<PyList>(await Success(
            "structureJumpBridgeMgr", "GetLinkedStructure", [new PyLong(80000001)], 1));
        Assert.Equal([new PyInt(80000002), new PyInt(30000142)], linkedReply.Items);
        Assert.Same(PyNone.Instance, await Success(
            "structureJumpBridgeMgr", "GetLinkedStructure", [new PyLong(80000003)], 2));

        Assert.Same(PyBool.True, await Success(
            "structureSettings", "CharacterCheckService", [new PyLong(80000001), new PyInt(5)], 3));
        Assert.Equal(new PyReal(0.025), await Success(
            "structureSettings", "CharacterGetService", [new PyLong(80000001), new PyInt(5)], 4));
        Assert.Same(PyBool.True, await Success(
            "structureSettings", "CharacterHasService", [new PyLong(80000001), new PyInt(5)], 5));
        Assert.Same(PyBool.True, await Success(
            "structureSettings", "CharacterHasSetting", [new PyLong(80000001), new PyInt(19)], 6));
        var services = Assert.IsType<PyDict>(await Success(
            "structureSettings", "CharacterGetServices", [new PyLong(80000001)], 7));
        Assert.Equal(new PyReal(0.025), services.Entries.Single().Value);

        var burstIds = Enumerable.Range(1, 500).Select(id => (PyValue)new PyLong(80000000 + id)).ToArray();
        Assert.Same(PyNone.Instance, await Success(
            "structureVulnerability", "UpdateReinforceTimesFromClient",
            [new PyList(burstIds), new PyInt(2), new PyInt(18)], 8, withMachoVersion: true));
        Assert.Equal(500, reinforcementBatches[0].Ids.Length);
        Assert.Equal((2, 18), (reinforcementBatches[0].Weekday, reinforcementBatches[0].Hour));
        Assert.Equal(80000001, reinforcementBatches[0].Ids[0]);
        Assert.Equal(80000500, reinforcementBatches[0].Ids[^1]);

        var oversized = await Call(
            "structureVulnerability", "UpdateReinforceTimesFromClient",
            [new PyList([.. burstIds, new PyLong(80000501)]), new PyInt(2), new PyInt(18)], 9,
            withMachoVersion: true);
        Assert.Equal((int)MachoMessage.ErrorResponse, PacketType(oversized));
        Assert.Single(reinforcementBatches);
    }

    private sealed class TestStructureSettingsReadCallbacks : IStructureSettingsReadCallbacks
    {
        public ValueTask<PyDict> CharacterGetServicesAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyInt(5), new PyReal(0.025)),
            ]));

        public ValueTask<PyValue> CharacterGetServiceAsync(
            long characterId, long structureId, long serviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(new PyReal(0.025));

        public ValueTask<bool> CharacterHasServiceAsync(
            long characterId, long structureId, long serviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(serviceId == 5);

        public ValueTask<bool> CharacterHasSettingAsync(
            long characterId, long structureId, long settingId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(settingId == 19);
    }
}
