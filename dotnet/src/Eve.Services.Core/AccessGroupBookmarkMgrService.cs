using Eve.Bookmarks;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The client's <c>accessGroupBookmarkMgr</c>: the character's bookmark folders, subfolders and bookmarks (the store is
/// <see cref="BookmarkStore"/>). The payloads are the reference's (<c>bookmarkPayloads.js</c>): a folder is a KeyVal, a bookmark is a
/// KeyVal, and a bookmark made from a place answers the eight-part tuple the client unpacks.
/// </summary>
[Service("accessGroupBookmarkMgr")]
public sealed partial class AccessGroupBookmarkMgrService(BookmarkStore store, Func<long, BookmarkTarget?> resolveStaticTarget)
{
    private const string DefaultFolderName = "My Locations";
    private const int StationTypeFallback = 5; // TYPE_SOLAR_SYSTEM, the reference's fallback type

    [Method("GetMyActiveBookmarks")]
    public async ValueTask<PyValue> GetMyActiveBookmarks(ServiceCall call)
    {
        var active = await store.ActiveAsync(Character(call), call.Cancellation);
        return new PyList([
            new PyList([.. active.Folders.Select(FolderPayload)]),
            new PyList([.. active.Bookmarks.Select(BookmarkPayload)]),
            new PyList([.. active.Subfolders.Select(SubfolderPayload)]),
        ]);
    }

    [Method("AddFolder")]
    public async ValueTask<PyValue> AddFolder(ServiceCall call)
    {
        var isPersonal = call.Args.Count == 0 || call.Arg(0) is PyNone || JsTruthy(call.Arg(0));
        var name = Text(call.Arg(1));
        var view = await store.AddFolderAsync(Character(call), isPersonal, name.Length == 0 ? DefaultFolderName : name, Text(call.Arg(2)), call.Cancellation);
        return FolderPayload(view);
    }

    /// <summary>Renames the folder; answers the character's access to it (the personal folder's).</summary>
    [Method("UpdateFolder")]
    public async ValueTask<PyValue> UpdateFolder(ServiceCall call)
    {
        var view = await store.UpdateFolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, Text(call.Arg(1)), Text(call.Arg(2)), call.Cancellation);
        return new PyInt(view.AccessLevel);
    }

    [Method("DeleteFolder")]
    public async ValueTask<PyValue> DeleteFolder(ServiceCall call)
    {
        await store.DeleteFolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetFolderInfo")]
    public async ValueTask<PyValue> GetFolderInfo(ServiceCall call) =>
        FolderPayload(await store.FolderInfoAsync(Character(call), Integral(call.Arg(0)) ?? 0, call.Cancellation));

    /// <summary>The character's own folders (the shared folders with admin access are not built).</summary>
    [Method("SearchFoldersWithAdminAccess")]
    public async ValueTask<PyValue> SearchFoldersWithAdminAccess(ServiceCall call)
    {
        var folders = await store.PersonalFoldersAsync(Character(call), call.Cancellation);
        return new PyList([.. folders.Where(view => view.Folder.IsPersonal).Select(FolderPayload)]);
    }

    [Method("AddToKnownFolders")]
    public async ValueTask<PyValue> AddToKnownFolders(ServiceCall call)
    {
        var opened = await store.AddKnownFolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, IsActiveArgument(call.Arg(1)), call.Cancellation);
        return OpenedPayload(opened);
    }

    [Method("RemoveFromKnownFolders")]
    public async ValueTask<PyValue> RemoveFromKnownFolders(ServiceCall call)
    {
        await store.RemoveKnownFolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateKnownFolderState")]
    public async ValueTask<PyValue> UpdateKnownFolderState(ServiceCall call)
    {
        var opened = await store.SetKnownFolderActiveAsync(Character(call), Integral(call.Arg(0)) ?? 0, IsActiveArgument(call.Arg(1)), call.Cancellation);
        return OpenedPayload(opened);
    }

    /// <summary>Bookmarks a station or a solar system: the bookmark's place is the static data's (<see cref="resolveStaticTarget"/>).</summary>
    [Method("BookmarkStaticLocation")]
    public async ValueTask<PyValue> BookmarkStaticLocation(ServiceCall call)
    {
        var itemId = Integral(call.Arg(0)) ?? 0;
        var target = resolveStaticTarget(itemId) ?? throw new BookmarkError("BookmarkNotAvailable");
        if (BookmarkRules.DisabledSystems.Contains(target.LocationId))
        {
            throw new BookmarkError("BookmarkNotAvailable", "Bookmarks are disabled in this system.");
        }
        var bookmark = await store.CreateBookmarkAsync(Character(call), Integral(call.Arg(1)) ?? 0, target, Text(call.Arg(2)), Text(call.Arg(3)),
            (int)(Integral(call.Arg(4)) ?? 0), Integral(call.Arg(5)), call.Cancellation);
        return ReplyTuple(bookmark);
    }

    /// <summary>
    /// Bookmarks the ship's place in space: the place needs the space scene, which the bookmark store does not read. Refused with the notice
    /// rather than answered with a made-up place.
    /// </summary>
    [Method("BookmarkLocation")]
    public ValueTask<PyValue> BookmarkLocation(ServiceCall call) =>
        throw Notify("Bookmarks of places in space are not available on this server.");

    [Method("UpdateBookmark")]
    public async ValueTask<PyValue> UpdateBookmark(ServiceCall call)
    {
        var folderId = Integral(call.Arg(1)) ?? 0;
        await store.UpdateBookmarkAsync(Character(call), Integral(call.Arg(0)) ?? 0, folderId, Text(call.Arg(2)), Text(call.Arg(3)),
            OptionalPositive(call.Arg(4)), Integral(call.Arg(5)) ?? folderId, Truthy(call.Arg(6)), call.Cancellation);
        return PyNone.Instance;
    }

    [Method("DeleteBookmarks")]
    public async ValueTask<PyValue> DeleteBookmarks(ServiceCall call)
    {
        var deleted = await store.DeleteBookmarksAsync(Character(call), Integral(call.Arg(0)) ?? 0, IdsOf(call.Arg(1)), call.Cancellation);
        return new PyList([.. deleted.Select(id => InventoryWire.Id(id))]);
    }

    [Method("MoveBookmarksToFolderAndSubfolder")]
    public async ValueTask<PyValue> MoveBookmarksToFolderAndSubfolder(ServiceCall call)
    {
        var moved = await store.MoveBookmarksAsync(Character(call), Integral(call.Arg(0)) ?? 0, Integral(call.Arg(1)) ?? 0,
            OptionalPositive(call.Arg(2)), IdsOf(call.Arg(3)), call.Cancellation);
        return new PyList([new PyList([.. moved.Select(BookmarkPayload)]), PyNone.Instance]);
    }

    /// <summary>The copies, keyed by their new bookmark ids.</summary>
    [Method("CopyBookmarksToFolderAndSubfolder")]
    public async ValueTask<PyValue> CopyBookmarksToFolderAndSubfolder(ServiceCall call)
    {
        var copies = await store.CopyBookmarksAsync(Character(call), Integral(call.Arg(0)) ?? 0, Integral(call.Arg(1)) ?? 0,
            OptionalPositive(call.Arg(2)), IdsOf(call.Arg(3)), call.Cancellation);
        return new PyList([new PyDict([.. copies.Select(copy => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(copy.BookmarkId), BookmarkPayload(copy)))]), PyNone.Instance]);
    }

    [Method("CreateSubfolder")]
    public async ValueTask<PyValue> CreateSubfolder(ServiceCall call) =>
        SubfolderPayload(await store.CreateSubfolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, Text(call.Arg(1)), call.Cancellation));

    [Method("UpdateSubfolder")]
    public async ValueTask<PyValue> UpdateSubfolder(ServiceCall call) =>
        PyValue.Of(await store.UpdateSubfolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, Integral(call.Arg(1)) ?? 0, Text(call.Arg(2)), call.Cancellation));

    [Method("DeleteSubfolder")]
    public async ValueTask<PyValue> DeleteSubfolder(ServiceCall call)
    {
        var removed = await store.DeleteSubfolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, Integral(call.Arg(1)) ?? 0, call.Cancellation);
        return new PyList([.. removed.Select(id => InventoryWire.Id(id))]);
    }

    private static long Character(ServiceCall call)
    {
        var id = call.Session.CharacterId ?? 0;
        return id > 0 ? id : throw Notify("Bookmarks need a character.");
    }

    private static long? OptionalPositive(PyValue value)
    {
        var id = Integral(value);
        return id is > 0 ? id : null;
    }

    private static bool IsActiveArgument(PyValue value) => value is PyNone || JsTruthy(value) || value is null;

    private static bool JsTruthy(PyValue value) => Eve.Macho.JsSemantics.Truthy(value);

    private static bool Truthy(PyValue value) => Eve.Macho.JsSemantics.Truthy(value);

    private static long? Integral(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value == Math.Floor(real.Value) => (long)real.Value,
        _ => null,
    };

    private static string Text(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        _ => "",
    };

    /// <summary>A bookmark id set: the client sends a Python set (an <c>ObjectEx</c> over <c>__builtin__.set</c>) or a list.</summary>
    private static IReadOnlyList<long> IdsOf(PyValue value) =>
        ArgsOf(value).Select(Integral).Where(id => id is > 0).Select(id => id!.Value).Distinct().ToList();

    private static IEnumerable<PyValue> ArgsOf(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        PyObjectEx { Header: PyTuple { Items.Count: 2 } header } ex when header.Items[0] is PyToken { Value: "__builtin__.set" } && header.Items[1] is PyTuple { Items.Count: 1 } payload
            => (payload.Items[0] is PyList first ? first.Items : []).Concat(ex.List),
        _ => [],
    };

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));

    private static PyValue FolderPayload(FolderView view) => Py.KeyVal(
        ("folderID", InventoryWire.Id(view.Folder.FolderId)),
        ("folderName", new PyWString(view.Folder.Name)),
        ("description", new PyWString(view.Folder.Description)),
        ("creatorID", InventoryWire.Id(view.Folder.OwnerId)),
        ("isPersonal", PyValue.Of(view.Folder.IsPersonal)),
        ("isActive", PyValue.Of(view.IsActive)),
        ("accessLevel", new PyInt(view.AccessLevel)),
        ("adminGroupID", PyNone.Instance),
        ("manageGroupID", PyNone.Instance),
        ("useGroupID", PyNone.Instance),
        ("viewGroupID", PyNone.Instance));

    /// <summary>What the client takes from an opened folder: the folder, its bookmarks and its subfolders.</summary>
    private static PyValue OpenedPayload(FolderOpened opened) => new PyList([
        FolderPayload(opened.Folder),
        new PyList([.. opened.Bookmarks.Select(BookmarkPayload)]),
        new PyList([.. opened.Subfolders.Select(SubfolderPayload)]),
    ]);

    private static PyValue SubfolderPayload(BookmarkSubfolder subfolder) => Py.KeyVal(
        ("subfolderID", InventoryWire.Id(subfolder.SubfolderId)),
        ("folderID", InventoryWire.Id(subfolder.FolderId)),
        ("subfolderName", new PyWString(subfolder.Name)),
        ("creatorID", InventoryWire.Id(subfolder.CreatorId)));

    private static PyValue BookmarkPayload(Bookmark bookmark) => Py.KeyVal(
        ("bookmarkID", InventoryWire.Id(bookmark.BookmarkId)),
        ("folderID", InventoryWire.Id(bookmark.FolderId)),
        ("itemID", InventoryWire.Id(bookmark.ItemId)),
        ("typeID", InventoryWire.Id(bookmark.TypeId == 0 ? StationTypeFallback : bookmark.TypeId)),
        ("flag", PyNone.Instance),
        ("memo", new PyWString(bookmark.Memo)),
        ("created", new PyLong(Py.FileTimeFromUnixMs(bookmark.CreatedMs))),
        ("expiry", bookmark.ExpiryMs is { } expiry ? new PyLong(Py.FileTimeFromUnixMs(expiry)) : PyNone.Instance),
        ("x", PyNone.Instance),
        ("y", PyNone.Instance),
        ("z", PyNone.Instance),
        ("locationID", InventoryWire.Id(bookmark.LocationId)),
        ("note", new PyWString(bookmark.Note)),
        ("subfolderID", bookmark.SubfolderId is { } sub ? InventoryWire.Id(sub) : PyNone.Instance),
        ("creatorID", InventoryWire.Id(bookmark.CreatorId)));

    /// <summary>The eight-part tuple the client unpacks from a new bookmark: id, item, type, x, y, z, location, expiry.</summary>
    private static PyValue ReplyTuple(Bookmark bookmark) => new PyTuple([
        InventoryWire.Id(bookmark.BookmarkId),
        InventoryWire.Id(bookmark.ItemId),
        InventoryWire.Id(bookmark.TypeId),
        PyNone.Instance,
        PyNone.Instance,
        PyNone.Instance,
        InventoryWire.Id(bookmark.LocationId),
        bookmark.ExpiryMs is { } expiry ? new PyLong(Py.FileTimeFromUnixMs(expiry)) : PyNone.Instance,
    ]);
}

/// <summary>
/// Answers a refusal of the bookmark store as the client's user error: the reference's code, or the notice the store gives. The store
/// keeps its rules free of the client's error types.
/// </summary>
public sealed class BookmarkRefusals(IService inner) : IService
{
    public string Name => inner.Name;

    public bool HasMethod(string method) => inner.HasMethod(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        try
        {
            return await inner.CallAsync(call);
        }
        catch (BookmarkError error)
        {
            throw error.Notice is null
                ? MachoErrors.ThrowUserError(error.Code)
                : MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(error.Notice)));
        }
    }
}
