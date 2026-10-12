using Npgsql;

namespace Eve.Bookmarks.Tests;

public sealed class BookmarkStoreTests
{
    private const long CharacterId = 90000001;
    private const long OtherCharacterId = 90000002;
    private static readonly BookmarkTarget Station = new(60000001, 1529, 30000142);

    [PgFact]
    public async Task A_character_without_folders_gets_the_default_active_personal_folder()
    {
        await using var rig = await Rig.CreateAsync();

        var active = await rig.Store.ActiveAsync(CharacterId);

        var folder = Assert.Single(active.Folders).Folder;
        Assert.Equal("Personal Locations", folder.Name);
        Assert.True(folder.IsPersonal);
        Assert.Empty(active.Bookmarks);
        Assert.Empty(active.Subfolders);
    }

    [PgFact]
    public async Task Only_five_personal_folders_are_active_and_at_most_thirty_are_known()
    {
        await using var rig = await Rig.CreateAsync();

        // the default folder is active, so four more fill the five active places
        var views = new List<FolderView>();
        for (var i = 0; i < 6; i++) views.Add(await rig.Store.AddFolderAsync(CharacterId, true, $"Folder {i}", ""));
        Assert.Equal(4, views.Count(view => view.IsActive));
        Assert.False(views[4].IsActive);
        Assert.False(views[5].IsActive);

        // the default folder and six more are known; 23 more fill the list, the next is refused
        for (var i = 6; i < 29; i++) await rig.Store.AddFolderAsync(CharacterId, true, $"Folder {i}", "");
        await Assert.ThrowsAsync<BookmarkError>(async () => await rig.Store.AddFolderAsync(CharacterId, true, "one too many", ""));
    }

    [PgFact]
    public async Task A_shared_folder_is_refused_because_the_access_group_store_is_not_built()
    {
        await using var rig = await Rig.CreateAsync();

        var error = await Assert.ThrowsAsync<BookmarkError>(async () => await rig.Store.AddFolderAsync(CharacterId, false, "Corp", ""));

        Assert.Equal("SharedFoldersNotBuilt", error.Code);
        Assert.NotNull(error.Notice);
    }

    [PgFact]
    public async Task A_bookmark_of_a_place_expires_on_its_time_and_the_read_removes_it()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = (await rig.Store.ActiveAsync(CharacterId)).Folders.Single().Folder;
        await rig.Store.CreateBookmarkAsync(CharacterId, folder.FolderId, Station, "Dock", "note", 1, null);
        await rig.Store.CreateBookmarkAsync(CharacterId, folder.FolderId, Station, "Keep", "", 0, null);

        Assert.Equal(2, (await rig.Store.ActiveAsync(CharacterId)).Bookmarks.Count);
        rig.Now += 5L * 3_600_000;
        var after = await rig.Store.ActiveAsync(CharacterId);

