using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Jump_bridge_wire_payloads_include_the_fields_consumed_by_map_and_deployment_clients()
    {
        const long characterId = 9000000001;
        const long corporationId = 9800000001;
        const long allianceId = 99000001;
        const long profileId = 50001;
        const long groupId = 7000000001;
        const long firstBridgeId = 8800000001;
        const long secondBridgeId = 8800000002;
        const long firstSystemId = 30000142;
        const long secondSystemId = 30000143;

        var tables = new Dictionary<string, JsonObject>
        {
            ["structures"] = new()
            {
                [firstBridgeId.ToString()] = new JsonObject
                {
                    ["structureID"] = firstBridgeId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = firstSystemId,
                    ["ownerCorpID"] = corporationId,
                    ["allianceID"] = allianceId,
                    ["profileID"] = profileId,
                    ["itemName"] = "Bridge A",
                    ["serviceStates"] = new JsonObject { ["15"] = 1 },
                    ["devFlags"] = new JsonObject { ["destinationSolarsystemID"] = secondSystemId },
                },
                [secondBridgeId.ToString()] = new JsonObject
                {
                    ["structureID"] = secondBridgeId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = secondSystemId,
                    ["ownerCorpID"] = corporationId,
                    ["allianceID"] = allianceId,
                    ["profileID"] = profileId,
                    ["itemName"] = "Bridge B",
                    ["serviceStates"] = new JsonObject { ["15"] = 1 },
                    ["devFlags"] = new JsonObject { ["destinationSolarsystemID"] = firstSystemId },
                },
            },
            ["structureProfiles"] = new()
            {
                ["profilesByID"] = new JsonObject
                {
                    [profileId.ToString()] = new JsonObject
                    {
                        ["corporationID"] = corporationId,
                        ["settingsBySettingID"] = new JsonObject
                        {
                            ["34"] = new JsonArray(new JsonObject { ["groupID"] = groupId, ["value"] = 1 }),
                        },
                    },
                },
            },
            ["accessGroups"] = new()
            {
                [groupId.ToString()] = new JsonObject
                {
                    ["creatorID"] = 9000000003,
                    ["members"] = new JsonArray(new JsonObject
                    {
                        ["memberID"] = characterId,
                        ["membershipType"] = 0,
                    }),
                },
            },
        };

        Task<JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((JsonObject)tables.GetValueOrDefault(name, new JsonObject()).DeepClone());
        Task<Eve.Relations.Affiliation> GetAffiliation(long _, CancellationToken __) =>
            Task.FromResult(new Eve.Relations.Affiliation(corporationId, allianceId, null));
        var readModel = new PersistenceStructureDirectoryReadModel(ReadTable, GetAffiliation);

        Start(
            configure: registry => registry.Register(new StructureDirectoryService(readModel)),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("solarsystemid2", new PyInt(firstSystemId));
            });
        var (front, open) = await OpenFrontAsync();

        var jumpBridgeAccess = Assert.IsType<PyTuple>(await HostRpcAsync(
            front, open, 1, "structureDirectory", "GetJumpBridgesWithMyAccess"));
        var pairs = Assert.IsType<PyList>(jumpBridgeAccess.Items[0]);
        Assert.Single(pairs.Items);
        var pair = Assert.IsType<PyList>(pairs.Items[0]);
        Assert.Equal(2, pair.Items.Count);
        Assert.Equal(firstSystemId, WireInteger(WireField(pair.Items[0], "solarSystemID")));
        Assert.Equal(firstBridgeId, WireInteger(WireField(pair.Items[0], "structureID")));
        Assert.Equal(corporationId, WireInteger(WireField(pair.Items[0], "ownerID")));
        Assert.Equal(secondSystemId, WireInteger(WireField(pair.Items[1], "solarSystemID")));
        Assert.Equal(secondBridgeId, WireInteger(WireField(pair.Items[1], "structureID")));
        Assert.Equal(corporationId, WireInteger(WireField(pair.Items[1], "ownerID")));
        var accessibleBridgeIds = Assert.IsType<PyList>(jumpBridgeAccess.Items[1]).Items;
        Assert.Equal([firstBridgeId, secondBridgeId], accessibleBridgeIds.Select(WireInteger));
        Assert.Empty(Assert.IsType<PyList>(jumpBridgeAccess.Items[2]).Items);

        var nearby = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 2, "structureDirectory", "GetNearbyJumpBridges"));
        Assert.Equal(2, nearby.Items.Count);
        var first = nearby.Items.Single(row => WireInteger(WireField(row, "solarSystemID")) == firstSystemId);
        Assert.Equal(firstBridgeId, WireInteger(WireField(first, "structureID")));
        Assert.Equal(corporationId, WireInteger(WireField(first, "ownerID")));
        Assert.Equal("Bridge A", WireText(WireField(first, "structureName")));
        Assert.Equal(PyBool.False, WireField(first, "alignedToCurrentSystem"));
        var second = nearby.Items.Single(row => WireInteger(WireField(row, "solarSystemID")) == secondSystemId);
        Assert.Equal(secondBridgeId, WireInteger(WireField(second, "structureID")));
        Assert.Equal(corporationId, WireInteger(WireField(second, "ownerID")));
        Assert.Equal("Bridge B", WireText(WireField(second, "structureName")));
        Assert.Equal(PyBool.True, WireField(second, "alignedToCurrentSystem"));
    }
}
