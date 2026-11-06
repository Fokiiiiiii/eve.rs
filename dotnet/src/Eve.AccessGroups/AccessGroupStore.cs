using System.Text.RegularExpressions;
using Eve.Data;
using Npgsql;

namespace Eve.AccessGroups;

public sealed partial class AccessGroupStore
{
    public const string DefaultSchema = "access_groups";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly Func<long> _nowMs;

    public AccessGroupStore(NpgsqlDataSource source, string schema, Func<long> nowMs)
    {
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _schema = schema;
        _nowMs = nowMs;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(AccessGroupStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>The groups the character can see: groups they manage and their corporation's built-in group. The built-in group is created on first access.</summary>
    public Task<IReadOnlyList<GroupView>> VisibleGroupsAsync(Affiliation who, CancellationToken token = default) =>
        InTransactionAsync(0, async (connection, tx) =>
        {
            await EnsureCorporationGroupAsync(connection, tx, who.CorporationId, token);
            var visible = new List<GroupView>();
            foreach (var (group, members) in await TouchingWithMembersAsync(connection, tx, who, token))
            {
                var role = AccessRules.RoleOf(group, members, who);
                if (AccessRules.CanSee(role, group)) visible.Add(new GroupView(group, role, AdminsOf(group, members)));
            }
            return (IReadOnlyList<GroupView>)visible;
        }, token);

    /// <summary>
    /// The lists the character holds a member, manager or admin role in (the role by list id). A blocked role and no role are left out. A
    /// corporation's built-in list counts for its members.
    /// </summary>
    public Task<IReadOnlyDictionary<long, int>> MemberRolesAsync(Affiliation who, CancellationToken token = default) =>
        InTransactionAsync(0, async (connection, tx) =>
        {
            await EnsureCorporationGroupAsync(connection, tx, who.CorporationId, token);
            var roles = new Dictionary<long, int>();
            foreach (var (group, members) in await TouchingWithMembersAsync(connection, tx, who, token))
            {
                var role = AccessRules.RoleOf(group, members, who);
                if (role is MembershipType.Member or MembershipType.Manager or MembershipType.Admin) roles[group.GroupId] = (int)role;
            }
            return (IReadOnlyDictionary<long, int>)roles;
        }, token);

    /// <summary>One group the character can see, or null when it does not exist or is not visible to the character.</summary>
    public Task<GroupView?> VisibleGroupAsync(Affiliation who, long groupId, CancellationToken token = default) =>
        InTransactionAsync(0, async (connection, tx) =>
        {
            await EnsureCorporationGroupAsync(connection, tx, who.CorporationId, token);
            var group = await GroupAsync(connection, tx, groupId, token);
            if (group is null) return null;
            var members = await MembersOfAsync(connection, tx, groupId, token);
            var role = AccessRules.RoleOf(group, members, who);
            return AccessRules.CanSee(role, group) ? new GroupView(group, role, AdminsOf(group, members)) : null;
        }, token);

    /// <summary>The group summary available to a character with an effective member, manager, or admin role.</summary>
    public Task<GroupView?> GroupSummaryAsync(Affiliation who, long groupId, CancellationToken token = default) =>
        InTransactionAsync(0, async (connection, tx) =>
        {
            var group = await GroupAsync(connection, tx, groupId, token);
            if (group is null) return null;
            var members = await SummaryMembersOfAsync(connection, tx, groupId, who, token);
            var role = AccessRules.RoleOf(group, members, who);
            return AccessRules.CanReadSummary(role) ? new GroupView(group, role, AdminsOf(group, members)) : null;
        }, token);

    public Task<IReadOnlyList<AccessMember>> MembersAsync(Affiliation who, long groupId, CancellationToken token = default) =>
        InTransactionAsync(0, async (connection, tx) =>
        {
            var group = await GroupAsync(connection, tx, groupId, token);
            if (group is null) return (IReadOnlyList<AccessMember>)[];
            var members = await MembersOfAsync(connection, tx, groupId, token);
            return AccessRules.CanManage(AccessRules.RoleOf(group, members, who)) ? members : (IReadOnlyList<AccessMember>)[];
        }, token);

    /// <summary>The change log of a group, for a character that manages it (oldest first).</summary>
    public Task<IReadOnlyList<AccessLogEntry>> LogsAsync(Affiliation who, long groupId, CancellationToken token = default) =>
        InTransactionAsync(0, async (connection, tx) =>
        {
            var (group, members) = await LoadAsync(connection, tx, groupId, token);
            if (!AccessRules.CanManage(AccessRules.RoleOf(group, members, who))) throw Denied();
            await using var command = Command(connection, tx,
                $"SELECT log_id, character_id, action_id, owner_id, old_type, new_type, old_text, new_text, logged_ms FROM {_schema}.logs WHERE group_id=$1 ORDER BY log_id",
                groupId);
            await using var reader = await command.ExecuteReaderAsync(token);
            var logs = new List<AccessLogEntry>();
            while (await reader.ReadAsync(token))
                logs.Add(new AccessLogEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt16(2), reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt16(4), reader.IsDBNull(5) ? null : reader.GetInt16(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8)));
            return (IReadOnlyList<AccessLogEntry>)logs;
        }, token);

    /// <summary>A new group: the creator is its admin. The change is the creation, heard by the creator.</summary>
    public Task<GroupMutation<AccessGroup>> CreateAsync(Affiliation who, string? name, string? description, CancellationToken token = default) =>
        InTransactionAsync(0, async (connection, tx) =>
        {
            var groupId = await NextIdAsync(connection, tx, token);
            var group = new AccessGroup(groupId, who.CharacterId, AccessRules.Name(name), AccessRules.Description(description), false, _nowMs());
            await Execute(connection, tx,
                $"INSERT INTO {_schema}.groups(group_id,creator_id,group_name,description,corp_built_in,created_ms) VALUES($1,$2,$3,$4,false,$5)",
                token, groupId, who.CharacterId, group.Name, group.Description, group.CreatedMs);
            await InsertMemberAsync(connection, tx, groupId, who.CharacterId, MemberKind.Character, MembershipType.Admin, token);
            await LogAsync(connection, tx, groupId, who.CharacterId, AccessGroupChanges.Created, 0, null, null, null, token);
            var audience = AudienceOf(group, [new AccessMember(who.CharacterId, MemberKind.Character, MembershipType.Admin)], [who.CharacterId]);
            return new GroupMutation<AccessGroup>(group, [new GroupChange(groupId, [AccessGroupChanges.Created], audience)]);
        }, token);

    /// <summary>Renames the group (an empty name keeps the old one). Only the admin, and never the corporation's built-in list.</summary>
    public Task<GroupMutation<AccessGroup>> RenameAsync(Affiliation who, long groupId, string? name, CancellationToken token = default) =>
        InTransactionAsync(groupId, async (connection, tx) =>
        {
            var (group, members) = await RequireAdminAsync(connection, tx, who, groupId, token);
            var old = group.Name;
            var updated = group with { Name = AccessRules.Name(name, group.Name) };
            await Execute(connection, tx, $"UPDATE {_schema}.groups SET group_name=$2 WHERE group_id=$1", token, groupId, updated.Name);
            await LogAsync(connection, tx, groupId, who.CharacterId, AccessGroupChanges.NameChanged, 0, null, null, (old, updated.Name), token);
            return new GroupMutation<AccessGroup>(updated, [new GroupChange(groupId, [AccessGroupChanges.NameChanged], AudienceOf(group, members, [who.CharacterId]))]);
        }, token);

    /// <summary>Sets the description. Only the admin, and never the corporation's built-in list.</summary>
    public Task<GroupMutation<AccessGroup>> DescribeAsync(Affiliation who, long groupId, string? description, CancellationToken token = default) =>
        InTransactionAsync(groupId, async (connection, tx) =>
        {
            var (group, members) = await RequireAdminAsync(connection, tx, who, groupId, token);
            var updated = group with { Description = AccessRules.Description(description) };
            await Execute(connection, tx, $"UPDATE {_schema}.groups SET description=$2 WHERE group_id=$1", token, groupId, updated.Description);
            await LogAsync(connection, tx, groupId, who.CharacterId, AccessGroupChanges.DescriptionChanged, 0, null, null, (group.Description, updated.Description), token);
            return new GroupMutation<AccessGroup>(updated, [new GroupChange(groupId, [AccessGroupChanges.DescriptionChanged], AudienceOf(group, members, [who.CharacterId]))]);
        }, token);

    /// <summary>Deletes the group and its entries. Only the admin, and never the corporation's built-in list.</summary>
    public Task<GroupMutation<AccessGroup>> DeleteAsync(Affiliation who, long groupId, CancellationToken token = default) =>
        InTransactionAsync(groupId, async (connection, tx) =>
        {
            var (group, members) = await RequireAdminAsync(connection, tx, who, groupId, token);
            var audience = AudienceOf(group, members, [who.CharacterId]);
            await LogAsync(connection, tx, groupId, who.CharacterId, AccessGroupChanges.Deleted, 0, null, null, null, token);
            await Execute(connection, tx, $"DELETE FROM {_schema}.groups WHERE group_id=$1", token, groupId);
            return new GroupMutation<AccessGroup>(group, [new GroupChange(groupId, [AccessGroupChanges.Deleted], audience)]);
        }, token);

    /// <summary>
    /// Adds entries. A manager adds member or blocked entries only; corporations and alliances are member or blocked, and Everyone is member only.
    /// Answers the entries that were refused, with the reason, and the change the rest made.
    /// </summary>
    public Task<GroupMutation<IReadOnlyList<MemberFailure>>> AddMembersAsync(Affiliation who, long groupId, IReadOnlyList<NewMember> entries,
        CancellationToken token = default) =>
        InTransactionAsync(groupId, async (connection, tx) =>
        {
            var (group, members, role) = await RequireManagerAsync(connection, tx, who, groupId, token);
            var before = members.ToList();
            var memberIds = members.Select(member => member.MemberId).ToHashSet();
            var failures = new List<MemberFailure>();
            foreach (var entry in entries.DistinctBy(entry => entry.MemberId))
            {
                if (memberIds.Contains(entry.MemberId)) { failures.Add(new(entry.MemberId, "AlreadyMember")); continue; }
                if (!AccessRules.ValidForKind(entry.Kind, entry.Type) || !AccessRules.MayAdd(role, entry.Type)) { failures.Add(new(entry.MemberId, "NotAllowed")); continue; }
                memberIds.Add(entry.MemberId);
                await InsertMemberAsync(connection, tx, groupId, entry.MemberId, entry.Kind, entry.Type, token);
                await LogAsync(connection, tx, groupId, who.CharacterId, AccessGroupChanges.MemberAdded, entry.MemberId, null, (int)entry.Type, null, token);
                members.Add(new AccessMember(entry.MemberId, entry.Kind, entry.Type));
            }
            return Changed(groupId, group, before, members, [who.CharacterId], AccessGroupChanges.MemberAdded, failures);
        }, token);

    /// <summary>Removes entries. A manager removes member or blocked entries and its own manager entry. Answers refusals by id and reason.</summary>
    public Task<GroupMutation<IReadOnlyList<MemberFailure>>> DeleteMembersAsync(Affiliation who, long groupId, IReadOnlyList<long> memberIds,
        CancellationToken token = default) =>
        InTransactionAsync(groupId, async (connection, tx) =>
        {
            var (group, members, role) = await RequireManagerAsync(connection, tx, who, groupId, token);
            var before = members.ToList();
            var membersById = members.ToDictionary(member => member.MemberId);
            var removedIds = new HashSet<long>();
            var failures = new List<MemberFailure>();
            foreach (var memberId in memberIds.Distinct())
            {
                if (!membersById.TryGetValue(memberId, out var current)) { failures.Add(new(memberId, "NotAMember")); continue; }
                if (!AccessRules.MayRemove(role, current.Type, who.CharacterId == memberId)) { failures.Add(new(memberId, "NotAllowed")); continue; }
                await Execute(connection, tx, $"DELETE FROM {_schema}.members WHERE group_id=$1 AND member_id=$2", token, groupId, memberId);
                await LogAsync(connection, tx, groupId, who.CharacterId, AccessGroupChanges.MemberRemoved, memberId, (int)current.Type, -1, null, token);
                membersById.Remove(memberId);
                removedIds.Add(memberId);
            }
            if (removedIds.Count > 0) members.RemoveAll(member => removedIds.Contains(member.MemberId));
            return Changed(groupId, group, before, members, [who.CharacterId], AccessGroupChanges.MemberRemoved, failures);
        }, token);

    /// <summary>
    /// Changes the membership type of entries. An admin sets any type; a manager sets member or blocked on member or blocked entries and
    /// may demote its own manager entry.
    /// Entries already of that type are left alone.
    /// </summary>
    public Task<GroupMutation<IReadOnlyList<MemberFailure>>> ChangeMembershipTypesAsync(Affiliation who, long groupId, IReadOnlyList<long> memberIds,
        MembershipType type, CancellationToken token = default) =>
        InTransactionAsync(groupId, async (connection, tx) =>
        {
            var (group, members, role) = await RequireManagerAsync(connection, tx, who, groupId, token);
            var before = members.ToList();
            var membersById = members.ToDictionary(member => member.MemberId);
            var failures = new List<MemberFailure>();
            var changed = false;
            foreach (var memberId in memberIds.Distinct())
            {
                if (!membersById.TryGetValue(memberId, out var current)) { failures.Add(new(memberId, "NotAMember")); continue; }
                if (current.Type == type) continue;
                if (!AccessRules.ValidForKind(current.Kind, type) || !AccessRules.MayChange(role, current.Type, type, who.CharacterId == memberId))
                {
                    failures.Add(new(memberId, "NotAllowed"));
                    continue;
                }
                await Execute(connection, tx, $"UPDATE {_schema}.members SET membership_type=$3 WHERE group_id=$1 AND member_id=$2", token, groupId, memberId, (int)type);
                await LogAsync(connection, tx, groupId, who.CharacterId, AccessGroupChanges.MemberTypeChanged, memberId, (int)current.Type, (int)type, null, token);
                membersById[memberId] = current with { Type = type };
                changed = true;
            }
            if (changed)
                for (var index = 0; index < members.Count; index++) members[index] = membersById[members[index].MemberId];
            return Changed(groupId, group, before, members, [who.CharacterId], AccessGroupChanges.MemberTypeChanged, failures);
        }, token);

    /// <summary>The corporation's built-in group uses the corporation ID and is created on the first group lookup for that corporation.</summary>
    private async Task EnsureCorporationGroupAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long corporationId, CancellationToken token)
    {
        if (corporationId <= 0) return;
        await Execute(connection, tx,
            $"INSERT INTO {_schema}.groups(group_id,creator_id,group_name,description,corp_built_in,created_ms) VALUES($1,$1,'','',true,$2) ON CONFLICT (group_id) DO NOTHING",
            token, corporationId, _nowMs());
    }

