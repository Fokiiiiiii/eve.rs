using Npgsql;

namespace Eve.Bookmarks;

public sealed partial class BookmarkStore
{
    private const string FolderColumns = "folder_id, owner_id, folder_name, description, is_personal, is_active, created_ms, admin_group_id, manage_group_id, use_group_id, view_group_id";
    private const string FolderColumnsF = "f.folder_id, f.owner_id, f.folder_name, f.description, f.is_personal, f.is_active, f.created_ms, f.admin_group_id, f.manage_group_id, f.use_group_id, f.view_group_id";
    private const string BookmarkColumns = "bookmark_id, folder_id, subfolder_id, item_id, type_id, location_id, memo, note, creator_id, created_ms, expiry_ms";
    private const string BookmarkSelect = "b.bookmark_id, b.folder_id, b.subfolder_id, b.item_id, b.type_id, b.location_id, b.memo, b.note, b.creator_id, b.created_ms, b.expiry_ms";
    private const string SubfolderColumns = "subfolder_id, folder_id, subfolder_name, creator_id, created_ms";

    /// <summary>The column after the folder columns of a folder view query: the character's connection state (null when it has none).</summary>
    private const int ConnectionColumn = 11;

    /// <summary>Runs one change of a character's bookmarks: the character's lock, the default folder if there is none, then the work, all in one transaction.</summary>
    private async Task<T> InTransactionAsync<T>(long owner, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action, CancellationToken token)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await Execute(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", token, $"bookmarks:{_schema}:{owner}");
        await EnsureDefaultAsync(connection, transaction, owner, token);
        var result = await action(connection, transaction);
        await transaction.CommitAsync(token);
        return result;
    }

