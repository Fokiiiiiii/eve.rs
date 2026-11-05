using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Content;
using Eve.Host;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public sealed class PersistenceDungeonReadModelTests
{
    [Fact]
    public async Task SDE_catalog_and_active_content_and_dungeon_state_feed_the_client_services()
    {
        const long systemId = 30000142;
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        Task<IReadOnlyList<SdeRow>> ReadSde(string table, CancellationToken _)
        {
            reads[table] = reads.GetValueOrDefault(table) + 1;
            IReadOnlyList<SdeRow> rows = table switch
            {
                "dungeons" => [Row(5001, """{"name":{"en":"Test Site"},"factionID":500010,"archetypeID":24}""")],
                "archetypes" => [Row(24, """{"title":{"en":"Combat Sites"}}""")],
                "factions" => [Row(500010, "{}")],
                _ => [],
            };
            return Task.FromResult(rows);
        }

        Task<IReadOnlyList<ContentInstance>> ActiveByKind(string kind, CancellationToken _)
        {
            IReadOnlyList<ContentInstance> rows = kind switch
            {
                "anomaly" => [new ContentInstance(801, "anomaly", "test", systemId, "active",
                    "{\"siteID\":801,\"dungeonID\":5001,\"archetypeID\":24}", 1, null, null, null)],
                "signature" => [new ContentInstance(802, "signature", "test", systemId + 1, "active",
                    "{\"siteKind\":\"signature\",\"solarSystemID\":30000143}", 1, null, null, null)],
                _ => [],
            };
            return Task.FromResult(rows);
        }

        Task<JsonObject> ReadGameStoreTable(string name, CancellationToken _) => Task.FromResult(
            name == "dungeonRuntimeState"
                ? new JsonObject
                {
                    ["instancesByID"] = new JsonObject
                    {
                        ["803"] = new JsonObject
                        {
                            ["siteKind"] = "anomaly",
                            ["instanceScope"] = "shared",
                            ["lifecycleState"] = "active",
                            ["solarSystemID"] = systemId,
                            ["sourceDungeonID"] = 5001,
                            ["archetypeID"] = 24,
                        },
                        ["804"] = new JsonObject
                        {
                            ["siteKind"] = "anomaly",
                            ["lifecycleState"] = "completed",
                            ["solarSystemID"] = systemId,
                            ["sourceDungeonID"] = 5001,
                            ["archetypeID"] = 24,
                        },
                        ["805"] = new JsonObject
                        {
                            ["siteKind"] = "anomaly",
                            ["instanceScope"] = "character",
                            ["lifecycleState"] = "active",
                            ["solarSystemID"] = systemId,
                            ["sourceDungeonID"] = 5001,
                            ["archetypeID"] = 24,
                        },
                    },
                }
                : new JsonObject());

        var readModel = new PersistenceDungeonReadModel(ReadSde, ActiveByKind, ReadGameStoreTable, () => 1000);
        var catalog = await readModel.ReadEditorCatalogAsync(CancellationToken.None);
        var dungeon = Assert.Single(catalog.Dungeons);
        Assert.Equal(5001, dungeon.DungeonId);
        Assert.Equal("Test Site", dungeon.DungeonName);
        Assert.Equal((long?)500010, dungeon.FactionId);
        Assert.Equal((long?)24, dungeon.ArchetypeId);
        Assert.Equal(new DungeonEditorOption("Combat Sites", 24), Assert.Single(catalog.Archetypes));
        Assert.Equal([500010L], catalog.Factions);
        Assert.Empty(catalog.RoomsByDungeonId);
        Assert.Empty(Assert.IsType<PyDict>(catalog.RoomObjectPaletteData).Entries);

        var dungeonService = new DungeonService(readModel.ReadEditorCatalogAsync);
        var dungeonRows = Assert.IsType<PyList>(await dungeonService.CallAsync(Request("DEGetDungeons")));
        Assert.Single(dungeonRows.Items);

        var snapshot = await readModel.ReadCacheSnapshotAsync(CancellationToken.None);
        var cacheService = new DungeonInstanceCacheMgrService(
            _ => ValueTask.FromResult(snapshot), () => 1000);
        var combat = Assert.IsType<PyDict>(await cacheService.CallAsync(Request("GetCombatAnomalyInstances")));
        var combatRows = Assert.IsType<PyList>(Lookup(combat, systemId));
        Assert.Equal(2, combatRows.Items.Count);
        var signatures = Assert.IsType<PyDict>(await cacheService.CallAsync(Request("GetSignatureInstances")));
        Assert.Equal(1L, Assert.IsType<PyInt>(Lookup(signatures, systemId + 1)).Value);
        Assert.Equal(1, reads["dungeons"]);
        Assert.Equal(1, reads["archetypes"]);
        Assert.Equal(1, reads["factions"]);
    }

    [Fact]
    public async Task Current_dungeon_uses_the_active_mission_session_and_SDE_catalog()
    {
        const long characterId = 90000001;
        const long shipId = 900001;
        const long systemId = 30000142;
        const long dungeonId = 5001;
        const long instanceId = 81;
        var missionState = JsonNode.Parse("""
            {"charactersByID":{"90000001":{"missionsByAgentID":{"7001":{
              "runtimeStatus":"accepted","missionSystemID":30000142,
              "missionPosition":{"x":100,"y":200,"z":300},
              "dungeonID":5001,"dungeonInstanceID":81
            }}}}}
            """)!.AsObject();
        var dungeonState = JsonNode.Parse("""
            {"instancesByID":{"81":{
              "lifecycleState":"active","solarSystemID":30000142,
              "characterID":90000001,"shipID":900001,
              "sourceDungeonID":5001,"roomID":17
            }}}
            """)!.AsObject();

        Task<IReadOnlyList<SdeRow>> ReadSde(string table, CancellationToken _) => Task.FromResult<IReadOnlyList<SdeRow>>(
            table switch
            {
                "dungeons" => [Row(dungeonId, """{"name":{"en":"Test Dungeon"}}""")],
                _ => [],
            });
        Task<IReadOnlyList<ContentInstance>> ActiveByKind(string _, CancellationToken __) =>
            Task.FromResult<IReadOnlyList<ContentInstance>>([]);
        Task<JsonObject> ReadGameStoreTable(string table, CancellationToken _) =>
            Task.FromResult((JsonObject)(table == "missionRuntimeState" ? missionState.DeepClone()! : dungeonState.DeepClone()!));

        var readModel = new PersistenceDungeonReadModel(ReadSde, ActiveByKind, ReadGameStoreTable, () => 1000);
        var payload = Assert.IsType<PyTuple>(await readModel.ReadCurrentDungeonAsync(
            characterId, shipId, systemId, 100, 200, 300, false, CancellationToken.None));

        Assert.Equal(new PyInt((int)dungeonId), payload.Items[0]);
        Assert.Equal(new PyInt(17), payload.Items[1]);
        Assert.Equal(new PyInt((int)instanceId), payload.Items[2]);
        Assert.Empty(Assert.IsType<PyDict>(payload.Items[3]).Entries);
        Assert.Null(await readModel.ReadCurrentDungeonAsync(
            characterId, shipId, systemId, 1_000_000, 200, 300, false, CancellationToken.None));
    }

    private static SdeRow Row(long key, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new SdeRow(key, document.RootElement.Clone());
    }

    private static ServiceCall Request(string method) => new()
    {
        Method = method,
        Args = [],
        Session = new ClientSession(),
    };

    private static PyValue Lookup(PyDict dictionary, long key) => dictionary.Entries
        .Single(entry => Assert.IsType<PyInt>(entry.Key).Value == key).Value;
}
