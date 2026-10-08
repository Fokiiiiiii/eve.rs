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
        AllianceApplicationScheduleCallback? scheduleDue, CancellationToken token)
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
            if (alliance?.ActiveExecutorId is not { } executor || alliance.Deleted) throw new CorporationError("ALLIANCE_EXECUTOR_REQUIRED");
            await RequireRoleAsync(connection, tx, executor, actor, CorporationRoles.Director, token);
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

    public async Task<IReadOnlyList<(long AllianceId,long StartedMs,bool Deleted)>> CorporationAllianceHistoryAsync(long corporation,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT e.alliance_id,e.started_ms,COALESCE(a.deleted,false)
            FROM {_schema}.alliance_employment e
            LEFT JOIN {_schema}.alliances a ON a.id=e.alliance_id
            WHERE e.corporation_id=$1
            ORDER BY e.started_ms DESC,e.alliance_id DESC
            """,corporation);
        var rows = new List<(long AllianceId,long StartedMs,bool Deleted)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add((reader.GetInt64(0),reader.GetInt64(1),reader.GetBoolean(2)));
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
}