    /// <summary>A shared folder's bookmark writes are serialised per folder, so its capacity holds under concurrent calls from several characters.</summary>
    private static async Task LockFolderAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, long folderId, CancellationToken token) =>
        await Execute(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", token, $"bookmarks:{schema}:folder:{folderId}");

    /// <summary>A character with no folder gets the default personal folder, active (the reference's <c>ensureDefaultPersonalFolder</c>).</summary>
    private async Task EnsureDefaultAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long owner, CancellationToken token)
    {
        if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.folders WHERE owner_id=$1", token, owner) > 0) return;
        await Execute(connection, tx,
            $"INSERT INTO {_schema}.folders(folder_id,owner_id,folder_name,description,is_personal,known,is_active,created_ms) VALUES(nextval('{_schema}.bookmark_ids'),$1,$2,'',true,true,true,$3)",
            token, owner, BookmarkRules.DefaultFolderName, _nowMs());
    }

    /// <summary>The folder's view for a change: its bookmarks and subfolders when it is active for the character, nothing when it is not.</summary>
    private async Task<FolderOpened> OpenedAsync(NpgsqlConnection connection, NpgsqlTransaction tx, BookmarkFolder folder, int level, bool active,
        CancellationToken token)
    {
        var view = new FolderView(folder, level, active);
        if (!active) return new FolderOpened(view, [], []);
        var ids = new[] { folder.FolderId };
        return new FolderOpened(view, await BookmarksInAsync(connection, tx, ids, token), await SubfoldersInAsync(connection, tx, ids, token));
    }

    private static int CapacityOf(BookmarkFolder folder) =>
        folder.IsPersonal ? BookmarkRules.MaxBookmarksInPersonalFolder : BookmarkRules.MaxBookmarksInSharedFolder;

    /// <summary>The character's level in a folder, which must allow the action (the reference's <c>FolderAccessDenied</c>).</summary>
    private static int RequireLevel(BookmarkFolder folder, long characterId, AccessLists lists, Func<int, bool> allows)
    {
        var level = FolderAccess.LevelOf(folder, characterId, lists);
        return allows(level) ? level : throw new BookmarkError("FolderAccessDenied");
    }

    private static int RequireAdmin(BookmarkFolder folder, long characterId, AccessLists lists)
    {
        var level = FolderAccess.LevelOf(folder, characterId, lists);
        if (level == FolderAccess.None) throw new BookmarkError("FolderAccessDenied");
        return FolderAccess.IsAdmin(level) ? level : throw new BookmarkError("AdminAccessRequired");
    }

    private static void RequireAssignable(SharedGroups groups, AccessLists lists)
    {
        foreach (var group in new[] { groups.AdminGroupId, groups.ManageGroupId, groups.UseGroupId, groups.ViewGroupId })
            if (group is { } id && !lists.MayAssign(id))
                throw new BookmarkError("AccessListNotAssignable", "Only an admin or a manager of an access list can put it on a shared folder.");
    }

    private async Task<BookmarkFolder> FolderOfAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long folderId, CancellationToken token)
    {
        await using var command = Command(connection, tx, $"SELECT {FolderColumns} FROM {_schema}.folders WHERE folder_id=$1", folderId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new BookmarkError("BookmarkFolderNoLongerThere");
        return ReadFolder(reader);
    }

    /// <summary>The folders matching the condition, each with the character's view of it (its connection state included).</summary>
    private async Task<IReadOnlyList<FolderView>> ViewsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long characterId, AccessLists lists,
        string where, CancellationToken token, params object?[] more)
    {
        var args = new object?[] { characterId }.Concat(more).ToArray();
        await using var command = Command(connection, tx,
            $"SELECT {FolderColumnsF}, c.is_active FROM {_schema}.folders f LEFT JOIN {_schema}.connections c ON c.folder_id=f.folder_id AND c.character_id=$1 WHERE {where} ORDER BY f.folder_id",
            args);
        await using var reader = await command.ExecuteReaderAsync(token);
        var views = new List<FolderView>();
        while (await reader.ReadAsync(token))
        {
            var folder = ReadFolder(reader);
            var connected = !reader.IsDBNull(ConnectionColumn) && reader.GetBoolean(ConnectionColumn);
            var level = FolderAccess.LevelOf(folder, characterId, lists);
            if (!FolderAccess.CanView(level)) continue;
            views.Add(new FolderView(folder, level, folder.IsPersonal ? folder.IsActive : connected));
        }
        return views;
    }

    /// <summary>Removes the bookmarks whose expiry has come and answers them: the expiry is absolute, so no character's read decides it.</summary>
    private async Task<IReadOnlyList<(long FolderId, long BookmarkId)>> DeleteExpiredAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, CancellationToken token)
    {
        await using var command = Command(connection, tx!,
            $"DELETE FROM {_schema}.bookmarks WHERE expiry_ms IS NOT NULL AND expiry_ms <= $1 RETURNING folder_id, bookmark_id", _nowMs());
        await using var reader = await command.ExecuteReaderAsync(token);
        var removed = new List<(long FolderId, long BookmarkId)>();
        while (await reader.ReadAsync(token)) removed.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return removed;
    }

    private async Task<bool> ConnectionActiveAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long folderId, long characterId, CancellationToken token)
    {
        await using var command = Command(connection, tx, $"SELECT is_active FROM {_schema}.connections WHERE folder_id=$1 AND character_id=$2", folderId, characterId);
        return await command.ExecuteScalarAsync(token) is bool active && active;
    }

    private async Task<IReadOnlyList<Bookmark>> BookmarksInAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long[] folderIds, CancellationToken token) =>
        await BookmarkRowsAsync(connection, tx, $"SELECT {BookmarkColumns} FROM {_schema}.bookmarks WHERE folder_id = ANY($1) ORDER BY bookmark_id", token, folderIds);

    private async Task<IReadOnlyList<BookmarkSubfolder>> SubfoldersInAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long[] folderIds, CancellationToken token)
    {
        await using var command = Command(connection, tx,
            $"SELECT {SubfolderColumns} FROM {_schema}.subfolders WHERE folder_id = ANY($1) ORDER BY subfolder_id", folderIds);
        await using var reader = await command.ExecuteReaderAsync(token);
        var subfolders = new List<BookmarkSubfolder>();
        while (await reader.ReadAsync(token)) subfolders.Add(ReadSubfolder(reader));
        return subfolders;
    }

    private async Task<Bookmark?> BookmarkAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long bookmarkId, CancellationToken token) =>
        (await BookmarkRowsAsync(connection, tx, $"SELECT {BookmarkColumns} FROM {_schema}.bookmarks WHERE bookmark_id=$1", token, bookmarkId))
        .FirstOrDefault();

    private async Task<BookmarkSubfolder?> SubfolderAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long subfolderId, CancellationToken token)
    {
        await using var command = Command(connection, tx, $"SELECT {SubfolderColumns} FROM {_schema}.subfolders WHERE subfolder_id=$1", subfolderId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadSubfolder(reader) : null;
    }

    /// <summary>A subfolder must be in the folder the bookmark goes to (the reference's <c>BookmarkSubfolderNoLongerThere</c>).</summary>
    private async Task RequireSubfolderInAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long folderId, long? subfolderId, CancellationToken token)
    {
        if (subfolderId is not { } subfolder) return;
        var subfolderFolder = await CountAsync(connection, tx,
            $"SELECT count(*) FROM {_schema}.subfolders WHERE subfolder_id=$1 AND folder_id=$2", token, subfolder, folderId);
        if (subfolderFolder == 0) throw new BookmarkError("BookmarkSubfolderNoLongerThere");
    }

    /// <summary>The character's personal folders that are known and active (the limit of active personal folders).</summary>
    private async Task<long> ActiveCountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long owner, CancellationToken token) =>
        await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.folders WHERE owner_id=$1 AND is_personal AND known AND is_active", token, owner);

    /// <summary>The shared folders the character has active (the limit of active shared folders, three in the client).</summary>
    private async Task<long> SharedActiveCountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long characterId, CancellationToken token) =>
        await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.connections WHERE character_id=$1 AND is_active", token, characterId);

    /// <summary>The folders the character has in its list: its known personal folders and its shared connections.</summary>
    private async Task<long> KnownCountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long characterId, CancellationToken token) =>
        await CountAsync(connection, tx,
            $"SELECT (SELECT count(*) FROM {_schema}.folders WHERE owner_id=$1 AND is_personal AND known) + (SELECT count(*) FROM {_schema}.connections WHERE character_id=$1)",
            token, characterId);

    private async Task InsertBookmarkAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Bookmark bookmark, CancellationToken token) =>
        await Execute(connection, tx,
            $"INSERT INTO {_schema}.bookmarks({BookmarkColumns}) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)", token,
            bookmark.BookmarkId, bookmark.FolderId, bookmark.SubfolderId, bookmark.ItemId, bookmark.TypeId, bookmark.LocationId,
            bookmark.Memo, bookmark.Note, bookmark.CreatorId, bookmark.CreatedMs, bookmark.ExpiryMs);

    private async Task<long> NextIdAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken token)
    {
        await using var command = Command(connection, tx, $"SELECT nextval('{_schema}.bookmark_ids')");
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    private async Task<IReadOnlyList<Bookmark>> BookmarkRowsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken token, params object?[] args)
    {
        await using var command = Command(connection, tx, sql, args);
        return await ReadBookmarksAsync(command, token);
    }

    private static async Task<IReadOnlyList<Bookmark>> ReadBookmarksAsync(NpgsqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        var bookmarks = new List<Bookmark>();
        while (await reader.ReadAsync(token)) bookmarks.Add(ReadBookmark(reader));
        return bookmarks;
    }

    private async Task<List<long>> IdsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken token, params object?[] args)
    {
        await using var command = Command(connection, tx, sql, args);
        await using var reader = await command.ExecuteReaderAsync(token);
        var ids = new List<long>();
        while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken token, params object?[] args)
    {
        await using var command = Command(connection, tx, sql, args);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken token, params object?[] args)
    {
        await using var command = Command(connection, tx, sql, args);
        await command.ExecuteNonQueryAsync(token);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, params object?[] args)
    {
        var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var value in args) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }

    private static BookmarkFolder ReadFolder(NpgsqlDataReader r)
    {
        SharedGroups? groups = r.IsDBNull(7) && r.IsDBNull(8) && r.IsDBNull(9) && r.IsDBNull(10) ? null
            : new SharedGroups(r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8),
                r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10));
        return new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetInt64(6), groups);
    }

    private static BookmarkSubfolder ReadSubfolder(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4));

    private static Bookmark ReadBookmark(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2),
        r.GetInt64(3), r.GetInt32(4), r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetInt64(9),
        r.IsDBNull(10) ? null : r.GetInt64(10));
}
