using Npgsql;

namespace Eve.Corporations;

// Client-visible Alliance contracts follow EveJS v0.12.9 allianceRegistryRuntime.js and allianceViewState.js (828823a75f211fdecb945b087a26c225f111491f).

public sealed partial class CorporationStore
{
    private const string AllianceColumns = "id,name,short_name,description,url,executor_corporation_id,creator_corporation_id,creator_character_id,created_ms,deleted,suspended,dictatorial";
    private const string AllianceApplicationColumns = "id,alliance_id,corporation_id,text,status,created_ms,updated_ms,archived,due_ms";
    public sealed record AllianceContact(long ContactId,long RelationshipId,long LabelMask);
    public sealed record AllianceContactLabel(long LabelId,string Name,int Color);

    private static Alliance ReadAlliance(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),
        r.IsDBNull(5) ? null : r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),r.GetInt64(8),r.GetBoolean(9),r.GetBoolean(10),r.GetBoolean(11));
    private static AllianceApplication ReadAllianceApplication(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetString(3),
        r.GetInt32(4),r.GetInt64(5),r.GetInt64(6),r.GetBoolean(7),r.IsDBNull(8) ? null : r.GetInt64(8));

    public async Task<Alliance?> AllianceAsync(long id,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        return await AllianceAsync(connection,null,id,false,token);
    }

    public async Task<IReadOnlyList<Alliance>> AllAlliancesAsync(CancellationToken token = default)
    {
        await using var command = _source.CreateCommand($"SELECT {AllianceColumns} FROM {_schema}.alliances WHERE deleted=FALSE ORDER BY id");
        var result = new List<Alliance>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(ReadAlliance(reader));
        return result;
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

    public Task<Alliance> CreateAllianceAsync(long founder, NewAlliance input, string operationKey, CancellationToken token = default) =>
        CreateAllianceCoreAsync(founder, input, operationKey, null, token);

    public Task<Alliance> CreateAllianceAsync(long founder, NewAlliance input, string operationKey,
        AllianceAffiliationMutationCallback writeAffiliation, CancellationToken token = default) =>
        CreateAllianceCoreAsync(founder, input, operationKey, writeAffiliation, token);

    private Task<Alliance> CreateAllianceCoreAsync(long founder, NewAlliance input, string operationKey,
        AllianceAffiliationMutationCallback? writeAffiliation, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var request = AllianceRules.Validate(input);
        return WriteAsync(async (connection, tx) =>
        {
            var character = await _characters.LockAndGetAsync(connection, tx, founder, token) ?? throw new CorporationError("ALLIANCE_FOUNDER_INVALID");
            Alliance? priorAlliance = null;
            await using (var prior = Command(connection, tx, $"SELECT {AllianceColumns} FROM {_schema}.alliances WHERE founding_key=$1", operationKey))
            await using (var r = await prior.ExecuteReaderAsync(token))
                if (await r.ReadAsync(token)) priorAlliance = ReadAlliance(r);
            if (priorAlliance is { } old)
            {
                if (old.CreatorCharacterId != founder || old.CreatorCorporationId != character.CorporationId || old.Name != request.Name ||
                    old.ShortName != request.ShortName || old.Description != request.Description || old.Url != request.Url)
                    throw new CorporationError("ALLIANCE_FOUNDING_OPERATION_MISMATCH");
                if (writeAffiliation is not null)
                    await writeAffiliation(connection, tx, old.CreatorCorporationId, old.Id, token);
                return old;
            }

            var corp = await RequireRoleAsync(connection, tx, character.CorporationId, founder, CorporationRoles.Director, token);
            if (corp.CeoId != founder) throw new CorporationError("ALLIANCE_FOUNDER_INVALID");
            if (corp.AllianceId is not null) throw new CorporationError("CORPORATION_ALREADY_IN_ALLIANCE");
            if (await _skills.LevelAsync(connection, tx, founder, 3732, token) < 5) throw new CorporationError("ALLIANCE_EMPIRE_CONTROL_REQUIRED");
            await using (var npc = Command(connection, tx, $"SELECT 1 FROM {_schema}.owner_names WHERE ticker_key=$1 AND is_npc AND NOT deleted", Key(request.ShortName)))
                if (await npc.ExecuteScalarAsync(token) is not null) throw new CorporationError("ALLIANCE_SHORT_NAME_TAKEN");
            await using var allocate = Command(connection, tx, $"SELECT nextval('{_schema}.alliance_ids')");
            var id = (long)(await allocate.ExecuteScalarAsync(token))!;
            try
            {
                await using var owner = Command(connection, tx, $"INSERT INTO {_schema}.owner_names(owner_id,name_key,ticker_key) VALUES($1,$2,$3)",
                    id, Key(request.Name), Key(request.ShortName));
                await owner.ExecuteNonQueryAsync(token);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new CorporationError(error.ConstraintName?.Contains("ticker", StringComparison.Ordinal) == true
                    ? "ALLIANCE_SHORT_NAME_TAKEN" : "ALLIANCE_NAME_TAKEN");
            }
            var now = _now();
            await using (var insert = Command(connection, tx, $"""
                INSERT INTO {_schema}.alliances(id,name,short_name,description,url,executor_corporation_id,creator_corporation_id,creator_character_id,created_ms,founding_key)
                VALUES($1,$2,$3,$4,$5,$6,$6,$7,$8,$9)
                """, id, request.Name, request.ShortName, request.Description, request.Url, corp.Id, founder, now, operationKey))
                await insert.ExecuteNonQueryAsync(token);
            await _wallets.AdjustAsync(connection, tx, founder, -AllianceFoundingCost, 1000125, 48, "Alliance registration fee", now,
                operationKey + ":alliance-fee", token, referenceId: id, referenceType: 16159);
            await JoinAllianceAsync(connection, tx, id, corp.Id, corp.Id, now, writeAffiliation, token);
            return (await AllianceAsync(connection, tx, id, false, token))!;
        }, token);
    }

    private async Task JoinAllianceAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long alliance, long corporation,
        long? executor, long now, AllianceAffiliationMutationCallback? writeAffiliation, CancellationToken token)
    {
        await using (var previous = Command(connection, tx,
            $"UPDATE {_schema}.alliance_employment SET ended_ms=$2 WHERE corporation_id=$1 AND alliance_id IS NULL AND ended_ms IS NULL",
            corporation, now))
            await previous.ExecuteNonQueryAsync(token);
        await using (var command = Command(connection, tx, $"""
            WITH joined AS (
                INSERT INTO {_schema}.alliance_members(alliance_id,corporation_id,started_ms,chosen_executor_id)
                VALUES($1,$2,$3,$4) RETURNING corporation_id
            ), history AS (
                INSERT INTO {_schema}.alliance_employment(corporation_id,alliance_id,started_ms)
                SELECT corporation_id,$1,$3 FROM joined RETURNING corporation_id
            )
            UPDATE {_schema}.corporations SET alliance_id=$1 WHERE id IN(SELECT corporation_id FROM history)
            """, alliance, corporation, now, executor))
            await command.ExecuteNonQueryAsync(token);
        await _characters.SetCorporationAllianceAsync(connection, tx, corporation, alliance, token);
        if (writeAffiliation is not null)
            await writeAffiliation(connection, tx, corporation, alliance, token);
    }

    public Task<long> ApplyToAllianceAsync(long actor,long alliance,string text,CancellationToken token = default) =>
        ApplyToAllianceAsync(actor,alliance,text,false,false,token);

    public Task<long> ApplyToAllianceAsync(long actor,long alliance,string text,bool requireCeo,bool rejectExisting,CancellationToken token = default)
    {
        if (text.Length > 1000) throw new CorporationError("ALLIANCE_APPLICATION_TEXT_TOO_LONG");
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

    public Task<AllianceApplication> RespondToAllianceAsync(long actor, long applicationId, string? applicationText, int status, CancellationToken token = default) =>
        RespondToAllianceCoreAsync(actor, applicationId, applicationText, status, null, token);

    public Task<AllianceApplication> RespondToAllianceAsync(long actor, long applicationId, string? applicationText, int status,
        AllianceApplicationScheduleCallback scheduleDue, CancellationToken token = default) =>
        RespondToAllianceCoreAsync(actor, applicationId, applicationText, status, scheduleDue, token);

    private Task<AllianceApplication> RespondToAllianceCoreAsync(long actor, long applicationId, string? applicationText, int status,
        AllianceApplicationScheduleCallback? scheduleDue, CancellationToken token, bool gameMaster = false)
    {
        if (status is not (2 or 4)) throw new CorporationError("ALLIANCE_APPLICATION_INVALID_STATE");
        if (applicationText is not null && applicationText.Length > 1000)
            throw new CorporationError("ALLIANCE_APPLICATION_TEXT_TOO_LONG");
        return WriteAsync(async (connection, tx) =>
        {
            AllianceApplication app;
            await using (var command = Command(connection, tx, $"SELECT {AllianceApplicationColumns} FROM {_schema}.alliance_applications WHERE id=$1 FOR UPDATE", applicationId))
            await using (var r = await command.ExecuteReaderAsync(token))
                app = await r.ReadAsync(token) ? ReadAllianceApplication(r) : throw new CorporationError("ALLIANCE_APPLICATION_NOT_FOUND");
            var alliance = await AllianceAsync(connection, tx, app.AllianceId, true, token);
            if (alliance is not { Deleted:false })
                throw new CorporationError("ALLIANCE_NOT_FOUND");
            if (!gameMaster)
            {
                if (alliance.ActiveExecutorId is not { } executor)
                    throw new CorporationError("ALLIANCE_EXECUTOR_REQUIRED");
                await RequireRoleAsync(connection, tx, executor, actor, CorporationRoles.Director, token);
            }
            if (status == 2 && scheduleDue is null) throw new CorporationError("ALLIANCE_DUE_SCHEDULER_UNAVAILABLE");
            if (app.Status == status)
            {
                if (applicationText is not null && app.Text != applicationText)
                    throw new CorporationError("ALLIANCE_APPLICATION_REPLAY_MISMATCH");
                if (status == 2 && app.DueMs is { } existingDue && scheduleDue is not null)
                    await scheduleDue(connection, tx, new(app.Id, app.AllianceId, app.CorporationId, existingDue), token);
                return app;
            }
            if (app.Archived || app.Status != 1) throw new CorporationError("ALLIANCE_APPLICATION_INVALID_STATE");
            var corp = await GetAsync(connection, tx, app.CorporationId, true, token);
            if (corp is null || corp.Deleted || corp.IsNpc || corp.AllianceId is not null) throw new CorporationError("INVALID_APPLICANT_CORPORATION");
            var now = _now();
            var text = applicationText ?? app.Text;
            long? due = status == 2 ? checked(now + AllianceRules.ApplicationDelayMs) : null;
            await using var update = Command(connection, tx,
                $"UPDATE {_schema}.alliance_applications SET text=$2,status=$3,updated_ms=$4,due_ms=$5,archived=$6 WHERE id=$1",
                app.Id, text, status, now, due, status == 4);
            await update.ExecuteNonQueryAsync(token);
            var updated = app with { Text = text, Status = status, UpdatedMs = now, DueMs = due, Archived = status == 4 };
            if (status == 2 && due is { } dueAt && scheduleDue is not null)
                await scheduleDue(connection, tx, new(app.Id, app.AllianceId, app.CorporationId, dueAt), token);
            return updated;
        }, token);
    }

    /// <summary>Accepted applications are re-enqueued after restart from their persisted due dates.</summary>

    public Task<AllianceApplication> ForceAcceptAllianceApplicationAsync(long applicationId,
        AllianceApplicationScheduleCallback scheduleDue,CancellationToken token = default) =>
        RespondToAllianceCoreAsync(0,applicationId,null,2,scheduleDue,token,gameMaster:true);

    public async Task<IReadOnlyList<AllianceApplicationDueJob>> PendingAllianceApplicationJobsAsync(CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,alliance_id,corporation_id,due_ms FROM {_schema}.alliance_applications " +
            "WHERE status=2 AND NOT archived AND due_ms IS NOT NULL ORDER BY due_ms,id";
        var jobs = new List<AllianceApplicationDueJob>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            jobs.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        return jobs;
    }


    public async Task<IReadOnlyList<(long CorporationId, long AllianceId)>> CurrentAllianceAffiliationsAsync(CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,alliance_id FROM {_schema}.corporations " +
            "WHERE alliance_id IS NOT NULL AND NOT deleted ORDER BY id";
        var affiliations = new List<(long CorporationId, long AllianceId)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            affiliations.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return affiliations;
    }


    public async Task<IReadOnlyList<(long CorporationId, long? AllianceId)>> CorporationAllianceAffiliationsAsync(
        CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,CASE WHEN deleted THEN NULL ELSE alliance_id END FROM {_schema}.corporations ORDER BY id";
        var affiliations = new List<(long CorporationId, long? AllianceId)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            affiliations.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        return affiliations;
    }

    public Task<AllianceApplicationDueOutcome> ProcessDueAllianceApplicationAsync(
        AllianceApplicationDueJob job, AllianceWarReadCallback readWars, AllianceAffiliationMutationCallback writeAffiliation,
        CancellationToken token = default) =>
        WriteAsync((connection, tx) => ProcessDueAllianceApplicationAsync(connection, tx, job, readWars, writeAffiliation, token), token);

    public async Task<AllianceApplicationDueOutcome> ProcessDueAllianceApplicationAsync(
        System.Data.Common.DbConnection databaseConnection, System.Data.Common.DbTransaction databaseTransaction,
        AllianceApplicationDueJob job, AllianceWarReadCallback readWars,
        AllianceAffiliationMutationCallback writeAffiliation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(readWars);
        ArgumentNullException.ThrowIfNull(writeAffiliation);
        if (databaseConnection is not NpgsqlConnection connection || databaseTransaction is not NpgsqlTransaction tx ||
            !ReferenceEquals(databaseTransaction.Connection, databaseConnection))
            throw new ArgumentException("Alliance due jobs require the scheduler's PostgreSQL transaction.");

        AllianceApplication? application = null;
        await using (var command = Command(connection, tx,
            $"SELECT {AllianceApplicationColumns} FROM {_schema}.alliance_applications WHERE id=$1 FOR UPDATE", job.ApplicationId))
        await using (var reader = await command.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token)) application = ReadAllianceApplication(reader);

        if (application is null || application.AllianceId != job.AllianceId || application.CorporationId != job.CorporationId ||
            application.Status != 2 || application.Archived)
            return new(false, null);

        var now = _now();
        if (application.DueMs is not { } dueAt)
        {
            await using var repair = Command(connection, tx,
                $"UPDATE {_schema}.alliance_applications SET due_ms=$2,updated_ms=$2 WHERE id=$1", application.Id, now);
            await repair.ExecuteNonQueryAsync(token);
            return new(false, now);
        }
        if (dueAt > now) return new(false, dueAt);

        var alliance = await AllianceAsync(connection, tx, application.AllianceId, true, token);
        var corporation = await GetAsync(connection, tx, application.CorporationId, true, token);
        if (alliance is not { Deleted: false } || corporation is null || corporation.Deleted || corporation.IsNpc ||
            (corporation.AllianceId is { } currentAlliance && currentAlliance != application.AllianceId))
        {
            await using var reject = Command(connection, tx,
                $"UPDATE {_schema}.alliance_applications SET status=4,archived=true,due_ms=NULL,updated_ms=$2 WHERE id=$1",
                application.Id, now);
            await reject.ExecuteNonQueryAsync(token);
            return new(false, null);
        }

        var warReason = AllianceRules.ApplicationWarHoldReason(application.CorporationId, application.AllianceId,
            await readWars(application.CorporationId, application.AllianceId, token));
        if (warReason is not null)
        {
            var retryAt = checked(now + AllianceRules.ApplicationWarRecheckDelayMs);
            await using var defer = Command(connection, tx,
                $"UPDATE {_schema}.alliance_applications SET due_ms=$2,updated_ms=$3 WHERE id=$1",
                application.Id, retryAt, now);
            await defer.ExecuteNonQueryAsync(token);
            return new(false, retryAt);
        }

        if (corporation.AllianceId == application.AllianceId)
        {
            await _characters.SetCorporationAllianceAsync(connection, tx, application.CorporationId, application.AllianceId, token);
            await writeAffiliation(connection, tx, application.CorporationId, application.AllianceId, token);
        }
        else
        {
            await JoinAllianceAsync(connection, tx, application.AllianceId, application.CorporationId,
                alliance.ExecutorCorporationId, now, writeAffiliation, token);
        }
        await using (var finish = Command(connection, tx,
            $"UPDATE {_schema}.alliance_applications SET status=3,archived=true,due_ms=NULL,updated_ms=$2 WHERE id=$1",
            application.Id, now))
            await finish.ExecuteNonQueryAsync(token);
        await ReelectAsync(connection, tx, alliance, token);
        return new(true, null);
    }

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

    public async Task<IReadOnlyList<Alliance>> RankedAlliancesAsync(long? limit,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        var select = $"SELECT {AllianceColumns} FROM {_schema}.alliances WHERE NOT deleted ORDER BY id";
        await using var command = limit is > 0
            ? Command(connection,null,select + " LIMIT $1",limit.Value)
            : Command(connection,null,select);
        var rows = new List<Alliance>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(ReadAlliance(reader));
        return rows;
    }


    public async Task<IReadOnlyDictionary<long,long>> AllianceCharacterCountsAsync(IReadOnlyList<long> alliances,CancellationToken token = default)
    {
        if (alliances.Count == 0) return new Dictionary<long,long>();
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT am.alliance_id,COUNT(m.character_id)
            FROM {_schema}.alliance_members am
            LEFT JOIN {_schema}.members m ON m.corporation_id=am.corporation_id
            WHERE am.alliance_id=ANY($1)
            GROUP BY am.alliance_id
            """,alliances.ToArray());
        var counts = new Dictionary<long,long>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) counts.Add(reader.GetInt64(0),reader.GetInt64(1));
        return counts;
    }

    public async Task<IReadOnlyList<AllianceApplication>> AllianceApplicationsForCorporationAsync(long corporation,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,
            $"SELECT {AllianceApplicationColumns} FROM {_schema}.alliance_applications WHERE corporation_id=$1 ORDER BY alliance_id,id",corporation);
        var rows = new List<AllianceApplication>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(ReadAllianceApplication(reader));
        return rows;
    }

    public async Task<IReadOnlyList<(long? AllianceId,long StartedMs,bool Deleted)>> CorporationAllianceHistoryAsync(
        long corporation,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT e.alliance_id,e.started_ms,COALESCE(a.deleted,false)
            FROM {_schema}.alliance_employment e
            LEFT JOIN {_schema}.alliances a ON a.id=e.alliance_id
            WHERE e.corporation_id=$1
            ORDER BY e.started_ms DESC,e.alliance_id DESC NULLS LAST
            """,corporation);
        var rows = new List<(long? AllianceId,long StartedMs,bool Deleted)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add((reader.IsDBNull(0) ? null : reader.GetInt64(0),reader.GetInt64(1),reader.GetBoolean(2)));
        return rows;
    }

    public Task<Alliance> UpdateAllianceAsync(long actor,long alliance,string description,string url,CancellationToken token = default)
    {
        if (description.Length > 5000 || url.Length > 2048) throw new CorporationError("ALLIANCE_PUBLIC_INFO_INVALID_LENGTH");
        return WriteAsync(async (connection,tx) =>
        {
            var current = await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await using var command = Command(connection,tx,$"UPDATE {_schema}.alliances SET description=$2,url=$3 WHERE id=$1",alliance,description,url);
            await command.ExecuteNonQueryAsync(token);
            return current with { Description=description,Url=url };
        },token);
    }


    public async Task SetAlliancePrimeHourAsync(long actor,long alliance,int hour,long validAfterFileTime,CancellationToken token = default)
    {
        if (hour is < 0 or > 23) throw new CorporationError("ALLIANCE_PRIME_HOUR_INVALID");
        await WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            short currentHour;
            short newHour;
            long validAfter;
            await using (var state = Command(connection,tx,$"SELECT current_prime_hour,new_prime_hour,new_prime_hour_valid_after_filetime FROM {_schema}.alliances WHERE id=$1 FOR UPDATE",alliance))
            await using (var reader = await state.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new CorporationError("ALLIANCE_NOT_FOUND");
                currentHour = reader.GetInt16(0);
                newHour = reader.GetInt16(1);
                validAfter = reader.GetInt64(2);
            }

            var now = VoteNowFiletime;
            if (validAfter > 0 && validAfter <= now)
            {
                currentHour = newHour;
                newHour = currentHour;
                validAfter = 0;
            }
            if (validAfterFileTime <= now)
                throw new CorporationError("ALLIANCE_PRIME_TIME_WINDOW_UNAVAILABLE");
            if (currentHour == hour && validAfter == 0) return true;
            if (newHour == hour && validAfter > 0) return true;

            newHour = (short)hour;
            validAfter = validAfterFileTime;
            await using var update = Command(connection,tx,$"UPDATE {_schema}.alliances SET current_prime_hour=$2,new_prime_hour=$3,new_prime_hour_valid_after_filetime=$4 WHERE id=$1",
                alliance,currentHour,newHour,validAfter);
            await update.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

public async Task<(short CurrentHour, short NewHour, long ValidAfterFileTime)> AlliancePrimeTimeAsync(
        long alliance, CancellationToken token = default) =>
        await WriteAsync(async (connection, tx) =>
        {
            short currentHour;
            short newHour;
            long validAfter;
            await using (var state = Command(connection, tx,
                $"SELECT current_prime_hour,new_prime_hour,new_prime_hour_valid_after_filetime FROM {_schema}.alliances WHERE id=$1 FOR UPDATE", alliance))
            await using (var reader = await state.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new CorporationError("ALLIANCE_NOT_FOUND");
                currentHour = reader.GetInt16(0);
                newHour = reader.GetInt16(1);
                validAfter = reader.GetInt64(2);
            }

            if (validAfter > 0 && validAfter <= VoteNowFiletime)
            {
                currentHour = newHour;
                newHour = currentHour;
                validAfter = 0;
                await using var update = Command(connection, tx,
                    $"UPDATE {_schema}.alliances SET current_prime_hour=$2,new_prime_hour=$3,new_prime_hour_valid_after_filetime=0 WHERE id=$1",
                    alliance, currentHour, newHour);
                await update.ExecuteNonQueryAsync(token);
            }
            return (currentHour, newHour, validAfter);
        }, token);

    public async Task<(long? CurrentSystemId, long? NewSystemId, long ValidAfterFileTime)> AllianceCapitalSystemAsync(
        long alliance, long? soleSystemId = null, CancellationToken token = default) =>
        await WriteAsync(async (connection, tx) =>
        {
            long? current;
            long? pending;
            long validAfter;
            await using (var state = Command(connection, tx,
                $"SELECT current_capital_system_id,new_capital_system_id,new_capital_system_valid_after_filetime FROM {_schema}.alliances WHERE id=$1 FOR UPDATE",
                alliance))
            await using (var reader = await state.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new CorporationError("ALLIANCE_NOT_FOUND");
                current = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                pending = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                validAfter = reader.GetInt64(2);
            }

            var now = VoteNowFiletime;
            if (soleSystemId is > 0)
            {
                if (!await SolarSystemExistsAsync(connection, tx, soleSystemId.Value, token))
                    throw new CorporationError("ALLIANCE_CAPITAL_SYSTEM_INVALID");
                if (current != soleSystemId || pending is not null || validAfter != 0)
                {
                    current = soleSystemId;
                    pending = null;
                    validAfter = 0;
                    await using var update = Command(connection, tx,
                        $"UPDATE {_schema}.alliances SET current_capital_system_id=$2,new_capital_system_id=NULL,new_capital_system_valid_after_filetime=0 WHERE id=$1",
                        alliance, current);
                    await update.ExecuteNonQueryAsync(token);
                }
            }
            else if (validAfter > 0 && validAfter <= now)
            {
                current = pending;
                pending = null;
                validAfter = 0;
                await using var update = Command(connection, tx,
                    $"UPDATE {_schema}.alliances SET current_capital_system_id=$2,new_capital_system_id=NULL,new_capital_system_valid_after_filetime=0 WHERE id=$1",
                    alliance, current);
                await update.ExecuteNonQueryAsync(token);
            }
            return (current, pending, validAfter);
        }, token);

    public async Task SetAllianceCapitalSystemAsync(
        long actor, long alliance, long solarSystemId, long? soleSystemId, CancellationToken token = default)
    {
        if (solarSystemId <= 0) throw new CorporationError("ALLIANCE_CAPITAL_SYSTEM_INVALID");
        await WriteAsync(async (connection, tx) =>
        {
            await RequireAllianceExecutorAsync(connection, tx, actor, alliance, token);
            if (!await SolarSystemExistsAsync(connection, tx, solarSystemId, token))
                throw new CorporationError("ALLIANCE_CAPITAL_SYSTEM_INVALID");
            if (soleSystemId is > 0 && soleSystemId.Value != solarSystemId)
                throw new CorporationError("ALLIANCE_CAPITAL_SYSTEM_NOT_SOV_HUB");

            long? current;
            long? pending;
            long validAfter;
            await using (var state = Command(connection, tx,
                $"SELECT current_capital_system_id,new_capital_system_id,new_capital_system_valid_after_filetime FROM {_schema}.alliances WHERE id=$1 FOR UPDATE",
                alliance))
            await using (var reader = await state.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new CorporationError("ALLIANCE_NOT_FOUND");
                current = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                pending = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                validAfter = reader.GetInt64(2);
            }

            var now = VoteNowFiletime;
            if (soleSystemId is > 0)
            {
                current = soleSystemId;
                pending = null;
                validAfter = 0;
            }
            else
            {
                if (validAfter > 0 && validAfter <= now)
                {
                    current = pending;
                    pending = null;
                    validAfter = 0;
                }
                if (pending == solarSystemId || (current == solarSystemId && pending is null && validAfter == 0))
                    return true;
                pending = solarSystemId;
                validAfter = checked(now + 7 * VoteTicksPerDay);
            }

            await using var update = Command(connection, tx,
                $"UPDATE {_schema}.alliances SET current_capital_system_id=$2,new_capital_system_id=$3,new_capital_system_valid_after_filetime=$4 WHERE id=$1",
                alliance, current, pending, validAfter);
            await update.ExecuteNonQueryAsync(token);
            return true;
        }, token);
    }

    public async Task CancelAllianceCapitalSystemTransitionAsync(
        long actor, long alliance, long? soleSystemId, CancellationToken token = default)
    {
        await WriteAsync(async (connection, tx) =>
        {
            await RequireAllianceExecutorAsync(connection, tx, actor, alliance, token);
            if (soleSystemId is > 0 && !await SolarSystemExistsAsync(connection, tx, soleSystemId.Value, token))
                throw new CorporationError("ALLIANCE_CAPITAL_SYSTEM_INVALID");

            long? current;
            long? pending;
            long validAfter;
            await using (var state = Command(connection, tx,
                $"SELECT current_capital_system_id,new_capital_system_id,new_capital_system_valid_after_filetime FROM {_schema}.alliances WHERE id=$1 FOR UPDATE",
                alliance))
            await using (var reader = await state.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new CorporationError("ALLIANCE_NOT_FOUND");
                current = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                pending = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                validAfter = reader.GetInt64(2);
            }

            if (soleSystemId is > 0)
            {
                current = soleSystemId;
                pending = null;
                validAfter = 0;
            }
            else if (validAfter > 0 && validAfter <= VoteNowFiletime)
            {
                current = pending;
                pending = null;
                validAfter = 0;
            }
            else if (pending is not null)
            {
                pending = null;
                validAfter = 0;
            }
            else
            {
                return true;
            }

            await using var update = Command(connection, tx,
                $"UPDATE {_schema}.alliances SET current_capital_system_id=$2,new_capital_system_id=$3,new_capital_system_valid_after_filetime=$4 WHERE id=$1",
                alliance, current, pending, validAfter);
            await update.ExecuteNonQueryAsync(token);
            return true;
        }, token);
    }

    private static async Task<bool> SolarSystemExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long solarSystemId, CancellationToken token)
    {
        var key = solarSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var command = Command(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM sde.\"mapSolarSystems\" WHERE key=$1)", key);
        return await command.ExecuteScalarAsync(token) is true;
    }


    private async Task<Alliance> RequireAllianceExecutorAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long actor,long alliance,CancellationToken token)
    {
        var character = await _characters.LockAndGetAsync(connection,tx,actor,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
        var corporation = await RequireRoleAsync(connection,tx,character.CorporationId,actor,CorporationRoles.Director,token);
        if (corporation.AllianceId != alliance) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var current = await AllianceAsync(connection,tx,alliance,true,token);
        if (current is not { Deleted:false } || current.ActiveExecutorId != corporation.Id)
            throw new CorporationError("ALLIANCE_EXECUTOR_REQUIRED");
        return current;
    }

    public Task WriteAllianceRelationshipAsync(long actor,long alliance,Func<NpgsqlConnection,NpgsqlTransaction,Task> write,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await write(connection,tx);
            return true;
        },token);

    public async Task<IReadOnlyList<AllianceContact>> AllianceContactsAsync(long alliance,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT contact_id,relationship_id,label_mask FROM {_schema}.alliance_contacts WHERE alliance_id=$1 ORDER BY contact_id",alliance);
        await using var reader = await command.ExecuteReaderAsync(token);
        var contacts = new List<AllianceContact>();
        while (await reader.ReadAsync(token))
            contacts.Add(new AllianceContact(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2)));
        return contacts;
    }

    public async Task<IReadOnlyList<AllianceContactLabel>> AllianceContactLabelsAsync(long alliance,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT label_id,name,color FROM {_schema}.alliance_contact_labels WHERE alliance_id=$1 ORDER BY label_id",alliance);
        await using var reader = await command.ExecuteReaderAsync(token);
        var labels = new List<AllianceContactLabel>();
        while (await reader.ReadAsync(token))
            labels.Add(new AllianceContactLabel(reader.GetInt64(0),reader.GetString(1),reader.GetInt32(2)));
        return labels;
    }

    public Task AddAllianceContactAsync(long actor,long alliance,long contact,long relationshipId,CancellationToken token = default)
    {
        ValidateAllianceContact(alliance,contact,relationshipId);
        return WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await using var command = Command(connection,tx,$"INSERT INTO {_schema}.alliance_contacts (alliance_id,contact_id,relationship_id) VALUES ($1,$2,$3) ON CONFLICT (alliance_id,contact_id) DO UPDATE SET relationship_id=EXCLUDED.relationship_id",alliance,contact,relationshipId);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task EditAllianceContactAsync(long actor,long alliance,long contact,long relationshipId,CancellationToken token = default)
    {
        ValidateAllianceContact(alliance,contact,relationshipId);
        return WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await using var command = Command(connection,tx,$"UPDATE {_schema}.alliance_contacts SET relationship_id=$3 WHERE alliance_id=$1 AND contact_id=$2",alliance,contact,relationshipId);
            if (await command.ExecuteNonQueryAsync(token) == 0) throw new CorporationError("ALLIANCE_CONTACT_NOT_FOUND");
            return true;
        },token);
    }

    public Task RemoveAllianceContactsAsync(long actor,long alliance,IReadOnlyCollection<long> contacts,CancellationToken token = default)
    {
        if (contacts.Any(contact => contact <= 0)) throw new CorporationError("ALLIANCE_CONTACT_INVALID");
        if (contacts.Count == 0) return Task.CompletedTask;
        var ids = contacts.Distinct().ToArray();
        return WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await using var command = Command(connection,tx,$"DELETE FROM {_schema}.alliance_contacts WHERE alliance_id=$1 AND contact_id=ANY($2)",alliance,ids);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task EditAllianceContactsRelationshipAsync(long actor,long alliance,IReadOnlyCollection<long> contacts,long relationshipId,CancellationToken token = default)
    {
        if (relationshipId is < -10 or > 10 || contacts.Any(contact => contact <= 0))
            throw new CorporationError("ALLIANCE_CONTACT_INVALID");
        if (contacts.Count == 0) return Task.CompletedTask;
        var ids = contacts.Distinct().ToArray();
        return WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await using var command = Command(connection,tx,$"UPDATE {_schema}.alliance_contacts SET relationship_id=$3 WHERE alliance_id=$1 AND contact_id=ANY($2)",alliance,ids,relationshipId);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task<AllianceContactLabel> CreateAllianceContactLabelAsync(long actor,long alliance,string name,int color,CancellationToken token = default)
    {
        ValidateAllianceLabel(name,color);
        return WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await using var nextCommand = Command(connection,tx,$"SELECT COALESCE(MAX(label_id),0)+1 FROM {_schema}.alliance_contact_labels WHERE alliance_id=$1",alliance);
            var labelId = Convert.ToInt64(await nextCommand.ExecuteScalarAsync(token));
            if (labelId > 63) throw new CorporationError("ALLIANCE_LABEL_LIMIT");
            await using var command = Command(connection,tx,$"INSERT INTO {_schema}.alliance_contact_labels (alliance_id,label_id,name,color) VALUES ($1,$2,$3,$4)",alliance,labelId,name,color);
            await command.ExecuteNonQueryAsync(token);
            return new AllianceContactLabel(labelId,name,color);
        },token);
    }

    public Task DeleteAllianceContactLabelAsync(long actor,long alliance,long labelId,CancellationToken token = default)
    {
        ValidateAllianceLabelId(labelId);
        return WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await using var command = Command(connection,tx,$"DELETE FROM {_schema}.alliance_contact_labels WHERE alliance_id=$1 AND label_id=$2",alliance,labelId);
            if (await command.ExecuteNonQueryAsync(token) > 0)
            {
                var bit = 1L << ((int)labelId - 1);
                await using var clear = Command(connection,tx,$"UPDATE {_schema}.alliance_contacts SET label_mask=label_mask & $2 WHERE alliance_id=$1",alliance,~bit);
                await clear.ExecuteNonQueryAsync(token);
            }
            return true;
        },token);
    }

    public Task EditAllianceContactLabelAsync(long actor,long alliance,long labelId,string? name,int? color,CancellationToken token = default)
    {
        ValidateAllianceLabelId(labelId);
        if ((name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 100)) || color is < 0 or > 16777215)
            throw new CorporationError("ALLIANCE_LABEL_INVALID");
        return WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            await using var command = Command(connection,tx,$"UPDATE {_schema}.alliance_contact_labels SET name=COALESCE($3,name),color=COALESCE($4,color) WHERE alliance_id=$1 AND label_id=$2",alliance,labelId,name,color);
            if (await command.ExecuteNonQueryAsync(token) == 0) throw new CorporationError("ALLIANCE_LABEL_NOT_FOUND");
            return true;
        },token);
    }

    public Task AssignAllianceContactLabelsAsync(long actor,long alliance,IReadOnlyCollection<long> contacts,long labelMask,CancellationToken token = default) =>
        ChangeAllianceContactLabelsAsync(actor,alliance,contacts,labelMask,true,token);

    public Task RemoveAllianceContactLabelsAsync(long actor,long alliance,IReadOnlyCollection<long> contacts,long labelMask,CancellationToken token = default) =>
        ChangeAllianceContactLabelsAsync(actor,alliance,contacts,labelMask,false,token);

    private Task ChangeAllianceContactLabelsAsync(long actor,long alliance,IReadOnlyCollection<long> contacts,long labelMask,bool add,CancellationToken token)
    {
        if (labelMask < 0 || contacts.Any(contact => contact <= 0)) throw new CorporationError("ALLIANCE_LABEL_INVALID");
        if (contacts.Count == 0 || labelMask == 0) return Task.CompletedTask;
        var ids = contacts.Distinct().ToArray();
        return WriteAsync(async (connection,tx) =>
        {
            await RequireAllianceExecutorAsync(connection,tx,actor,alliance,token);
            var allowed = await AllianceContactLabelMaskAsync(connection,tx,alliance,token);
            if ((labelMask & ~allowed) != 0) throw new CorporationError("ALLIANCE_LABEL_NOT_FOUND");
            var operation = add ? "|" : "& ~";
            await using var command = Command(connection,tx,$"UPDATE {_schema}.alliance_contacts SET label_mask=label_mask {operation} $3 WHERE alliance_id=$1 AND contact_id=ANY($2)",alliance,ids,labelMask);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    private async Task<long> AllianceContactLabelMaskAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long alliance,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"SELECT label_id FROM {_schema}.alliance_contact_labels WHERE alliance_id=$1 ORDER BY label_id",alliance);
        await using var reader = await command.ExecuteReaderAsync(token);
        long mask = 0;
        while (await reader.ReadAsync(token))
            mask |= 1L << (reader.GetInt16(0) - 1);
        return mask;
    }

    private static void ValidateAllianceContact(long alliance,long contact,long relationshipId)
    {
        if (contact <= 0 || contact == alliance || relationshipId is < -10 or > 10)
            throw new CorporationError("ALLIANCE_CONTACT_INVALID");
    }

    private static void ValidateAllianceLabel(string name,int color)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || color is < 0 or > 16777215)
            throw new CorporationError("ALLIANCE_LABEL_INVALID");
    }

    private static void ValidateAllianceLabelId(long labelId)
    {
        if (labelId is < 1 or > 63) throw new CorporationError("ALLIANCE_LABEL_INVALID");
    }

    public async Task<IReadOnlyList<CorporationContact>> CorporationContactsAsync(long corporationId,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT contact_id,relationship_id,label_mask,in_watchlist FROM {_schema}.corporation_contacts WHERE corporation_id=$1 ORDER BY contact_id",corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var contacts = new List<CorporationContact>();
        while (await reader.ReadAsync(token))
            contacts.Add(new CorporationContact(reader.GetInt64(0),reader.GetInt32(1),reader.GetInt64(2),reader.GetBoolean(3)));
        return contacts;
    }

    public async Task<IReadOnlyList<CorporationContactLabel>> CorporationContactLabelsAsync(long corporationId,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT label_id,name,color FROM {_schema}.corporation_contact_labels WHERE corporation_id=$1 ORDER BY label_id",corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var labels = new List<CorporationContactLabel>();
        while (await reader.ReadAsync(token))
            labels.Add(new CorporationContactLabel(reader.GetInt64(0),reader.GetString(1),reader.GetInt32(2)));
        return labels;
    }

    public async Task<IReadOnlyList<CorporationBulletin>> CorporationBulletinsAsync(long corporationId,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT bulletin_id,corporation_id,create_character_id,create_filetime,edit_character_id,edit_filetime,title,body,sort_order FROM {_schema}.corporation_bulletins WHERE corporation_id=$1 ORDER BY sort_order,bulletin_id",corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var bulletins = new List<CorporationBulletin>();
        while (await reader.ReadAsync(token))
            bulletins.Add(ReadCorpBulletin(reader));
        return bulletins;
    }

    public async Task<CorporationBulletin?> CorporationBulletinAsync(long corporationId,long bulletinId,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT bulletin_id,corporation_id,create_character_id,create_filetime,edit_character_id,edit_filetime,title,body,sort_order FROM {_schema}.corporation_bulletins WHERE corporation_id=$1 AND bulletin_id=$2",corporationId,bulletinId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadCorpBulletin(reader) : null;
    }

    public Task AddCorporationContactAsync(long corporationId,long contactId,int relationshipId,CancellationToken token = default)
    {
        if (corporationId <= 0 || contactId <= 0 || relationshipId is < -10 or > 10)
            throw new CorporationError("CORPORATION_CONTACT_INVALID");
        return WriteAsync(async (connection,tx) =>
        {
            await using var command = Command(connection,tx,$"INSERT INTO {_schema}.corporation_contacts(corporation_id,contact_id,relationship_id,label_mask,in_watchlist) VALUES($1,$2,$3,0,false) ON CONFLICT(corporation_id,contact_id) DO UPDATE SET relationship_id=EXCLUDED.relationship_id,label_mask=0,in_watchlist=false",corporationId,contactId,relationshipId);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task EditCorporationContactRelationshipsAsync(long corporationId,IReadOnlyCollection<long> contactIds,int relationshipId,CancellationToken token = default)
    {
        if (corporationId <= 0 || relationshipId is < -10 or > 10 || contactIds.Any(id => id <= 0))
            throw new CorporationError("CORPORATION_CONTACT_INVALID");
        var ids = contactIds.Distinct().ToArray();
        if (ids.Length == 0) return Task.CompletedTask;
        return WriteAsync(async (connection,tx) =>
        {
            await using var command = Command(connection,tx,$"INSERT INTO {_schema}.corporation_contacts(corporation_id,contact_id,relationship_id) SELECT $1,id,$3 FROM unnest($2::bigint[]) AS ids(id) ON CONFLICT(corporation_id,contact_id) DO UPDATE SET relationship_id=EXCLUDED.relationship_id",corporationId,ids,relationshipId);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task RemoveCorporationContactsAsync(long corporationId,IReadOnlyCollection<long> contactIds,CancellationToken token = default)
    {
        if (corporationId <= 0 || contactIds.Any(id => id <= 0))
            throw new CorporationError("CORPORATION_CONTACT_INVALID");
        var ids = contactIds.Distinct().ToArray();
        if (ids.Length == 0) return Task.CompletedTask;
        return WriteAsync(async (connection,tx) =>
        {
            await using var command = Command(connection,tx,$"DELETE FROM {_schema}.corporation_contacts WHERE corporation_id=$1 AND contact_id=ANY($2)",corporationId,ids);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task<CorporationContactLabel> CreateCorporationContactLabelAsync(long corporationId,string name,int color,CancellationToken token = default)
    {
        ValidateCorporationContactLabel(name,color);
        return WriteAsync(async (connection,tx) =>
        {
            await using var corporation = Command(connection,tx,$"SELECT id FROM {_schema}.corporations WHERE id=$1 AND NOT deleted FOR UPDATE",corporationId);
            if (await corporation.ExecuteScalarAsync(token) is null)
                throw new CorporationError("CORPORATION_NOT_FOUND");
            await using var counterInsert = Command(connection,tx,$"INSERT INTO {_schema}.corporation_contact_label_counters(corporation_id,next_label_id) VALUES($1,1) ON CONFLICT(corporation_id) DO NOTHING",corporationId);
            await counterInsert.ExecuteNonQueryAsync(token);
            await using var counterRead = Command(connection,tx,$"SELECT next_label_id FROM {_schema}.corporation_contact_label_counters WHERE corporation_id=$1 FOR UPDATE",corporationId);
            var labelId = Convert.ToInt64(await counterRead.ExecuteScalarAsync(token));
            if (labelId == 0)
                throw new CorporationError("CORPORATION_LABEL_LIMIT");
            await using var duplicate = Command(connection,tx,$"SELECT 1 FROM {_schema}.corporation_contact_labels WHERE corporation_id=$1 AND lower(name)=lower($2)",corporationId,name);
            if (await duplicate.ExecuteScalarAsync(token) is not null)
                throw new CorporationError("CORPORATION_LABEL_EXISTS");
            var nextLabelId = labelId > 2305843009213693952L ? 0 : labelId * 2;
            await using var counterUpdate = Command(connection,tx,$"UPDATE {_schema}.corporation_contact_label_counters SET next_label_id=$2 WHERE corporation_id=$1",corporationId,nextLabelId);
            await counterUpdate.ExecuteNonQueryAsync(token);
            await using var insert = Command(connection,tx,$"INSERT INTO {_schema}.corporation_contact_labels(corporation_id,label_id,name,color) VALUES($1,$2,$3,$4)",corporationId,labelId,name,color);
            await insert.ExecuteNonQueryAsync(token);
            return new CorporationContactLabel(labelId,name,color);
        },token);
    }

    public Task EditCorporationContactLabelAsync(long corporationId,long labelId,string? name,int? color,CancellationToken token = default)
    {
        if (corporationId <= 0 || !IsCorporationLabelId(labelId) || name is not null && (name.Length is < 1 or > 100) || color is < 0 or > 16777215)
            throw new CorporationError("CORPORATION_LABEL_INVALID");
        return WriteAsync(async (connection,tx) =>
        {
            await using var command = Command(connection,tx,$"UPDATE {_schema}.corporation_contact_labels SET name=COALESCE($3,name),color=COALESCE($4,color) WHERE corporation_id=$1 AND label_id=$2",corporationId,labelId,name,color);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task DeleteCorporationContactLabelAsync(long corporationId,long labelId,CancellationToken token = default)
    {
        if (corporationId <= 0 || !IsCorporationLabelId(labelId))
            throw new CorporationError("CORPORATION_LABEL_INVALID");
        return WriteAsync(async (connection,tx) =>
        {
            await using var clear = Command(connection,tx,$"UPDATE {_schema}.corporation_contacts SET label_mask=label_mask & ~($2::bigint) WHERE corporation_id=$1",corporationId,labelId);
            await clear.ExecuteNonQueryAsync(token);
            await using var delete = Command(connection,tx,$"DELETE FROM {_schema}.corporation_contact_labels WHERE corporation_id=$1 AND label_id=$2",corporationId,labelId);
            await delete.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task ChangeCorporationContactLabelsAsync(long corporationId,IReadOnlyCollection<long> contactIds,long labelMask,bool add,CancellationToken token = default)
    {
        if (corporationId <= 0 || labelMask < 0 || contactIds.Any(id => id <= 0))
            throw new CorporationError("CORPORATION_LABEL_INVALID");
        var ids = contactIds.Distinct().ToArray();
        if (ids.Length == 0 || labelMask == 0) return Task.CompletedTask;
        return WriteAsync(async (connection,tx) =>
        {
            await using var labels = Command(connection,tx,$"SELECT COALESCE(bit_or(label_id),0) FROM {_schema}.corporation_contact_labels WHERE corporation_id=$1",corporationId);
            var allowed = Convert.ToInt64(await labels.ExecuteScalarAsync(token));
            if ((labelMask & ~allowed) != 0)
                throw new CorporationError("CORPORATION_LABEL_NOT_FOUND");
            if (add)
            {
                await using var command = Command(connection,tx,$"INSERT INTO {_schema}.corporation_contacts(corporation_id,contact_id,relationship_id,label_mask,in_watchlist) SELECT $1,id,0,$3,false FROM unnest($2::bigint[]) AS ids(id) ON CONFLICT(corporation_id,contact_id) DO UPDATE SET label_mask={_schema}.corporation_contacts.label_mask | EXCLUDED.label_mask",corporationId,ids,labelMask);
                await command.ExecuteNonQueryAsync(token);
            }
            else
            {
                await using var command = Command(connection,tx,$"UPDATE {_schema}.corporation_contacts SET label_mask=label_mask & ~($3::bigint) WHERE corporation_id=$1 AND contact_id=ANY($2)",corporationId,ids,labelMask);
                await command.ExecuteNonQueryAsync(token);
            }
            return true;
        },token);
    }

    public Task<long> AddCorporationBulletinAsync(long corporationId,long actorId,string title,string body,long? bulletinId,long createFiletime,long editFiletime,CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0 || createFiletime <= 0 || editFiletime <= 0 || bulletinId is <= 0)
            throw new CorporationError("CORPORATION_BULLETIN_INVALID");
        return WriteAsync(async (connection,tx) =>
        {
            await using var corporation = Command(connection,tx,$"SELECT id FROM {_schema}.corporations WHERE id=$1 AND NOT deleted FOR UPDATE",corporationId);
            if (await corporation.ExecuteScalarAsync(token) is null)
                throw new CorporationError("CORPORATION_NOT_FOUND");
            var id = bulletinId ?? 0;
            if (id == 0)
            {
                await using var next = Command(connection,tx,$"SELECT COALESCE(MAX(bulletin_id),0)+1 FROM {_schema}.corporation_bulletins WHERE corporation_id=$1",corporationId);
                id = Convert.ToInt64(await next.ExecuteScalarAsync(token));
            }
            await using var existing = Command(connection,tx,$"SELECT 1 FROM {_schema}.corporation_bulletins WHERE corporation_id=$1 AND bulletin_id=$2 FOR UPDATE",corporationId,id);
            if (await existing.ExecuteScalarAsync(token) is not null)
            {
                await using var update = Command(connection,tx,$"UPDATE {_schema}.corporation_bulletins SET edit_character_id=$3,edit_filetime=$4,title=$5,body=$6 WHERE corporation_id=$1 AND bulletin_id=$2",corporationId,id,actorId,editFiletime,title,body);
                await update.ExecuteNonQueryAsync(token);
                return id;
            }
            await using var order = Command(connection,tx,$"SELECT COALESCE(MAX(sort_order),-1)+1 FROM {_schema}.corporation_bulletins WHERE corporation_id=$1",corporationId);
            var sortOrder = Convert.ToInt32(await order.ExecuteScalarAsync(token));
            await using var insert = Command(connection,tx,$"INSERT INTO {_schema}.corporation_bulletins(corporation_id,bulletin_id,create_character_id,create_filetime,edit_character_id,edit_filetime,title,body,sort_order) VALUES($1,$2,$3,$4,$3,$5,$6,$7,$8)",corporationId,id,actorId,createFiletime,editFiletime,title,body,sortOrder);
            await insert.ExecuteNonQueryAsync(token);
            return id;
        },token);
    }

    public Task DeleteCorporationBulletinAsync(long corporationId,long bulletinId,CancellationToken token = default)
    {
        if (corporationId <= 0 || bulletinId <= 0)
            throw new CorporationError("CORPORATION_BULLETIN_INVALID");
        return WriteAsync(async (connection,tx) =>
        {
            await using var command = Command(connection,tx,$"DELETE FROM {_schema}.corporation_bulletins WHERE corporation_id=$1 AND bulletin_id=$2",corporationId,bulletinId);
            await command.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task UpdateCorporationBulletinOrderAsync(long corporationId,IReadOnlyList<long> bulletinIds,CancellationToken token = default)
    {
        if (corporationId <= 0 || bulletinIds.Any(id => id <= 0))
            throw new CorporationError("CORPORATION_BULLETIN_INVALID");
        var order = new Dictionary<long,int>();
        for (var index = 0; index < bulletinIds.Count; index++)
            order[bulletinIds[index]] = index;
        if (order.Count == 0) return Task.CompletedTask;
        return WriteAsync(async (connection,tx) =>
        {
            foreach (var (id,sortOrder) in order)
            {
                await using var command = Command(connection,tx,$"UPDATE {_schema}.corporation_bulletins SET sort_order=$3 WHERE corporation_id=$1 AND bulletin_id=$2",corporationId,id,sortOrder);
                await command.ExecuteNonQueryAsync(token);
            }
            return true;
        },token);
    }

    public Task<(long WarId,long BillId)> CreateWarDeclarationAsync(
        long corporationId,long declaredByCharacterId,long againstId,long? warHqId,long declaredAtFiletime,
        long startedAtFiletime,long dueFiletime,int billTypeId,decimal amount,long creditorId,string operationKey,
        Func<NpgsqlConnection,NpgsqlTransaction,long,CancellationToken,Task<long>> createWar,
        CancellationToken token = default)
    {
        if (corporationId <= 0 || declaredByCharacterId <= 0 || againstId <= 0 || againstId == corporationId ||
            declaredAtFiletime <= 0 || startedAtFiletime <= declaredAtFiletime || dueFiletime <= declaredAtFiletime ||
            billTypeId <= 0 || amount <= 0 || creditorId <= 0 || string.IsNullOrWhiteSpace(operationKey))
            throw new CorporationError("CORPORATION_WAR_DECLARATION_INVALID");
        ArgumentNullException.ThrowIfNull(createWar);
        var fingerprint = System.Text.Json.JsonSerializer.Serialize(new
        {
            corporationId,declaredByCharacterId,againstId,warHqId,billTypeId,amount,creditorId
        });
        return WriteAsync(async (connection,tx) =>
        {
            await using var corporation = Command(connection,tx,$"SELECT is_npc,deleted FROM {_schema}.corporations WHERE id=$1 FOR UPDATE",corporationId);
            await using (var reader = await corporation.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token) || reader.GetBoolean(0) || reader.GetBoolean(1))
                    throw new CorporationError("CORPORATION_NOT_FOUND");
            }
            await using (var prior = Command(connection,tx,$"SELECT bill_id,war_id,request_fingerprint FROM {_schema}.corporation_bills WHERE operation_key=$1 FOR UPDATE",operationKey))
            await using (var reader = await prior.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                {
                    var priorBillId = reader.GetInt64(0);
                    var priorWarId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    if (reader.GetString(2) != fingerprint || priorWarId <= 0)
                        throw new CorporationError("CORPORATION_WAR_OPERATION_CONFLICT");
                    return (priorWarId,priorBillId);
                }
            }
            await using var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.corporation_bills
                    (bill_type_id,amount_isk,interest,debtor_id,creditor_id,due_filetime,external_id,external_id2,
                     war_id,operation_key,request_fingerprint)
                VALUES($1,$2,0,$3,$4,$5,$6,-1,NULL,$7,$8)
                RETURNING bill_id
                """,billTypeId,amount,corporationId,creditorId,dueFiletime,againstId,operationKey,fingerprint);
            var billId = Convert.ToInt64(await insert.ExecuteScalarAsync(token));
            var warId = await createWar(connection,tx,billId,token);
            if (warId <= 0)
                throw new CorporationError("CORPORATION_WAR_DECLARATION_FAILED");
            await using var link = Command(connection,tx,$"UPDATE {_schema}.corporation_bills SET war_id=$2 WHERE bill_id=$1",billId,warId);
            await link.ExecuteNonQueryAsync(token);
            return (warId,billId);
        },token);
    }

    public async Task<IReadOnlyList<CorporationBill>> CorporationBillsAsync(long corporationId,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT bill_id,bill_type_id,amount_isk,interest,debtor_id,creditor_id,due_filetime,external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key FROM {_schema}.corporation_bills WHERE debtor_id=$1 AND NOT paid ORDER BY due_filetime,bill_id",corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var bills = new List<CorporationBill>();
        while (await reader.ReadAsync(token)) bills.Add(ReadCorporationBill(reader));
        return bills;
    }

    public async Task<IReadOnlyList<CorporationBill>> CorporationBillsReceivableAsync(long corporationId,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT bill_id,bill_type_id,amount_isk,interest,debtor_id,creditor_id,due_filetime,external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key FROM {_schema}.corporation_bills WHERE creditor_id=$1 AND NOT paid ORDER BY due_filetime,bill_id",corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var bills = new List<CorporationBill>();
        while (await reader.ReadAsync(token)) bills.Add(ReadCorporationBill(reader));
        return bills;
    }

    public async Task<bool> PayCorporationBillAsync(long billId,long corporationId,long paidByOwnerId,int accountKey,long paidAtMs,CancellationToken token = default)
    {
        if (billId <= 0 || corporationId <= 0 || paidByOwnerId <= 0 || accountKey is < 1000 or > 1006 || paidAtMs < 0)
            return false;
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = Command(connection,transaction,$"SELECT bill_id,bill_type_id,amount_isk,interest,debtor_id,creditor_id,due_filetime,external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key FROM {_schema}.corporation_bills WHERE bill_id=$1 AND debtor_id=$2 AND NOT paid FOR UPDATE",billId,corporationId);
        CorporationBill bill;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token))
                return false;
            bill = ReadCorporationBill(reader);
        }
        Eve.Wallet.WalletSettlement settlement;
        try
        {
            settlement = await _wallets.TransferAsync(connection,transaction,
                new Eve.Wallet.WalletAccount(corporationId,accountKey),new Eve.Wallet.WalletAccount(bill.CreditorId,1000),
                bill.AmountIsk,17,"Bill payment",paidAtMs,$"corporation-bill:{billId}:payment","bill_payment",token);
        }
        catch (Exception error) when (error is Eve.Wallet.InsufficientFundsException or Eve.Wallet.MoneyLimitException or
            Eve.Wallet.WalletNotFoundException or Eve.Wallet.WalletOperationConflictException)
        {
            return false;
        }
        var paidFiletime = checked((paidAtMs+11_644_473_600_000L)*10_000L);
        await using var mark = Command(connection,transaction,$"UPDATE {_schema}.corporation_bills SET paid=true,paid_filetime=$2,paid_by_owner_id=$3 WHERE bill_id=$1 AND NOT paid",billId,paidFiletime,paidByOwnerId);
        if (await mark.ExecuteNonQueryAsync(token) != 1)
            throw new CorporationError("BILL_NOT_PAYABLE");
        await transaction.CommitAsync(token);
        if (!settlement.Replayed)
            foreach (var entry in settlement.Entries.GroupBy(entry => (entry.OwnerId,entry.AccountKey)).Select(group => group.Last()))
                _wallets.AnnounceCommitted(new Eve.Wallet.WalletBalanceChange(entry.OwnerId,entry.AccountKey,entry.Balance));
        return true;
    }

    private static CorporationBill ReadCorporationBill(NpgsqlDataReader reader) =>
        new(reader.GetInt64(0),reader.GetInt32(1),reader.GetDecimal(2),reader.GetDecimal(3),reader.GetInt64(4),
            reader.GetInt64(5),reader.GetInt64(6),reader.GetInt64(7),reader.GetInt64(8),
            reader.IsDBNull(9) ? 0 : reader.GetInt64(9),reader.GetBoolean(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),reader.GetString(13));

    private static CorporationBulletin ReadCorpBulletin(NpgsqlDataReader reader) =>
        new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),
            reader.GetInt64(4),reader.GetInt64(5),reader.GetString(6),reader.GetString(7),reader.GetInt32(8));

    private static void ValidateCorporationContactLabel(string name,int color)
    {
        if (name.Length is < 1 or > 100 || color is < 0 or > 16777215)
            throw new CorporationError("CORPORATION_LABEL_INVALID");
    }

    private static bool IsCorporationLabelId(long labelId) =>
        labelId > 0 && labelId <= 4611686018427387904L && (labelId & (labelId - 1)) == 0;

    public async Task<IReadOnlyList<CorporationBulletin>> AllianceBulletinsAsync(
        long allianceId, CancellationToken token = default)
    {
        if (allianceId <= 0) return [];
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null, $"""
            SELECT bulletin_id,alliance_id,create_character_id,create_filetime,edit_character_id,
                   edit_filetime,title,body,sort_order
            FROM {_schema}.alliance_bulletins
            WHERE alliance_id=$1
            ORDER BY sort_order,edit_filetime DESC,bulletin_id
            """, allianceId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var bulletins = new List<CorporationBulletin>();
        while (await reader.ReadAsync(token)) bulletins.Add(ReadCorpBulletin(reader));
        return bulletins;
    }

    public async Task<CorporationBulletin?> AllianceBulletinAsync(
        long allianceId, long bulletinId, CancellationToken token = default)
    {
        if (allianceId <= 0 || bulletinId <= 0) return null;
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null, $"""
            SELECT bulletin_id,alliance_id,create_character_id,create_filetime,edit_character_id,
                   edit_filetime,title,body,sort_order
            FROM {_schema}.alliance_bulletins
            WHERE alliance_id=$1 AND bulletin_id=$2
            """, allianceId, bulletinId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadCorpBulletin(reader) : null;
    }

    public Task<long> AddAllianceBulletinAsync(long allianceId,long actorId,string title,string body,
        long? bulletinId,long createFiletime,long editFiletime,CancellationToken token = default)
    {
        if (allianceId <= 0 || actorId <= 0 || createFiletime <= 0 || editFiletime <= 0 || bulletinId is <= 0)
            throw new CorporationError("ALLIANCE_BULLETIN_INVALID");
        return WriteAsync(async (connection,tx) =>
        {
            await using var alliance = Command(connection,tx,
                $"SELECT id FROM {_schema}.alliances WHERE id=$1 AND NOT deleted FOR UPDATE",allianceId);
            if (await alliance.ExecuteScalarAsync(token) is null)
                throw new CorporationError("ALLIANCE_NOT_FOUND");
            var character = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var corporation = await RequireRoleAsync(connection,tx,character.CorporationId,actorId,
                CorporationRoles.Director,token);
            if (corporation.AllianceId != allianceId)
                throw new CorporationError("ALLIANCE_ACCESS_DENIED");
            var id = bulletinId ?? 0;
            if (id == 0)
            {
                await using var next = Command(connection,tx,
                    $"SELECT COALESCE(MAX(bulletin_id),0)+1 FROM {_schema}.alliance_bulletins WHERE alliance_id=$1",
                    allianceId);
                id = Convert.ToInt64(await next.ExecuteScalarAsync(token));
            }
            await using var existing = Command(connection,tx,
                $"SELECT 1 FROM {_schema}.alliance_bulletins WHERE alliance_id=$1 AND bulletin_id=$2 FOR UPDATE",
                allianceId,id);
            if (await existing.ExecuteScalarAsync(token) is not null)
            {
                await using var update = Command(connection,tx,$"""
                    UPDATE {_schema}.alliance_bulletins
                    SET edit_character_id=$3,edit_filetime=$4,title=$5,body=$6
                    WHERE alliance_id=$1 AND bulletin_id=$2
                    """,allianceId,id,actorId,editFiletime,title,body);
                await update.ExecuteNonQueryAsync(token);
                return id;
            }
            await using var order = Command(connection,tx,
                $"SELECT COALESCE(MAX(sort_order),-1)+1 FROM {_schema}.alliance_bulletins WHERE alliance_id=$1",
                allianceId);
            var sortOrder = Convert.ToInt32(await order.ExecuteScalarAsync(token));
            await using var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.alliance_bulletins
                    (alliance_id,bulletin_id,create_character_id,create_filetime,edit_character_id,
                     edit_filetime,title,body,sort_order)
                VALUES($1,$2,$3,$4,$3,$5,$6,$7,$8)
                """,allianceId,id,actorId,createFiletime,editFiletime,title,body,sortOrder);
            await insert.ExecuteNonQueryAsync(token);
            return id;
        },token);
    }

    public Task DeleteAllianceBulletinAsync(long allianceId,long actorId,long bulletinId,CancellationToken token = default)
    {
        if (allianceId <= 0 || actorId <= 0 || bulletinId <= 0)
            throw new CorporationError("ALLIANCE_BULLETIN_INVALID");
        return WriteAsync(async (connection,tx) =>
        {
            await using var alliance = Command(connection,tx,
                $"SELECT id FROM {_schema}.alliances WHERE id=$1 AND NOT deleted FOR UPDATE",allianceId);
            if (await alliance.ExecuteScalarAsync(token) is null)
                throw new CorporationError("ALLIANCE_NOT_FOUND");
            var character = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var corporation = await RequireRoleAsync(connection,tx,character.CorporationId,actorId,
                CorporationRoles.Director,token);
            if (corporation.AllianceId != allianceId)
                throw new CorporationError("ALLIANCE_ACCESS_DENIED");
            await using var delete = Command(connection,tx,
                $"DELETE FROM {_schema}.alliance_bulletins WHERE alliance_id=$1 AND bulletin_id=$2",
                allianceId,bulletinId);
            await delete.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public Task UpdateAllianceBulletinOrderAsync(long allianceId,long actorId,IReadOnlyList<long> bulletinIds,
        CancellationToken token = default)
    {
        if (allianceId <= 0 || actorId <= 0 || bulletinIds.Any(id => id <= 0))
            throw new CorporationError("ALLIANCE_BULLETIN_INVALID");
        var order = new Dictionary<long,int>();
        for (var index = 0; index < bulletinIds.Count; index++)
            order[bulletinIds[index]] = index;
        if (order.Count == 0) return Task.CompletedTask;
        return WriteAsync(async (connection,tx) =>
        {
            await using var alliance = Command(connection,tx,
                $"SELECT id FROM {_schema}.alliances WHERE id=$1 AND NOT deleted FOR UPDATE",allianceId);
            if (await alliance.ExecuteScalarAsync(token) is null)
                throw new CorporationError("ALLIANCE_NOT_FOUND");
            var character = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var corporation = await RequireRoleAsync(connection,tx,character.CorporationId,actorId,
                CorporationRoles.Director,token);
            if (corporation.AllianceId != allianceId)
                throw new CorporationError("ALLIANCE_ACCESS_DENIED");
            foreach (var (id,sortOrder) in order)
            {
                await using var update = Command(connection,tx,$"""
                    UPDATE {_schema}.alliance_bulletins SET sort_order=$3
                    WHERE alliance_id=$1 AND bulletin_id=$2
                    """,allianceId,id,sortOrder);
                await update.ExecuteNonQueryAsync(token);
            }
            return true;
        },token);
    }

    public async Task<bool> HasAllianceDepartureReceiptAsync(long allianceId,long corporationId,long actorId,
        string mode,CancellationToken token = default)
    {
        if (allianceId <= 0 || corporationId <= 0 || actorId <= 0 ||
            mode is not ("self-leave" or "executor-kick"))
            return false;
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT actor_id,mode FROM {_schema}.alliance_departures
            WHERE alliance_id=$1 AND corporation_id=$2
            ORDER BY left_ms DESC,membership_started_ms DESC
            LIMIT 1
            """,allianceId,corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) && reader.GetInt64(0) == actorId && reader.GetString(1) == mode;
    }

    public Task<AllianceDepartureMutation> RemoveAllianceMemberAsync(long allianceId,long corporationId,long actorId,
        string mode,long leftMs,AllianceAffiliationMutationCallback writeAffiliation,CancellationToken token = default)
    {
        if (allianceId <= 0 || corporationId <= 0 || actorId <= 0 || leftMs <= 0 ||
            mode is not ("self-leave" or "executor-kick"))
            throw new CorporationError("ALLIANCE_DEPARTURE_INVALID");
        ArgumentNullException.ThrowIfNull(writeAffiliation);
        return WriteAsync(async (connection,tx) =>
        {
            var alliance = await AllianceAsync(connection,tx,allianceId,true,token);
            if (alliance is not { Deleted:false })
                throw new CorporationError("ALLIANCE_NOT_FOUND");
            var actorCharacter = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var actorCorporation = await RequireRoleAsync(connection,tx,actorCharacter.CorporationId,actorId,
                CorporationRoles.Director,token);
            if (actorCorporation.AllianceId != allianceId ||
                mode == "self-leave" && actorCorporation.Id != corporationId ||
                mode == "executor-kick" && alliance.ActiveExecutorId != actorCorporation.Id)
                throw new CorporationError("ALLIANCE_ACCESS_DENIED");
            var corporation = await GetAsync(connection,tx,corporationId,true,token);
            var members = await AllianceMembersAsync(connection,tx,allianceId,token);
            var member = members.SingleOrDefault(row => row.CorporationId == corporationId);
            if (member is null)
            {
                await using var previous = Command(connection,tx,$"""
                    SELECT actor_id,mode FROM {_schema}.alliance_departures
                    WHERE alliance_id=$1 AND corporation_id=$2
                    ORDER BY left_ms DESC,membership_started_ms DESC
                    LIMIT 1 FOR UPDATE
                    """,allianceId,corporationId);
                await using var reader = await previous.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token) && reader.GetInt64(0) == actorId && reader.GetString(1) == mode)
                    return new AllianceDepartureMutation(null,alliance,alliance,true);
                throw new CorporationError("ALLIANCE_MEMBER_NOT_FOUND");
            }
            if (corporation is null || corporation.AllianceId != allianceId)
                throw new CorporationError("ALLIANCE_MEMBERSHIP_CHANGED");

            await using (var prior = Command(connection,tx,$"""
                SELECT actor_id,mode FROM {_schema}.alliance_departures
                WHERE alliance_id=$1 AND corporation_id=$2 AND membership_started_ms=$3
                FOR UPDATE
                """,allianceId,corporationId,member.StartedMs))
            await using (var reader = await prior.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                {
                    if (reader.GetInt64(0) == actorId && reader.GetString(1) == mode)
                        return new AllianceDepartureMutation(member,alliance,alliance,true);
                    throw new CorporationError("ALLIANCE_DEPARTURE_CONFLICT");
                }
            }

            await using (var receipt = Command(connection,tx,$"""
                INSERT INTO {_schema}.alliance_departures
                    (alliance_id,corporation_id,membership_started_ms,actor_id,mode,left_ms)
                VALUES($1,$2,$3,$4,$5,$6)
                """,allianceId,corporationId,member.StartedMs,actorId,mode,leftMs))
                await receipt.ExecuteNonQueryAsync(token);
            await using (var history = Command(connection,tx,$"""
                UPDATE {_schema}.alliance_employment SET ended_ms=$3
                WHERE corporation_id=$1 AND alliance_id=$2 AND ended_ms IS NULL
                """,corporationId,allianceId,leftMs))
                await history.ExecuteNonQueryAsync(token);
            await using (var history = Command(connection,tx,$"""
                INSERT INTO {_schema}.alliance_employment(corporation_id,alliance_id,started_ms)
                VALUES($1,NULL,$2)
                """,corporationId,leftMs))
                await history.ExecuteNonQueryAsync(token);
            await using (var affiliation = Command(connection,tx,
                $"UPDATE {_schema}.corporations SET alliance_id=NULL WHERE id=$1 AND alliance_id=$2",
                corporationId,allianceId))
                if (await affiliation.ExecuteNonQueryAsync(token) != 1)
                    throw new CorporationError("ALLIANCE_MEMBERSHIP_CHANGED");
            await _characters.SetCorporationAllianceAsync(connection,tx,corporationId,null,token);
            await writeAffiliation(connection,tx,corporationId,null,token);
            await using (var applications = Command(connection,tx,$"""
                UPDATE {_schema}.alliance_applications
                SET status=4,archived=true,due_ms=NULL,updated_ms=$3
                WHERE alliance_id=$1 AND corporation_id=$2 AND NOT archived
                """,allianceId,corporationId,leftMs))
                await applications.ExecuteNonQueryAsync(token);
            await using (var remove = Command(connection,tx,
                $"DELETE FROM {_schema}.alliance_members WHERE alliance_id=$1 AND corporation_id=$2",
                allianceId,corporationId))
                if (await remove.ExecuteNonQueryAsync(token) != 1)
                    throw new CorporationError("ALLIANCE_MEMBER_NOT_FOUND");
            await using (var clearVotes = Command(connection,tx,$"""
                UPDATE {_schema}.alliance_members SET chosen_executor_id=NULL
                WHERE alliance_id=$1 AND chosen_executor_id=$2
                """,allianceId,corporationId))
                await clearVotes.ExecuteNonQueryAsync(token);
            var remaining = await AllianceMembersAsync(connection,tx,allianceId,token);
            if (remaining.Count == 0)
            {
                await using var close = Command(connection,tx,$"""
                    UPDATE {_schema}.alliances
                    SET executor_corporation_id=NULL,suspended=true,deleted=true
                    WHERE id=$1
                    """,allianceId);
                await close.ExecuteNonQueryAsync(token);
            }
            else
            {
                await ReelectAsync(connection,tx,alliance,token);
            }
            var after = await AllianceAsync(connection,tx,allianceId,false,token);
            return new AllianceDepartureMutation(member,alliance,after,false);
        },token);
    }

    public async Task<IReadOnlyList<CorporationBill>> AllianceBillsAsync(long allianceId,
        CancellationToken token = default)
    {
        if (allianceId <= 0) return [];
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT bill_id,bill_type_id,amount_isk,interest,debtor_alliance_id,creditor_id,due_filetime,
                   external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key
            FROM {_schema}.alliance_bills
            WHERE debtor_alliance_id=$1 AND NOT paid
            ORDER BY due_filetime,bill_id
            """,allianceId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var bills = new List<CorporationBill>();
        while (await reader.ReadAsync(token)) bills.Add(ReadCorporationBill(reader));
        return bills;
    }

    public async Task<IReadOnlyList<CorporationBill>> AllianceBillsReceivableAsync(long allianceId,
        CancellationToken token = default)
    {
        if (allianceId <= 0) return [];
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT bill_id,bill_type_id,amount_isk,interest,debtor_alliance_id,creditor_id,due_filetime,
                   external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key
            FROM {_schema}.alliance_bills
            WHERE creditor_id=$1 AND NOT paid
            ORDER BY due_filetime,bill_id
            """,allianceId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var bills = new List<CorporationBill>();
        while (await reader.ReadAsync(token)) bills.Add(ReadCorporationBill(reader));
        return bills;
    }

    public Task<decimal?> CorporationWalletBalanceAsync(long corporationId,int accountKey,
        CancellationToken token = default) =>
        corporationId > 0 && accountKey is >= 1000 and <= 1006
            ? _wallets.AccountBalanceAsync(new Eve.Wallet.WalletAccount(corporationId,accountKey),token)
            : Task.FromResult<decimal?>(null);

    public Task<(long WarId,long BillId,bool Replayed)> CreateAllianceWarDeclarationAsync(
        long allianceId,long declaredByCharacterId,long againstId,long? warHqId,long declaredAtFiletime,
        long startedAtFiletime,long dueFiletime,int billTypeId,decimal amount,long creditorId,string operationKey,
        Func<NpgsqlConnection,NpgsqlTransaction,long,CancellationToken,Task<long>> createWar,
        CancellationToken token = default)
    {
        if (allianceId <= 0 || declaredByCharacterId <= 0 || againstId <= 0 || againstId == allianceId ||
            declaredAtFiletime <= 0 || startedAtFiletime <= declaredAtFiletime || dueFiletime <= declaredAtFiletime ||
            billTypeId <= 0 || amount <= 0 || creditorId <= 0 || string.IsNullOrWhiteSpace(operationKey))
            throw new CorporationError("ALLIANCE_WAR_DECLARATION_INVALID");
        ArgumentNullException.ThrowIfNull(createWar);
        var fingerprint = System.Text.Json.JsonSerializer.Serialize(new
        {
            allianceId,declaredByCharacterId,againstId,warHqId,billTypeId,amount,creditorId
        });
        return WriteAsync(async (connection,tx) =>
        {
            var alliance = await AllianceAsync(connection,tx,allianceId,true,token);
            if (alliance is not { Deleted:false })
                throw new CorporationError("ALLIANCE_NOT_FOUND");
            var character = await _characters.LockAndGetAsync(connection,tx,declaredByCharacterId,token)
                ?? throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var corporation = await RequireRoleAsync(connection,tx,character.CorporationId,declaredByCharacterId,
                CorporationRoles.Director,token);
            if (corporation.AllianceId != allianceId || alliance.ActiveExecutorId != corporation.Id)
                throw new CorporationError("ALLIANCE_ACCESS_DENIED");
            await using (var prior = Command(connection,tx,$"""
                SELECT bill_id,war_id,request_fingerprint FROM {_schema}.alliance_bills
                WHERE operation_key=$1 FOR UPDATE
                """,operationKey))
            await using (var reader = await prior.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                {
                    var priorBillId = reader.GetInt64(0);
                    var priorWarId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    if (reader.GetString(2) != fingerprint || priorWarId <= 0)
                        throw new CorporationError("ALLIANCE_WAR_OPERATION_CONFLICT");
                    return (priorWarId,priorBillId,true);
                }
            }
            await using var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.alliance_bills
                    (bill_type_id,amount_isk,interest,debtor_alliance_id,creditor_id,due_filetime,external_id,
                     external_id2,war_id,operation_key,request_fingerprint)
                VALUES($1,$2,0,$3,$4,$5,$6,-1,NULL,$7,$8)
                RETURNING bill_id
                """,billTypeId,amount,allianceId,creditorId,dueFiletime,againstId,operationKey,fingerprint);
            var billId = Convert.ToInt64(await insert.ExecuteScalarAsync(token));
            var warId = await createWar(connection,tx,billId,token);
            if (warId <= 0) throw new CorporationError("ALLIANCE_WAR_DECLARATION_FAILED");
            await using var link = Command(connection,tx,
                $"UPDATE {_schema}.alliance_bills SET war_id=$2 WHERE bill_id=$1",billId,warId);
            await link.ExecuteNonQueryAsync(token);
            return (warId,billId,false);
        },token);
    }

    public async Task<bool> PayAllianceBillAsync(long billId,long allianceId,long payerCorporationId,
    long paidByOwnerId,int accountKey,long paidAtMs,CancellationToken token = default)
    {
        if (billId <= 0 || allianceId <= 0 || payerCorporationId <= 0 || paidByOwnerId <= 0 ||
            accountKey is < 1000 or > 1006 || paidAtMs < 0)
            return false;
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var alliance = await AllianceAsync(connection,transaction,allianceId,true,token);
        if (alliance is not { Deleted:false } || alliance.ActiveExecutorId != payerCorporationId)
            return false;
        var payerCharacter = await _characters.LockAndGetAsync(connection,transaction,paidByOwnerId,token)
            ?? throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var payer = await RequireRoleAsync(connection,transaction,payerCharacter.CorporationId,paidByOwnerId,
            CorporationRoles.Accountant,token);
        if (payer.Id != payerCorporationId || payer.AllianceId != allianceId)
            throw new CorporationError("ALLIANCE_ACCESS_DENIED");
        var settlement = await SettleAllianceBillAsync(connection,transaction,billId,allianceId,
            payerCorporationId,paidByOwnerId,accountKey,paidAtMs,null,token);
        if (settlement is null) return false;
        await transaction.CommitAsync(token);
        AnnounceAllianceBillPayment(settlement);
        return true;
    }

    private async Task<Eve.Wallet.WalletSettlement?> SettleAllianceBillAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long billId, long allianceId,
        long payerCorporationId, long paidByOwnerId, int accountKey, long paidAtMs,
        int? expectedBillTypeId, CancellationToken token)
    {
        var alliance = await AllianceAsync(connection, transaction, allianceId, true, token);
        if (alliance is not { Deleted: false } || alliance.ActiveExecutorId != payerCorporationId)
            return null;
        var payer = await GetAsync(connection, transaction, payerCorporationId, true, token);
        if (payer?.AllianceId != allianceId) return null;

        await using var command = Command(connection, transaction, $"""
            SELECT bill_id,bill_type_id,amount_isk,interest,debtor_alliance_id,creditor_id,due_filetime,
                   external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key
            FROM {_schema}.alliance_bills
            WHERE bill_id=$1 AND debtor_alliance_id=$2 AND NOT paid
            FOR UPDATE
            """, billId, allianceId);
        CorporationBill bill;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return null;
            bill = ReadCorporationBill(reader);
        }
        if (expectedBillTypeId is { } typeId && bill.BillTypeId != typeId) return null;

        Eve.Wallet.WalletSettlement settlement;
        try
        {
            settlement = await _wallets.TransferAsync(connection, transaction,
                new Eve.Wallet.WalletAccount(payerCorporationId, accountKey),
                new Eve.Wallet.WalletAccount(bill.CreditorId, 1000), bill.AmountIsk, 17, "Bill payment", paidAtMs,
                $"alliance-bill:{billId}:payment", "bill_payment", token);
        }
        catch (Exception error) when (error is Eve.Wallet.InsufficientFundsException or Eve.Wallet.MoneyLimitException or
            Eve.Wallet.WalletNotFoundException or Eve.Wallet.WalletOperationConflictException)
        {
            return null;
        }
        var paidFiletime = checked((paidAtMs + EveFiletimeEpochOffsetMs) * 10_000L);
        await using var mark = Command(connection, transaction, $"""
            UPDATE {_schema}.alliance_bills
            SET paid=true,paid_filetime=$2,paid_by_owner_id=$3
            WHERE bill_id=$1 AND NOT paid
            """, billId, paidFiletime, paidByOwnerId);
        if (await mark.ExecuteNonQueryAsync(token) != 1)
            throw new CorporationError("BILL_NOT_PAYABLE");
        return settlement;
    }

    private void AnnounceAllianceBillPayment(Eve.Wallet.WalletSettlement settlement)
    {
        if (settlement.Replayed) return;
        foreach (var entry in settlement.Entries.GroupBy(entry => (entry.OwnerId, entry.AccountKey)).Select(group => group.Last()))
            _wallets.AnnounceCommitted(new Eve.Wallet.WalletBalanceChange(entry.OwnerId, entry.AccountKey, entry.Balance));
    }

    private const int AllianceMaintenanceBillTypeId = 5;
    private const long AllianceMaintenanceFeePerCorporation = 2_000_000L;
    private const long AllianceMaintenancePeriodFiletime = 30L * 24 * 60 * 60 * 10_000_000;
    private const long EveFiletimeEpochOffsetMs = 11_644_473_600_000L;
    private const long ConcordCorporationId = 1_000_125;

    public Func<long, CancellationToken, Task>? AllianceMaintenanceBillWriter { get; set; }

    public async Task<IReadOnlyList<CorporationBill>> AllianceMaintenanceBillsAsync(
        long allianceId, CancellationToken token = default)
    {
        if (allianceId <= 0) return [];
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null, $"""
            SELECT bill_id,bill_type_id,amount_isk,interest,debtor_alliance_id,creditor_id,due_filetime,
                   external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key
            FROM {_schema}.alliance_bills
            WHERE debtor_alliance_id=$1 AND bill_type_id=$2 AND war_id IS NULL
            ORDER BY due_filetime,bill_id
            """, allianceId, AllianceMaintenanceBillTypeId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var bills = new List<CorporationBill>();
        while (await reader.ReadAsync(token)) bills.Add(ReadCorporationBill(reader));
        return bills;
    }

    public async Task<IReadOnlyList<CorporationBill>> PendingAllianceMaintenanceBillsAsync(
        CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null, $"""
            SELECT bill_id,bill_type_id,amount_isk,interest,debtor_alliance_id,creditor_id,due_filetime,
                   external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key
            FROM {_schema}.alliance_bills AS bill
            WHERE bill.bill_type_id=$1 AND bill.war_id IS NULL
              AND (NOT bill.paid OR NOT EXISTS (
                  SELECT 1 FROM {_schema}.alliance_bills AS next
                  WHERE next.debtor_alliance_id=bill.debtor_alliance_id
                    AND next.bill_type_id=bill.bill_type_id
                    AND next.due_filetime=bill.due_filetime+$2
              ))
            ORDER BY bill.due_filetime,bill.bill_id
            """, AllianceMaintenanceBillTypeId, AllianceMaintenancePeriodFiletime);
        await using var reader = await command.ExecuteReaderAsync(token);
        var bills = new List<CorporationBill>();
        while (await reader.ReadAsync(token)) bills.Add(ReadCorporationBill(reader));
        return bills;
    }

    public Task<CorporationBill?> EnsureInitialAllianceMaintenanceBillAsync(
        long allianceId, CancellationToken token = default) =>
        CreateAllianceMaintenanceBillAsync(allianceId, null, token);

    public Task<CorporationBill?> CreateNextAllianceMaintenanceBillAsync(
        long allianceId, long previousDueFiletime, CancellationToken token = default) =>
        previousDueFiletime <= 0
            ? Task.FromResult<CorporationBill?>(null)
            : CreateAllianceMaintenanceBillAsync(allianceId, previousDueFiletime, token);

    private Task<CorporationBill?> CreateAllianceMaintenanceBillAsync(
        long allianceId, long? previousDueFiletime, CancellationToken token)
    {
        if (allianceId <= 0) return Task.FromResult<CorporationBill?>(null);
        return WriteAsync(async (connection, transaction) =>
        {
            var alliance = await AllianceAsync(connection, transaction, allianceId, true, token);
            if (alliance is not { Deleted: false }) return null;

            var bills = new List<CorporationBill>();
            await using (var existing = Command(connection, transaction, $"""
                SELECT bill_id,bill_type_id,amount_isk,interest,debtor_alliance_id,creditor_id,due_filetime,
                       external_id,external_id2,war_id,paid,paid_filetime,paid_by_owner_id,operation_key
                FROM {_schema}.alliance_bills
                WHERE debtor_alliance_id=$1 AND bill_type_id=$2 AND war_id IS NULL
                ORDER BY due_filetime,bill_id FOR UPDATE
                """, allianceId, AllianceMaintenanceBillTypeId))
            await using (var reader = await existing.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) bills.Add(ReadCorporationBill(reader));

            if (bills.FirstOrDefault(bill => !bill.Paid) is { } unpaid) return unpaid;
            if (previousDueFiletime is null && bills.Count != 0) return null;

            var dueFiletime = previousDueFiletime is { } previousDue
                ? checked(previousDue + AllianceMaintenancePeriodFiletime)
                : checked((_now() + EveFiletimeEpochOffsetMs) * 10_000L + AllianceMaintenancePeriodFiletime);
            if (bills.FirstOrDefault(bill => bill.DueFiletime == dueFiletime) is { } existingNext)
                return existingNext;

            var memberCount = (await AllianceMembersAsync(connection, transaction, allianceId, token)).Count;
            if (memberCount == 0) return null;
            var amount = checked(memberCount * AllianceMaintenanceFeePerCorporation);
            var operationKey = $"alliance-maintenance:{allianceId}:{dueFiletime}";
            var fingerprint = System.Text.Json.JsonSerializer.Serialize(new
            {
                allianceId,
                billTypeId = AllianceMaintenanceBillTypeId,
                amount,
                dueFiletime
            });
            await using var insert = Command(connection, transaction, $"""
                INSERT INTO {_schema}.alliance_bills
                    (bill_type_id,amount_isk,interest,debtor_alliance_id,creditor_id,due_filetime,
                     external_id,external_id2,war_id,operation_key,request_fingerprint)
                VALUES($1,$2,0,$3,$4,$5,$3,-1,NULL,$6,$7)
                RETURNING bill_id
                """, AllianceMaintenanceBillTypeId, amount, allianceId, ConcordCorporationId,
                dueFiletime, operationKey, fingerprint);
            var billId = Convert.ToInt64(await insert.ExecuteScalarAsync(token));
            return new CorporationBill(billId, AllianceMaintenanceBillTypeId, amount, 0, allianceId,
                ConcordCorporationId, dueFiletime, allianceId, -1, 0, false, null, null, operationKey);
        }, token);
    }

    public async Task<bool> PayAllianceMaintenanceBillAsync(
        long billId, long allianceId, long payerCorporationId, int accountKey, long paidAtMs,
        CancellationToken token = default)
    {
        if (billId <= 0 || allianceId <= 0 || payerCorporationId <= 0 ||
            accountKey is < 1000 or > 1006 || paidAtMs < 0)
            return false;
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var settlement = await SettleAllianceBillAsync(connection, transaction, billId, allianceId,
            payerCorporationId, payerCorporationId, accountKey, paidAtMs,
            AllianceMaintenanceBillTypeId, token);
        if (settlement is null) return false;
        await transaction.CommitAsync(token);
        AnnounceAllianceBillPayment(settlement);
        return true;
    }
}
