using Npgsql;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    private const string AllianceColumns = "id,name,short_name,description,url,executor_corporation_id,creator_corporation_id,creator_character_id,created_ms,deleted,suspended,dictatorial";
    private const string AllianceApplicationColumns = "id,alliance_id,corporation_id,text,status,created_ms,updated_ms,archived,due_ms";
    private static Alliance ReadAlliance(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),
        r.IsDBNull(5) ? null : r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),r.GetInt64(8),r.GetBoolean(9),r.GetBoolean(10),r.GetBoolean(11));
    private static AllianceApplication ReadAllianceApplication(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetString(3),
        r.GetInt32(4),r.GetInt64(5),r.GetInt64(6),r.GetBoolean(7),r.IsDBNull(8) ? null : r.GetInt64(8));

    public async Task<Alliance?> AllianceAsync(long id,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        return await AllianceAsync(connection,null,id,false,token);
    }
    private async Task<Alliance?> AllianceAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long id,bool locked,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"SELECT {AllianceColumns} FROM {_schema}.alliances WHERE id=$1" + (locked ? " FOR UPDATE" : ""),id);
        await using var r = await command.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? ReadAlliance(r) : null;
    }
    public async Task<IReadOnlyList<AllianceMember>> AllianceMembersAsync(long alliance,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        return await AllianceMembersAsync(connection,null,alliance,token);
    }
    private async Task<IReadOnlyList<AllianceMember>> AllianceMembersAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long alliance,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"SELECT alliance_id,corporation_id,started_ms,chosen_executor_id FROM {_schema}.alliance_members WHERE alliance_id=$1 ORDER BY corporation_id",alliance);
        var rows = new List<AllianceMember>();
        await using var r = await command.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) rows.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.IsDBNull(3) ? null : r.GetInt64(3)));
        return rows;
    }

    public Task<Alliance> CreateAllianceAsync(long founder,NewAlliance input,string operationKey,CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var request = AllianceRules.Validate(input);
        return WriteAsync(async (connection,tx) =>
        {
            var character = await _characters.LockAndGetAsync(connection,tx,founder,token) ?? throw new CorporationError("ALLIANCE_FOUNDER_INVALID");
            await using (var prior = Command(connection,tx,$"SELECT {AllianceColumns} FROM {_schema}.alliances WHERE founding_key=$1",operationKey))
            await using (var r = await prior.ExecuteReaderAsync(token))
            {
                if (await r.ReadAsync(token))
                {
                    var old = ReadAlliance(r);
                    if (old.CreatorCharacterId != founder || old.CreatorCorporationId != character.CorporationId || old.Name != request.Name ||
                        old.ShortName != request.ShortName || old.Description != request.Description || old.Url != request.Url)
                        throw new CorporationError("ALLIANCE_FOUNDING_OPERATION_MISMATCH");
                    return old;
                }
            }
            var corp = await RequireRoleAsync(connection,tx,character.CorporationId,founder,CorporationRoles.Director,token);
            if (corp.CeoId != founder) throw new CorporationError("ALLIANCE_FOUNDER_INVALID");
            if (corp.AllianceId is not null) throw new CorporationError("CORPORATION_ALREADY_IN_ALLIANCE");
            if (await _skills.LevelAsync(connection,tx,founder,3732,token) < 5) throw new CorporationError("ALLIANCE_EMPIRE_CONTROL_REQUIRED");
            await using (var npc = Command(connection,tx,$"SELECT 1 FROM {_schema}.owner_names WHERE ticker_key=$1 AND is_npc AND NOT deleted",Key(request.ShortName)))
                if (await npc.ExecuteScalarAsync(token) is not null) throw new CorporationError("ALLIANCE_SHORT_NAME_TAKEN");
            await using var allocate = Command(connection,tx,$"SELECT nextval('{_schema}.alliance_ids')");
            var id = (long)(await allocate.ExecuteScalarAsync(token))!;
            try
            {
                await using var owner = Command(connection,tx,$"INSERT INTO {_schema}.owner_names(owner_id,name_key,ticker_key) VALUES($1,$2,$3)",id,Key(request.Name),Key(request.ShortName));
                await owner.ExecuteNonQueryAsync(token);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new CorporationError(error.ConstraintName?.Contains("ticker",StringComparison.Ordinal) == true ? "ALLIANCE_SHORT_NAME_TAKEN" : "ALLIANCE_NAME_TAKEN");
            }
            var now = _now();
            await using (var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.alliances(id,name,short_name,description,url,executor_corporation_id,creator_corporation_id,creator_character_id,created_ms,founding_key)
                VALUES($1,$2,$3,$4,$5,$6,$6,$7,$8,$9)
                """,id,request.Name,request.ShortName,request.Description,request.Url,corp.Id,founder,now,operationKey))
                await insert.ExecuteNonQueryAsync(token);
            await _wallets.AdjustAsync(connection,tx,founder,-AllianceFoundingCost,1000125,48,"Alliance registration fee",now,operationKey + ":alliance-fee",token,
                referenceId:id,referenceType:16159);
            await JoinAllianceAsync(connection,tx,id,corp.Id,corp.Id,now,token);
            return (await AllianceAsync(connection,tx,id,false,token))!;
        },token);
    }

    private async Task JoinAllianceAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long alliance,long corporation,long? executor,long now,CancellationToken token)
    {
        await using (var command = Command(connection,tx,$"""
            WITH joined AS (
                INSERT INTO {_schema}.alliance_members(alliance_id,corporation_id,started_ms,chosen_executor_id)
                VALUES($1,$2,$3,$4) RETURNING corporation_id
            ), history AS (
                INSERT INTO {_schema}.alliance_employment(corporation_id,alliance_id,started_ms)
                SELECT corporation_id,$1,$3 FROM joined RETURNING corporation_id
            )
            UPDATE {_schema}.corporations SET alliance_id=$1 WHERE id IN(SELECT corporation_id FROM history)
            """,alliance,corporation,now,executor))
            await command.ExecuteNonQueryAsync(token);
        await _characters.SetCorporationAllianceAsync(connection,tx,corporation,alliance,token);
    }

    public Task<long> ApplyToAllianceAsync(long actor,long alliance,string text,CancellationToken token = default) =>
        ApplyToAllianceAsync(actor,alliance,text,false,false,token);

    public Task<long> ApplyToAllianceAsync(long actor,long alliance,string text,bool requireCeo,bool rejectExisting,CancellationToken token = default)
    {
        if (text.EnumerateRunes().Count() > 1000) throw new CorporationError("ALLIANCE_APPLICATION_TEXT_TOO_LONG");
        return WriteAsync(async (connection,tx) =>
        {
            var target = await AllianceAsync(connection,tx,alliance,true,token);
            if (target is null || target.Deleted) throw new CorporationError("ALLIANCE_NOT_FOUND");
            var character = await _characters.LockAndGetAsync(connection,tx,actor,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corp = await RequireRoleAsync(connection,tx,character.CorporationId,actor,CorporationRoles.Director,token);
            if (requireCeo && corp.CeoId != actor) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            if (corp.AllianceId is not null) throw new CorporationError("CORPORATION_ALREADY_IN_ALLIANCE");
            if (rejectExisting)
            {
                await using var existing = Command(connection,tx,$"SELECT 1 FROM {_schema}.alliance_applications WHERE alliance_id=$1 AND corporation_id=$2 AND (NOT archived OR status=4) ORDER BY id DESC LIMIT 1 FOR UPDATE",alliance,corp.Id);
                if (await existing.ExecuteScalarAsync(token) is not null)
                    throw new CorporationError("ALLIANCE_APPLICATION_ALREADY_EXISTS");
            }
            var now = _now();
            await using var command = Command(connection,tx,$"""
                INSERT INTO {_schema}.alliance_applications(alliance_id,corporation_id,text,status,created_ms,updated_ms)
                VALUES($1,$2,$3,1,$4,$4)
                ON CONFLICT(alliance_id,corporation_id) WHERE NOT archived DO UPDATE SET text=$3,updated_ms=CASE WHEN alliance_applications.status=1 THEN $4 ELSE alliance_applications.updated_ms END
                RETURNING id
                """,alliance,corp.Id,text,now);
            return (long)(await command.ExecuteScalarAsync(token))!;
        },token);
    }

    public async Task<IReadOnlyList<AllianceApplication>> AllianceApplicationsAsync(long alliance,CancellationToken token = default)
    {
        await using var command = _source.CreateCommand($"SELECT {AllianceApplicationColumns} FROM {_schema}.alliance_applications WHERE alliance_id=$1 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value=alliance });
        var rows = new List<AllianceApplication>();
        await using var r = await command.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) rows.Add(ReadAllianceApplication(r));
        return rows;
    }

    public Task<bool> WithdrawAllianceApplicationAsync(long actor,long alliance,CancellationToken token = default) =>
        WriteAsync<bool>(async (connection,tx) =>
        {
            var character = await _characters.LockAndGetAsync(connection,tx,actor,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corporation = await RequireRoleAsync(connection,tx,character.CorporationId,actor,CorporationRoles.Director,token);
            AllianceApplication? application = null;
            await using (var command = Command(connection,tx,$"SELECT {AllianceApplicationColumns} FROM {_schema}.alliance_applications WHERE alliance_id=$1 AND corporation_id=$2 ORDER BY id DESC LIMIT 1 FOR UPDATE",alliance,corporation.Id))
            await using (var reader = await command.ExecuteReaderAsync(token))
                if (await reader.ReadAsync(token)) application = ReadAllianceApplication(reader);
            if (application is null) return false;
            if (application.Status is not (1 or 4)) throw new CorporationError("ALLIANCE_APPLICATION_INVALID_STATE");
            await using var delete = Command(connection,tx,$"DELETE FROM {_schema}.alliance_applications WHERE id=$1",application.Id);
            await delete.ExecuteNonQueryAsync(token);
            return true;
        },token);

    public Task<AllianceApplication> RespondToAllianceAsync(long actor,long applicationId,int status,CancellationToken token = default) =>
        RespondToAllianceAsync(actor,applicationId,null,status,token);

    public Task<AllianceApplication> RespondToAllianceAsync(long actor,long applicationId,string? applicationText,int status,CancellationToken token = default)
    {
        if (status is not (2 or 4)) throw new CorporationError("ALLIANCE_APPLICATION_INVALID_STATE");
        if (applicationText is not null && applicationText.EnumerateRunes().Count() > 1000)
            throw new CorporationError("ALLIANCE_APPLICATION_TEXT_TOO_LONG");
        return WriteAsync(async (connection,tx) =>
        {
            AllianceApplication app;
            await using (var command = Command(connection,tx,$"SELECT {AllianceApplicationColumns} FROM {_schema}.alliance_applications WHERE id=$1 FOR UPDATE",applicationId))
            await using (var r = await command.ExecuteReaderAsync(token))
                app = await r.ReadAsync(token) ? ReadAllianceApplication(r) : throw new CorporationError("ALLIANCE_APPLICATION_NOT_FOUND");
            var alliance = await AllianceAsync(connection,tx,app.AllianceId,true,token);
            if (alliance?.ActiveExecutorId is not { } executor || alliance.Deleted) throw new CorporationError("ALLIANCE_EXECUTOR_REQUIRED");
            await RequireRoleAsync(connection,tx,executor,actor,CorporationRoles.Director,token);
            if (app.Status == status)
            {
                if (applicationText is not null && app.Text != applicationText)
                    throw new CorporationError("ALLIANCE_APPLICATION_REPLAY_MISMATCH");
                return app;
            }
            if (app.Archived || app.Status != 1) throw new CorporationError("ALLIANCE_APPLICATION_INVALID_STATE");
            var corp = await GetAsync(connection,tx,app.CorporationId,true,token);
            if (corp is null || corp.Deleted || corp.IsNpc || corp.AllianceId is not null) throw new CorporationError("INVALID_APPLICANT_CORPORATION");
            var now = _now();
            var text = applicationText ?? app.Text;
            long? due = status == 2 ? checked(now + AllianceRules.ApplicationDelayMs) : null;
            await using var update = Command(connection,tx,$"UPDATE {_schema}.alliance_applications SET text=$2,status=$3,updated_ms=$4,due_ms=$5,archived=$6 WHERE id=$1",
                app.Id,text,status,now,due,status==4);
            await update.ExecuteNonQueryAsync(token);
            return app with { Text=text,Status=status,UpdatedMs=now,DueMs=due,Archived=status==4 };
        },token);
    }

    /// <summary>Durable due dates survive restarts. Returns changed corporations for after-commit affiliation notifications.</summary>
    public Task<IReadOnlyList<long>> ActivateDueAllianceApplicationsAsync(CancellationToken token = default) =>
        WriteAsync<IReadOnlyList<long>>(async (connection,tx) =>
        {
            var due = new List<AllianceApplication>();
            await using (var command = Command(connection,tx,$"SELECT {AllianceApplicationColumns} FROM {_schema}.alliance_applications WHERE status=2 AND NOT archived AND due_ms <= $1 ORDER BY alliance_id,id FOR UPDATE",_now()))
            await using (var r = await command.ExecuteReaderAsync(token))
                while (await r.ReadAsync(token)) due.Add(ReadAllianceApplication(r));
            var changed = new List<long>();
            foreach (var app in due)
            {
                var alliance = await AllianceAsync(connection,tx,app.AllianceId,true,token);
                var corp = await GetAsync(connection,tx,app.CorporationId,true,token);
                var join = alliance is { Deleted:false } && corp is { Deleted:false,IsNpc:false,AllianceId:null };
                if (join)
                {
                    await JoinAllianceAsync(connection,tx,app.AllianceId,app.CorporationId,alliance!.ExecutorCorporationId,_now(),token);
                    changed.Add(app.CorporationId);
                }
                await using var finish = Command(connection,tx,$"UPDATE {_schema}.alliance_applications SET status=$2,archived=true,updated_ms=$3 WHERE id=$1",
                    app.Id,join ? 3 : 4,_now());
                await finish.ExecuteNonQueryAsync(token);
                if (join) await ReelectAsync(connection,tx,alliance!,token);
            }
            return changed;
        },token);

    private async Task ReelectAsync(NpgsqlConnection connection,NpgsqlTransaction tx,Alliance alliance,CancellationToken token)
    {
        var members = await AllianceMembersAsync(connection,tx,alliance.Id,token);
        var election = AllianceRules.Elect(members,alliance.ExecutorCorporationId,alliance.Dictatorial);
        await using var command = Command(connection,tx,$"UPDATE {_schema}.alliances SET executor_corporation_id=$2,suspended=$3 WHERE id=$1",
            alliance.Id,election.Executor,election.Suspended);
        await command.ExecuteNonQueryAsync(token);
    }

    public Task ChooseExecutorAsync(long actor,long chosen,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            var character = await _characters.LockAndGetAsync(connection,tx,actor,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corp = await RequireRoleAsync(connection,tx,character.CorporationId,actor,CorporationRoles.Director,token);
            if (corp.AllianceId is not { } id) throw new CorporationError("ALLIANCE_NOT_FOUND");
            var alliance = await AllianceAsync(connection,tx,id,true,token) ?? throw new CorporationError("ALLIANCE_NOT_FOUND");
            if (alliance.Dictatorial && chosen != alliance.ExecutorCorporationId || !(await AllianceMembersAsync(connection,tx,id,token)).Any(m => m.CorporationId == chosen)) throw new CorporationError("INVALID_EXECUTOR_CORPORATION");
            await using (var update = Command(connection,tx,$"UPDATE {_schema}.alliance_members SET chosen_executor_id=$3 WHERE alliance_id=$1 AND corporation_id=$2",id,corp.Id,chosen))
                await update.ExecuteNonQueryAsync(token);
            await ReelectAsync(connection,tx,alliance,token);
            return 0;
        },token);
}
