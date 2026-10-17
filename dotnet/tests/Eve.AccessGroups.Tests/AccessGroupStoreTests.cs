namespace Eve.AccessGroups.Tests;

/// <summary>The access group store on PostgreSQL: the wiki's rules applied to the entries, the change codes and audiences, the logs, a restart.</summary>
public sealed class AccessGroupStoreTests
{
    private const long Alice = 90000001;
    private const long Bob = 90000002;
    private const long Carol = 90000003;
    private const long Dan = 90000004;
    private const long CorpA = 98000001;
    private const long CorpB = 98000002;
    private const long AllianceA = 99000001;

    private static Affiliation Aff(long character, long corp = CorpA, long alliance = AllianceA) => new(character, corp, alliance);

    [PgFact]
    public async Task The_creator_is_the_admin_and_the_creation_is_heard_by_the_creator()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Store.CreateAsync(Aff(Alice), "  Haulers  ", "fleet");

        Assert.Equal("Haulers", created.Result.Name);
        var change = Assert.Single(created.Changes);
        Assert.Equal([AccessGroupChanges.Created], change.Codes);
        Assert.Contains(Alice, change.Audience.Characters);

        var mine = (await rig.Store.VisibleGroupsAsync(Aff(Alice))).Single(view => !view.Group.CorpBuiltIn);
        Assert.Equal(MembershipType.Admin, mine.Role);
        Assert.Equal([Alice], mine.Admins);
        Assert.Null(await rig.Store.VisibleGroupAsync(Aff(Bob, CorpB, 0), mine.Group.GroupId));
    }

    [PgFact]
    public async Task A_manager_adds_members_but_cannot_add_a_manager_or_remove_an_admin()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;
        await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(Bob, MemberKind.Character, MembershipType.Manager)]);

        var added = await rig.Store.AddMembersAsync(Aff(Bob), group,
            [new NewMember(Dan, MemberKind.Character, MembershipType.Member), new NewMember(Carol, MemberKind.Character, MembershipType.Manager)]);
        Assert.Equal(new[] { (Carol, "NotAllowed") }, added.Result.Select(failure => (failure.MemberId, failure.Reason)).ToArray());

        var removed = await rig.Store.DeleteMembersAsync(Aff(Bob), group, [Alice, Dan]);
        Assert.Equal([(Alice, "NotAllowed")], removed.Result.Select(failure => (failure.MemberId, failure.Reason)).ToArray());
        var members = await rig.Store.MembersAsync(Aff(Alice), group);
        Assert.DoesNotContain(members, member => member.MemberId == Dan);
        Assert.Contains(members, member => member.MemberId == Alice && member.Type == MembershipType.Admin);
    }

    [PgFact]
    public async Task A_member_cannot_manage_the_list_nor_see_its_members()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;
        await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(Carol, MemberKind.Character, MembershipType.Member)]);

        var error = await Assert.ThrowsAsync<AccessGroupError>(() =>
            rig.Store.AddMembersAsync(Aff(Carol), group, [new NewMember(Dan, MemberKind.Character, MembershipType.Member)]));
        Assert.Equal("AccessGroupPermission", error.Code);
        Assert.Empty(await rig.Store.MembersAsync(Aff(Carol), group));
        Assert.DoesNotContain((await rig.Store.VisibleGroupsAsync(Aff(Carol))), view => view.Group.GroupId == group);
    }

    [PgFact]
    public async Task A_corporation_entry_gives_membership_and_a_blocked_character_entry_overrides_it()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;
        await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(CorpB, MemberKind.Corporation, MembershipType.Member)]);

        var corpMember = await rig.Store.VisibleGroupAsync(Aff(Dan, CorpB, 0), group);
        Assert.Null(corpMember);
        await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(Dan, MemberKind.Character, MembershipType.Excluded)]);
        Assert.Null(await rig.Store.VisibleGroupAsync(Aff(Dan, CorpB, 0), group));
        await rig.Store.ChangeMembershipTypesAsync(Aff(Alice), group, [Dan], MembershipType.Manager);
        Assert.Equal(MembershipType.Manager, (await rig.Store.VisibleGroupAsync(Aff(Dan, CorpB, 0), group))!.Role);
    }

    [PgFact]
    public async Task A_corporation_or_alliance_entry_cannot_be_an_admin_or_a_manager()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;

        var added = await rig.Store.AddMembersAsync(Aff(Alice), group,
            [new NewMember(CorpB, MemberKind.Corporation, MembershipType.Admin), new NewMember(AllianceA, MemberKind.Alliance, MembershipType.Member)]);

        Assert.Equal([(CorpB, "NotAllowed")], added.Result.Select(failure => (failure.MemberId, failure.Reason)).ToArray());
        var changed = await rig.Store.ChangeMembershipTypesAsync(Aff(Alice), group, [AllianceA], MembershipType.Manager);
        Assert.Equal([(AllianceA, "NotAllowed")], changed.Result.Select(failure => (failure.MemberId, failure.Reason)).ToArray());
    }

    [PgFact]
    public async Task The_corporation_built_in_list_is_fixed_and_usable_by_its_members_only()
    {
        await using var rig = await Rig.CreateAsync();
        var corp = (await rig.Store.VisibleGroupsAsync(Aff(Alice))).Single(view => view.Group.CorpBuiltIn);
        Assert.Equal(CorpA, corp.Group.GroupId);
        Assert.Equal(MembershipType.Member, corp.Role);
        Assert.Equal([CorpA], corp.Admins);
        Assert.Contains((await rig.Store.VisibleGroupsAsync(Aff(Bob))), view => view.Group.GroupId == CorpA);
        Assert.DoesNotContain((await rig.Store.VisibleGroupsAsync(Aff(Dan, CorpB, 0))), view => view.Group.GroupId == CorpA);

        var fixedError = await Assert.ThrowsAsync<AccessGroupError>(() => rig.Store.RenameAsync(Aff(Alice), CorpA, "New"));
        Assert.Equal("AccessGroupFixed", fixedError.Code);
        await Assert.ThrowsAsync<AccessGroupError>(() => rig.Store.AddMembersAsync(Aff(Alice), CorpA, [new NewMember(Dan, MemberKind.Character, MembershipType.Member)]));
        await Assert.ThrowsAsync<AccessGroupError>(() => rig.Store.DeleteAsync(Aff(Alice), CorpA));
    }

    [PgFact]
    public async Task Only_the_admin_renames_redescribes_and_deletes_and_a_deleted_list_is_gone()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;
        await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(Bob, MemberKind.Character, MembershipType.Manager)]);

        var denied = await Assert.ThrowsAsync<AccessGroupError>(() => rig.Store.RenameAsync(Aff(Bob), group, "Bob's"));
        Assert.Equal("AccessGroupPermission", denied.Code);
        var renamed = await rig.Store.RenameAsync(Aff(Alice), group, "Renamed");
        Assert.Equal("Renamed", renamed.Result.Name);
        Assert.Equal("Renamed", (await rig.Store.RenameAsync(Aff(Alice), group, "")).Result.Name);
        Assert.Equal(new string('d', 3900), (await rig.Store.DescribeAsync(Aff(Alice), group, new string('d', 4000))).Result.Description);

        var deleted = await rig.Store.DeleteAsync(Aff(Alice), group);
        Assert.Equal([AccessGroupChanges.Deleted], deleted.Changes.Single().Codes);
        Assert.Null(await rig.Store.VisibleGroupAsync(Aff(Alice), group));
        var gone = await Assert.ThrowsAsync<AccessGroupError>(() => rig.Store.RenameAsync(Aff(Alice), group, "x"));
        Assert.Equal("AccessGroupNotFound", gone.Code);
    }

    [PgFact]
    public async Task The_logs_keep_each_change_in_order_with_its_codes()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;
        await rig.Store.RenameAsync(Aff(Alice), group, "H");
        await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(Bob, MemberKind.Character, MembershipType.Member)]);
        await rig.Store.ChangeMembershipTypesAsync(Aff(Alice), group, [Bob], MembershipType.Manager);
        await rig.Store.DeleteMembersAsync(Aff(Alice), group, [Bob]);

        var logs = await rig.Store.LogsAsync(Aff(Alice), group);

        Assert.Equal([AccessGroupChanges.Created, AccessGroupChanges.NameChanged, AccessGroupChanges.MemberAdded,
            AccessGroupChanges.MemberTypeChanged, AccessGroupChanges.MemberRemoved], logs.Select(log => log.ActionId).ToArray());
        Assert.Equal(("G", "H"), (logs[1].OldText, logs[1].NewText));
        Assert.Null(logs[2].OldType);
        Assert.Equal<int?>(0, logs[2].NewType);
        Assert.Equal(((int?)0, (int?)1), (logs[3].OldType, logs[3].NewType));
        Assert.Equal(((int?)1, (int?)-1), (logs[4].OldType, logs[4].NewType));
        await Assert.ThrowsAsync<AccessGroupError>(() => rig.Store.LogsAsync(Aff(Carol), group));
    }

    [PgFact]
    public async Task An_added_member_hears_the_change_and_an_everyone_entry_reaches_everyone()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;

        var added = await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(Bob, MemberKind.Character, MembershipType.Member)]);
        var change = Assert.Single(added.Changes);
        Assert.Equal([AccessGroupChanges.MemberAdded, AccessGroupChanges.ChangedForListener], change.Codes);
        Assert.Contains(Bob, change.Audience.Characters);
        Assert.False(change.Audience.Everyone);

        var everyone = await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(0, MemberKind.Everyone, MembershipType.Member)]);
        Assert.True(Assert.Single(everyone.Changes).Audience.Everyone);
    }

    [PgFact]
    public async Task A_refused_or_unchanged_request_changes_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;
        await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(Bob, MemberKind.Character, MembershipType.Member)]);

        var same = await rig.Store.ChangeMembershipTypesAsync(Aff(Alice), group, [Bob], MembershipType.Member);
        Assert.Empty(same.Changes);
        Assert.Empty(same.Result);
        var missing = await rig.Store.DeleteMembersAsync(Aff(Alice), group, [Carol]);
        Assert.Equal([(Carol, "NotAMember")], missing.Result.Select(failure => (failure.MemberId, failure.Reason)).ToArray());
        Assert.Empty(missing.Changes);
    }

    [PgFact]
    public async Task The_groups_and_their_entries_survive_a_restart()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "Durable", "d")).Result.GroupId;
        await rig.Store.AddMembersAsync(Aff(Alice), group, [new NewMember(Bob, MemberKind.Character, MembershipType.Manager)]);

        var restarted = rig.Reopen();

        var view = (await restarted.VisibleGroupAsync(Aff(Bob), group))!;
        Assert.Equal("Durable", view.Group.Name);
        Assert.Equal(MembershipType.Manager, view.Role);
        Assert.Equal([Alice], view.Admins);
    }

    [PgFact]
    public async Task Concurrent_adds_by_one_admin_all_land_once()
    {
        await using var rig = await Rig.CreateAsync();
        var group = (await rig.Store.CreateAsync(Aff(Alice), "G", "")).Result.GroupId;
        var ids = Enumerable.Range(0, 12).Select(i => 91000000L + i).ToArray();

        var results = await Task.WhenAll(ids.Select(id => rig.Store.AddMembersAsync(Aff(Alice), group,
            [new NewMember(id, MemberKind.Character, MembershipType.Member)])));

        Assert.All(results, result => Assert.Empty(result.Result));
        var members = await rig.Store.MembersAsync(Aff(Alice), group);
        Assert.Equal(ids.Length + 1, members.Count);
        Assert.Equal(ids.Length + 1, members.Select(member => member.MemberId).Distinct().Count());
    }
}
