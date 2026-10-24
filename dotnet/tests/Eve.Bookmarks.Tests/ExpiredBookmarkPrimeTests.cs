using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Bookmarks.Tests;

/// <summary>Client wire coverage for shared bookmarks expiring during GetMyActiveBookmarks.</summary>
public sealed class ExpiredBookmarkPrimeTests
{
    private const long Alice = 90000001;
    private const long Bob = 90000002;
    private const long AdminList = 2000000001;
    private const long ViewList = 2000000004;
    private static readonly BookmarkTarget Station = new(60000001, 1529, 30000142);

    [PgFact]
    public async Task Priming_removes_expired_shared_bookmarks_and_notifies_other_active_viewers_after_the_reply()
    {
        await using var rig = await Rig.CreateAsync();
        var aliceLists = new AccessLists(new Dictionary<long, int> { [AdminList] = 2, [ViewList] = 2 });
        var bobLists = new AccessLists(new Dictionary<long, int> { [ViewList] = 0 });
        var sessions = new CharacterSessions();
        var aliceSink = new Sink();
        var bobSink = new Sink();
        var alice = Connect(sessions, Alice, aliceSink);
        var bob = Connect(sessions, Bob, bobSink);
        var lists = new Dictionary<long, AccessLists> { [Alice] = aliceLists, [Bob] = bobLists };
        var groups = new SharedGroups(AdminList, null, null, ViewList);
        var folder = (await rig.Store.AddFolderAsync(Alice, false, "Shared", "", groups, aliceLists)).Folder.FolderId;
        await rig.Store.AddKnownFolderAsync(Bob, folder, true, bobLists);
        var bookmark = await rig.Store.CreateBookmarkAsync(Alice, folder, Station, "short", "", BookmarkRules.Expiry4Hours, null, aliceLists);
        var notifier = new SharedFolderNotifier(rig.Store,
            (who, _) => ValueTask.FromResult(lists.GetValueOrDefault(who.CharacterId) ?? AccessLists.Empty), sessions);
        IService service = new BookmarkRefusals(new AccessGroupBookmarkMgrService(
            rig.Store,
            id => id == Station.ItemId ? Station : null,
            (who, _) => ValueTask.FromResult(lists.GetValueOrDefault(who.CharacterId) ?? AccessLists.Empty),
            sessions,
            notifier));

        rig.Now += 5L * 3_600_000;
        var result = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetMyActiveBookmarks",
            Args = [],
            Session = bob,
        }));

        Assert.Equal(3, result.Items.Count);
        Assert.Empty(Assert.IsType<PyList>(result.Items[1]).Items);
        Assert.Empty(aliceSink.Packets);
        Assert.Empty(bobSink.Packets);
        bob.RunAfterResponse();

        var notification = Assert.IsType<PyObject>(Assert.Single(aliceSink.Packets));
        var stream = Assert.IsType<PyTuple>(MarshalCodec.Decode(Walk(notification).OfType<PyBuffer>().Single().Value));
        var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(stream.Items[1]).Items[1]);
        var update = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(args.Items[0]).Items));
        Assert.Equal(folder, Number(Assert.Single(Assert.IsType<PyList>(update.Items[0]).Items)));
        Assert.Equal("bookmarksRemoved", Text(update.Items[1]));
        Assert.Equal(bookmark.BookmarkId, Number(Assert.Single(Assert.IsType<PyList>(update.Items[2]).Items)));
        Assert.Empty(bobSink.Packets);
    }

    private static ClientSession Connect(CharacterSessions sessions, long id, Sink sink)
    {
        var session = new ClientSession(sink) { CharacterId = id };
        sessions.Enter(id, session);
        return session;
    }

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException($"not an integer: {value}"),
    };

    private static string Text(PyValue value) => Eve.Macho.JsSemantics.StringValue(value) ?? "";

    private static IEnumerable<PyValue> Walk(PyValue value)
    {
        yield return value;
        switch (value)
        {
            case PyTuple tuple:
                foreach (var item in tuple.Items) foreach (var nested in Walk(item)) yield return nested;
                break;
            case PyList list:
                foreach (var item in list.Items) foreach (var nested in Walk(item)) yield return nested;
                break;
            case PyObject obj:
                foreach (var nested in Walk(obj.Args)) yield return nested;
                break;
        }
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
