using System.Text;
using Eve.Bookmarks;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
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
            _ => null,
            (_, _) => ValueTask.FromResult(AccessLists.Empty),
            null));
        var calls = new (string Method, PyValue[] Args)[]
        {
            ("AddFolder", [PyBool.True, new PyStr("folder"), new PyStr(""), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]),
            ("AddToKnownFolders", [new PyLong(10), PyBool.True]),
            ("BookmarkStaticLocation", [new PyLong(10), new PyLong(10), new PyStr("bookmark"), new PyStr(""), new PyInt(0), PyNone.Instance]),
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
            var request = Packets.Call(open, index + 1, "accessGroupBookmarkMgr", call.Method, call.Args);
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session);
            var response = Assert.Single(sink.Packets);
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
            var wireText = Encoding.ASCII.GetString(MarshalCodec.Encode(response));
            Assert.Contains(call.Method == "BookmarkStaticLocation" ? "BookmarkNotAvailable" : "CustomNotify", wireText);
        }
    }
}
