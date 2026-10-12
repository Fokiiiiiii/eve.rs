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

public sealed record BookmarkFolder(long FolderId, long OwnerId, string Name, string Description, bool IsPersonal, bool IsActive, long CreatedMs);

public sealed record BookmarkSubfolder(long SubfolderId, long FolderId, string Name, long CreatorId, long CreatedMs);

public sealed record Bookmark(long BookmarkId, long FolderId, long? SubfolderId, long ItemId, int TypeId, long LocationId,
    string Memo, string Note, long CreatorId, long CreatedMs, long? ExpiryMs);

/// <summary>The place a static bookmark points at: a station, a solar system (its own location).</summary>
public sealed record BookmarkTarget(long ItemId, int TypeId, long LocationId);

/// <summary>A folder with what the client shows of it: its access (the owner's personal folder is personal access), its active state.</summary>
public sealed record FolderView(BookmarkFolder Folder, int AccessLevel, bool IsActive);

public sealed record ActiveBookmarks(IReadOnlyList<FolderView> Folders, IReadOnlyList<Bookmark> Bookmarks, IReadOnlyList<BookmarkSubfolder> Subfolders);

/// <summary>What a folder change answers the client with: the folder and, when it is active, its bookmarks and subfolders.</summary>
public sealed record FolderOpened(FolderView Folder, IReadOnlyList<Bookmark> Bookmarks, IReadOnlyList<BookmarkSubfolder> Subfolders);

/// <summary>The limits and text rules of the reference (<c>bookmarkConstants.js</c>, <c>bookmarkRuntimeState.js</c>).</summary>
public static class BookmarkRules
{
    public const int MaxActivePersonalFolders = 5;
    public const int MaxKnownFolders = 30;
    public const int MaxSubfolders = 30;
    public const int MaxBookmarksInPersonalFolder = 3000;
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
/// The bookmark folders a character owns, in PostgreSQL (schema <c>bookmarks</c>). Every change runs in one transaction that holds the
/// owner's advisory lock, so the active-folder and capacity limits hold under concurrent calls. Only personal folders are stored: the
/// shared (access group) folders need the access group store, which is not built.
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

    /// <summary>The folders the character has in its list (known), with their active state.</summary>
    public Task<IReadOnlyList<FolderView>> KnownFoldersAsync(long characterId, CancellationToken token = default) =>
        InTransactionAsync<IReadOnlyList<FolderView>>(characterId, async (connection, tx) =>
        {
            var folders = await FoldersAsync(connection, tx, characterId, "known", token);
            return folders.Select(folder => new FolderView(folder, BookmarkRules.AccessPersonal, folder.IsActive)).ToArray();
        }, token);

