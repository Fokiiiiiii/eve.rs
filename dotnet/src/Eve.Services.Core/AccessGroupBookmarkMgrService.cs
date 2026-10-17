using Eve.AccessGroups;
using Eve.Bookmarks;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The client's <c>accessGroupBookmarkMgr</c>: the bookmark folders (personal and shared), their subfolders and bookmarks. The store is
/// <see cref="BookmarkStore"/>; who may do what is <see cref="FolderAccess"/>, given the character's access lists (<c>listsOf</c>, the
/// owner groups of <see cref="AccessGroupStore"/>). The payloads are the reference's (<c>bookmarkPayloads.js</c>). A change to a shared
/// folder is heard after the response by the characters that have the folder active, as <c>OnSharedBookmarksFolderUpdated</c> (one update
/// per notification) or <c>OnSharedBookmarksFolderDeleted</c>.
/// </summary>
[Service("accessGroupBookmarkMgr")]
public sealed partial class AccessGroupBookmarkMgrService(BookmarkStore store, Func<long, BookmarkTarget?> resolveStaticTarget,
    Func<Affiliation, CancellationToken, ValueTask<AccessLists>> listsOf, CharacterSessions? sessions, SharedFolderNotifier? notifier = null)
{
    private const string DefaultFolderName = "My Locations";
    private const int StationTypeFallback = 5; // TYPE_SOLAR_SYSTEM, the reference's fallback type

    [Method("GetMyActiveBookmarks")]
    public async ValueTask<PyValue> GetMyActiveBookmarks(ServiceCall call)
    {
        var character = Character(call);
        var active = await store.ActiveAsync(character, await Lists(call), call.Cancellation);
        if (notifier is not null && active.Expired is { Count: > 0 } expired &&
            await notifier.PlanRemovalsAsync(expired, character, call.Cancellation) is { } send)
            call.Session.AfterResponse(send);
        return new PyList([
            new PyList([.. active.Folders.Select(FolderPayload)]),
            new PyList([.. active.Bookmarks.Select(BookmarkPayload)]),
            new PyList([.. active.Subfolders.Select(SubfolderPayload)]),
        ]);
    }

    /// <summary>A personal folder, or a shared one on the access lists given (the admin list is required).</summary>
    [Method("AddFolder")]
    public async ValueTask<PyValue> AddFolder(ServiceCall call)
    {
        var isPersonal = call.Args.Count == 0 || call.Arg(0) is PyNone || JsTruthy(call.Arg(0));
        var name = Text(call.Arg(1));
        var view = await store.AddFolderAsync(Character(call), isPersonal, name.Length == 0 ? DefaultFolderName : name, Text(call.Arg(2)),
            isPersonal ? null : Groups(call, 3), await Lists(call), call.Cancellation);
        return FolderPayload(view);
    }

    /// <summary>Renames the folder and sets its lists (admin only); answers the character's access level after the change.</summary>
    [Method("UpdateFolder")]
    public async ValueTask<PyValue> UpdateFolder(ServiceCall call)
    {
        var folderId = Integral(call.Arg(0)) ?? 0;
        var view = await store.UpdateFolderAsync(Character(call), folderId, Text(call.Arg(1)), Text(call.Arg(2)), Groups(call, 3), await Lists(call), call.Cancellation);
        await Publish(call, folderId, Update(folderId, "folderUpdated", new PyList([new PyWString(view.Folder.Name), new PyWString(view.Folder.Description)])));
        return new PyInt(view.AccessLevel);
    }

    /// <summary>Deletes the folder with its contents (admin only); the characters that had it active hear of it.</summary>
    [Method("DeleteFolder")]
    public async ValueTask<PyValue> DeleteFolder(ServiceCall call)
    {
        var folderId = Integral(call.Arg(0)) ?? 0;
        var viewers = await store.DeleteFolderAsync(Character(call), folderId, await Lists(call), call.Cancellation);
        if (sessions is not null && viewers.Count > 0)
            call.Session.AfterResponse(() =>
            {
                foreach (var viewer in viewers)
                    sessions.Of(viewer)?.SendNotification("OnSharedBookmarksFolderDeleted", "clientID", InventoryWire.Id(folderId));
            });
        return PyNone.Instance;
    }

    [Method("GetFolderInfo")]
    public async ValueTask<PyValue> GetFolderInfo(ServiceCall call) =>
        FolderPayload(await store.FolderInfoAsync(Character(call), Integral(call.Arg(0)) ?? 0, await Lists(call), call.Cancellation));

    /// <summary>The folders the character searches as admin: its personal folders and the shared folders whose admin list it holds.</summary>
    [Method("SearchFoldersWithAdminAccess")]
    public async ValueTask<PyValue> SearchFoldersWithAdminAccess(ServiceCall call)
    {
        var folders = await store.AdminFoldersAsync(Character(call), await Lists(call), call.Cancellation);
        return new PyList([.. folders.Select(FolderPayload)]);
    }

    [Method("AddToKnownFolders")]
    public async ValueTask<PyValue> AddToKnownFolders(ServiceCall call)
    {
        var opened = await store.AddKnownFolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, IsActiveArgument(call.Arg(1)), await Lists(call), call.Cancellation);
        return OpenedPayload(opened);
    }

    [Method("RemoveFromKnownFolders")]
    public async ValueTask<PyValue> RemoveFromKnownFolders(ServiceCall call)
    {
        await store.RemoveKnownFolderAsync(Character(call), Integral(call.Arg(0)) ?? 0, await Lists(call), call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateKnownFolderState")]
    public async ValueTask<PyValue> UpdateKnownFolderState(ServiceCall call)
    {
        var opened = await store.SetKnownFolderActiveAsync(Character(call), Integral(call.Arg(0)) ?? 0, IsActiveArgument(call.Arg(1)), await Lists(call), call.Cancellation);
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
        var folderId = Integral(call.Arg(1)) ?? 0;
        var bookmark = await store.CreateBookmarkAsync(Character(call), folderId, target, Text(call.Arg(2)), Text(call.Arg(3)),
            (int)(Integral(call.Arg(4)) ?? 0), Integral(call.Arg(5)), await Lists(call), call.Cancellation);
        await Publish(call, folderId, Update(folderId, "bookmarksAdded", new PyList([BookmarkPayload(bookmark)])));
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
        var bookmarkId = Integral(call.Arg(0)) ?? 0;
        var folderId = Integral(call.Arg(1)) ?? 0;
        var newFolderId = Integral(call.Arg(5)) ?? folderId;
        var updated = await store.UpdateBookmarkAsync(Character(call), bookmarkId, folderId, Text(call.Arg(2)), Text(call.Arg(3)),
            OptionalPositive(call.Arg(4)), newFolderId, Truthy(call.Arg(6)), await Lists(call), call.Cancellation);
        if (newFolderId == folderId)
        {
            await Publish(call, folderId, Update(folderId, "bookmarksUpdated", new PyList([BookmarkPayload(updated)])));
        }
        else
        {
            var moved = Update(folderId, "bookmarksMoved", Moved([updated], folderId, newFolderId));
            await Publish(call, folderId, moved);
            await Publish(call, newFolderId, moved);
        }
        return PyNone.Instance;
    }

    [Method("DeleteBookmarks")]
    public async ValueTask<PyValue> DeleteBookmarks(ServiceCall call)
    {
        var folderId = Integral(call.Arg(0)) ?? 0;
        var deleted = await store.DeleteBookmarksAsync(Character(call), folderId, IdsOf(call.Arg(1)), await Lists(call), call.Cancellation);
        if (deleted.Count > 0)
            await Publish(call, folderId, Update(folderId, "bookmarksRemoved", Ids(deleted)));
        return new PyList([.. deleted.Select(id => InventoryWire.Id(id))]);
    }

    [Method("MoveBookmarksToFolderAndSubfolder")]
    public async ValueTask<PyValue> MoveBookmarksToFolderAndSubfolder(ServiceCall call)
    {
        var fromFolder = Integral(call.Arg(0)) ?? 0;
        var toFolder = Integral(call.Arg(1)) ?? 0;
        var moved = await store.MoveBookmarksAsync(Character(call), fromFolder, toFolder, OptionalPositive(call.Arg(2)), IdsOf(call.Arg(3)),
            await Lists(call), call.Cancellation);
        if (moved.Count > 0)
        {
            var update = Update(fromFolder, "bookmarksMoved", Moved(moved, fromFolder, toFolder));
            await Publish(call, fromFolder, update);
            if (toFolder != fromFolder) await Publish(call, toFolder, update);
        }
        return new PyList([new PyList([.. moved.Select(BookmarkPayload)]), PyNone.Instance]);
    }

    /// <summary>The copies, keyed by their new bookmark ids.</summary>
    [Method("CopyBookmarksToFolderAndSubfolder")]
    public async ValueTask<PyValue> CopyBookmarksToFolderAndSubfolder(ServiceCall call)
    {
        var toFolder = Integral(call.Arg(1)) ?? 0;
        var copies = await store.CopyBookmarksAsync(Character(call), Integral(call.Arg(0)) ?? 0, toFolder, OptionalPositive(call.Arg(2)), IdsOf(call.Arg(3)),
            await Lists(call), call.Cancellation);
        if (copies.Count > 0)
            await Publish(call, toFolder, Update(toFolder, "bookmarksAdded", new PyList([.. copies.Select(BookmarkPayload)])));
        return new PyList([new PyDict([.. copies.Select(copy => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(copy.BookmarkId), BookmarkPayload(copy)))]), PyNone.Instance]);
    }

    [Method("CreateSubfolder")]
    public async ValueTask<PyValue> CreateSubfolder(ServiceCall call)
    {
        var folderId = Integral(call.Arg(0)) ?? 0;
        var subfolder = await store.CreateSubfolderAsync(Character(call), folderId, Text(call.Arg(1)), await Lists(call), call.Cancellation);
        await Publish(call, folderId, Update(folderId, "subfolderAdded", SubfolderPayload(subfolder)));
        return SubfolderPayload(subfolder);
    }

    /// <summary>Renames a subfolder; the new name is heard when it is given (an empty name keeps the old one).</summary>
    [Method("UpdateSubfolder")]
    public async ValueTask<PyValue> UpdateSubfolder(ServiceCall call)
    {
        var folderId = Integral(call.Arg(0)) ?? 0;
        var subfolderId = Integral(call.Arg(1)) ?? 0;
        var name = Text(call.Arg(2));
        var changed = await store.UpdateSubfolderAsync(Character(call), folderId, subfolderId, name, await Lists(call), call.Cancellation);
        var stored = BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, "");
        if (changed && stored.Length > 0)
            await Publish(call, folderId, Update(folderId, "subfolderUpdated", new PyList([InventoryWire.Id(subfolderId), new PyWString(stored)])));
        return PyValue.Of(changed);
    }

    /// <summary>Deletes a subfolder with its bookmarks; the bookmarks that went are heard, then the subfolder.</summary>
    [Method("DeleteSubfolder")]
    public async ValueTask<PyValue> DeleteSubfolder(ServiceCall call)
    {
        var folderId = Integral(call.Arg(0)) ?? 0;
        var subfolderId = Integral(call.Arg(1)) ?? 0;
        var removed = await store.DeleteSubfolderAsync(Character(call), folderId, subfolderId, await Lists(call), call.Cancellation);
        if (removed.Count > 0)
            await Publish(call, folderId, Update(folderId, "bookmarksRemoved", Ids(removed)));
        await Publish(call, folderId, Update(folderId, "subfolderRemoved", InventoryWire.Id(subfolderId)));
        return new PyList([.. removed.Select(id => InventoryWire.Id(id))]);
    }

    /// <summary>The calling character with its corporation and alliance, from its session.</summary>
    private static Affiliation Who(ServiceCall call)
    {
        var id = call.Session.CharacterId ?? 0;
        if (id <= 0) throw Notify("Bookmarks need a character.");
        return new Affiliation(id, call.Session.GetInt("corpid") ?? 0, call.Session.GetInt("allianceid") ?? 0);
    }

    private static long Character(ServiceCall call) => Who(call).CharacterId;

    private async ValueTask<AccessLists> Lists(ServiceCall call) => await listsOf(Who(call), call.Cancellation);

    /// <summary>The four list ids of a shared folder from the call's arguments, from <paramref name="first"/> (admin, manage, use, view).</summary>
    private static SharedGroups Groups(ServiceCall call, int first) =>
        new(OptionalPositive(call.Arg(first)), OptionalPositive(call.Arg(first + 1)), OptionalPositive(call.Arg(first + 2)), OptionalPositive(call.Arg(first + 3)));

    /// <summary>
    /// Sends one update, after the response, to each connected character that has the folder active and whose lists still give it a level to
    /// view the folder (the actor is not among them).
    /// </summary>
    private async Task Publish(ServiceCall call, long folderId, PyValue update)
    {
        if (sessions is null) return;
        var heard = new List<ClientSession>();
        foreach (var viewer in await store.ActiveViewersAsync(folderId, call.Session.CharacterId ?? 0, call.Cancellation))
            if (sessions.Of(viewer) is { } session && await CanView(session, folderId, call.Cancellation)) heard.Add(session);
        if (heard.Count == 0) return;
        call.Session.AfterResponse(() =>
        {
            foreach (var session in heard)
                session.SendNotification("OnSharedBookmarksFolderUpdated", "clientID", new PyList([update]));
        });
    }

    private async Task<bool> CanView(ClientSession session, long folderId, CancellationToken token)
    {
        var who = new Affiliation(session.CharacterId ?? 0, session.GetInt("corpid") ?? 0, session.GetInt("allianceid") ?? 0);
        var level = await store.LevelAsync(folderId, who.CharacterId, await listsOf(who, token), token);
        return FolderAccess.CanView(level);
    }

    /// <summary>One folder update: the target folder list, the update type, its arguments (the reference's <c>buildFolderUpdateTuple</c>).</summary>
    internal static PyValue Update(long folderId, string type, PyValue arguments) =>
        new PyList([new PyList([InventoryWire.Id(folderId)]), new PyStr(type), arguments]);

    private static PyValue Moved(IEnumerable<Bookmark> bookmarks, long fromFolder, long toFolder) =>
        new PyList([new PyList([.. bookmarks.Select(BookmarkPayload)]), InventoryWire.Id(fromFolder), InventoryWire.Id(toFolder)]);

    internal static PyList Ids(IEnumerable<long> ids) => new([.. ids.Select(id => InventoryWire.Id(id))]);

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

    // the client sends ASCII text as a byte string (decoded as PyBytes): read every text type the JS string reads
    private static string Text(PyValue value) => Eve.Macho.JsSemantics.StringValue(value) ?? "";

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
        ("adminGroupID", GroupId(view.Folder.Groups?.AdminGroupId)),
        ("manageGroupID", GroupId(view.Folder.Groups?.ManageGroupId)),
        ("useGroupID", GroupId(view.Folder.Groups?.UseGroupId)),
        ("viewGroupID", GroupId(view.Folder.Groups?.ViewGroupId)));

    private static PyValue GroupId(long? id) => id is { } value ? InventoryWire.Id(value) : PyNone.Instance;

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
                ? MachoErrors.ThrowUserError(error.Code, LimitValues(error.Code))
                : MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(error.Notice)));
        }
    }

    /// <summary>The limits the client's own text of a refusal names (its <c>UserError</c> arguments).</summary>
    private static (string Name, PyValue Value)[] LimitValues(string code) => code switch
    {
        "TooManyKnownFolders" => [("maxKnownFolders", new PyInt(BookmarkRules.MaxKnownFolders))],
        "TooManyActiveFolders" =>
        [
            ("maxPersonalFolders", new PyInt(BookmarkRules.MaxActivePersonalFolders)),
            ("maxSharedFolders", new PyInt(BookmarkRules.MaxActiveSharedFolders)),
        ],
        _ => [],
    };
}