    private GroupMutation<IReadOnlyList<MemberFailure>> Changed(long groupId, AccessGroup group, List<AccessMember> before, List<AccessMember> after,
        IReadOnlyList<long> actors, int code, List<MemberFailure> failures)
    {
        var changes = new List<GroupChange>();
        if (!before.SequenceEqual(after))
            changes.Add(new GroupChange(groupId, [code, AccessGroupChanges.ChangedForListener],
                AudienceOf(group, [.. before, .. after], actors)));
        return new GroupMutation<IReadOnlyList<MemberFailure>>(failures, changes);
    }

    /// <summary>The admins a client lists: the admin characters, or the corporation for its built-in list.</summary>
    private static IReadOnlyList<long> AdminsOf(AccessGroup group, IEnumerable<AccessMember> members) =>
        group.CorpBuiltIn ? [group.CreatorId] : members.Where(member => member.Kind == MemberKind.Character && member.Type == MembershipType.Admin)
            .Select(member => member.MemberId).ToArray();

    private static Audience AudienceOf(AccessGroup group, IEnumerable<AccessMember> members, IEnumerable<long> characters)
    {
        var all = members.ToList();
        var corporations = all.Where(member => member.Kind == MemberKind.Corporation).Select(member => member.MemberId).ToHashSet();
        if (group.CorpBuiltIn) corporations.Add(group.CreatorId);
        return new Audience(
            all.Where(member => member.Kind == MemberKind.Character).Select(member => member.MemberId).Concat(characters).ToHashSet(),
            corporations,
            all.Where(member => member.Kind == MemberKind.Alliance).Select(member => member.MemberId).ToHashSet(),
            all.Any(member => member.Kind == MemberKind.Everyone));
    }