    /// <summary>The character's active folders with their bookmarks and subfolders; the expired bookmarks are removed first.</summary>
    public Task<ActiveBookmarks> ActiveAsync(long characterId, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            await Execute(connection, tx,
                $"DELETE FROM {_schema}.bookmarks b USING {_schema}.folders f WHERE b.folder_id=f.folder_id AND f.owner_id=$1 AND b.expiry_ms IS NOT NULL AND b.expiry_ms <= $2",
                token, characterId, _nowMs());
            var folders = (await FoldersAsync(connection, tx, characterId, "known AND is_active", token))
                .Select(folder => new FolderView(folder, BookmarkRules.AccessPersonal, true)).ToArray();
            var bookmarks = await BookmarksOfAsync(connection, tx, characterId, "f.known AND f.is_active", token);
            var subfolders = await SubfoldersOfAsync(connection, tx, characterId, "f.known AND f.is_active", token);
            return new ActiveBookmarks(folders, bookmarks, subfolders);
        }, token);

    /// <summary>The character's folders that it has as personal and knows (what the search of admin folders lists).</summary>
    public Task<IReadOnlyList<FolderView>> PersonalFoldersAsync(long characterId, CancellationToken token = default) =>
        KnownFoldersAsync(characterId, token);

    /// <summary>One folder the character owns (its view), or the reference's refusal: no such folder, or another's.</summary>
    public Task<FolderView> FolderInfoAsync(long characterId, long folderId, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = await FolderOfAsync(connection, tx, folderId, token);
            return new FolderView(RequireOwner(folder, characterId), BookmarkRules.AccessPersonal, folder.IsActive);
        }, token);

    /// <summary>A personal folder, known to the character and active when the active limit allows it (the reference's <c>addFolder</c>).</summary>
    public Task<FolderView> AddFolderAsync(long characterId, bool isPersonal, string? name, string? description, CancellationToken token = default)
    {
        if (!isPersonal)
            throw new BookmarkError("SharedFoldersNotBuilt", "Shared bookmark folders are not available on this server.");
        return InTransactionAsync(characterId, async (connection, tx) =>
        {
            if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.folders WHERE owner_id=$1 AND known", token, characterId) >= BookmarkRules.MaxKnownFolders)
                throw new BookmarkError("TooManyKnownFolders", "You have too many bookmark folders.");
            var active = await ActiveCountAsync(connection, tx, characterId, token) < BookmarkRules.MaxActivePersonalFolders;
            var folderId = await NextIdAsync(connection, tx, token);
            var folder = new BookmarkFolder(folderId, characterId, BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, BookmarkRules.DefaultFolderName),
                BookmarkRules.Description(description), true, active, _nowMs());
            await Execute(connection, tx,
                $"INSERT INTO {_schema}.folders(folder_id,owner_id,folder_name,description,is_personal,known,is_active,created_ms) VALUES($1,$2,$3,$4,true,true,$5,$6)",
                token, folder.FolderId, characterId, folder.Name, folder.Description, active, folder.CreatedMs);
            return new FolderView(folder, BookmarkRules.AccessPersonal, active);
        }, token);
    }

    /// <summary>Renames the folder and sets its description; an empty name keeps the old one.</summary>
    public Task<FolderView> UpdateFolderAsync(long characterId, long folderId, string? name, string? description, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            var updated = folder with
            {
                Name = BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, folder.Name),
                Description = BookmarkRules.Description(description),
            };
            await Execute(connection, tx, $"UPDATE {_schema}.folders SET folder_name=$2, description=$3 WHERE folder_id=$1",
                token, folderId, updated.Name, updated.Description);
            return new FolderView(updated, BookmarkRules.AccessPersonal, updated.IsActive);
        }, token);

    /// <summary>Deletes the folder with its subfolders and bookmarks.</summary>
    public Task DeleteFolderAsync(long characterId, long folderId, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            await Execute(connection, tx, $"DELETE FROM {_schema}.folders WHERE folder_id=$1", token, folderId);
            return 0;
        }, token);

    /// <summary>Puts a folder in the character's list (known) and active when the active limit allows it; the reference does the same.</summary>
    public Task<FolderOpened> AddKnownFolderAsync(long characterId, long folderId, bool isActive, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            var active = isActive && await ActiveCountAsync(connection, tx, characterId, token) < BookmarkRules.MaxActivePersonalFolders;
            await Execute(connection, tx, $"UPDATE {_schema}.folders SET known=true, is_active=$2 WHERE folder_id=$1", token, folderId, active);
            return await OpenedAsync(connection, tx, folder with { IsActive = active }, token);
        }, token);

    /// <summary>Takes a folder out of the character's list. The folder itself stays.</summary>
    public Task RemoveKnownFolderAsync(long characterId, long folderId, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            await Execute(connection, tx, $"UPDATE {_schema}.folders SET known=false, is_active=false WHERE folder_id=$1", token, folderId);
            return 0;
        }, token);

    /// <summary>Sets whether a known folder is active. Making one active when the limit is reached is refused.</summary>
    public Task<FolderOpened> SetKnownFolderActiveAsync(long characterId, long folderId, bool isActive, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var folder = RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            if (isActive && await ActiveCountAsync(connection, tx, characterId, token) >= BookmarkRules.MaxActivePersonalFolders)
                throw new BookmarkError("TooManyActiveFolders", "You have too many active bookmark folders.");
            await Execute(connection, tx, $"UPDATE {_schema}.folders SET known=true, is_active=$2 WHERE folder_id=$1", token, folderId, isActive);
            return await OpenedAsync(connection, tx, folder with { IsActive = isActive }, token);
        }, token);

    public Task<BookmarkSubfolder> CreateSubfolderAsync(long characterId, long folderId, string? name, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.subfolders WHERE folder_id=$1", token, folderId) >= BookmarkRules.MaxSubfolders)
                throw new BookmarkError("MaxSubfolders", "A folder can hold at most 30 subfolders.");
            var subfolder = new BookmarkSubfolder(await NextIdAsync(connection, tx, token), folderId,
                BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, "Subfolder"), characterId, _nowMs());
            await Execute(connection, tx, $"INSERT INTO {_schema}.subfolders(subfolder_id,folder_id,subfolder_name,creator_id,created_ms) VALUES($1,$2,$3,$4,$5)",
                token, subfolder.SubfolderId, folderId, subfolder.Name, characterId, subfolder.CreatedMs);
            return subfolder;
        }, token);

    /// <summary>Renames a subfolder of the folder. False when the subfolder is not in that folder (the reference's answer).</summary>
    public Task<bool> UpdateSubfolderAsync(long characterId, long folderId, long subfolderId, string? name, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            var current = await SubfolderAsync(connection, tx, subfolderId, token);
            if (current is null || current.FolderId != folderId) return false;
            await Execute(connection, tx, $"UPDATE {_schema}.subfolders SET subfolder_name=$2 WHERE subfolder_id=$1",
                token, subfolderId, BookmarkRules.Name(name, BookmarkRules.MaxFolderNameLength, current.Name));
            return true;
        }, token);

    /// <summary>Deletes a subfolder and its bookmarks; answers the ids of the bookmarks that went with it.</summary>
    public Task<IReadOnlyList<long>> DeleteSubfolderAsync(long characterId, long folderId, long subfolderId, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            var current = await SubfolderAsync(connection, tx, subfolderId, token);
            if (current is null || current.FolderId != folderId)
                throw new BookmarkError("BookmarkSubfolderNoLongerThere");
            var removed = await IdsAsync(connection, tx,
                $"DELETE FROM {_schema}.bookmarks WHERE subfolder_id=$1 RETURNING bookmark_id", token, subfolderId);
            await Execute(connection, tx, $"DELETE FROM {_schema}.subfolders WHERE subfolder_id=$1", token, subfolderId);
            return (IReadOnlyList<long>)removed;
        }, token);

    /// <summary>Makes a bookmark of a static place (a station or a solar system) in a folder the character owns.</summary>
    public Task<Bookmark> CreateBookmarkAsync(long characterId, long folderId, BookmarkTarget target, string? memo, string? note,
        int expiryMode, long? subfolderId, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.bookmarks WHERE folder_id=$1", token, folderId) >= BookmarkRules.MaxBookmarksInPersonalFolder)
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
    /// Changes a bookmark: its memo, note, subfolder, folder and expiry (cancelled on request). The bookmark must be in the old folder
    /// and the character must own that folder and the new one.
    /// </summary>
    public Task<Bookmark> UpdateBookmarkAsync(long characterId, long bookmarkId, long oldFolderId, string? memo, string? note,
        long? subfolderId, long newFolderId, bool expiryCancel, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            var current = await BookmarkAsync(connection, tx, bookmarkId, token);
            if (current is null || current.FolderId != oldFolderId)
                throw new BookmarkError("BookmarkNotAvailable");
            RequireOwner(await FolderOfAsync(connection, tx, current.FolderId, token), characterId);
            RequireOwner(await FolderOfAsync(connection, tx, newFolderId, token), characterId);
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

    /// <summary>Deletes the listed bookmarks of a folder the character owns; answers the ids that were removed.</summary>
    public Task<IReadOnlyList<long>> DeleteBookmarksAsync(long characterId, long folderId, IReadOnlyList<long> bookmarkIds, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, folderId, token), characterId);
            return (IReadOnlyList<long>)await IdsAsync(connection, tx,
                $"DELETE FROM {_schema}.bookmarks WHERE folder_id=$1 AND bookmark_id = ANY($2) RETURNING bookmark_id", token, folderId, bookmarkIds.ToArray());
        }, token);

    /// <summary>Moves the listed bookmarks of one owned folder into another owned folder (and subfolder). Answers the moved bookmarks.</summary>
    public Task<IReadOnlyList<Bookmark>> MoveBookmarksAsync(long characterId, long fromFolderId, long toFolderId, long? subfolderId,
        IReadOnlyList<long> bookmarkIds, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, fromFolderId, token), characterId);
            RequireOwner(await FolderOfAsync(connection, tx, toFolderId, token), characterId);
            await RequireSubfolderInAsync(connection, tx, toFolderId, subfolderId, token);
            var moved = await BookmarkRowsAsync(connection, tx,
                $"UPDATE {_schema}.bookmarks SET folder_id=$2, subfolder_id=$3 WHERE folder_id=$1 AND bookmark_id = ANY($4) RETURNING {BookmarkColumns}",
                token, fromFolderId, toFolderId, subfolderId, bookmarkIds.ToArray());
            return (IReadOnlyList<Bookmark>)moved;
        }, token);

    /// <summary>Copies the listed bookmarks of one owned folder into another owned folder (new ids). Answers the copies.</summary>
    public Task<IReadOnlyList<Bookmark>> CopyBookmarksAsync(long characterId, long fromFolderId, long toFolderId, long? subfolderId,
        IReadOnlyList<long> bookmarkIds, CancellationToken token = default) =>
        InTransactionAsync(characterId, async (connection, tx) =>
        {
            RequireOwner(await FolderOfAsync(connection, tx, fromFolderId, token), characterId);
            RequireOwner(await FolderOfAsync(connection, tx, toFolderId, token), characterId);
            await RequireSubfolderInAsync(connection, tx, toFolderId, subfolderId, token);
            var sources = await BookmarkRowsAsync(connection, tx,
                $"SELECT {BookmarkColumns} FROM {_schema}.bookmarks WHERE folder_id=$1 AND bookmark_id = ANY($2) ORDER BY bookmark_id",
                token, fromFolderId, bookmarkIds.ToArray());
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
}
