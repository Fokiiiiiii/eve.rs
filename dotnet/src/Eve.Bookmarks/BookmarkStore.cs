using System.Text.RegularExpressions;
using Eve.Data;
using Npgsql;

namespace Eve.Bookmarks;

/// <summary>
/// A bookmark refusal. <see cref="Code"/> is the client's user error (the reference's <c>bookmarkError</c> code); <see cref="Notice"/> is the
/// text shown when the client needs one (the reference's text is the code alone, so a few carry their own).
/// </summary>
public sealed class BookmarkError(string code, string? notice = null) : Exception(code)
{
    public string Code { get; } = code;

    public string? Notice { get; } = notice;
}

/// <summary>A folder: a personal one (its owner's), or a shared one whose <see cref="SharedGroups"/> give its access levels.</summary>
public sealed record BookmarkFolder(long FolderId, long OwnerId, string Name, string Description, bool IsPersonal, bool IsActive, long CreatedMs,
    SharedGroups? Groups = null);

public sealed record BookmarkSubfolder(long SubfolderId, long FolderId, string Name, long CreatorId, long CreatedMs);

public sealed record Bookmark(long BookmarkId, long FolderId, long? SubfolderId, long ItemId, int TypeId, long LocationId,
    string Memo, string Note, long CreatorId, long CreatedMs, long? ExpiryMs);

/// <summary>The place a static bookmark points at: a station, a solar system (its own location).</summary>
public sealed record BookmarkTarget(long ItemId, int TypeId, long LocationId);

/// <summary>A folder with what the client shows of it: the character's access level to it, its active state.</summary>
public sealed record FolderView(BookmarkFolder Folder, int AccessLevel, bool IsActive);

public sealed record ActiveBookmarks(IReadOnlyList<FolderView> Folders, IReadOnlyList<Bookmark> Bookmarks, IReadOnlyList<BookmarkSubfolder> Subfolders);

/// <summary>What a folder change answers the client with: the folder and, when it is active, its bookmarks and subfolders.</summary>
public sealed record FolderOpened(FolderView Folder, IReadOnlyList<Bookmark> Bookmarks, IReadOnlyList<BookmarkSubfolder> Subfolders);

/// <summary>The limits and text rules of the reference (<c>bookmarkConstants.js</c>, <c>bookmarkRuntimeState.js</c>).</summary>
public static class BookmarkRules
{
    public const int MaxActivePersonalFolders = 5;
    public const int MaxActiveSharedFolders = 3;
    public const int MaxKnownFolders = 30;
    public const int MaxSubfolders = 30;
    public const int MaxBookmarksInPersonalFolder = 3000;
    public const int MaxBookmarksInSharedFolder = 500;
    public const int MaxFolderNameLength = 40;
    public const int MaxBookmarkNameLength = 100;
    public const int MaxDescriptionLength = 3900;
    public const int AccessPersonal = 1;
    public const int AccessNone = 0;

    public const int ExpiryNone = 0;
    public const int Expiry4Hours = 1;
    public const int Expiry2Days = 2;
    public const int Expiry24Hours = 3;

    public const string DefaultFolderName = "Personal Locations";

    /// <summary>Systems where no bookmark can be made: Zarzakh (30100000), "Bookmarks are disabled in system." (the EVE University wiki).</summary>
    public static readonly IReadOnlySet<long> DisabledSystems = new HashSet<long> { 30_100_000 };

    /// <summary>The expiry an expiry mode gives a bookmark made at <paramref name="nowMs"/>, or null for none (the reference's <c>translateExpiryToFiletime</c>).</summary>
    public static long? ExpiryMs(int mode, long nowMs) => mode switch
    {
        Expiry4Hours => nowMs + 4L * 3_600_000,
        Expiry24Hours => nowMs + 24L * 3_600_000,
        Expiry2Days => nowMs + 48L * 3_600_000,
        _ => null,
    };

