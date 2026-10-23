namespace Eve.Bookmarks;

/// <summary>
/// The access lists of a shared folder, by level: the admin, manage, use and view list ids (the client's ids; null when a level has no list).
/// </summary>
public sealed record SharedGroups(long? AdminGroupId, long? ManageGroupId, long? UseGroupId, long? ViewGroupId);

/// <summary>
/// The access lists a character holds a role in, by list id: 0 member, 1 manager, 2 admin (the access list rules). A character holds a
/// list through a member, manager or admin entry, and <see cref="CorporationId"/> is its corporation, whose built-in list the corporation's
/// members may put on a folder.
/// </summary>
public sealed record AccessLists(IReadOnlyDictionary<long, int> RoleByGroup, long CorporationId = 0)
{
    public static readonly AccessLists Empty = new(new Dictionary<long, int>());

    /// <summary>The character is a member, manager or admin of the list.</summary>
    public bool Holds(long groupId) => RoleByGroup.ContainsKey(groupId);

    public bool MayAssign(long groupId) =>
        groupId > 0 && ((RoleByGroup.TryGetValue(groupId, out var role) && role >= 1) || (CorporationId > 0 && groupId == CorporationId));
}

public static class FolderAccess
{
    public const int None = 0;
    public const int Personal = 1;
    public const int View = 2;
    public const int Use = 3;
    public const int Manage = 4;
    public const int Admin = 5;

    /// <summary>
    /// </summary>
    public static int LevelOf(BookmarkFolder folder, long characterId, AccessLists lists)
    {
        if (folder.IsPersonal) return folder.OwnerId == characterId ? Personal : None;
        if (folder.Groups is not { } groups) return None;
        if (groups.AdminGroupId is { } admin && lists.Holds(admin)) return Admin;
        if (groups.ManageGroupId is { } manage && lists.Holds(manage)) return Manage;
        if (groups.UseGroupId is { } use && lists.Holds(use)) return Use;
        if (groups.ViewGroupId is { } view && lists.Holds(view)) return View;
        return None;
    }

    public static bool CanView(int level) => level >= Personal;

    public static bool CanUse(int level) => level == Personal || level >= Use;

    public static bool CanManage(int level) => level == Personal || level >= Manage;

    public static bool IsAdmin(int level) => level == Personal || level == Admin;
}
