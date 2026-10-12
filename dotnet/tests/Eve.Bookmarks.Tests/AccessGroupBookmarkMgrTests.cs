using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Bookmarks.Tests;

/// <summary>The RPC shapes of <c>accessGroupBookmarkMgr</c>: the lists and tuples the client unpacks, and the user errors.</summary>
public sealed class AccessGroupBookmarkMgrTests
{
    private const long CharacterId = 90000003;
    private static readonly BookmarkTarget Station = new(60000001, 1529, 30000142);

    [PgFact]
    public async Task The_active_bookmarks_answer_is_three_lists_folders_bookmarks_and_subfolders()
    {
        await using var rig = await Rig.CreateAsync();
        var service = Service(rig, resolve: id => id == Station.ItemId ? Station : null);
        var session = Session();

        var answer = Assert.IsType<PyList>(await Call(service, session, "GetMyActiveBookmarks"));

        Assert.Equal(3, answer.Items.Count);
        var folders = Assert.IsType<PyList>(answer.Items[0]);
        var folder = Assert.IsType<PyObject>(Assert.Single(folders.Items));
    }

    [PgFact]
    public async Task A_station_bookmark_answers_the_eight_part_tuple_and_an_unknown_place_is_refused_by_its_code()
    {
        await using var rig = await Rig.CreateAsync();
        var service = Service(rig, resolve: id => id == Station.ItemId ? Station : null);
        var session = Session();

        var reply = Assert.IsType<PyTuple>(await Call(service, session, "BookmarkStaticLocation",
            new PyLong(Station.ItemId), new PyLong(await DefaultFolderAsync(rig)), new PyStr("Jita"), new PyStr(""), new PyInt(0)));
        Assert.Equal(8, reply.Items.Count);
        Assert.Equal(PyNone.Instance, reply.Items[3]);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Call(service, session, "BookmarkStaticLocation", new PyLong(99999999999), new PyLong(await DefaultFolderAsync(rig))));
        Assert.Equal("BookmarkNotAvailable", error.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task Deleting_bookmarks_answers_the_ids_that_were_removed()
    {
        await using var rig = await Rig.CreateAsync();
        var service = Service(rig, resolve: id => id == Station.ItemId ? Station : null);
        var session = Session();
        var folder = await DefaultFolderAsync(rig);
        var made = Assert.IsType<PyTuple>(await Call(service, session, "BookmarkStaticLocation",
            new PyLong(Station.ItemId), new PyLong(folder), new PyStr("Jita"), new PyStr(""), new PyInt(0)));
        var bookmarkId = Assert.IsType<PyInt>(made.Items[0]).Value;

        var removed = Assert.IsType<PyList>(await Call(service, session, "DeleteBookmarks", new PyLong(folder),
            new PyList([new PyInt(bookmarkId)])));

        var id = Assert.IsType<PyInt>(Assert.Single(removed.Items));
        Assert.Equal(bookmarkId, id.Value);
    }

    private static AccessGroupBookmarkMgrService CreateService(Rig rig, Func<long, BookmarkTarget?> resolve) =>
        new(rig.Store, resolve);

    private static IService Service(Rig rig, Func<long, BookmarkTarget?> resolve) =>
        new BookmarkRefusals(CreateService(rig, resolve));

    private static ClientSession Session()
    {
        var session = new ClientSession { CharacterId = CharacterId };
        return session;
    }

    private static async Task<long> DefaultFolderAsync(Rig rig) =>
        (await rig.Store.ActiveAsync(CharacterId)).Folders.Single().Folder.FolderId;

    private static async Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });
}