    /// <summary>The text as stored: trimmed and cut to <paramref name="max"/>, or the fallback when empty.</summary>
    public static string Name(string? value, int max, string fallback)
    {
        var text = (value ?? fallback).Trim();
        if (text.Length > max) text = text[..max];
        return text.Length == 0 ? fallback : text;
    }

    /// <summary>A description as stored: not trimmed, cut to the limit.</summary>
    public static string Description(string? value) =>
        value is null ? "" : value.Length > MaxDescriptionLength ? value[..MaxDescriptionLength] : value;
}

/// <summary>
/// The bookmark folders in PostgreSQL (schema <c>bookmarks</c>): the personal folders of a character and the shared folders its access
/// lists give it. Every change runs in one transaction that holds the acting character's advisory lock, and a shared folder's writes also
/// hold the folder's lock, so the active limits and the capacities hold under concurrent calls. Who may do what is <see cref="FolderAccess"/>.
/// </summary>
public sealed partial class BookmarkStore
{
    public const string DefaultSchema = "bookmarks";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly Func<long> _nowMs;

    public BookmarkStore(NpgsqlDataSource source, string schema, Func<long> nowMs)
    {
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _schema = schema;
        _nowMs = nowMs;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(BookmarkStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>The folders the character has in its list (its personal folders and its shared connections), with their active state.</summary>
    public Task<IReadOnlyList<FolderView>> KnownFoldersAsync(long characterId, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, (connection, tx) =>
            ViewsAsync(connection, tx, characterId, lists ?? AccessLists.Empty,
                "(f.is_personal AND f.owner_id=$1 AND f.known) OR c.character_id IS NOT NULL", token), token);

    /// <summary>The character's active folders with their bookmarks and subfolders; the expired bookmarks are removed first.</summary>
    public Task<ActiveBookmarks> ActiveAsync(long characterId, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            await Execute(connection, tx, $"DELETE FROM {_schema}.bookmarks WHERE expiry_ms IS NOT NULL AND expiry_ms <= $1", token, _nowMs());
            var active = (await ViewsAsync(connection, tx, characterId, lists ?? AccessLists.Empty,
                    "(f.is_personal AND f.owner_id=$1 AND f.known) OR c.character_id IS NOT NULL", token))
                .Where(view => view.IsActive).ToArray();
            var ids = active.Select(view => view.Folder.FolderId).ToArray();
            return new ActiveBookmarks(active, await BookmarksInAsync(connection, tx, ids, token), await SubfoldersInAsync(connection, tx, ids, token));
        }, token);

    /// <summary>The folders the character searches as admin: its personal folders and the shared folders whose admin list it holds.</summary>
    public Task<IReadOnlyList<FolderView>> AdminFoldersAsync(long characterId, AccessLists? lists = null, CancellationToken token = default)
    {
        var access = lists ?? AccessLists.Empty;
        var adminLists = access.RoleByGroup.Where(entry => entry.Value >= 2).Select(entry => entry.Key).ToArray();
        return InTransactionAsync(characterId, (connection, tx) =>
            ViewsAsync(connection, tx, characterId, access,
                "(f.is_personal AND f.owner_id=$1 AND f.known) OR f.admin_group_id = ANY($2)", token, adminLists), token);
    }

    /// <summary>One folder the character can see, with its level and state; a folder it cannot see is refused as access denied.</summary>
    public Task<FolderView> FolderInfoAsync(long characterId, long folderId, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            var level = RequireLevel(folder, characterId, lists ?? AccessLists.Empty, FolderAccess.CanView);
            var active = folder.IsPersonal ? folder.IsActive : await ConnectionActiveAsync(connection, tx, folderId, characterId, token);
            return new FolderView(folder, level, active);
        }, token);

