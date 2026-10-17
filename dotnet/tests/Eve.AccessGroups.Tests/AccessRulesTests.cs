namespace Eve.AccessGroups.Tests;

/// <summary>The rules of owner groups as the EVE University wiki's access list page states them, without a database.</summary>
public sealed class AccessRulesTests
{
    private const long Character = 90000001;
    private const long Corporation = 98000001;
    private const long Alliance = 99000001;
    private static readonly Affiliation Who = new(Character, Corporation, Alliance);

    [Fact]
    public void The_character_entry_decides_before_the_corporation_alliance_and_everyone_entries()
    {
        var group = Group();
        var everyone = new AccessMember(0, MemberKind.Everyone, MembershipType.Member);
        var alliance = new AccessMember(Alliance, MemberKind.Alliance, MembershipType.Excluded);
        var corporation = new AccessMember(Corporation, MemberKind.Corporation, MembershipType.Member);
        var character = new AccessMember(Character, MemberKind.Character, MembershipType.Manager);

        Assert.Equal(MembershipType.Member, AccessRules.RoleOf(group, [everyone, alliance, corporation], Who));
        Assert.Equal(MembershipType.Excluded, AccessRules.RoleOf(group, [everyone, alliance], Who));
        Assert.Equal(MembershipType.Manager, AccessRules.RoleOf(group, [everyone, alliance, corporation, character], Who));
        Assert.Equal(MembershipType.Excluded, AccessRules.RoleOf(group, [everyone, new AccessMember(Character, MemberKind.Character, MembershipType.Excluded)], Who));
        Assert.Equal(MembershipType.None, AccessRules.RoleOf(group, [], Who));
    }

    [Fact]
    public void A_corporation_or_alliance_or_everyone_entry_is_member_or_blocked_only()
    {
        Assert.True(AccessRules.ValidForKind(MemberKind.Corporation, MembershipType.Member));
        Assert.True(AccessRules.ValidForKind(MemberKind.Alliance, MembershipType.Excluded));
        Assert.False(AccessRules.ValidForKind(MemberKind.Corporation, MembershipType.Admin));
        Assert.False(AccessRules.ValidForKind(MemberKind.Alliance, MembershipType.Manager));
        Assert.False(AccessRules.ValidForKind(MemberKind.Everyone, MembershipType.Admin));
        Assert.True(AccessRules.ValidForKind(MemberKind.Character, MembershipType.Admin));
    }

    [Fact]
    public void A_manager_adds_removes_and_changes_only_member_or_blocked_entries()
    {
        Assert.True(AccessRules.MayAdd(MembershipType.Manager, MembershipType.Member));
        Assert.False(AccessRules.MayAdd(MembershipType.Manager, MembershipType.Manager));
        Assert.True(AccessRules.MayRemove(MembershipType.Manager, MembershipType.Excluded));
        Assert.False(AccessRules.MayRemove(MembershipType.Manager, MembershipType.Admin));
        Assert.True(AccessRules.MayChange(MembershipType.Manager, MembershipType.Member, MembershipType.Excluded));
        Assert.False(AccessRules.MayChange(MembershipType.Manager, MembershipType.Member, MembershipType.Manager));
        Assert.True(AccessRules.MayChange(MembershipType.Admin, MembershipType.Admin, MembershipType.Member));
    }

    [Fact]
    public void Only_an_admin_or_a_manager_manages_and_a_member_sees_nothing_but_a_corporation_list()
    {
        var plain = Group();
        Assert.False(AccessRules.CanSee(MembershipType.Member, plain));
        Assert.True(AccessRules.CanSee(MembershipType.Manager, plain));
        Assert.False(AccessRules.CanManage(MembershipType.Member));
        var builtIn = Group(corpBuiltIn: true, creator: Corporation);
        Assert.True(AccessRules.CanSee(MembershipType.Member, builtIn));
        Assert.Equal(MembershipType.Member, AccessRules.RoleOf(builtIn, [], Who));
        Assert.Equal(MembershipType.None, AccessRules.RoleOf(builtIn, [], new Affiliation(Character, 98000002, 0)));
    }

    [Fact]
    public void Names_and_descriptions_keep_the_limits_and_an_empty_name_falls_back()
    {
        Assert.Equal("New Group", AccessRules.Name("   "));
        Assert.Equal("Keep", AccessRules.Name("   ", "Keep"));
        Assert.Equal(100, AccessRules.Name(new string('x', 150)).Length);
        Assert.Equal(3900, AccessRules.Description(new string('d', 4000)).Length);
        Assert.Equal("", AccessRules.Description(null));
    }

    private static AccessGroup Group(bool corpBuiltIn = false, long creator = Character) =>
        new(2000000001, creator, "Group", "", corpBuiltIn, 0);
}
