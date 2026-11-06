using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_service_access_uses_the_client_service_setting_table_over_registered_routes()
    {
        const long characterId = 90_000_101;
        const long corporationId = 98_000_101;
        const long structureId = 88_000_101;
        const long profileId = 50_101;
        const long solarSystemId = 30_000_142;
        int[] serviceIds = [1, 2, 3, 4, 5, 6, 8, 9, 11, 14, 15, 16, 18, 20, 21, 22, 23, 24, 30, 31, 32, 33, 34, 40, 41, 42, 43];
        int[] settingIds = [3, 4, 17, 19, 20, 23, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36];
        var settings = new JsonObject();
        foreach (var settingId in settingIds)
            settings[settingId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                new JsonArray(new JsonObject { ["groupID"] = 0, ["value"] = 1 });

        var tables = new Dictionary<string, JsonObject>
        {
            ["structures"] = new()
            {
                [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["structureID"] = structureId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = solarSystemId,
                    ["ownerCorpID"] = corporationId,
                    ["profileID"] = profileId,
                    ["serviceStates"] = new JsonObject(serviceIds.ToDictionary(
                        id => id.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => (JsonNode?)JsonValue.Create(1))),
                },
            },
            ["structureProfiles"] = new()
            {
                ["profilesByID"] = new JsonObject
                {
                    [profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["corporationID"] = corporationId,
                        ["settingsBySettingID"] = settings,
                    },
                },
            },
        };

        Task<JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((JsonObject)tables.GetValueOrDefault(name, new JsonObject()).DeepClone());
        Task<Eve.Relations.Affiliation> GetAffiliation(long _, CancellationToken __) =>
            Task.FromResult(new Eve.Relations.Affiliation(corporationId, null, null));
        var readModel = new PersistenceStructureDirectoryReadModel(ReadTable, GetAffiliation);

        Start(
            configure: registry =>
            {
                registry.Register(new StructureSettingsService(readModel));
                registry.Register(new StructureDirectoryService(readModel));
            },
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("corpid", new PyLong(corporationId));
                session.Set("structureid", new PyLong(structureId));
                session.Set("solarsystemid2", new PyLong(solarSystemId));
                session.Set("regionid", new PyLong(10_000_001));
            });

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

        var services = Assert.IsType<PyDict>(await Call(
            "structureSettings", "CharacterGetServices", Py.Tuple(new PyLong(structureId))));
        Assert.Equal(serviceIds.Select(id => (long)id).Order(),
            services.Entries.Select(entry => Assert.IsType<PyInt>(entry.Key).Value).Order());
        foreach (var serviceId in new[] { 4, 8, 9 })
        {
            var argument = Py.Tuple(new PyLong(structureId), new PyInt(serviceId));
            Assert.Equal(PyBool.True, await Call("structureSettings", "CharacterHasService", argument));
            Assert.Equal(PyBool.True, await Call("structureSettings", "CharacterCheckService", argument));
        }

        var directory = Assert.IsType<PyDict>(await Call(
            "structureDirectory", "GetMyCharacterStructures", Py.Tuple()));
        var info = Assert.IsType<PyObject>(Assert.Single(directory.Entries).Value);
        var infoFields = Assert.IsType<PyDict>(info.Args);
        var servicesField = Assert.IsType<PyDict>(Assert.Single(infoFields.Entries,
            entry => entry.Key switch
            {
                PyStr name => name.Value == "services",
                PyBytes name => System.Text.Encoding.UTF8.GetString(name.Value.Span) == "services",
                _ => false,
            }).Value);
        Assert.All(new[] { 2, 8, 9 }, serviceId =>
            Assert.Contains(servicesField.Entries, entry => entry.Key == new PyInt(serviceId) && entry.Value == new PyInt(1)));
    }
}
