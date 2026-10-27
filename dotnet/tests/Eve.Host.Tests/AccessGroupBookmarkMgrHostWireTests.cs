using System.Text;
using Eve.Bookmarks;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class AccessGroupBookmarkMgrHostWireTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public async Task Registered_bookmark_methods_reach_the_real_dispatcher_and_return_wire_errors_without_a_database()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=not-used;Username=not-used;Timeout=1");
        var service = new BookmarkRefusals(new AccessGroupBookmarkMgrService(
            new BookmarkStore(source, BookmarkStore.DefaultSchema, () => 1_790_646_377_000),
            (_, _) => ValueTask.FromResult<BookmarkTarget?>(null),
            (_, _) => ValueTask.FromResult(AccessLists.Empty),
            null));
        var calls = new (string Method, PyValue[] Args)[]
        {
            ("AddFolder", [PyBool.True, new PyStr("folder"), new PyStr(""), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]),
            ("AddToKnownFolders", [new PyLong(10), PyBool.True]),
            ("BookmarkStaticLocation", [new PyLong(10), new PyLong(10), new PyStr("bookmark"), new PyStr(""), new PyInt(0)]),
            ("CopyBookmarksToFolderAndSubfolder", [new PyLong(10), new PyLong(20), PyNone.Instance, new PyList([new PyLong(30)])]),
            ("CreateSubfolder", [new PyLong(10), new PyStr("subfolder")]),
            ("DeleteBookmarks", [new PyLong(10), new PyList([new PyLong(30)])]),
            ("DeleteFolder", [new PyLong(10)]),
            ("DeleteSubfolder", [new PyLong(10), new PyLong(20)]),
            ("GetFolderInfo", [new PyLong(10)]),
            ("GetMyActiveBookmarks", []),
            ("MoveBookmarksToFolderAndSubfolder", [new PyLong(10), new PyLong(20), PyNone.Instance, new PyList([new PyLong(30)])]),
            ("RemoveFromKnownFolders", [new PyLong(10)]),
            ("SearchFoldersWithAdminAccess", []),
            ("UpdateBookmark", [new PyLong(30), new PyLong(10), new PyStr("bookmark"), new PyStr(""), PyNone.Instance, new PyLong(10), PyBool.False]),
            ("UpdateFolder", [new PyLong(10), new PyStr("folder"), new PyStr(""), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]),
            ("UpdateKnownFolderState", [new PyLong(10), PyBool.True]),
            ("UpdateSubfolder", [new PyLong(10), new PyLong(20), new PyStr("subfolder")]),
        };

        var registry = new ServiceRegistry();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry);
        var sink = new Sink();
        var session = new ClientSession(sink);
        var open = new SessionOpen(1_790_646_377_000UL * 15, 42_000_042, 42, 0, "127.0.0.1", "EN", "GB");

        Assert.Equal(17, calls.Length);
        foreach (var (index, call) in calls.Select((call, index) => (index, call)))
        {
            Assert.True(service.HasMethod(call.Method), call.Method);
            sink.Packets.Clear();
            var kwargs = call.Method == "BookmarkStaticLocation"
                ? Kwargs(("machoVersion", new PyInt(1)), ("subfolderID", new PyLong(701)))
                : Kwargs(("machoVersion", new PyInt(1)));
            var request = Packets.CallWithKwargs(open, index + 1, "accessGroupBookmarkMgr", call.Method, new PyTuple(call.Args), kwargs);
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session);
            var response = Assert.Single(sink.Packets);
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
            var wireText = Encoding.ASCII.GetString(MarshalCodec.Encode(response));
            Assert.Contains(call.Method == "BookmarkStaticLocation" ? "BookmarkNotAvailable" : "CustomNotify", wireText);
        }
    }

    [Fact]
    public async Task Static_bookmark_targets_cover_system_parents_and_persisted_structures()
    {
        var stations = new StationCatalog([]);
        var systems = new SolarSystemCatalog([new SolarSystemInfo(30_000_142, "System", 20_000_001, 10_000_001, 0.5)]);
        var structure = new PersistedStructureInfo(1_000_000_001, true,
            Py.KeyVal(("typeID", new PyInt(35832)), ("solarSystemID", new PyInt(30_000_142))));
        ValueTask<PersistedStructureInfo?> ReadStructure(long id, CancellationToken _) =>
            ValueTask.FromResult<PersistedStructureInfo?>(id == structure.StructureId ? structure : null);

        Assert.Equal(new BookmarkTarget(20_000_001, 4, 20_000_001),
            await CharacterServicesComposition.ResolveStaticBookmarkTargetAsync(stations, systems, ReadStructure, 20_000_001, default));
        Assert.Equal(new BookmarkTarget(10_000_001, 3, 10_000_001),
            await CharacterServicesComposition.ResolveStaticBookmarkTargetAsync(stations, systems, ReadStructure, 10_000_001, default));
        Assert.Equal(new BookmarkTarget(structure.StructureId, 35832, 30_000_142),
            await CharacterServicesComposition.ResolveStaticBookmarkTargetAsync(stations, systems, ReadStructure, structure.StructureId, default));
        Assert.Null(await CharacterServicesComposition.ResolveStaticBookmarkTargetAsync(stations, systems, ReadStructure, 99, default));
    }

    [Fact]
    public void Bookmark_expiry_modes_follow_the_wiki_duration_and_keep_the_client_wire_values()
    {
        const long now = 1_800_000_000_000;
        Assert.Null(BookmarkRules.ExpiryMs(BookmarkRules.ExpiryNone, now));
        Assert.Equal(now + 3L * 3_600_000, BookmarkRules.ExpiryMs(BookmarkRules.Expiry4Hours, now));
        Assert.Equal(now + 48L * 3_600_000, BookmarkRules.ExpiryMs(BookmarkRules.Expiry2Days, now));
        Assert.Equal(now + 24L * 3_600_000, BookmarkRules.ExpiryMs(BookmarkRules.Expiry24Hours, now));
    }

    private static PyDict Kwargs(params (string Name, PyValue Value)[] entries) =>
        new([.. entries.Select(entry => new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Name), entry.Value))]);
}
