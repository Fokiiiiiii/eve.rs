using System.Text;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Dungeon_and_dynamic_resource_cache_calls_round_trip_through_host_registration()
    {
        const long systemId = 30000142;
        var catalog = new DungeonEditorCatalog(
            [new DungeonEditorDungeon(5001, 7001, "Combat Site", 500010, 68),
             new DungeonEditorDungeon(5002, 7002, "Ice Heist", 500011, 74)],
            [new DungeonEditorOption("Battlefields", 68), new DungeonEditorOption("Ice Heists", 74)],
            [500010, 500011],
            new Dictionary<long, IReadOnlyList<DungeonEditorRoom>>(),
            new PyDict([]));
        var snapshot = new DungeonCacheSnapshot(
            catalog,
            [
                Site(801, systemId, 5001, 68, "anomaly"),
                Site(802, systemId + 1, 5002, 74, "anomaly"),
                Site(803, systemId + 2, 5001, 68, "signature"),
            ],
            new Dictionary<long, int>(),
            new Dictionary<long, DungeonIceSystemAuthority>(),
            new Dictionary<long, int>());
        var resourceSystems = new[]
        {
            new EssMgrSystemData(1, systemId, 1.75, 100, 20),
            new EssMgrSystemData(2, systemId + 1, 0, 0, 0),
        };

        Start(strictRpc: true, configure: registry =>
        {
            registry.Register(new DungeonService(_ => ValueTask.FromResult(catalog)));
            registry.Register(new DungeonInstanceCacheMgrService(
                _ => ValueTask.FromResult(snapshot), () => FixedNowMs));
            registry.Register(new DynamicResourceCacheMgrService(
                listSystems: _ => ValueTask.FromResult<IReadOnlyList<EssMgrSystemData>>(resourceSystems)));
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        long callId = 0;
        async Task<PyValue> Call(string service, string method, params PyValue[] args)
        {
            var id = Interlocked.Increment(ref callId);
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.Call(open, id, service, method, args))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)) ?? PyNone.Instance;
        }

        var archetypes = Assert.IsType<PyList>(await Call("dungeon", "DEGetArchetypes"));
        Assert.Equal(2, archetypes.Items.Count);
        var battlefieldOption = Assert.IsType<PyTuple>(archetypes.Items[0]);
        Assert.Equal("Battlefields", Text(battlefieldOption.Items[0]));
        Assert.Equal(new PyInt(68), battlefieldOption.Items[1]);
        var iceOption = Assert.IsType<PyTuple>(archetypes.Items[1]);
        Assert.Equal("Ice Heists", Text(iceOption.Items[0]));
        Assert.Equal(new PyInt(74), iceOption.Items[1]);
        Assert.Equal(new PyList([new PyInt(500010), new PyInt(500011)]),
            await Call("dungeon", "DEGetFactions"));

        var escalation = Assert.IsType<PyObject>(await Call("dungeonExplorationMgr", "GetMyEscalatingPathDetails"));
        var cached = Assert.IsType<PyTuple>(escalation.Args);
        var rowset = Assert.IsType<PyObject>(Assert.IsType<PySubStream>(cached.Items[1]).Decoded);
        Assert.Equal(
            new[] { "instanceID", "dungeon", "destDungeon", "srcDungeon", "expiryTime", "journalEntryTemplateID" },
            Assert.IsType<PyList>(RowField(rowset, "header")).Items.Select(Text));

        var battlefields = Assert.IsType<PyDict>(
            await Call("dungeonInstanceCacheMgr", "GetFactionWarfareBattlefieldInstances"));
        var battlefield = Assert.IsType<PyList>(EntryValue(battlefields, systemId));
        Assert.Equal(new PyInt(801), RowField(Assert.IsType<PyObject>(battlefield.Items[0]), "instanceID"));

        var iceHeists = Assert.IsType<PyDict>(
            await Call("dungeonInstanceCacheMgr", "GetPirateInsurgencyIceHeistInstances"));
        Assert.Equal(new PyInt(802),
            RowField(Assert.IsType<PyObject>(Assert.IsType<PyList>(EntryValue(iceHeists, systemId + 1)).Items[0]), "instanceID"));

        var signatures = Assert.IsType<PyDict>(
            await Call("dungeonInstanceCacheMgr", "GetSignatureInstances"));
        Assert.Equal(new PyInt(1), EntryValue(signatures, systemId + 2));

        var settings = Assert.IsType<PyDict>(
            await Call("dynamicResourceCacheMgr", "GetDynamicResourceSettings"));
        Assert.Equal(new PyInt(1), EntryValue(settings, "minOutput"));
        Assert.Equal(new PyInt(2), EntryValue(settings, "maxOutput"));
        Assert.Equal(new PyReal(1.5), EntryValue(settings, "equilibriumValue"));
        var outputBySystem = Assert.IsType<PyDict>(
            await Call("dynamicResourceCacheMgr", "GetDBSMapData"));
        Assert.Equal(new PyReal(1.75), EntryValue(outputBySystem, systemId));
        Assert.DoesNotContain(outputBySystem.Entries, entry => entry.Key == new PyInt(systemId + 1));

        Assert.Equal(new PyTuple([new PyInt(0), new PyInt(0)]),
            await Call("dynamicBountyMgr", "GetOutputForClientSolarSystem"));
    }

    private static JsonObject Site(long instanceId, long systemId, long dungeonId, long archetypeId, string kind) =>
        new()
        {
            ["instanceID"] = instanceId,
            ["solarSystemID"] = systemId,
            ["dungeonID"] = dungeonId,
            ["sourceDungeonID"] = dungeonId,
            ["archetypeID"] = archetypeId,
            ["siteKind"] = kind,
            ["instanceScope"] = "shared",
            ["lifecycleState"] = "active",
        };

    private static PyValue EntryValue(PyDict dictionary, long key) =>
        dictionary.Entries.Single(entry => entry.Key == new PyInt(key)).Value;

    private static PyValue EntryValue(PyDict dictionary, string key) =>
        dictionary.Entries.Single(entry => Text(entry.Key) == key).Value;

    private static PyValue RowField(PyObject value, string name) =>
        ((PyDict)value.Args).Entries.Single(entry => Text(entry.Key) == name).Value;

    private static string Text(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        _ => throw new InvalidOperationException($"Expected a wire string, got {value.GetType().Name}."),
    };
}
