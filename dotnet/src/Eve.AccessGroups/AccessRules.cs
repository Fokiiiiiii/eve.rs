namespace Eve.AccessGroups;

/// <summary>The membership types of the client's <c>ownergroupConst</c>: none, member, manager, admin, excluded (blocked).</summary>
public enum MembershipType { None = -1, Member = 0, Manager = 1, Admin = 2, Excluded = 3 }

/// <summary>What a member entry names: everyone (the public entry, id 0), a character, a corporation or an alliance.</summary>
public enum MemberKind { Everyone = 0, Character = 1, Corporation = 2, Alliance = 3 }

/// <summary>The character and its corporation and alliance, the way the entries of a group name them.</summary>
public sealed record Affiliation(long CharacterId, long CorporationId, long AllianceId);

public sealed record AccessGroup(long GroupId, long CreatorId, string Name, string Description, bool CorpBuiltIn, long CreatedMs);

public sealed record AccessMember(long MemberId, MemberKind Kind, MembershipType Type);

/// <summary>A group as one character sees it: its role there and its admins.</summary>
public sealed record GroupView(AccessGroup Group, MembershipType Role, IReadOnlyList<long> Admins);

/// <summary>An entry to add: the member, what kind of entry it is, and its membership type.</summary>
public sealed record NewMember(long MemberId, MemberKind Kind, MembershipType Type);

public sealed record MemberFailure(long MemberId, string Reason);

public sealed record AccessLogEntry(long LogId, long CharacterId, int ActionId, long OwnerId, int? OldType, int? NewType,
    string? OldText, string? NewText, long LoggedMs);

/// <summary>
/// Who hears about a change: the characters, corporations and alliances the group's entries name (and the creator), and everyone when a
/// public entry exists.
/// </summary>
public sealed record Audience(IReadOnlySet<long> Characters, IReadOnlySet<long> Corporations, IReadOnlySet<long> Alliances, bool Everyone);

/// <summary>The change codes of the client's <c>ownergroupConst</c>, as they are recorded for a group (the <c>OnOwnerGroupsUpdated</c> payload).</summary>
public static class AccessGroupChanges
{
    public const int Created = 1;
    public const int MemberAdded = 2;
    public const int MemberTypeChanged = 3;
    public const int MemberRemoved = 4;
    public const int NameChanged = 5;
    public const int DescriptionChanged = 6;
    public const int Deleted = 7;
    public const int ChangedForListener = 8;
}

public sealed record GroupChange(long GroupId, IReadOnlyList<int> Codes, Audience Audience);

public sealed record GroupMutation<T>(T Result, IReadOnlyList<GroupChange> Changes);

/// <summary>A refusal of the rules: <see cref="Code"/> names it; <see cref="Notice"/> is the text the client shows.</summary>
public sealed class AccessGroupError(string code, string? notice = null) : Exception(code)
{
    public string Code { get; } = code;

    public string? Notice { get; } = notice;
}

/// <summary>
/// The rules of owner groups, from the EVE University wiki's access list page: the creator is the admin; the admin edits and deletes the
/// list and manages every entry; a manager adds members and removes or changes only member and blocked entries; corporations, alliances and
/// everyone can be member or blocked only; the most specific entry decides (character, then corporation, then alliance, then everyone).
/// A corporation's built-in list is fixed: its corporation is its admin, and every member of the corporation may use it.
/// </summary>
public static class AccessRules
{
    public const string DefaultName = "New Group";
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 3900;

    public static string Name(string? value, string fallback = DefaultName)
    {
        var text = (value ?? "").Trim();
        if (text.Length > MaxNameLength) text = text[..MaxNameLength];
        return text.Length == 0 ? fallback : text;
    }

    public static string Description(string? value) =>
        value is null ? "" : value.Length > MaxDescriptionLength ? value[..MaxDescriptionLength] : value;

    /// <summary>The membership type the character has in the group: the most specific entry that names it, or none.</summary>
    public static MembershipType RoleOf(AccessGroup group, IReadOnlyList<AccessMember> members, Affiliation who)
    {
        if (group.CorpBuiltIn) return who.CorporationId > 0 && who.CorporationId == group.CreatorId ? MembershipType.Member : MembershipType.None;
        var entry = Find(members, MemberKind.Character, who.CharacterId)
            ?? Find(members, MemberKind.Corporation, who.CorporationId)
            ?? Find(members, MemberKind.Alliance, who.AllianceId)
            ?? Find(members, MemberKind.Everyone, 0);
        return entry?.Type ?? MembershipType.None;
    }

    /// <summary>A member or blocked entry of the group, which the client lists; the admin and manager ones are seen by managers only.</summary>
    public static bool CanSee(MembershipType role, AccessGroup group) =>
        role is MembershipType.Admin or MembershipType.Manager || (group.CorpBuiltIn && role == MembershipType.Member);

    /// <summary>Admins and managers manage a list: its members, its logs, the entries.</summary>
    public static bool CanManage(MembershipType role) => role is MembershipType.Admin or MembershipType.Manager;

    /// <summary>Corporations, alliances and everyone can only be member or blocked; a character can hold any role.</summary>
    public static bool ValidForKind(MemberKind kind, MembershipType type) =>
        kind == MemberKind.Character ? type is MembershipType.Member or MembershipType.Manager or MembershipType.Admin or MembershipType.Excluded
            : type is MembershipType.Member or MembershipType.Excluded;

    /// <summary>An admin adds any entry; a manager adds member or blocked entries.</summary>
    public static bool MayAdd(MembershipType actor, MembershipType type) =>
        actor == MembershipType.Admin || type is MembershipType.Member or MembershipType.Excluded;

    /// <summary>An admin removes any entry; a manager removes member or blocked entries.</summary>
    public static bool MayRemove(MembershipType actor, MembershipType current) =>
        actor == MembershipType.Admin || current is MembershipType.Member or MembershipType.Excluded;

    /// <summary>An admin changes any entry; a manager changes member or blocked entries to member or blocked.</summary>
    public static bool MayChange(MembershipType actor, MembershipType current, MembershipType target) =>
        actor == MembershipType.Admin || (current is MembershipType.Member or MembershipType.Excluded && target is MembershipType.Member or MembershipType.Excluded);

    private static AccessMember? Find(IReadOnlyList<AccessMember> members, MemberKind kind, long id)
    {
        if (kind != MemberKind.Everyone && id <= 0) return null;
        foreach (var member in members)
            if (member.Kind == kind && member.MemberId == id) return member;
        return null;
    }
}
