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
        var excessiveIds = new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyLong(id)).ToArray());
        var limitError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Call(service, session, "DeleteBookmarks", new PyInt(0), excessiveIds));
        Assert.Equal("BOOKMARK_BULK_LIMIT_EXCEEDED", limitError.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task A_folder_named_with_a_byte_string_keeps_its_name_as_the_client_sends_it()
    {
        // the client sends ASCII text as a byte string, which the decoder reads as PyBytes (LongString), not PyStr
        await using var rig = await Rig.CreateAsync();
        var service = Service(rig, resolve: _ => null);
        var session = Session();

        var folder = Assert.IsType<PyObject>(await Call(service, session, "AddFolder", PyValue.Of(true),
            new PyBytes("Walk folder"u8.ToArray()), new PyBytes("a personal folder"u8.ToArray())));
        var fields = ((PyDict)folder.Args).Entries.ToDictionary(e => Eve.Macho.JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal("Walk folder", Eve.Macho.JsSemantics.StringValue(fields["folderName"]));
        Assert.Equal("a personal folder", Eve.Macho.JsSemantics.StringValue(fields["description"]));
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
    public async Task A_place_in_a_system_with_bookmarks_disabled_is_refused_with_its_code()
    {
        // Zarzakh (30100000): "Bookmarks are disabled in system." (the wiki); the Fulcrum station is in it
        await using var rig = await Rig.CreateAsync();
        var fulcrum = new BookmarkTarget(60015187, 78334, 30100000);
        var service = Service(rig, resolve: id => id == fulcrum.ItemId ? fulcrum : null);
        var session = Session();

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Call(service, session, "BookmarkStaticLocation", new PyLong(fulcrum.ItemId), new PyLong(await DefaultFolderAsync(rig))));
        // a refusal with a notice reaches the client as CustomNotify (BookmarkRefusals)
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
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
        new(rig.Store, resolve, (_, _) => ValueTask.FromResult(AccessLists.Empty), null);

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
