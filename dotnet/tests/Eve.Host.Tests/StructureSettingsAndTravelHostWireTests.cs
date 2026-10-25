using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_settings_jump_bridge_and_reinforcement_calls_use_registered_host_routes()
    {
        const long characterId = 90000001;
        const long corporationId = 98000001;
        const long structureId = 88000001;
        const long linkedStructureId = 88000002;
        const long profileId = 50001;
        const long sourceSystemId = 30000142;
        const long destinationSystemId = 30000143;

        var tables = new Dictionary<string, JsonObject>
        {
            ["structures"] = new()
            {
                [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["structureID"] = structureId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = sourceSystemId,
                    ["ownerCorpID"] = corporationId,
                    ["profileID"] = profileId,
                    ["serviceStates"] = new JsonObject { ["5"] = 1 },
                    ["devFlags"] = new JsonObject { ["destinationSolarsystemID"] = destinationSystemId },
                },
                [linkedStructureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["structureID"] = linkedStructureId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = destinationSystemId,
                    ["devFlags"] = new JsonObject { ["destinationSolarsystemID"] = sourceSystemId },
                },
            },
            ["structureProfiles"] = new()
            {
                ["profilesByID"] = new JsonObject
                {
                    [profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["corporationID"] = corporationId,
                        ["settingsBySettingID"] = new JsonObject
                        {
                            ["4"] = new JsonArray(new JsonObject { ["groupID"] = 0, ["value"] = 0.025 }),
                            ["19"] = new JsonArray(new JsonObject { ["groupID"] = 0, ["value"] = 1 }),
                        },
                    },
                },
            },
        };

        Task<JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((JsonObject)tables.GetValueOrDefault(name, new JsonObject()).DeepClone());
        Task<Eve.Relations.Affiliation> GetAffiliation(long _, CancellationToken __) =>
            Task.FromResult(new Eve.Relations.Affiliation(corporationId, null, null));
        var readModel = new PersistenceStructureDirectoryReadModel(ReadTable, GetAffiliation);

        IReadOnlyList<long>? updatedIds = null;
        var updatedWeekday = -1;
        var updatedHour = -1;
        var callbacks = new StructureRpcCallbacks
        {
            UpdateReinforceTimesFromClientAsync = (_, ids, weekday, hour) =>
            {
                updatedIds = ids;
                updatedWeekday = weekday;
                updatedHour = hour;
                return ValueTask.CompletedTask;
            },
        };

        Start(
            configure: registry =>
            {
                registry.Register(new StructureSettingsService(readModel));
                registry.Register(new StructureTravelRpcService(
                    "structureJumpBridgeMgr",
                    linkedStructure: readModel.GetLinkedStructureAsync));
                registry.Register(new StructureRpcService("structureVulnerability", callbacks));
            },
            sessionOpened: session => session.CharacterId = characterId);

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        uint callId = 0;
        async Task<PyValue> Call(string service, string method, PyTuple args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, service, method, args, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)) ?? PyNone.Instance;
        }

        Assert.Equal(
            new PyList([new PyInt(linkedStructureId), new PyInt(destinationSystemId)]),
            await Call("structureJumpBridgeMgr", "GetLinkedStructure", Py.Tuple(new PyInt(structureId))));
        Assert.Equal(
            PyNone.Instance,
            await Call("structureJumpBridgeMgr", "GetLinkedStructure", Py.Tuple(new PyInt(88000003))));

        Assert.Equal(PyBool.True, await Call(
            "structureSettings", "CharacterCheckService",
            Py.Tuple(new PyInt(structureId), new PyInt(Eve.Structures.StructureServiceIds.Market))));
        Assert.Equal(new PyReal(0.025), await Call(
            "structureSettings", "CharacterGetService",
            Py.Tuple(new PyInt(structureId), new PyInt(Eve.Structures.StructureServiceIds.Market))));

        var services = Assert.IsType<PyDict>(await Call(
            "structureSettings", "CharacterGetServices", Py.Tuple(new PyInt(structureId))));
        Assert.Equal(new PyInt(1), services.Entries.Single().Value);
        Assert.Equal(PyBool.True, await Call(
            "structureSettings", "CharacterHasService",
            Py.Tuple(new PyInt(structureId), new PyInt(Eve.Structures.StructureServiceIds.Market))));
        Assert.Equal(PyBool.True, await Call(
            "structureSettings", "CharacterHasSetting",
            Py.Tuple(new PyInt(structureId), new PyInt(19))));

        var ids = Enumerable.Range(1, 500).Select(id => (PyValue)new PyInt(8_800_000 + id)).ToArray();
        Assert.Equal(PyNone.Instance, await Call(
            "structureVulnerability",
            "UpdateReinforceTimesFromClient",
            Py.Tuple(new PyList(ids), new PyInt(255), new PyInt(23))));
        Assert.Equal(500, updatedIds?.Count);
        Assert.Equal(255, updatedWeekday);
        Assert.Equal(23, updatedHour);
    }
}