    /// <summary>
    /// A personal folder, known to the character and active when the active limit allows it (the reference's <c>addFolder</c>), or a shared
    /// folder on the access lists given: the admin list is required, and each list must be one the character may assign.
    /// </summary>
    public Task<FolderView> AddFolderAsync(long characterId, bool isPersonal, string? name, string? description, SharedGroups? groups = null,
        AccessLists? lists = null, CancellationToken token = default)
    {
        var access = lists ?? AccessLists.Empty;
        if (!isPersonal)
        {
            if (groups?.AdminGroupId is null) throw new BookmarkError("AdminAccessRequired");
            RequireAssignable(groups, access);
        }
        return InTransactionAsync(characterId, async (connection, tx) =>
        {
            if (await KnownCountAsync(connection, tx, characterId, token) >= BookmarkRules.MaxKnownFolders)
                throw new BookmarkError("TooManyKnownFolders", "You have too many bookmark folders.");
            var folderId = await NextIdAsync(connection, tx, token);
            var now = _nowMs();
            var folderName = BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, BookmarkRules.DefaultFolderName);
            if (isPersonal)
            {
                var active = await ActiveCountAsync(connection, tx, characterId, token) < BookmarkRules.MaxActivePersonalFolders;
                var folder = new BookmarkFolder(folderId, characterId, folderName, BookmarkRules.Description(description), true, active, now);
                await Execute(connection, tx,
                    $"INSERT INTO {_schema}.folders(folder_id,owner_id,folder_name,description,is_personal,known,is_active,created_ms) VALUES($1,$2,$3,$4,true,true,$5,$6)",
                    token, folderId, characterId, folder.Name, folder.Description, active, now);
                return new FolderView(folder, BookmarkRules.AccessPersonal, active);
            }
            var shared = new BookmarkFolder(folderId, characterId, folderName, BookmarkRules.Description(description), false, false, now, groups);
            await Execute(connection, tx,
                $"INSERT INTO {_schema}.folders(folder_id,owner_id,folder_name,description,is_personal,known,is_active,created_ms,admin_group_id,manage_group_id,use_group_id,view_group_id) VALUES($1,$2,$3,$4,false,false,false,$5,$6,$7,$8,$9)",
                token, folderId, characterId, shared.Name, shared.Description, now, groups!.AdminGroupId, groups.ManageGroupId, groups.UseGroupId, groups.ViewGroupId);
            var connected = await SharedActiveCountAsync(connection, tx, characterId, token) < BookmarkRules.MaxActiveSharedFolders;
            await Execute(connection, tx, $"INSERT INTO {_schema}.connections(folder_id,character_id,is_active) VALUES($1,$2,$3)", token, folderId, characterId, connected);
            return new FolderView(shared, FolderAccess.LevelOf(shared, characterId, access), connected);
        }, token);
    }

    /// <summary>
    /// Renames a folder, sets its description and, for a shared folder, its lists (an empty name keeps the old one). Only the admin changes it.
    /// Answers the folder with the character's level after the change (none when the change took its access away).
    /// </summary>
    public Task<FolderView> UpdateFolderAsync(long characterId, long folderId, string? name, string? description, SharedGroups? groups = null,
        AccessLists? lists = null, CancellationToken token = default)
    {
        var access = lists ?? AccessLists.Empty;
        return InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            RequireAdmin(folder, characterId, access);
            if (groups is not null && !folder.IsPersonal)
            {
                if (groups.AdminGroupId is null) throw new BookmarkError("AdminAccessRequired");
                RequireAssignable(groups, access);
            }
            var updated = folder with
            {
                Name = BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, folder.Name),
                Description = BookmarkRules.Description(description),
                Groups = folder.IsPersonal || groups is null ? folder.Groups : groups,
            };
            await Execute(connection, tx,
                $"UPDATE {_schema}.folders SET folder_name=$2, description=$3, admin_group_id=$4, manage_group_id=$5, use_group_id=$6, view_group_id=$7 WHERE folder_id=$1",
                token, folderId, updated.Name, updated.Description, updated.Groups?.AdminGroupId, updated.Groups?.ManageGroupId,
                updated.Groups?.UseGroupId, updated.Groups?.ViewGroupId);
            var active = updated.IsPersonal ? updated.IsActive : await ConnectionActiveAsync(connection, tx, folderId, characterId, token);
            return new FolderView(updated, FolderAccess.LevelOf(updated, characterId, access), active);
        }, token);
    }

    /// <summary>
    /// Deletes the folder with its subfolders, bookmarks and connections (the wiki). Only the admin deletes it. Answers the characters that had
    /// it active (other than the caller), who hear of the deletion.
    /// </summary>
    public Task<IReadOnlyList<long>> DeleteFolderAsync(long characterId, long folderId, AccessLists? lists = null, CancellationToken token = default)
    {
        var access = lists ?? AccessLists.Empty;
        return InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            RequireAdmin(folder, characterId, access);
            var viewers = await ActiveViewersAsync(connection, tx, folderId, characterId, token);
            await Execute(connection, tx, $"DELETE FROM {_schema}.folders WHERE folder_id=$1", token, folderId);
            return viewers;
        }, token);
    }

    /// <summary>
    /// Puts a folder in the character's list and active when the active limit allows it (the reference's <c>addKnownFolder</c>). A shared
    /// folder needs a level that can view it.
    /// </summary>
    public Task<FolderOpened> AddKnownFolderAsync(long characterId, long folderId, bool isActive, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            var level = RequireLevel(folder, characterId, lists ?? AccessLists.Empty, FolderAccess.CanView);
            if (folder.IsPersonal)
            {
                var active = isActive && await ActiveCountAsync(connection, tx, characterId, token) < BookmarkRules.MaxActivePersonalFolders;
                await Execute(connection, tx, $"UPDATE {_schema}.folders SET known=true, is_active=$2 WHERE folder_id=$1", token, folderId, active);
                return await OpenedAsync(connection, tx, folder with { IsActive = active }, level, active, token);
            }
            var already = await ConnectionActiveAsync(connection, tx, folderId, characterId, token);
            if (!already && await KnownCountAsync(connection, tx, characterId, token) >= BookmarkRules.MaxKnownFolders)
                throw new BookmarkError("TooManyKnownFolders", "You have too many bookmark folders.");
            var connected = isActive && (already || await SharedActiveCountAsync(connection, tx, characterId, token) < BookmarkRules.MaxActiveSharedFolders);
            await Execute(connection, tx,
                $"INSERT INTO {_schema}.connections(folder_id,character_id,is_active) VALUES($1,$2,$3) ON CONFLICT (folder_id, character_id) DO UPDATE SET is_active=EXCLUDED.is_active",
                token, folderId, characterId, connected);
            return await OpenedAsync(connection, tx, folder, level, connected, token);
        }, token);

    /// <summary>Takes a folder out of the character's list. A personal folder stays; a shared folder's connection is removed.</summary>
    public Task RemoveKnownFolderAsync(long characterId, long folderId, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            if (folder.IsPersonal)
            {
                RequireLevel(folder, characterId, lists ?? AccessLists.Empty, level => level != FolderAccess.None);
                await Execute(connection, tx, $"UPDATE {_schema}.folders SET known=false, is_active=false WHERE folder_id=$1", token, folderId);
            }
            else
            {
                await Execute(connection, tx, $"DELETE FROM {_schema}.connections WHERE folder_id=$1 AND character_id=$2", token, folderId, characterId);
            }
            return 0;
        }, token);

    /// <summary>Sets whether a known folder is active. Making one active when the limit is reached is refused.</summary>
    public Task<FolderOpened> SetKnownFolderActiveAsync(long characterId, long folderId, bool isActive, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            var level = RequireLevel(folder, characterId, lists ?? AccessLists.Empty, FolderAccess.CanView);
            if (folder.IsPersonal)
            {
                if (isActive && await ActiveCountAsync(connection, tx, characterId, token) >= BookmarkRules.MaxActivePersonalFolders)
                    throw new BookmarkError("TooManyActiveFolders", "You have too many active bookmark folders.");
                await Execute(connection, tx, $"UPDATE {_schema}.folders SET known=true, is_active=$2 WHERE folder_id=$1", token, folderId, isActive);
                return await OpenedAsync(connection, tx, folder with { IsActive = isActive }, level, isActive, token);
            }
            var already = await ConnectionActiveAsync(connection, tx, folderId, characterId, token);
            if (isActive && !already && await SharedActiveCountAsync(connection, tx, characterId, token) >= BookmarkRules.MaxActiveSharedFolders)
                throw new BookmarkError("TooManyActiveFolders", "You have too many active bookmark folders.");
            await Execute(connection, tx,
                $"INSERT INTO {_schema}.connections(folder_id,character_id,is_active) VALUES($1,$2,$3) ON CONFLICT (folder_id, character_id) DO UPDATE SET is_active=EXCLUDED.is_active",
                token, folderId, characterId, isActive);
            return await OpenedAsync(connection, tx, folder, level, isActive, token);
        }, token);

    public Task<BookmarkSubfolder> CreateSubfolderAsync(long characterId, long folderId, string? name, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            RequireLevel(folder, characterId, lists ?? AccessLists.Empty, FolderAccess.CanUse);
            if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.subfolders WHERE folder_id=$1", token, folderId) >= BookmarkRules.MaxSubfolders)
                throw new BookmarkError("MaxSubfolders", "A folder can hold at most 30 subfolders.");
            var subfolder = new BookmarkSubfolder(await NextIdAsync(connection, tx, token), folderId,
                BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, "Subfolder"), characterId, _nowMs());
            await Execute(connection, tx, $"INSERT INTO {_schema}.subfolders(subfolder_id,folder_id,subfolder_name,creator_id,created_ms) VALUES($1,$2,$3,$4,$5)",
                token, subfolder.SubfolderId, folderId, subfolder.Name, characterId, subfolder.CreatedMs);
            return subfolder;
        }, token);

    /// <summary>
    /// Renames a subfolder of the folder: the manager renames any, a user renames its own. False when the subfolder is not in that folder
    /// (the reference's answer).
    /// </summary>
    public Task<bool> UpdateSubfolderAsync(long characterId, long folderId, long subfolderId, string? name, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            var level = RequireLevel(folder, characterId, lists ?? AccessLists.Empty, FolderAccess.CanUse);
            var current = await SubfolderAsync(connection, tx, subfolderId, token);
            if (current is null || current.FolderId != folderId) return false;
            if (!FolderAccess.CanManage(level) && current.CreatorId != characterId) throw new BookmarkError("FolderAccessDenied");
            await Execute(connection, tx, $"UPDATE {_schema}.subfolders SET subfolder_name=$2 WHERE subfolder_id=$1",
                token, subfolderId, BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, current.Name));
            return true;
        }, token);

    /// <summary>
    /// Deletes a subfolder and its bookmarks, answering the ids of the bookmarks that went with it. A user deletes its own subfolder, and only
    /// when every bookmark in it is its own (the reference's <c>CouldNotDeleteBookmarksInSubfolder</c>).
    /// </summary>
    public Task<IReadOnlyList<long>> DeleteSubfolderAsync(long characterId, long folderId, long subfolderId, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            var level = RequireLevel(folder, characterId, lists ?? AccessLists.Empty, FolderAccess.CanUse);
            var current = await SubfolderAsync(connection, tx, subfolderId, token);
            if (current is null || current.FolderId != folderId) throw new BookmarkError("BookmarkSubfolderNoLongerThere");
            if (!FolderAccess.CanManage(level) && current.CreatorId != characterId) throw new BookmarkError("FolderAccessDenied");
            if (!FolderAccess.CanManage(level) && await CountAsync(connection, tx,
                    $"SELECT count(*) FROM {_schema}.bookmarks WHERE subfolder_id=$1 AND creator_id<>$2", token, subfolderId, characterId) > 0)
                throw new BookmarkError("CouldNotDeleteBookmarksInSubfolder");
            var removed = await IdsAsync(connection, tx, $"DELETE FROM {_schema}.bookmarks WHERE subfolder_id=$1 RETURNING bookmark_id", token, subfolderId);
            await Execute(connection, tx, $"DELETE FROM {_schema}.subfolders WHERE subfolder_id=$1", token, subfolderId);
            return (IReadOnlyList<long>)removed;
        }, token);

    /// <summary>Makes a bookmark of a static place (a station or a solar system) in a folder the character can use.</summary>
    public Task<Bookmark> CreateBookmarkAsync(long characterId, long folderId, BookmarkTarget target, string? memo, string? note,
        int expiryMode, long? subfolderId, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            RequireLevel(folder, characterId, lists ?? AccessLists.Empty, FolderAccess.CanUse);
            if (!folder.IsPersonal) await LockFolderAsync(connection, tx, _schema, folderId, token);
            if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.bookmarks WHERE folder_id=$1", token, folderId) >= CapacityOf(folder))
                throw new BookmarkError("FolderCapacityExceeded", "This folder is full.");
            await RequireSubfolderInAsync(connection, tx, folderId, subfolderId, token);
            var now = _nowMs();
            var bookmark = new Bookmark(await NextIdAsync(connection, tx, token), folderId, subfolderId, target.ItemId, target.TypeId, target.LocationId,
                BookmarkRules.Name(memo, BookmarkRules.MaxBookmarkNameLength, "Location"), BookmarkRules.Description(note),
                characterId, now, BookmarkRules.ExpiryMs(expiryMode, now));
            await InsertBookmarkAsync(connection, tx, bookmark, token);
            return bookmark;
        }, token);

    /// <summary>
    /// Changes a bookmark: its memo, note, subfolder, folder and expiry (cancelled on request). The bookmark must be in the old folder; the
    /// character edits it when it manages that folder or uses it and made the bookmark, and moves it only into a folder it can use.
    /// </summary>
    public Task<Bookmark> UpdateBookmarkAsync(long characterId, long bookmarkId, long oldFolderId, string? memo, string? note,
        long? subfolderId, long newFolderId, bool expiryCancel, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var access = lists ?? AccessLists.Empty;
            var current = await BookmarkAsync(connection, tx, bookmarkId, token);
            if (current is null || current.FolderId != oldFolderId)
                throw new BookmarkError("BookmarkNotAvailable");
            var oldFolder = await FolderOfAsync(connection, tx, current.FolderId, token);
            RequireEditable(oldFolder, current, characterId, access);
            var newFolder = await FolderOfAsync(connection, tx, newFolderId, token);
            RequireLevel(newFolder, characterId, access, FolderAccess.CanUse);
            if (newFolderId != current.FolderId)
            {
                if (!newFolder.IsPersonal) await LockFolderAsync(connection, tx, _schema, newFolderId, token);
                if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.bookmarks WHERE folder_id=$1", token, newFolderId) >= CapacityOf(newFolder))
                    throw new BookmarkError("FolderCapacityExceeded", "This folder is full.");
            }
            await RequireSubfolderInAsync(connection, tx, newFolderId, subfolderId, token);
            var updated = current with
            {
                FolderId = newFolderId,
                Memo = BookmarkRules.Name(memo, BookmarkRules.MaxBookmarkNameLength, current.Memo),
                Note = BookmarkRules.Description(note),
                SubfolderId = subfolderId,
                ExpiryMs = expiryCancel ? null : current.ExpiryMs,
            };
            await Execute(connection, tx,
                $"UPDATE {_schema}.bookmarks SET folder_id=$2, memo=$3, note=$4, subfolder_id=$5, expiry_ms=$6 WHERE bookmark_id=$1",
                token, bookmarkId, updated.FolderId, updated.Memo, updated.Note, updated.SubfolderId, updated.ExpiryMs);
            return updated;
        }, token);

    /// <summary>
    /// Deletes the listed bookmarks of a folder. A character that manages the folder deletes any of them; one that uses it deletes its own.
    /// Answers the ids that were removed (the others are left alone).
    /// </summary>
    public Task<IReadOnlyList<long>> DeleteBookmarksAsync(long characterId, long folderId, IReadOnlyList<long> bookmarkIds, AccessLists? lists = null,
        CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            var deletable = await DeletableAsync(connection, tx, folder, characterId, bookmarkIds, lists ?? AccessLists.Empty, token);
            if (deletable.Count == 0) return (IReadOnlyList<long>)[];
            return (IReadOnlyList<long>)await IdsAsync(connection, tx,
                $"DELETE FROM {_schema}.bookmarks WHERE folder_id=$1 AND bookmark_id = ANY($2) RETURNING bookmark_id", token, folderId, deletable.ToArray());
        }, token);

    /// <summary>
    /// Moves the listed bookmarks of one folder into another (and a subfolder of it), with the same edit rule as deleting. The target folder
    /// must be one the character can use. Answers the moved bookmarks.
    /// </summary>
    public Task<IReadOnlyList<Bookmark>> MoveBookmarksAsync(long characterId, long fromFolderId, long toFolderId, long? subfolderId,
        IReadOnlyList<long> bookmarkIds, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var access = lists ?? AccessLists.Empty;
            var from = await FolderOfAsync(connection, tx, fromFolderId, token);
            var to = await FolderOfAsync(connection, tx, toFolderId, token);
            RequireLevel(to, characterId, access, FolderAccess.CanUse);
            var movable = await DeletableAsync(connection, tx, from, characterId, bookmarkIds, access, token);
            if (movable.Count == 0) return (IReadOnlyList<Bookmark>)[];
            if (fromFolderId != toFolderId)
            {
                if (!to.IsPersonal) await LockFolderAsync(connection, tx, _schema, toFolderId, token);
                if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.bookmarks WHERE folder_id=$1", token, toFolderId) + movable.Count > CapacityOf(to))
                    throw new BookmarkError("FolderCapacityExceeded", "This folder is full.");
            }
            await RequireSubfolderInAsync(connection, tx, toFolderId, subfolderId, token);
            return (IReadOnlyList<Bookmark>)await BookmarkRowsAsync(connection, tx,
                $"UPDATE {_schema}.bookmarks SET folder_id=$2, subfolder_id=$3 WHERE folder_id=$1 AND bookmark_id = ANY($4) RETURNING {BookmarkColumns}",
                token, fromFolderId, toFolderId, subfolderId, movable.ToArray());
        }, token);

    /// <summary>
    /// Copies the listed bookmarks of one folder into another (new ids, made by the character). The source must be viewable and the target
    /// usable. Answers the copies.
    /// </summary>
    public Task<IReadOnlyList<Bookmark>> CopyBookmarksAsync(long characterId, long fromFolderId, long toFolderId, long? subfolderId,
        IReadOnlyList<long> bookmarkIds, AccessLists? lists = null, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var access = lists ?? AccessLists.Empty;
            var from = await FolderOfAsync(connection, tx, fromFolderId, token);
            var to = await FolderOfAsync(connection, tx, toFolderId, token);
            RequireLevel(from, characterId, access, FolderAccess.CanView);
            RequireLevel(to, characterId, access, FolderAccess.CanUse);
            var sources = await BookmarkRowsAsync(connection, tx,
                $"SELECT {BookmarkColumns} FROM {_schema}.bookmarks WHERE folder_id=$1 AND bookmark_id = ANY($2) ORDER BY bookmark_id",
                token, fromFolderId, bookmarkIds.ToArray());
            if (sources.Count == 0) return (IReadOnlyList<Bookmark>)[];
            if (!to.IsPersonal) await LockFolderAsync(connection, tx, _schema, toFolderId, token);
            if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.bookmarks WHERE folder_id=$1", token, toFolderId) + sources.Count > CapacityOf(to))
                throw new BookmarkError("FolderCapacityExceeded", "This folder is full.");
            await RequireSubfolderInAsync(connection, tx, toFolderId, subfolderId, token);
            var now = _nowMs();
            var copies = new List<Bookmark>();
            foreach (var source in sources)
            {
                var copy = source with { BookmarkId = await NextIdAsync(connection, tx, token), FolderId = toFolderId, SubfolderId = subfolderId, CreatorId = characterId, CreatedMs = now };
                await InsertBookmarkAsync(connection, tx, copy, token);
                copies.Add(copy);
            }
            return (IReadOnlyList<Bookmark>)copies;
        }, token);

    /// <summary>The characters that have the folder active (other than the given one): who hears of a change to a shared folder.</summary>
    public async Task<IReadOnlyList<long>> ActiveViewersAsync(long folderId, long excludeCharacterId, CancellationToken token = default)
    {
        // a read of the connections only: no character's default folder is made for the viewer lookup
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var viewers = await ActiveViewersAsync(connection, transaction, folderId, excludeCharacterId, token);
        await transaction.CommitAsync(token);
        return viewers;
    }

    /// <summary>The level the lists give the character in the folder right now (none when the folder is gone or the lists give nothing).</summary>
    public async Task<int> LevelAsync(long folderId, long characterId, AccessLists lists, CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null!, $"SELECT {FolderColumns} FROM {_schema}.folders WHERE folder_id=$1", folderId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? FolderAccess.LevelOf(ReadFolder(reader), characterId, lists) : FolderAccess.None;
    }

    private async Task<IReadOnlyList<long>> ActiveViewersAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long folderId, long excludeCharacterId,
        CancellationToken token) =>
        await IdsAsync(connection, tx, $"SELECT character_id FROM {_schema}.connections WHERE folder_id=$1 AND is_active AND character_id<>$2 ORDER BY character_id",
            token, folderId, excludeCharacterId);

    /// <summary>The bookmark can be edited by the character: it manages the folder, or it uses the folder and made the bookmark.</summary>
    private static void RequireEditable(BookmarkFolder folder, Bookmark bookmark, long characterId, AccessLists lists)
    {
        var level = FolderAccess.LevelOf(folder, characterId, lists);
        if (!FolderAccess.CanManage(level) && !(FolderAccess.CanUse(level) && bookmark.CreatorId == characterId))
            throw new BookmarkError("FolderAccessDenied");
    }

    /// <summary>The listed bookmarks of the folder the character may delete or move: all when it manages the folder, its own when it uses it.</summary>
    private async Task<List<long>> DeletableAsync(NpgsqlConnection connection, NpgsqlTransaction tx, BookmarkFolder folder, long characterId,
        IReadOnlyList<long> bookmarkIds, AccessLists lists, CancellationToken token)
    {
        var level = RequireLevel(folder, characterId, lists, FolderAccess.CanView);
        if (bookmarkIds.Count == 0) return [];
        var rows = await BookmarkRowsAsync(connection, tx,
            $"SELECT {BookmarkColumns} FROM {_schema}.bookmarks WHERE folder_id=$1 AND bookmark_id = ANY($2)", token, folder.FolderId, bookmarkIds.ToArray());
        return rows.Where(bookmark => FolderAccess.CanManage(level) || (FolderAccess.CanUse(level) && bookmark.CreatorId == characterId))
            .Select(bookmark => bookmark.BookmarkId).ToList();
    }
}
