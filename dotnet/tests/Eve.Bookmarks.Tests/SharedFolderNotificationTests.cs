using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Bookmarks.Tests;

/// <summary>
/// The shared folder notifications: the characters with the folder active hear a change after the response (not the actor), a character
/// whose lists no longer give a view hears nothing more, and a deleted folder reaches the characters that had it active.
/// </summary>
public sealed class SharedFolderNotificationTests
{
    private const long Alice = 90000001;
    private const long Bob = 90000002;
    private const long Carol = 90000003;
    private const long AdminList = 2000000001;
    private const long ManageList = 2000000002;
    private const long UseList = 2000000003;
    private const long ViewList = 2000000004;
    private static readonly BookmarkTarget Station = new(60000001, 1529, 30000142);

    [PgFact]
    public async Task A_change_reaches_the_active_viewers_after_the_response_and_stops_when_their_lists_stop_giving_a_view()
    {
        await using var rig = await Rig.CreateAsync();
        var lists = new Dictionary<long, AccessLists>
        {
            [Alice] = new(new Dictionary<long, int> { [AdminList] = 2, [ManageList] = 2, [UseList] = 2, [ViewList] = 2 }),
            [Bob] = new(new Dictionary<long, int> { [ViewList] = 0 }),
        };
        var sessions = new CharacterSessions();
        var aliceSink = new Sink();
        var bobSink = new Sink();
        var alice = Connect(sessions, Alice, aliceSink);
        var bob = Connect(sessions, Bob, bobSink);
        var service = Service(rig, sessions, lists);

        var folder = Num(Fields(await Call(service, alice, "AddFolder", PyValue.Of(false), new PyWString("Shared"), new PyWString(""),
            new PyLong(AdminList), new PyLong(ManageList), new PyLong(UseList), new PyLong(ViewList)))["folderID"]);
        await Call(service, bob, "AddToKnownFolders", new PyLong(folder), PyValue.Of(true));
        Assert.Empty(bobSink.Packets);

        await Call(service, alice, "BookmarkStaticLocation", new PyLong(Station.ItemId), new PyLong(folder), new PyStr("Jita"), new PyStr(""), new PyInt(0));
        Assert.Empty(bobSink.Packets);
        alice.RunAfterResponse();
        var heard = Assert.Single(Notifications(bobSink));
        Assert.Equal("OnSharedBookmarksFolderUpdated", heard.Name);
        var payload = Assert.IsType<PyList>(Assert.IsType<PyTuple>(heard.Args).Items[0]);
        var update = Assert.IsType<PyList>(Assert.Single(payload.Items));
        Assert.Equal(folder, Num(Assert.Single(Assert.IsType<PyList>(update.Items[0]).Items)));
        Assert.Equal("bookmarksAdded", Text(update.Items[1]));
        Assert.Empty(Notifications(aliceSink));

        lists[Bob] = new(new Dictionary<long, int>());
        await Call(service, alice, "BookmarkStaticLocation", new PyLong(Station.ItemId), new PyLong(folder), new PyStr("Amarr"), new PyStr(""), new PyInt(0));
        alice.RunAfterResponse();
        Assert.Single(Notifications(bobSink));
    }

    [PgFact]
    public async Task Deleting_the_folder_reaches_the_characters_that_had_it_active()
    {
        await using var rig = await Rig.CreateAsync();
        var lists = new Dictionary<long, AccessLists>
        {
            [Alice] = new(new Dictionary<long, int> { [AdminList] = 2, [ManageList] = 2, [UseList] = 2, [ViewList] = 2 }),
            [Bob] = new(new Dictionary<long, int> { [ViewList] = 0 }),
            [Carol] = new(new Dictionary<long, int> { [UseList] = 0 }),
        };
        var sessions = new CharacterSessions();
        var aliceSink = new Sink();
        var bobSink = new Sink();
        var carolSink = new Sink();
        var alice = Connect(sessions, Alice, aliceSink);
        var bob = Connect(sessions, Bob, bobSink);
        Connect(sessions, Carol, carolSink);
        var service = Service(rig, sessions, lists);
        var folder = Num(Fields(await Call(service, alice, "AddFolder", PyValue.Of(false), new PyWString("Shared"), new PyWString(""),
            new PyLong(AdminList), new PyLong(ManageList), new PyLong(UseList), new PyLong(ViewList)))["folderID"]);
        await Call(service, bob, "AddToKnownFolders", new PyLong(folder), PyValue.Of(true));

        await Call(service, alice, "DeleteFolder", new PyLong(folder));
        alice.RunAfterResponse();

        var heard = Assert.Single(Notifications(bobSink));
        Assert.Equal("OnSharedBookmarksFolderDeleted", heard.Name);
        Assert.Equal(folder, Num(Assert.Single(Assert.IsType<PyTuple>(heard.Args).Items)));
        Assert.Empty(Notifications(carolSink));
        Assert.Empty(Notifications(aliceSink));
    }

    private static IService Service(Rig rig, CharacterSessions sessions, Dictionary<long, AccessLists> lists) =>
        new BookmarkRefusals(new AccessGroupBookmarkMgrService(rig.Store, id => id == Station.ItemId ? Station : null,
            (who, _) => ValueTask.FromResult(lists.TryGetValue(who.CharacterId, out var found) ? found : AccessLists.Empty), sessions));

    private static ClientSession Connect(CharacterSessions sessions, long characterId, Sink sink)
    {
        var session = new ClientSession(sink) { CharacterId = characterId };
        session.Set("corpid", new PyInt(98000001));
        sessions.Enter(characterId, session);
        return session;
    }

    private static async Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    private static Dictionary<string, PyValue> Fields(PyValue keyVal) =>
        ((PyDict)((PyObject)keyVal).Args).Entries.ToDictionary(e => Eve.Macho.JsSemantics.StringValue(e.Key)!, e => e.Value);

    private static long Num(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException($"not a number: {value}"),
    };

    private static string Text(PyValue value) => Eve.Macho.JsSemantics.StringValue(value) ?? "";

    /// <summary>The name and the arguments of each notification the session received.</summary>
    private static List<(string Name, PyValue Args)> Notifications(Sink sink)
    {
        var heard = new List<(string, PyValue)>();
        foreach (var packet in sink.Packets)
        {
            var nodes = Walk(packet).ToList();
            if (nodes.OfType<PyStr>().FirstOrDefault(node => node.Value.StartsWith("OnShared", StringComparison.Ordinal)) is not { } name) continue;
            var stream = Assert.IsType<PyTuple>(MarshalCodec.Decode(nodes.OfType<PyBuffer>().Single().Value));
            heard.Add((name.Value, Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(stream.Items[1]).Items[1])));
        }
        return heard;
    }

    private static IEnumerable<PyValue> Walk(PyValue value)
    {
        yield return value;
        switch (value)
        {
            case PyTuple tuple:
                foreach (var item in tuple.Items) foreach (var node in Walk(item)) yield return node;
                break;
            case PyList list:
                foreach (var item in list.Items) foreach (var node in Walk(item)) yield return node;
                break;
            case PyObject obj:
                foreach (var node in Walk(obj.Args)) yield return node;
                break;
        }
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