        var kept = Assert.Single(after.Bookmarks);
        Assert.Equal("Keep", kept.Memo);
        Assert.Null(kept.ExpiryMs);
    }

    [PgFact]
    public async Task Deleting_a_subfolder_takes_its_bookmarks_and_the_subfolder_limit_is_thirty()
    {
        await using var rig = await Rig.CreateAsync();
        var folderId = (await rig.Store.ActiveAsync(CharacterId)).Folders.Single().Folder.FolderId;
        var subfolder = await rig.Store.CreateSubfolderAsync(CharacterId, folderId, "Trade hubs");
        var inside = await rig.Store.CreateBookmarkAsync(CharacterId, folderId, Station, "Hub", "", 0, subfolder.SubfolderId);
        var outside = await rig.Store.CreateBookmarkAsync(CharacterId, folderId, Station, "Outside", "", 0, null);

        var removed = await rig.Store.DeleteSubfolderAsync(CharacterId, folderId, subfolder.SubfolderId);

        Assert.Equal([inside.BookmarkId], removed);
        var active = await rig.Store.ActiveAsync(CharacterId);
        Assert.Equal([outside.BookmarkId], active.Bookmarks.Select(bookmark => bookmark.BookmarkId));
        Assert.Empty(active.Subfolders);
        for (var i = 0; i < 30; i++) await rig.Store.CreateSubfolderAsync(CharacterId, folderId, $"s{i}");
        await Assert.ThrowsAsync<BookmarkError>(async () => await rig.Store.CreateSubfolderAsync(CharacterId, folderId, "31st"));
    }

    [PgFact]
    public async Task Moving_and_copying_keep_the_bookmark_in_one_folder_or_make_a_new_one()
    {
        await using var rig = await Rig.CreateAsync();
        var home = (await rig.Store.ActiveAsync(CharacterId)).Folders.Single().Folder.FolderId;
        var other = (await rig.Store.AddFolderAsync(CharacterId, true, "Other", "")).Folder.FolderId;
        var bookmark = await rig.Store.CreateBookmarkAsync(CharacterId, home, Station, "Hub", "", 0, null);

        var moved = await rig.Store.MoveBookmarksAsync(CharacterId, home, other, null, [bookmark.BookmarkId]);
        var copies = await rig.Store.CopyBookmarksAsync(CharacterId, other, home, null, [bookmark.BookmarkId]);

        Assert.Equal(other, Assert.Single(moved).FolderId);
        var copy = Assert.Single(copies);
        Assert.NotEqual(bookmark.BookmarkId, copy.BookmarkId);
        Assert.Equal(home, copy.FolderId);
        Assert.Equal(2, (await rig.Store.ActiveAsync(CharacterId)).Bookmarks.Count);
    }

    [PgFact]
    public async Task An_update_from_the_wrong_folder_is_refused_and_a_right_one_cancels_the_expiry()
    {
        await using var rig = await Rig.CreateAsync();
        var home = (await rig.Store.ActiveAsync(CharacterId)).Folders.Single().Folder.FolderId;
        var bookmark = await rig.Store.CreateBookmarkAsync(CharacterId, home, Station, "Old", "", 2, null);
        var wrong = await Assert.ThrowsAsync<BookmarkError>(async () =>
            await rig.Store.UpdateBookmarkAsync(CharacterId, bookmark.BookmarkId, home + 999, "New", "", null, home, false));
        Assert.Equal("BookmarkNotAvailable", wrong.Code);

        var updated = await rig.Store.UpdateBookmarkAsync(CharacterId, bookmark.BookmarkId, home, "New", "text", null, home, true);

        Assert.Equal("New", updated.Memo);
        Assert.Null(updated.ExpiryMs);
    }

    [PgFact]
    public async Task A_folder_of_another_character_is_refused_and_a_full_folder_is_refused()
    {
        await using var rig = await Rig.CreateAsync();
        var theirs = (await rig.Store.ActiveAsync(OtherCharacterId)).Folders.Single().Folder.FolderId;

        var denied = await Assert.ThrowsAsync<BookmarkError>(async () => await rig.Store.FolderInfoAsync(CharacterId, theirs));
        Assert.Equal("FolderAccessDenied", denied.Code);

        var home = (await rig.Store.ActiveAsync(CharacterId)).Folders.Single().Folder.FolderId;
        await using (var fill = rig.Source.CreateCommand(
            $"INSERT INTO {rig.Schema}.bookmarks(bookmark_id,folder_id,item_id,type_id,location_id,memo,note,creator_id,created_ms) " +
            $"SELECT nextval('{rig.Schema}.bookmark_ids'), $1, 60000001, 1529, 30000142, 'b', '', $2, 0 FROM generate_series(1, {BookmarkRules.MaxBookmarksInPersonalFolder})"))
        {
            fill.Parameters.Add(new NpgsqlParameter { Value = home });
            fill.Parameters.Add(new NpgsqlParameter { Value = CharacterId });
            await fill.ExecuteNonQueryAsync();
        }
        var full = await Assert.ThrowsAsync<BookmarkError>(async () => await rig.Store.CreateBookmarkAsync(CharacterId, home, Station, "one more", "", 0, null));
        Assert.Equal("FolderCapacityExceeded", full.Code);
    }

    [PgFact]
    public async Task The_folders_and_bookmarks_are_kept_when_the_host_restarts()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await rig.Store.AddFolderAsync(CharacterId, true, "Kept", "desc");
        await rig.Store.CreateBookmarkAsync(CharacterId, folder.Folder.FolderId, Station, "Stays", "", 0, null);

        var reopened = (await rig.Reopen().ActiveAsync(CharacterId));

        Assert.Contains(reopened.Folders, view => view.Folder.Name == "Kept" && view.Folder.Description == "desc");
        Assert.Equal("Stays", Assert.Single(reopened.Bookmarks).Memo);
    }
}
