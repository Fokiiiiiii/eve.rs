using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Bookmarks.Tests;

/// <summary>
/// Shared (access group) folders: the access levels the lists give, who may write what, the connections and their limits, losing access,
/// the subfolder and copy rules, deleting a folder, and the restart.
/// </summary>
public sealed class SharedFolderTests
{
    private const long Alice = 90000001;
    private const long Bob = 90000002;
    private const long Carol = 90000003;
    private const long Dan = 90000004;
    private const long Eve = 90000005;
    private const long AdminList = 2000000001;
    private const long ManageList = 2000000002;
    private const long UseList = 2000000003;
    private const long ViewList = 2000000004;
    private static readonly BookmarkTarget Station = new(60000001, 1529, 30000142);

    private static AccessLists Lists(params (long Group, int Role)[] roles) => new(roles.ToDictionary(role => role.Group, role => role.Role));

    private static SharedGroups All() => new(AdminList, ManageList, UseList, ViewList);

    /// <summary>Alice holds all four lists as admin, so she may put each on the folder she makes.</summary>
    private static AccessLists AliceLists() => Lists((AdminList, 2), (ManageList, 2), (UseList, 2), (ViewList, 2));

    [PgFact]
    public async Task A_character_creates_at_most_30_folders_counting_its_default_folder()
    {
        await using var rig = await Rig.CreateAsync();
        for (var i = 0; i < BookmarkRules.MaxCreatedFolders - 1; i++)
            await rig.Store.AddFolderAsync(Bob, true, $"Folder {i}", "");

        var refused = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.AddFolderAsync(Bob, true, "one too many", ""));
        Assert.Equal("TooManyCreatedFolders", refused.Code);
        Assert.Equal(BookmarkRules.MaxCreatedFolders, (await rig.Store.AdminFoldersAsync(Bob)).Count);
    }

    [PgFact]
    public async Task Expiry_removes_a_shared_bookmark_on_its_time_and_answers_the_removed_ids()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        var short_ = await rig.Store.CreateBookmarkAsync(Alice, folder, Station, "short", "", BookmarkRules.Expiry4Hours, null, AliceLists());
        var kept = await rig.Store.CreateBookmarkAsync(Alice, folder, Station, "kept", "", BookmarkRules.ExpiryNone, null, AliceLists());

        Assert.Empty(await rig.Store.ExpireAsync());
        rig.Now += 5L * 3_600_000;
        var removed = await rig.Store.ExpireAsync();

        Assert.Equal([(folder, short_.BookmarkId)], removed);
        var remaining = await rig.Store.ActiveAsync(Alice, AliceLists());
        Assert.Equal([kept.BookmarkId], remaining.Bookmarks.Where(bookmark => bookmark.FolderId == folder).Select(bookmark => bookmark.BookmarkId));
    }

    [PgFact]
    public async Task Connected_folders_of_a_group_are_the_ones_whose_lists_include_it()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        await rig.Store.AddKnownFolderAsync(Bob, folder, true, Lists((ViewList, 0)));

        // the creator is connected too (its folder is active for it)
        Assert.Equal([(folder, Alice), (folder, Bob)], await rig.Store.ConnectedFoldersUsingAsync(ViewList));
        Assert.Equal([(folder, Alice), (folder, Bob)], await rig.Store.ConnectedFoldersUsingAsync(AdminList));
        Assert.Empty(await rig.Store.ConnectedFoldersUsingAsync(3000000009));
    }

    /// <summary>The shared folders in an active view (each character also has its default personal folder).</summary>
    private static List<FolderView> Shared(ActiveBookmarks active) => active.Folders.Where(view => !view.Folder.IsPersonal).ToList();

    private static async Task<long> SharedFolderAsync(Rig rig, string name = "Shared")
    {
        var folder = await rig.Store.AddFolderAsync(Alice, false, name, "fleet", All(), AliceLists());
        return folder.Folder.FolderId;
    }

    [PgFact]
    public async Task A_shared_folder_needs_an_admin_list_and_lists_the_creator_may_assign()
    {
        await using var rig = await Rig.CreateAsync();

        var noAdmin = await Assert.ThrowsAsync<BookmarkError>(() =>
            rig.Store.AddFolderAsync(Alice, false, "Shared", "", new SharedGroups(null, ManageList, null, null), AliceLists()));
        Assert.Equal("AdminAccessRequired", noAdmin.Code);
        var member = await Assert.ThrowsAsync<BookmarkError>(() =>
            rig.Store.AddFolderAsync(Alice, false, "Shared", "", All(), Lists((AdminList, 0), (ManageList, 2), (UseList, 2), (ViewList, 2))));
        Assert.Equal("AccessListNotAssignable", member.Code);

        var folder = await rig.Store.AddFolderAsync(Alice, false, "  Shared  ", "fleet", All(), AliceLists());
        Assert.Equal(FolderAccess.Admin, folder.AccessLevel);
        Assert.False(folder.Folder.IsPersonal);
        Assert.Equal("Shared", folder.Folder.Name);
        Assert.Equal(ViewList, folder.Folder.Groups!.ViewGroupId);
        Assert.True(folder.IsActive);
    }

    [PgFact]
    public async Task The_level_a_list_gives_decides_what_the_character_may_do_in_the_folder()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        var viewer = Lists((ViewList, 0));
        var user = Lists((UseList, 0));
        var manager = Lists((ManageList, 0));

        Assert.Equal(FolderAccess.View, (await rig.Store.FolderInfoAsync(Bob, folder, viewer)).AccessLevel);
        Assert.Equal(FolderAccess.Use, (await rig.Store.FolderInfoAsync(Carol, folder, user)).AccessLevel);
        Assert.Equal(FolderAccess.Manage, (await rig.Store.FolderInfoAsync(Dan, folder, manager)).AccessLevel);
        var denied = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.FolderInfoAsync(Eve, folder, Lists()));
        Assert.Equal("FolderAccessDenied", denied.Code);

        var made = await rig.Store.CreateBookmarkAsync(Carol, folder, Station, "Jita", "", 0, null, user);
        Assert.Equal(Carol, made.CreatorId);
        var refused = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.CreateBookmarkAsync(Bob, folder, Station, "Jita", "", 0, null, viewer));
        Assert.Equal("FolderAccessDenied", refused.Code);
    }

    [PgFact]
    public async Task Only_the_admin_renames_and_sets_the_lists_and_only_the_admin_deletes_the_folder()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);

        var user = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.UpdateFolderAsync(Carol, folder, "Renamed", "", null, Lists((UseList, 0))));
        Assert.Equal("AdminAccessRequired", user.Code);
        var viewer = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.DeleteFolderAsync(Bob, folder, Lists((ViewList, 0))));
        Assert.Equal("AdminAccessRequired", viewer.Code);

        var renamed = await rig.Store.UpdateFolderAsync(Alice, folder, "Renamed", "d", All(), AliceLists());
        Assert.Equal("Renamed", renamed.Folder.Name);
        var emptied = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.UpdateFolderAsync(Alice, folder, "x", "", new SharedGroups(null, null, null, null), AliceLists()));
        Assert.Equal("AdminAccessRequired", emptied.Code);
    }

    [PgFact]
    public async Task A_connection_holds_the_folder_and_the_shared_active_limit_is_three()
    {
        await using var rig = await Rig.CreateAsync();
        var first = await SharedFolderAsync(rig, "One");
        var second = await SharedFolderAsync(rig, "Two");
        var third = await SharedFolderAsync(rig, "Three");
        var fourth = await SharedFolderAsync(rig, "Four");
        var viewer = Lists((ViewList, 0));
        await rig.Store.CreateBookmarkAsync(Alice, first, Station, "Jita", "", 0, null, AliceLists());

        var opened = await rig.Store.AddKnownFolderAsync(Bob, first, true, viewer);
        Assert.True(opened.Folder.IsActive);
        Assert.Single(opened.Bookmarks);
        await rig.Store.AddKnownFolderAsync(Bob, second, true, viewer);
        await rig.Store.AddKnownFolderAsync(Bob, third, true, viewer);
        var overLimit = await rig.Store.AddKnownFolderAsync(Bob, fourth, true, viewer);
        Assert.False(overLimit.Folder.IsActive);
        var setActive = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.SetKnownFolderActiveAsync(Bob, fourth, true, viewer));
        Assert.Equal("TooManyActiveFolders", setActive.Code);

        var active = await rig.Store.ActiveAsync(Bob, viewer);
        var shared = Shared(active);
        Assert.Equal(3, shared.Count);
        Assert.Equal(new[] { first, second, third }.OrderBy(id => id), shared.Select(view => view.Folder.FolderId).OrderBy(id => id));
        Assert.Contains(Bob, await rig.Store.ActiveViewersAsync(first, Alice));
        Assert.DoesNotContain(Alice, await rig.Store.ActiveViewersAsync(first, Alice));
    }

    [PgFact]
    public async Task A_character_that_loses_its_list_no_longer_sees_the_folder()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        await rig.Store.AddKnownFolderAsync(Bob, folder, true, Lists((ViewList, 0)));
        Assert.Single(Shared(await rig.Store.ActiveAsync(Bob, Lists((ViewList, 0)))));

        var active = await rig.Store.ActiveAsync(Bob, Lists());

        Assert.Empty(Shared(active));
        // the connection stays, but the level the lists now give is none: no update reaches Bob
        Assert.Equal(FolderAccess.None, await rig.Store.LevelAsync(folder, Bob, Lists()));
        var denied = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.FolderInfoAsync(Bob, folder, Lists()));
        Assert.Equal("FolderAccessDenied", denied.Code);
    }

    [PgFact]
    public async Task A_user_edits_and_deletes_only_its_own_bookmarks_and_a_manager_edits_all()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        var user = Lists((UseList, 0));
        var manager = Lists((ManageList, 0));
        var mine = await rig.Store.CreateBookmarkAsync(Carol, folder, Station, "mine", "", 0, null, user);
        var theirs = await rig.Store.CreateBookmarkAsync(Alice, folder, Station, "theirs", "", 0, null, AliceLists());

        var deleted = await rig.Store.DeleteBookmarksAsync(Carol, folder, [mine.BookmarkId, theirs.BookmarkId], user);
        Assert.Equal([mine.BookmarkId], deleted);

        var denied = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.UpdateBookmarkAsync(Carol, theirs.BookmarkId, folder, "x", "", null, folder, false, user));
        Assert.Equal("FolderAccessDenied", denied.Code);
        var edited = await rig.Store.UpdateBookmarkAsync(Dan, theirs.BookmarkId, folder, "edited", "", null, folder, false, manager);
        Assert.Equal("edited", edited.Memo);
        Assert.Equal([theirs.BookmarkId], await rig.Store.DeleteBookmarksAsync(Dan, folder, [theirs.BookmarkId], manager));
    }

    [PgFact]
    public async Task A_subfolder_is_made_by_a_user_but_its_bookmarks_must_all_be_its_own_to_delete_it()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        var user = Lists((UseList, 0));
        var manager = Lists((ManageList, 0));
        var subfolder = await rig.Store.CreateSubfolderAsync(Carol, folder, "Carol's", user);
        Assert.Equal(Carol, subfolder.CreatorId);
        await rig.Store.CreateBookmarkAsync(Alice, folder, Station, "theirs", "", 0, subfolder.SubfolderId, AliceLists());

        var refused = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.DeleteSubfolderAsync(Carol, folder, subfolder.SubfolderId, user));
        Assert.Equal("CouldNotDeleteBookmarksInSubfolder", refused.Code);
        var viewer = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.CreateSubfolderAsync(Bob, folder, "x", Lists((ViewList, 0))));
        Assert.Equal("FolderAccessDenied", viewer.Code);

        var removed = await rig.Store.DeleteSubfolderAsync(Dan, folder, subfolder.SubfolderId, manager);
        Assert.Single(removed);
    }

    [PgFact]
    public async Task Copying_from_a_viewed_folder_makes_the_copy_its_maker_owns()
    {
        await using var rig = await Rig.CreateAsync();
        var shared = await SharedFolderAsync(rig);
        var source = await rig.Store.CreateBookmarkAsync(Alice, shared, Station, "Jita", "", 0, null, AliceLists());
        var bobPersonal = (await rig.Store.ActiveAsync(Bob)).Folders.Single().Folder.FolderId;

        var copies = await rig.Store.CopyBookmarksAsync(Bob, shared, bobPersonal, null, [source.BookmarkId], Lists((ViewList, 0)));

        var copy = Assert.Single(copies);
        Assert.Equal(Bob, copy.CreatorId);
        Assert.Equal(bobPersonal, copy.FolderId);
        Assert.NotEqual(source.BookmarkId, copy.BookmarkId);
    }

    [PgFact]
    public async Task A_shared_folder_holds_500_bookmarks_and_refuses_the_501st()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        var manager = Lists((ManageList, 0));
        for (var i = 0; i < BookmarkRules.MaxBookmarksInSharedFolder; i++)
            await rig.Store.CreateBookmarkAsync(Dan, folder, Station, $"b{i}", "", 0, null, manager);

        var full = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.CreateBookmarkAsync(Dan, folder, Station, "over", "", 0, null, manager));
        Assert.Equal("FolderCapacityExceeded", full.Code);
    }

    [PgFact]
    public async Task Deleting_the_folder_answers_the_viewers_that_had_it_active_and_it_is_gone()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        await rig.Store.AddKnownFolderAsync(Bob, folder, true, Lists((ViewList, 0)));
        await rig.Store.AddKnownFolderAsync(Carol, folder, false, Lists((UseList, 0)));

        var viewers = await rig.Store.DeleteFolderAsync(Alice, folder, AliceLists());

        Assert.Equal([Bob], viewers);
        var gone = await Assert.ThrowsAsync<BookmarkError>(() => rig.Store.FolderInfoAsync(Alice, folder, AliceLists()));
        Assert.Equal("BookmarkFolderNoLongerThere", gone.Code);
    }

    [PgFact]
    public async Task The_shared_folders_and_connections_survive_a_restart()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = await SharedFolderAsync(rig);
        await rig.Store.AddKnownFolderAsync(Bob, folder, true, Lists((ViewList, 0)));

        var restarted = rig.Reopen();

        var active = await restarted.ActiveAsync(Bob, Lists((ViewList, 0)));
        Assert.Equal(folder, Assert.Single(Shared(active)).Folder.FolderId);
        Assert.Equal(FolderAccess.View, Assert.Single(Shared(active)).AccessLevel);
    }
}
