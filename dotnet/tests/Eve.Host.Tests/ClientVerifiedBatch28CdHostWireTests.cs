using System.Text;
using System.Text.Json.Nodes;
using Eve.Corporations;
using Eve.Marshal;
using Eve.Services;
using Eve.Macho;
using Eve.Services.Core;
using Eve.Services.Corporations;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Client_calls_for_chat_community_offers_employment_and_agency_cross_host_wire()
    {
        const long characterId = 90000001;
        const long instanceId = 700001;
        const long solarSystemId = 30000142;
        var community = new CommunityTrackMgrService();
        community.SetInstanceAttributes(instanceId, 42, 7);

        var offerTracker = new ContextualOfferMgrService();
        var content = new PyObjectEx(
            ObjectExKind.Ex1,
            new PyTuple([
                new PyToken("agency.common.contentdata.CustomContentData"),
                new PyList([
                    new PyLong(90001), new PyInt(15), new PyLong(90001), new PyInt(15),
                    new PyLong(solarSystemId), new PyLong(134273341290000000), PyNone.Instance,
                ]),
            ]),
            [], []);
        var agency = new CustomAgencyProviderService(new CustomAgencyProviderRpcCallbacks(
            ReadContentAsync: (_, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([content])));

        long employmentReadCharacterId = 0;
        var employmentHistory = new[]
        {
            new Employment(1, characterId, 500001, 1_700_000_000_000, null, false),
            new Employment(2, characterId, 500002, 1_600_000_000_000, 1_700_000_000_000, false),
        };
        var corporation = new CorporationService(null!, null!,
            readEmploymentHistory: (id, _) =>
            {
                employmentReadCharacterId = id;
                return ValueTask.FromResult<IReadOnlyList<Employment>>(employmentHistory);
            });

        Start(configure: registry =>
        {
            registry.Register(community);
            registry.Register(offerTracker);
            registry.Register(agency);
            registry.Register(corporation);
        }, sessionOpened: session =>
        {
            session.Set("charid", new PyInt(characterId));
            session.Set("accountRole", new PyLong(GameMasterRoles.Qa));
        });
        var (front, open) = await OpenFrontAsync();

        var token = Assert.IsType<PyBytes>(await HostRpcAsync(
            front, open, 1, "chatAuthenticationService", "GetAuthenticationToken"));
        var tokenText = Encoding.UTF8.GetString(token.Value.Span);
        Assert.Matches("^[0-9a-f]{64}$", tokenText);
        Assert.Equal(tokenText, Encoding.UTF8.GetString(Assert.IsType<PyBytes>(await HostRpcAsync(
            front, open, 2, "chatAuthenticationService", "GetAuthenticationToken")).Value.Span));

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 3, "communityTrackMgr", "change_qa_mode", PyBool.False));
        Assert.False(community.QaMode);
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 4, "communityTrackMgr", "change_qa_mode", PyBool.True));
        Assert.True(community.QaMode);

        var attributes = Assert.IsType<PyTuple>(await HostRpcAsync(
            front, open, 5, "communityTrackMgr", "get_instance_attributes_public", new PyLong(instanceId)));
        Assert.Equal(new PyLong(42), attributes.Items[0]);
        Assert.Equal(new PyLong(7), attributes.Items[1]);

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 6, "contextualOfferMgr", "TrackOfferBuyPress", new PyLong(33001)));
        var offerEvent = Assert.Single(offerTracker.Events);
        Assert.Equal("TrackOfferBuyPress", offerEvent.MethodName);
        Assert.Equal(33001, offerEvent.OfferId);
        Assert.Equal(characterId, offerEvent.CharacterId);

        var rowset = Assert.IsType<PyObject>(await HostRpcAsync(
            front, open, 7, "corporationSvc", "GetEmploymentRecord", new PyInt(characterId)));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", WireText(rowset.Name));
        var rowsetFields = Assert.IsType<PyDict>(rowset.Args);
        Assert.Equal(["corporationID", "startDate", "deleted"],
            Assert.IsType<PyList>(DictField(rowsetFields, "header")).Items.Select(WireText).ToArray());
        var lines = Assert.IsType<PyList>(DictField(rowsetFields, "lines")).Items;
        Assert.Equal(characterId, employmentReadCharacterId);
        Assert.Equal(2, lines.Count);
        var currentEmployment = Assert.IsType<PyList>(lines[0]).Items;
        Assert.Equal(500001, WireInteger(currentEmployment[0]));
        Assert.IsType<PyLong>(currentEmployment[1]);
        Assert.Equal(0, WireInteger(currentEmployment[2]));

        var contentList = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 8, "custom_agency_provider", "get_content_data"));
        var contentData = Assert.IsType<PyObjectEx>(Assert.Single(contentList.Items));
        var contentHeader = Assert.IsType<PyTuple>(contentData.Header).Items;
        Assert.Equal("agency.common.contentdata.CustomContentData",
            Assert.IsType<PyToken>(contentHeader[0]).Value);
        var contentArgs = Assert.IsType<PyList>(contentHeader[1]).Items;
        Assert.Equal(15, WireInteger(contentArgs[3]));
        Assert.Equal(solarSystemId, WireInteger(contentArgs[4]));
    }

    [Fact]
    public async Task Custom_agency_default_core_route_returns_empty_content_without_a_provider()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        Assert.Empty(Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 1, "custom_agency_provider", "get_content_data")).Items);
    }

    [Fact]
    public async Task Dungeon_anomaly_tracker_reads_keep_client_shapes_over_host_wire()
    {
        const long systemA = 30000142;
        const long systemB = 30000148;
        const long fwSystem = 30000143;
        const long homefrontSystem = 30000144;
        const long insurgencySystem = 30000145;
        const long triglavianSystem = 30000146;
        const long oreSystem = 30000147;
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
            DungeonSite(1, systemA, 100, 24, position: new JsonObject { ["x"] = 1.5, ["y"] = 2.5, ["z"] = 3.5 }),
            DungeonSite(2, systemA, 100, 24, scope: "private"),
            DungeonSite(3, systemA, 100, 24, state: "completed"),
            DungeonSite(4, fwSystem, 103, 68, factionId: 500010),
            DungeonSite(5, homefrontSystem, 104, 70, state: "paused"),
            DungeonSite(6, insurgencySystem, 105, 74),
            DungeonSite(7, triglavianSystem, 106, 65),
            DungeonSite(8, oreSystem, 101, 27),
            DungeonSite(9, oreSystem, 14036, 27),
            DungeonSite(10, systemA, 102, 28, family: "ice", origin: "generatedMining",
                metadata: new JsonObject { ["siteID"] = 700 }),
            DungeonSite(11, systemB, 0, 0, kind: "signature"),
            DungeonSite(12, systemB, 102, 28, state: "depleted", family: "ice", origin: "generatedMining",
                reason: "depleted", timers: new JsonObject { ["expiresAtMs"] = 1500 }),
        };
        var snapshot = new DungeonCacheSnapshot(
            catalog,
            instances,
            new Dictionary<long, int> { [oreSystem] = 8 },
            new Dictionary<long, DungeonIceSystemAuthority>
            {
                [systemA] = new(102, 3),
                [systemB] = new(102, 2),
            },
            new Dictionary<long, int> { [systemB] = 2 });
        var service = new DungeonInstanceCacheMgrService(
            _ => ValueTask.FromResult(snapshot), () => 1000);
        Start(configure: registry => registry.Register(service));
        var (front, open) = await OpenFrontAsync();

        var combat = DungeonRows(await HostRpcAsync(
            front, open, 1, "dungeonInstanceCacheMgr", "GetCombatAnomalyInstances"), systemA);
        var combatRow = Fields(Assert.IsType<PyObject>(Assert.Single(combat.Items)));
        Assert.Equal(1, WireInteger(combatRow["instanceID"]));
        Assert.Equal(100, WireInteger(combatRow["dungeonID"]));
        Assert.Equal([1.5, 2.5, 3.5], Assert.IsType<PyList>(combatRow["position"]).Items
            .Select(value => value is PyReal real ? real.Value : (double)WireInteger(value)).ToArray());

        Assert.Equal(1, DungeonCount(await HostRpcAsync(
            front, open, 2, "dungeonInstanceCacheMgr", "GetCombatAnomaliesCount"), systemA));
        var factionalWarfare = DungeonRows(await HostRpcAsync(
            front, open, 3, "dungeonInstanceCacheMgr", "GetFactionWarfareInstances"), fwSystem);
        var factionalRow = Fields(Assert.IsType<PyObject>(Assert.Single(factionalWarfare.Items)));
        Assert.Equal(500010, WireInteger(factionalRow["factionID"]));
        Assert.Single(DungeonRows(await HostRpcAsync(
            front, open, 4, "dungeonInstanceCacheMgr", "GetHomefrontSiteInstances"), homefrontSystem).Items);
        Assert.Single(DungeonRows(await HostRpcAsync(
            front, open, 5, "dungeonInstanceCacheMgr", "GetPirateInsurgencyInstances"), insurgencySystem).Items);
        Assert.Single(DungeonRows(await HostRpcAsync(
            front, open, 6, "dungeonInstanceCacheMgr", "GetTriglavianSiteInstances"), triglavianSystem).Items);

        var iceRows = DungeonRows(await HostRpcAsync(
            front, open, 7, "dungeonInstanceCacheMgr", "GetIceBeltInstances"), systemA);
        var iceRow = Fields(Assert.IsType<PyObject>(Assert.Single(iceRows.Items)));
        Assert.Equal(700, WireInteger(iceRow["siteID"]));
        Assert.Equal("BYH-661", WireText(iceRow["targetID"]));
        Assert.Equal(1, DungeonCount(await HostRpcAsync(
            front, open, 8, "dungeonInstanceCacheMgr", "GetIceBeltsCount"), systemA));
        Assert.Equal(1, DungeonCount(await HostRpcAsync(
            front, open, 9, "dungeonInstanceCacheMgr", "GetIceBeltsCount"), systemB));

        Assert.Equal(2, DungeonCount(await HostRpcAsync(
            front, open, 10, "dungeonInstanceCacheMgr", "GetOreAnomaliesCount"), oreSystem));
        Assert.Equal(2, DungeonCount(await HostRpcAsync(
            front, open, 11, "dungeonInstanceCacheMgr", "GetOreAnomaliesCountInRange"), oreSystem));
        Assert.Equal(2, DungeonRows(await HostRpcAsync(
            front, open, 12, "dungeonInstanceCacheMgr", "GetOreAnomalyInstances"), oreSystem).Items.Count);
        Assert.Equal(1, DungeonCount(await HostRpcAsync(
            front, open, 13, "dungeonInstanceCacheMgr", "GetBulwarkMiningSiteCounts"), oreSystem));
    }

    private static PyList DungeonRows(PyValue value, long systemId)
    {
        var dictionary = Assert.IsType<PyDict>(value);
        return Assert.IsType<PyList>(dictionary.Entries.Single(entry => WireInteger(entry.Key) == systemId).Value);
    }

    private static long DungeonCount(PyValue value, long systemId)
    {
        var dictionary = Assert.IsType<PyDict>(value);
        return WireInteger(dictionary.Entries.Single(entry => WireInteger(entry.Key) == systemId).Value);
    }

    private static JsonObject DungeonSite(
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
}
