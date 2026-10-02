// Membership workflow ported from EVE.js corpRegistryRuntime.js and corporationRuntimeState.js (828823a).
using Eve.Characters;
using Npgsql;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    private const string ApplicationColumns = "id,corporation_id,character_id,text,status,created_ms,updated_ms,response,archived";
    private static Application ReadApplication(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),
        r.GetString(3),r.GetInt32(4),r.GetInt64(5),r.GetInt64(6),r.GetString(7),r.GetBoolean(8));

    private async Task<Member?> MemberAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long character,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"SELECT {MemberColumns} FROM {_schema}.members WHERE character_id=$1",character);
        await using var r = await command.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? ReadMember(r) : null;
    }
    private async Task<bool> HasRoleAsync(NpgsqlConnection connection,NpgsqlTransaction tx,Corporation corp,long actor,long role,CancellationToken token)
    {
        var character = await _characters.LockAndGetAsync(connection,tx,actor,token);
        if (character is null || character.CorporationId != corp.Id || corp.IsNpc || corp.Deleted) return false;
        if (corp.CeoId == actor) return true;
        var member = await MemberAsync(connection,tx,actor,token);
        if (member is null || member.CorporationId != corp.Id) return false;
        var titles = await TitlesAsync(connection,tx,corp.Id,token);
        var roles = CorporationRoles.Session(corp,member,character.StationId,titles).AtAll;
        return (roles & CorporationRoles.Director) != 0 || (roles & role) == role;
    }
    private async Task<Corporation> RequireRoleAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long corporation,long actor,long role,CancellationToken token)
    {
        var corp = await GetAsync(connection,tx,corporation,true,token) ?? throw new CorporationError("CORPORATION_NOT_FOUND");
        if (!await HasRoleAsync(connection,tx,corp,actor,role,token)) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return corp;
    }

    public async Task<long> ApplyAsync(long character,long corporation,string text,bool invitation = false,long? inviter = null,CancellationToken token = default) =>
        (await ApplyCoreAsync(character,corporation,text,invitation,inviter,token)).ApplicationId;

    public async Task<(long ApplicationId,long InvitationId)> CreateInvitationAsync(long character,long corporation,long inviter,CancellationToken token = default)
    {
        var result = await ApplyCoreAsync(character,corporation,"",true,inviter,token);
        return (result.ApplicationId,result.InvitationId ?? throw new InvalidOperationException("Invitation ID was not allocated."));
    }

    private Task<(long ApplicationId,long? InvitationId)> ApplyCoreAsync(
        long character,long corporation,string text,bool invitation,long? inviter,CancellationToken token) =>
        WriteAsync<(long ApplicationId,long? InvitationId)>(async (connection,tx) =>
        {
            var corp = await GetAsync(connection,tx,corporation,true,token) ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corp.Deleted || corp.IsNpc) throw new CorporationError("CUSTOM_CORPORATION_REQUIRED");
            if (invitation)
            {
                if (inviter is null || !await HasRoleAsync(connection,tx,corp,inviter.Value,CorporationRoles.PersonnelManager,token))
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
            }
            else if (corp.Settings.TryGetProperty("applicationsEnabled",out var enabled) && enabled.ValueKind == System.Text.Json.JsonValueKind.False)
                throw new CorporationError("APPLICATIONS_DISABLED");
            var applicant = await _characters.LockAndGetAsync(connection,tx,character,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            if (applicant.CorporationId == corporation) throw new CorporationError("ALREADY_A_MEMBER");
            var now = _now();
            var invitationIdSql = invitation ? $"nextval('{_schema}.invitation_ids')" : "NULL";
            var conflictSql = invitation ? "" : """
                ON CONFLICT(corporation_id,character_id) WHERE NOT archived AND status <> 8
                DO UPDATE SET text=$3,updated_ms=$5
                """;
            await using var command = Command(connection,tx,$"""
                INSERT INTO {_schema}.applications(corporation_id,character_id,text,status,created_ms,updated_ms,invitation_id)
                VALUES($1,$2,$3,$4,$5,$5,{invitationIdSql})
                {conflictSql}
                RETURNING id,invitation_id
                """,corporation,character,text,invitation ? 8 : 0,now);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Application insert returned no row.");
            return (reader.GetInt64(0),reader.IsDBNull(1) ? null : reader.GetInt64(1));
        },token);

    public async Task<IReadOnlyList<Application>> ApplicationsAsync(long? corporation = null,long? character = null,bool archived = false,CancellationToken token = default)
    {
        await using var command = _source.CreateCommand($"SELECT {ApplicationColumns} FROM {_schema}.applications WHERE ($1::bigint IS NULL OR corporation_id=$1) AND ($2::bigint IS NULL OR character_id=$2) AND archived=$3 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)corporation ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)character ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = archived });
        var result = new List<Application>();
        await using var r = await command.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) result.Add(ReadApplication(r));
        return result;
    }

    public Task<Application> RespondAsync(long actor,long applicationId,int status,string text,string response,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            Application application;
            await using (var command = Command(connection,tx,$"SELECT {ApplicationColumns} FROM {_schema}.applications WHERE id=$1 FOR UPDATE",applicationId))
            await using (var r = await command.ExecuteReaderAsync(token))
                application = await r.ReadAsync(token) ? ReadApplication(r) : throw new CorporationError("APPLICATION_NOT_FOUND");
            var corp = await GetAsync(connection,tx,application.CorporationId,true,token) ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (application.Archived) return application;
            if (status is 1 or 2 or 3 or 7)
            {
                if (actor != application.CharacterId || (status == 2 && application.Status is not (6 or 8)))
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
            }
            else if (status is 4 or 5 or 6)
            {
                if (application.Status is not (0 or 1 or 5) || !await HasRoleAsync(connection,tx,corp,actor,CorporationRoles.PersonnelManager,token))
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
            }
            else throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var now = _now();
            if (status == 2)
            {
                await using (var count = Command(connection,tx,$"SELECT count(*) FROM {_schema}.members WHERE corporation_id=$1",corp.Id))
                    if ((long)(await count.ExecuteScalarAsync(token))! >= corp.MemberLimit) throw new CorporationError("CORPORATION_FULL");
                var character = await _characters.LockAndGetAsync(connection,tx,application.CharacterId,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
                await CanLeaveAsync(connection,tx,character,now,token);
                await MoveAsync(connection,tx,character,corp.Id,corp.AllianceId,now,false,token);
                await using var others = Command(connection,tx,$"UPDATE {_schema}.applications SET status=7,archived=true,updated_ms=$2 WHERE character_id=$1 AND id<>$3 AND NOT archived",character.Id,now,applicationId);
                await others.ExecuteNonQueryAsync(token);
            }
            var terminal = status is 2 or 3 or 4 or 7;
            await using (var update = Command(connection,tx,$"UPDATE {_schema}.applications SET text=$2,status=$3,response=$4,updated_ms=$5,archived=$6 WHERE id=$1",applicationId,text,status,response,now,terminal))
                await update.ExecuteNonQueryAsync(token);
            return application with { Text = text,Status = status,Response = response,UpdatedMs = now,Archived = terminal };
        },token);

    private async Task CanLeaveAsync(NpgsqlConnection connection,NpgsqlTransaction tx,Character character,long now,CancellationToken token)
    {
        var member = await MemberAsync(connection,tx,character.Id,token);
        if (member is not null)
        {
            if (member.Masks != new RoleMasks() || member.TitleMask != 0)
            {
                var current = await GetAsync(connection,tx,character.CorporationId,true,token);
                if (current?.CeoId != character.Id) throw new CorporationError("CORPORATION_ROLES_MUST_BE_DROPPED");
            }
        }
        if (member?.StasisUntilMs > now) throw new CorporationError("CORPORATION_ROLE_STASIS");
        var old = await GetAsync(connection,tx,character.CorporationId,true,token);
        if (old is not null && !old.IsNpc && old.CeoId == character.Id)
        {
            throw new CorporationError("CEO_MUST_RESIGN");
        }
    }

    public Task LeaveAsync(long characterId,long npcCorporation,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            var character = await _characters.LockAndGetAsync(connection,tx,characterId,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var target = await GetAsync(connection,tx,npcCorporation,true,token);
            if (target is null || !target.IsNpc || target.Deleted) throw new CorporationError("NPC_CORPORATION_REQUIRED");
            if (character.CorporationId == target.Id) return 0;
            var now = _now();
            await CanLeaveAsync(connection,tx,character,now,token);
            await MoveAsync(connection,tx,character,target.Id,null,now,false,token);
            return 0;
        },token);

    public Task KickAsync(long actor,long target,long npcCorporation,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            var character = await _characters.LockAndGetAsync(connection,tx,target,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corp = await RequireRoleAsync(connection,tx,character.CorporationId,actor,CorporationRoles.PersonnelManager,token);
            if (corp.CeoId == target) throw new CorporationError("CEO_MUST_RESIGN");
            var npc = await GetAsync(connection,tx,npcCorporation,true,token);
            if (npc is null || !npc.IsNpc) throw new CorporationError("NPC_CORPORATION_REQUIRED");
            var now = _now();
            await CanLeaveAsync(connection,tx,character,now,token);
            await MoveAsync(connection,tx,character,npc.Id,null,now,false,token);
            return 0;
        },token);

    public Task ResignAsync(long actor,long successor,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            var character = await _characters.LockAndGetAsync(connection,tx,actor,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corp = await RequireRoleAsync(connection,tx,character.CorporationId,actor,CorporationRoles.Director,token);
            var next = await MemberAsync(connection,tx,successor,token);
            if (corp.CeoId != actor || successor == actor || next?.CorporationId != corp.Id) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            await using var update = Command(connection,tx,$"WITH leadership AS (UPDATE {_schema}.corporations SET ceo_id=$2 WHERE id=$1 RETURNING id) UPDATE {_schema}.members SET roles=roles & ~1 WHERE character_id=$3 AND corporation_id IN(SELECT id FROM leadership)",corp.Id,successor,actor);
            await update.ExecuteNonQueryAsync(token);
            return 0;
        },token);
}
