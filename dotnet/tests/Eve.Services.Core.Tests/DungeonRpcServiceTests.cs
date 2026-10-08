using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class DungeonRpcServiceTests
{
    [Theory]
    [InlineData("DEGetArchetypes")]
    [InlineData("DEGetDungeons")]
    [InlineData("DEGetFactions")]
    [InlineData("DEGetRooms")]
    [InlineData("DEGetRoomObjectPaletteData")]
    public void Dungeon_editor_registers_the_EveJS_RPC_names(string method)
    {
        var service = new DungeonService();
        Assert.Equal("dungeon", service.Name);
        Assert.True(service.HasMethod(method));
    }

    [Fact]
    public async Task Dungeon_editor_defaults_to_empty_data_with_the_EveJS_shapes()
    {
        var service = new DungeonService();

        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("DEGetDungeons"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("DEGetArchetypes"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("DEGetFactions"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("DEGetRooms"))).Items);
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("DEGetRoomObjectPaletteData"))).Entries);
    }

    [Fact]
    public async Task Dungeon_editor_uses_SDE_catalog_rows_filters_and_room_hooks()
    {
        var palette = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("paletteID"), new PyInt(7))]);
        var catalog = new DungeonEditorCatalog(
            [
                new DungeonEditorDungeon(100, 9001, "Combat Depot", 500010, 24),
                new DungeonEditorDungeon(101, 9002, "Ore Field", 500020, 27),
                new DungeonEditorDungeon(102, 9003, "Combat Site", 500020, 24),
            ],
            [new DungeonEditorOption("Combat Sites", 24), new DungeonEditorOption("Ore Anomalies", 27)],
            [500020, 500010],
            new Dictionary<long, IReadOnlyList<DungeonEditorRoom>>
            {
                [102] = [new DungeonEditorRoom(1, "Entry"), new DungeonEditorRoom(2, "Boss")],
            },
            palette);
        var service = new DungeonService(_ => ValueTask.FromResult(catalog));

        var rows = Assert.IsType<PyList>(await service.CallAsync(Request("DEGetDungeons", [],
            ("archetypeID", new PyInt(24)), ("factionID", new PyInt(500020)))));
        var dungeon = Fields(Assert.IsType<PyObject>(Assert.Single(rows.Items)));
        Assert.Equal(102L, Integer(dungeon["dungeonID"]));
        Assert.Equal(9003L, Integer(dungeon["dungeonNameID"]));
        Assert.Equal("Combat Site", String(dungeon["dungeonName"]));
        Assert.Equal(500020L, Integer(dungeon["factionID"]));
        Assert.Equal(24L, Integer(dungeon["archetypeID"]));

        var archetypes = Assert.IsType<PyList>(await service.CallAsync(Request("DEGetArchetypes")));
        Assert.Equal(["Combat Sites", "Ore Anomalies"], archetypes.Items
            .Select(value => Assert.IsType<PyTuple>(value).Items[0]).Select(String).ToArray());
        Assert.Equal([24L, 27L], archetypes.Items
            .Select(value => Assert.IsType<PyTuple>(value).Items[1]).Select(Integer).ToArray());

        var factions = Assert.IsType<PyList>(await service.CallAsync(Request("DEGetFactions")));
        Assert.Equal([500010L, 500020L], factions.Items.Select(Integer).ToArray());
        var rooms = Assert.IsType<PyList>(await service.CallAsync(Request("DEGetRooms", [],
            ("dungeonID", new PyInt(102)))));
        Assert.Equal([1L, 2L], rooms.Items.Select(value => Integer(Assert.IsType<PyTuple>(value).Items[0])).ToArray());
        Assert.Same(palette, await service.CallAsync(Request("DEGetRoomObjectPaletteData")));

        var positional = Assert.IsType<PyList>(await service.CallAsync(Request("DEGetDungeons", [new PyInt(100)])));
        Assert.Equal(100L, Integer(Fields(Assert.IsType<PyObject>(Assert.Single(positional.Items)))["dungeonID"]));
    }

    [Theory]
    [InlineData("GetBulwarkMiningSiteCounts")]
    [InlineData("GetCombatAnomaliesCount")]
    [InlineData("GetCombatAnomalyInstances")]
    [InlineData("GetFactionWarfareBattlefieldInstances")]
    [InlineData("GetFactionWarfareInstances")]
    [InlineData("GetHomefrontSiteInstances")]
    [InlineData("GetIceBeltInstances")]
    [InlineData("GetIceBeltsCount")]
    [InlineData("GetOreAnomaliesCount")]
    [InlineData("GetOreAnomaliesCountInRange")]
    [InlineData("GetOreAnomalyInstances")]
    [InlineData("GetPirateInsurgencyIceHeistInstances")]
    [InlineData("GetPirateInsurgencyInstances")]
    [InlineData("GetSignatureInstances")]
    [InlineData("GetTriglavianSiteInstances")]
    public async Task Instance_cache_registers_all_EveJS_methods_with_empty_dict_shapes(string method)
    {
        var service = new DungeonInstanceCacheMgrService();

        Assert.Equal("dungeonInstanceCacheMgr", service.Name);
        Assert.True(service.HasMethod(method));
        var result = Assert.IsType<PyDict>(await service.CallAsync(Request(method)));
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Instance_cache_projects_site_archetypes_counts_signatures_and_ice_authority()
    {
        const long systemA = 30000142;
        const long systemB = 30000148;
        var catalog = new DungeonEditorCatalog(
            [
                new DungeonEditorDungeon(100, null, "Combat", 500010, 24),
                new DungeonEditorDungeon(101, null, "Ore", 500020, 27),
                new DungeonEditorDungeon(102, null, "Ice", 500030, 28),
                new DungeonEditorDungeon(103, null, "Battlefield", 500010, 68),
                new DungeonEditorDungeon(104, null, "Homefront", 500020, 70),
                new DungeonEditorDungeon(105, null, "Ice Heist", 500030, 74),
                new DungeonEditorDungeon(106, null, "Triglavian", 500030, 65),
                new DungeonEditorDungeon(14036, null, "Bulwark", 500010, 27),
            ],
            [], [], new Dictionary<long, IReadOnlyList<DungeonEditorRoom>>(), new PyDict([]));
        var instances = new[]
        {
            Site(1, systemA, 100, 24, position: new JsonObject { ["x"] = 1.5, ["y"] = 2.5, ["z"] = 3.5 }),
            Site(2, systemA, 100, 24, scope: "private"),
            Site(3, systemA, 100, 24, state: "completed"),
            Site(4, 30000143, 103, 68, factionId: 500010),
            Site(5, 30000144, 104, 70, state: "paused"),
            Site(6, 30000145, 105, 74),
            Site(7, 30000146, 106, 65),
            Site(8, 30000147, 101, 27),
            Site(9, 30000147, 14036, 27),
            Site(10, systemA, 102, 28, family: "ice", origin: "generatedMining",
                metadata: new JsonObject { ["siteID"] = 700 }),
            Site(11, systemB, 0, 0, kind: "signature"),
            Site(12, systemB, 102, 28, state: "depleted", family: "ice", origin: "generatedMining",
                reason: "depleted", timers: new JsonObject { ["expiresAtMs"] = 1500 }),
        };
        var snapshot = new DungeonCacheSnapshot(
            catalog,
            instances,
            new Dictionary<long, int> { [30000147] = 8 },
            new Dictionary<long, DungeonIceSystemAuthority>
            {
                [systemA] = new(102, 3),
                [systemB] = new(102, 2),
            },
            new Dictionary<long, int> { [systemB] = 2 });
        var service = new DungeonInstanceCacheMgrService(
            _ => ValueTask.FromResult(snapshot), () => 1000);

        var combat = Assert.IsType<PyDict>(await service.CallAsync(Request("GetCombatAnomalyInstances")));
        var combatRows = Assert.IsType<PyList>(ByKey(combat, systemA));
        var combatRow = Fields(Assert.IsType<PyObject>(Assert.Single(combatRows.Items)));
        Assert.Equal(1L, Integer(combatRow["instanceID"]));
        Assert.Equal(100L, Integer(combatRow["dungeonID"]));
        Assert.Equal([1.5, 2.5, 3.5], Assert.IsType<PyList>(combatRow["position"]).Items.Select(Real).ToArray());

        Assert.Equal(1L, Count(await service.CallAsync(Request("GetCombatAnomaliesCount")), systemA));
        Assert.Equal(1, RowCount(await service.CallAsync(Request("GetFactionWarfareInstances")), 30000143));
        Assert.Equal(1, RowCount(await service.CallAsync(Request("GetFactionWarfareBattlefieldInstances")), 30000143));
        Assert.Equal(1, RowCount(await service.CallAsync(Request("GetHomefrontSiteInstances")), 30000144));
        Assert.Equal(1, RowCount(await service.CallAsync(Request("GetPirateInsurgencyInstances")), 30000145));
        Assert.Equal(1, RowCount(await service.CallAsync(Request("GetPirateInsurgencyIceHeistInstances")), 30000145));
        Assert.Equal(1, RowCount(await service.CallAsync(Request("GetTriglavianSiteInstances")), 30000146));

        var oreCounts = Assert.IsType<PyDict>(await service.CallAsync(Request("GetOreAnomaliesCount")));
        Assert.Equal(2L, Count(oreCounts, 30000147));
        Assert.Equal(2L, Count(await service.CallAsync(Request("GetOreAnomaliesCountInRange")), 30000147));
        Assert.Equal(2, RowCount(await service.CallAsync(Request("GetOreAnomalyInstances")), 30000147));
        Assert.Equal(1L, Count(await service.CallAsync(Request("GetBulwarkMiningSiteCounts")), 30000147));

        var iceRows = Assert.IsType<PyDict>(await service.CallAsync(Request("GetIceBeltInstances")));
        var iceRow = Fields(Assert.IsType<PyObject>(Assert.Single(Assert.IsType<PyList>(ByKey(iceRows, systemA)).Items)));
        Assert.Equal(700L, Integer(iceRow["siteID"]));
        Assert.Equal("BYH-661", Assert.IsType<PyStr>(iceRow["targetID"]).Value);
        var iceCounts = Assert.IsType<PyDict>(await service.CallAsync(Request("GetIceBeltsCount")));
        Assert.Equal(1L, Count(iceCounts, systemA));
        Assert.Equal(1L, Count(iceCounts, systemB));

        Assert.Equal(3L, Count(await service.CallAsync(Request("GetSignatureInstances")), systemB));
    }

    private static JsonObject Site(
        long instanceId,
        long systemId,
        long dungeonId,
        long archetypeId,
        long factionId = 0,
        string kind = "anomaly",
        string scope = "shared",
        string state = "active",
        string family = "",
        string origin = "",
        string reason = "",
        JsonObject? metadata = null,
        JsonObject? timers = null,
        JsonObject? position = null)
    {
        var site = new JsonObject
        {
            ["instanceID"] = instanceId,
            ["solarSystemID"] = systemId,
            ["sourceDungeonID"] = dungeonId,
            ["archetypeID"] = archetypeId,
            ["factionID"] = factionId,
            ["siteKind"] = kind,
            ["instanceScope"] = scope,
            ["lifecycleState"] = state,
            ["difficulty"] = 2,
            ["siteFamily"] = family,
            ["siteOrigin"] = origin,
            ["lifecycleReason"] = reason,
        };
        if (metadata is not null)
            site["metadata"] = metadata;
        if (timers is not null)
            site["timers"] = timers;
        if (position is not null)
            site["position"] = position;
        return site;
    }

    private static ServiceCall Request(
        string method,
        IReadOnlyList<PyValue>? args = null,
        params (string Key, PyValue Value)[] kwargs) => new()
    {
        Method = method,
        Args = args ?? [],
        Session = new ClientSession(),
        Kwargs = new PyDict(kwargs.Select(pair =>
            new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Key), pair.Value)).ToArray()),
    };

    private static Dictionary<string, PyValue> Fields(PyObject value) =>
        Assert.IsType<PyDict>(value.Args).Entries.ToDictionary(entry => String(entry.Key), entry => entry.Value);

    private static PyValue ByKey(PyDict dict, long key) => dict.Entries.Single(entry => Integer(entry.Key) == key).Value;

    private static long Count(PyValue value, long systemId) =>
        Integer(ByKey(Assert.IsType<PyDict>(value), systemId));

    private static int RowCount(PyValue value, long systemId) =>
        Assert.IsType<PyList>(ByKey(Assert.IsType<PyDict>(value), systemId)).Items.Count;

    private static long Integer(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an integer, got {value.GetType().Name}.")
    };

    private static double Real(PyValue value) => value switch
    {
        PyReal real => real.Value,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected a number, got {value.GetType().Name}.")
    };

    private static string String(PyValue value) => Assert.IsType<PyStr>(value).Value;
}
