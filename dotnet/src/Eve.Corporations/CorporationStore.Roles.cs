// Role and title updates ported from EVE.js corpRegistryRuntime.js / corporationRuntimeState.js (828823a).
using System.Text.Json;
using Npgsql;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    private const string TitleColumns = "id,name,roles,grantable_roles,roles_hq,grantable_hq,roles_base,grantable_base,roles_other,grantable_other";
    private static long[] Masks(RoleMasks m) => [m.Roles,m.GrantableRoles,m.AtHq,m.GrantableHq,m.AtBase,m.GrantableBase,m.AtOther,m.GrantableOther];
    private static readonly long[] AllowedMasks = [CorporationRoles.Administrative,CorporationRoles.Grantable,
        CorporationRoles.Locational,CorporationRoles.Locational,CorporationRoles.Locational,CorporationRoles.Locational,
        CorporationRoles.Locational,CorporationRoles.Locational];

    public async Task<IReadOnlyList<CorporationTitle>> TitlesAsync(long corporation,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        return await TitlesAsync(connection,null,corporation,token);
    }
    private async Task<IReadOnlyList<CorporationTitle>> TitlesAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long corporation,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"SELECT {TitleColumns} FROM {_schema}.titles WHERE corporation_id=$1 ORDER BY id",corporation);
        var titles = new List<CorporationTitle>();
        await using var r = await command.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token))
            titles.Add(new CorporationTitle(r.GetInt32(0),r.GetString(1),new RoleMasks(r.GetInt64(2),r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),r.GetInt64(8),r.GetInt64(9))));
        return titles;
    }
    public Task UpdateTitleAsync(long actor,long corporation,CorporationTitle title,CancellationToken token = default) =>
        UpdateTitlesAsync(actor,corporation,[title],token);

    public Task UpdateTitlesAsync(long actor,long corporation,IReadOnlyList<CorporationTitle> titles,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await RequireRoleAsync(connection,tx,corporation,actor,CorporationRoles.Director,token);
            if (titles.Select(t => t.Id).Distinct().Count() != titles.Count) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            foreach (var title in titles)
            {
                if (title.Id is < 1 or > 32768 || (title.Id & (title.Id-1)) != 0) throw new CorporationError("INVALID_TITLE");
                var masks = Masks(title.Masks);
                for (var i = 0; i < masks.Length; i++)
                    if (!CorporationRoles.IsKnown(masks[i],i == 0 ? CorporationRoles.Grantable : AllowedMasks[i])) throw new CorporationError("CORPORATION_ACCESS_DENIED");
                var name = title.Name.Length > 100 ? title.Name[..100] : title.Name;
                await using var update = Command(connection,tx,$"""
                    INSERT INTO {_schema}.titles(corporation_id,id,name,roles,grantable_roles,roles_hq,grantable_hq,
                        roles_base,grantable_base,roles_other,grantable_other)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                    ON CONFLICT(corporation_id,id) DO UPDATE SET name=$3,roles=$4,grantable_roles=$5,roles_hq=$6,
                        grantable_hq=$7,roles_base=$8,grantable_base=$9,roles_other=$10,grantable_other=$11
                    """,corporation,title.Id,name,masks[0],masks[1],masks[2],masks[3],masks[4],masks[5],masks[6],masks[7]);
                await update.ExecuteNonQueryAsync(token);
            }
            return 0;
        },token);

    public Task DeleteTitleAsync(long actor,long corporation,int titleId,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await RequireRoleAsync(connection,tx,corporation,actor,CorporationRoles.Director,token);
            if (titleId is < 1 or > 32768 || (titleId & (titleId-1)) != 0) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            await using (var clear = Command(connection,tx,$"""
                INSERT INTO {_schema}.titles(corporation_id,id,name)
                VALUES($1,$2,'')
                ON CONFLICT(corporation_id,id) DO UPDATE SET name='',roles=0,grantable_roles=0,
                    roles_hq=0,grantable_hq=0,roles_base=0,grantable_base=0,roles_other=0,grantable_other=0
                """,corporation,titleId))
                await clear.ExecuteNonQueryAsync(token);
            await using (var members = Command(connection,tx,$"""
                UPDATE {_schema}.members
                SET title_mask=title_mask & ~$2,
                    role_stasis_ms=GREATEST(COALESCE(role_stasis_ms,0),$3)
                WHERE corporation_id=$1 AND (title_mask & $2)<>0
                """,corporation,titleId,checked(_now()+86400000)))
                await members.ExecuteNonQueryAsync(token);
            return 0;
        },token);

    public async Task<Member> UpdateMemberAsync(long actor,Member desired,CancellationToken token = default) =>
        (await UpdateMembersAsync(actor,[desired],token)).Single();

    public Task<IReadOnlyList<Member>> UpdateMembersAsync(long actor,IReadOnlyList<Member> updates,CancellationToken token = default) =>
        WriteAsync<IReadOnlyList<Member>>(async (connection,tx) =>
        {
            var people = new Dictionary<long,Eve.Characters.Character>();
            foreach (var id in updates.Select(m => m.CharacterId).Append(actor).Distinct().Order())
                people[id] = await _characters.LockAndGetAsync(connection,tx,id,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var actorCharacter = people[actor];
            var corp = await GetAsync(connection,tx,actorCharacter.CorporationId,true,token) ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corp.IsNpc || corp.Deleted) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var actorMember = await MemberAsync(connection,tx,actor,token) ?? throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var titles = await TitlesAsync(connection,tx,corp.Id,token);
            var ceo = corp.CeoId == actor && !corp.CeoRolesSuspended;
            var administrator = ceo || (CorporationRoles.Session(corp,actorMember,actorCharacter.StationId,titles).AtAll & CorporationRoles.Director) != 0;
            var grantables = Masks(CorporationRoles.GrantableFor(corp,actorMember,actorCharacter.CorporationId,titles));
            var results = new List<Member>();
            foreach (var request in updates)
            {
                var old = await MemberAsync(connection,tx,request.CharacterId,token) ?? throw new CorporationError("MEMBER_NOT_FOUND");
                if (old.CorporationId != corp.Id || people[old.CharacterId].CorporationId != corp.Id) throw new CorporationError("CORPORATION_ACCESS_DENIED");
                var next = request with { CorporationId = old.CorporationId,StartedMs = old.StartedMs,StasisUntilMs = old.StasisUntilMs };
                if (next.Title.Length > 100) next = next with { Title = next.Title[..100] };
                var before = Masks(old.Masks);
                var after = Masks(next.Masks);
                for (var i = 0; i < after.Length; i++)
                    if (!CorporationRoles.IsKnown(after[i],AllowedMasks[i])) throw new CorporationError("CORPORATION_ACCESS_DENIED");
                if (next.TitleMask is < 0 or > 65535 || next.AccountKey != old.AccountKey) throw new CorporationError("CORPORATION_ACCESS_DENIED");
                var targetCeo = corp.CeoId == old.CharacterId;
                var director = (old.Masks.Roles & CorporationRoles.Director) != 0;
                var transition = director != ((next.Masks.Roles & CorporationRoles.Director) != 0);
                var metadataSame = next.Title == old.Title && next.BaseId == old.BaseId && next.DivisionId == old.DivisionId && next.SquadronId == old.SquadronId;
                var selfResign = actor == old.CharacterId && !targetCeo && next.BlockRoles && next.Masks == new RoleMasks() && next.TitleMask == 0 && metadataSame;
                var selfAllow = actor == old.CharacterId && !next.BlockRoles && next.Masks == old.Masks && next.TitleMask == old.TitleMask && metadataSame;
                if (!selfResign && !selfAllow)
                {
                    if (next.BlockRoles != old.BlockRoles) throw new CorporationError("CORPORATION_ACCESS_DENIED");
                    if (transition)
                    {
                        if (!ceo || targetCeo) throw new CorporationError("CORPORATION_ACCESS_DENIED");
                        next = next with { Masks = new RoleMasks((next.Masks.Roles & CorporationRoles.Director) != 0 ? 1 : 0),TitleMask = 0 };
                        after = Masks(next.Masks);
                    }
                    for (var i = 0; i < after.Length; i++)
                    {
                        if (after[i] == before[i]) continue;
                        if (targetCeo || (director && !transition) || (old.BlockRoles && (after[i] & ~before[i]) != 0))
                            throw new CorporationError("CORPORATION_ACCESS_DENIED");
                        if (transition) continue;
                        if (i % 2 == 1)
                        {
                            if (!administrator) throw new CorporationError("CORPORATION_ACCESS_DENIED");
                        }
                        else if (!administrator)
                        {
                            if (((before[i] ^ after[i]) & ~grantables[i+1]) != 0 || (i == 4 && old.BaseId != actorMember.BaseId))
                                throw new CorporationError("CORPORATION_ACCESS_DENIED");
                        }
                    }
                    if ((!metadataSame || next.TitleMask != old.TitleMask) && (!administrator || (targetCeo && !ceo)))
                        throw new CorporationError("CORPORATION_ACCESS_DENIED");
                    if (next.TitleMask != old.TitleMask && (director || (old.BlockRoles && (next.TitleMask & ~old.TitleMask) != 0)))
                        throw new CorporationError("CORPORATION_ACCESS_DENIED");
                }
                if (next == old) { results.Add(old); continue; }
                var promoted = !director && (next.Masks.Roles & CorporationRoles.Director) != 0;
                var removed = !promoted && (before.Where((mask,i) => (mask & ~after[i]) != 0).Any() || (old.TitleMask & ~next.TitleMask) != 0);
                if (removed) next = next with { StasisUntilMs = Math.Max(old.StasisUntilMs ?? 0,checked(_now() + 86400000)) };
                await using (var update = Command(connection,tx,$"""
                    UPDATE {_schema}.members SET title=$2,roles=$3,grantable_roles=$4,roles_hq=$5,grantable_hq=$6,
                        roles_base=$7,grantable_base=$8,roles_other=$9,grantable_other=$10,base_id=$11,title_mask=$12,
                        block_roles=$13,division_id=$14,squadron_id=$15,role_stasis_ms=$16 WHERE character_id=$1
                    """,old.CharacterId,next.Title,after[0],after[1],after[2],after[3],after[4],after[5],after[6],after[7],
                    next.BaseId,next.TitleMask,next.BlockRoles,next.DivisionId,next.SquadronId,next.StasisUntilMs))
                    await update.ExecuteNonQueryAsync(token);
                await using (var audit = Command(connection,tx,$"INSERT INTO {_schema}.role_history(corporation_id,character_id,actor_id,time_ms,old_roles,new_roles) VALUES($1,$2,$3,$4,$5::jsonb,$6::jsonb)",
                    corp.Id,old.CharacterId,actor,_now(),JsonSerializer.Serialize(old),JsonSerializer.Serialize(next)))
                    await audit.ExecuteNonQueryAsync(token);
                results.Add(next);
            }
            return results;
        },token);

    public sealed record RoleHistoryRecord(
        long CharacterId, long CorporationId, long ActorId, long ChangedMs, string OldRolesJson, string NewRolesJson);

    public async Task<IReadOnlyList<RoleHistoryRecord>> RoleHistoryAsync(
        long corporationId, long characterId, long? fromUnixMs, long? toUnixMs, int limit,
        CancellationToken cancellationToken = default)
    {
        if (corporationId <= 0 || characterId <= 0 || limit <= 0)
            return [];

        await using var command = _source.CreateCommand($"""
            SELECT character_id,corporation_id,actor_id,time_ms,old_roles::text,new_roles::text
            FROM {_schema}.role_history
            WHERE corporation_id=$1 AND character_id=$2
                AND ($3::bigint IS NULL OR time_ms >= $3)
                AND ($4::bigint IS NULL OR time_ms < $4)
            ORDER BY time_ms DESC,id DESC
            LIMIT $5
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = corporationId });
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter
        {
            DbType = System.Data.DbType.Int64,
            Value = fromUnixMs is { } from ? from : DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            DbType = System.Data.DbType.Int64,
            Value = toUnixMs is { } to ? to : DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter { Value = Math.Min(limit, 5000) });

        var rows = new List<RoleHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RoleHistoryRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5)));
        }
        return rows;
    }

    public Task SetAccountKeyAsync(long character,int key,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            if (key is < 1000 or > 1006) throw new CorporationError("INVALID_ACCOUNT_KEY");
            var c = await _characters.LockAndGetAsync(connection,tx,character,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corp = await GetAsync(connection,tx,c.CorporationId,true,token) ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            var member = await MemberAsync(connection,tx,character,token) ?? throw new CorporationError("MEMBER_NOT_FOUND");
            var roles = CorporationRoles.Session(corp,member,c.StationId,await TitlesAsync(connection,tx,corp.Id,token)).AtAll;
            if ((roles & (CorporationRoles.Director | CorporationRoles.Accountant | CorporationRoles.JuniorAccountant | CorporationRoles.WalletTakeRole(key))) == 0)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            await using var update = Command(connection,tx,$"UPDATE {_schema}.members SET account_key=$2 WHERE character_id=$1",character,key);
            await update.ExecuteNonQueryAsync(token);
            return 0;
        },token);
}
