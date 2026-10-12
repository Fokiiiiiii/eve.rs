using Npgsql;

namespace Eve.Bookmarks;

public sealed partial class BookmarkStore
{
    private const string FolderColumns = "folder_id, owner_id, folder_name, description, is_personal, is_active, created_ms";
    private const string BookmarkColumns = "bookmark_id, folder_id, subfolder_id, item_id, type_id, location_id, memo, note, creator_id, created_ms, expiry_ms";
    private const string BookmarkSelect = "b.bookmark_id, b.folder_id, b.subfolder_id, b.item_id, b.type_id, b.location_id, b.memo, b.note, b.creator_id, b.created_ms, b.expiry_ms";
    private const string SubfolderColumns = "subfolder_id, folder_id, subfolder_name, creator_id, created_ms";

    /// <summary>Runs one change of an owner's bookmarks: the owner's lock, the default folder if there is none, then the work, all in one transaction.</summary>
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

    /// <summary>A character with no folder gets the default personal folder, active (the reference's <c>ensureDefaultPersonalFolder</c>).</summary>
    private async Task EnsureDefaultAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long owner, CancellationToken token)
    {
        if (await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.folders WHERE owner_id=$1", token, owner) > 0) return;
        await Execute(connection, tx,
            $"INSERT INTO {_schema}.folders(folder_id,owner_id,folder_name,description,is_personal,known,is_active,created_ms) VALUES(nextval('{_schema}.bookmark_ids'),$1,$2,'',true,true,true,$3)",
            token, owner, BookmarkRules.DefaultFolderName, _nowMs());
    }

    /// <summary>Opens the folder's view: its bookmarks and subfolders when it is active, nothing when it is not.</summary>
    private async Task<FolderOpened> OpenedAsync(NpgsqlConnection connection, NpgsqlTransaction tx, BookmarkFolder folder, CancellationToken token)
    {
        var view = new FolderView(folder, BookmarkRules.AccessPersonal, folder.IsActive);
        if (!folder.IsActive) return new FolderOpened(view, [], []);
        var bookmarks = await BookmarksOfAsync(connection, tx, folder.OwnerId, $"f.folder_id = {folder.FolderId}", token);
        var subfolders = await SubfoldersOfAsync(connection, tx, folder.OwnerId, $"f.folder_id = {folder.FolderId}", token);
        return new FolderOpened(view, bookmarks, subfolders);
    }

    private static BookmarkFolder RequireOwner(BookmarkFolder folder, long characterId) =>
        folder.OwnerId == characterId ? folder : throw new BookmarkError("FolderAccessDenied");

    private async Task<BookmarkFolder> FolderOfAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long folderId, CancellationToken token)
    {
        await using var command = Command(connection, tx, $"SELECT {FolderColumns} FROM {_schema}.folders WHERE folder_id=$1", folderId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new BookmarkError("BookmarkFolderNoLongerThere");
        return ReadFolder(reader);
    }

    private async Task<IReadOnlyList<BookmarkFolder>> FoldersAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long owner, string where, CancellationToken token)
    {
        await using var command = Command(connection, tx,
            $"SELECT {FolderColumns} FROM {_schema}.folders WHERE owner_id=$1 AND {where} ORDER BY folder_id", owner);
        await using var reader = await command.ExecuteReaderAsync(token);
        var folders = new List<BookmarkFolder>();
        while (await reader.ReadAsync(token)) folders.Add(ReadFolder(reader));
        return folders;
    }

    private async Task<IReadOnlyList<Bookmark>> BookmarksOfAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long owner, string where, CancellationToken token)
    {
        await using var command = Command(connection, tx,
            $"SELECT {BookmarkSelect} FROM {_schema}.bookmarks b JOIN {_schema}.folders f ON f.folder_id=b.folder_id WHERE f.owner_id=$1 AND {where} ORDER BY b.bookmark_id", owner);
        return await ReadBookmarksAsync(command, token);
    }

    private async Task<IReadOnlyList<BookmarkSubfolder>> SubfoldersOfAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long owner, string where, CancellationToken token)
    {
        await using var command = Command(connection, tx,
            $"SELECT s.subfolder_id, s.folder_id, s.subfolder_name, s.creator_id, s.created_ms FROM {_schema}.subfolders s JOIN {_schema}.folders f ON f.folder_id=s.folder_id WHERE f.owner_id=$1 AND {where} ORDER BY s.subfolder_id", owner);
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

    private async Task<long> ActiveCountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long owner, CancellationToken token) =>
        await CountAsync(connection, tx, $"SELECT count(*) FROM {_schema}.folders WHERE owner_id=$1 AND is_personal AND known AND is_active", token, owner);

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

    private static BookmarkFolder ReadFolder(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        r.GetBoolean(4), r.GetBoolean(5), r.GetInt64(6));

    private static BookmarkSubfolder ReadSubfolder(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4));

    private static Bookmark ReadBookmark(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2),
        r.GetInt64(3), r.GetInt32(4), r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetInt64(9),
        r.IsDBNull(10) ? null : r.GetInt64(10));
}