    private async Task<(AccessGroup Group, List<AccessMember> Members)> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long groupId, CancellationToken token)
    {
        var group = await GroupAsync(connection, tx, groupId, token) ?? throw new AccessGroupError("AccessGroupNotFound", "This access group no longer exists.");
        return (group, await MembersOfAsync(connection, tx, groupId, token));
    }

    private static AccessGroupError Denied() => new("AccessGroupPermission", "You do not have permission to manage this access group.");

    private static AccessGroupError Fixed() => new("AccessGroupFixed", "This access group cannot be changed.");

    private async Task<(AccessGroup Group, List<AccessMember> Members, MembershipType Role)> RequireManagerAsync(NpgsqlConnection connection,
        NpgsqlTransaction tx, Affiliation who, long groupId, CancellationToken token)
    {
        var (group, members) = await LoadAsync(connection, tx, groupId, token);
        if (group.CorpBuiltIn) throw Fixed();
        var role = AccessRules.RoleOf(group, members, who);
        if (!AccessRules.CanManage(role)) throw Denied();
        return (group, members, role);
    }

    private async Task<(AccessGroup Group, List<AccessMember> Members)> RequireAdminAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Affiliation who, long groupId, CancellationToken token)
    {
        var (group, members) = await LoadAsync(connection, tx, groupId, token);
        if (group.CorpBuiltIn) throw Fixed();
        if (AccessRules.RoleOf(group, members, who) != MembershipType.Admin) throw Denied();
        return (group, members);
    }

    /// <summary>The groups an entry of the character, its corporation, its alliance or everyone names, or the corporation's built-in list.</summary>
    private async Task<IReadOnlyList<(AccessGroup Group, List<AccessMember> Members)>> TouchingWithMembersAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Affiliation who, CancellationToken token)
    {
        await using var command = Command(connection, tx,
            $"SELECT g.group_id, g.creator_id, g.group_name, g.description, g.corp_built_in, g.created_ms, m.member_id, m.member_kind, m.membership_type " +
            $"FROM {_schema}.groups g LEFT JOIN {_schema}.members m ON m.group_id=g.group_id " +
            $"WHERE (g.corp_built_in AND $2 > 0 AND g.group_id=$2) OR EXISTS (SELECT 1 FROM {_schema}.members m WHERE m.group_id=g.group_id AND " +
            "((m.member_kind=1 AND m.member_id=$1) OR (m.member_kind=2 AND $2 > 0 AND m.member_id=$2) OR (m.member_kind=3 AND $3 > 0 AND m.member_id=$3) OR m.member_kind=0)) " +
            "ORDER BY g.group_id, m.member_id",
            who.CharacterId, who.CorporationId, who.AllianceId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var groups = new List<(AccessGroup Group, List<AccessMember> Members)>();
        while (await reader.ReadAsync(token))
        {
            var groupId = reader.GetInt64(0);
            if (groups.Count == 0 || groups[^1].Group.GroupId != groupId)
                groups.Add((new AccessGroup(groupId, reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5)), []));
            if (!reader.IsDBNull(6))
                groups[^1].Members.Add(new AccessMember(reader.GetInt64(6), (MemberKind)reader.GetInt16(7), (MembershipType)reader.GetInt16(8)));
        }
        return groups;
    }

    private async Task<AccessGroup?> GroupAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long groupId, CancellationToken token)
    {
        await using var command = Command(connection, tx,
            $"SELECT group_id, creator_id, group_name, description, corp_built_in, created_ms FROM {_schema}.groups WHERE group_id=$1", groupId);
        return (await ReadGroupsAsync(command, token)).FirstOrDefault();
    }

    private async Task<List<AccessMember>> MembersOfAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long groupId, CancellationToken token)
    {
        await using var command = Command(connection, tx,
            $"SELECT member_id, member_kind, membership_type FROM {_schema}.members WHERE group_id=$1 ORDER BY member_id", groupId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var members = new List<AccessMember>();
        while (await reader.ReadAsync(token))
            members.Add(new AccessMember(reader.GetInt64(0), (MemberKind)reader.GetInt16(1), (MembershipType)reader.GetInt16(2)));
        return members;
    }

    private async Task<List<AccessMember>> SummaryMembersOfAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long groupId,
        Affiliation who, CancellationToken token)
    {
        // RoleOf needs only this reader's effective entries; the client summary also lists admins, not other member rows.
        await using var command = Command(connection, tx,
            $"SELECT member_id, member_kind, membership_type FROM {_schema}.members WHERE group_id=$1 AND " +
            "((member_kind=$2 AND member_id=$3) OR (member_kind=$4 AND $5>0 AND member_id=$5) OR " +
            "(member_kind=$6 AND $7>0 AND member_id=$7) OR (member_kind=$8 AND member_id=0) OR " +
            "(member_kind=$9 AND membership_type=$10)) ORDER BY member_id",
            groupId, (short)MemberKind.Character, who.CharacterId, (short)MemberKind.Corporation, who.CorporationId,
            (short)MemberKind.Alliance, who.AllianceId, (short)MemberKind.Everyone, (short)MemberKind.Character, (short)MembershipType.Admin);
        await using var reader = await command.ExecuteReaderAsync(token);
        var members = new List<AccessMember>();
        while (await reader.ReadAsync(token))
            members.Add(new AccessMember(reader.GetInt64(0), (MemberKind)reader.GetInt16(1), (MembershipType)reader.GetInt16(2)));
        return members;
    }

    private async Task InsertMemberAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long groupId, long memberId, MemberKind kind,
        MembershipType type, CancellationToken token) =>
        await Execute(connection, tx, $"INSERT INTO {_schema}.members(group_id,member_id,member_kind,membership_type) VALUES($1,$2,$3,$4)",
            token, groupId, memberId, (short)kind, (short)type);

    private async Task LogAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long groupId, long characterId, int action, long ownerId,
        int? oldType, int? newType, (string Old, string New)? text, CancellationToken token)
    {
        await Execute(connection, tx,
            $"INSERT INTO {_schema}.logs(group_id,character_id,action_id,owner_id,old_type,new_type,old_text,new_text,logged_ms) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)",
            token, groupId, characterId, action, ownerId, oldType, newType, text?.Old, text?.New, _nowMs());
    }

    private async Task<long> NextIdAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken token)
    {
        await using var command = Command(connection, tx, $"SELECT nextval('{_schema}.access_group_ids')");
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    /// <summary>Serializes each group's mutations before permission checks so concurrent edits use the latest membership.</summary>
    private async Task<T> InTransactionAsync<T>(long lockGroupId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action, CancellationToken token)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        if (lockGroupId != 0)
            await Execute(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", token, $"accessgroups:{_schema}:{lockGroupId}");
        var result = await action(connection, transaction);
        await transaction.CommitAsync(token);
        return result;
    }

    private static async Task<IReadOnlyList<AccessGroup>> ReadGroupsAsync(NpgsqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        var groups = new List<AccessGroup>();
        while (await reader.ReadAsync(token))
            groups.Add(new AccessGroup(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5)));
        return groups;
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
}
