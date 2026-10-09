using System.Data;
using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    private const int VoteWar = 1;
    private const int VoteCreateShares = 2;
    private const int VoteExpelMember = 3;
    private const int VoteGeneral = 4;
    private const int VoteLockBlueprint = 5;
    private const int VoteUnlockBlueprint = 6;
    private const long VoteFiletimeEpoch = 116444736000000000L;
    private const long VoteTicksPerDay = 864_000_000_000L;
    private const string VoteCaseColumns = "id,corporation_id,creator_id,vote_case_text,description,vote_type,options_payload,option_count,parameter,parameter1,parameter2,start_filetime,end_filetime,sanction_in_effect,sanction_expires_filetime";

    public sealed record CorporationVoteCase(long VoteCaseId,long CorporationId,long CreatorId,string VoteCaseText,
        string Description,int VoteType,byte[] OptionsMarshal,int OptionCount,long? Parameter,long? Parameter1,long? Parameter2,
        long StartDateTime,long EndDateTime,bool SanctionInEffect,long SanctionExpires);
    public sealed record CorporationVoteBallot(long ShareholderId,long OptionId,long VotingPower,long CreatedMs);
    public sealed record CorporationVoteSanction(long VoteCaseId,long CorporationId,int VoteType,long? Parameter,
        long? Parameter1,long? Parameter2,bool InEffect,long Expires);

    public long VoteNowFiletime => checked(VoteFiletimeEpoch + _now() * 10_000L);

    public async Task<long> InsertVoteCaseAsync(long corporationId,long creatorId,string voteCaseText,string description,
        int voteType,byte[] optionsMarshal,int optionCount,long? parameter,long? parameter1,long? parameter2,
        long durationDays,CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(voteCaseText) || voteType is < VoteRunForCeo or > VoteUnlockBlueprint ||
            optionsMarshal is null || optionsMarshal.Length == 0 ||
            (voteType == VoteGeneral ? optionCount is < 2 or > 50 : optionCount != 2))
            throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");

        durationDays = Math.Clamp(durationDays,1,30);
        return await WriteAsync(async (connection,tx) =>
        {
            await ResolveExpiredVotesInTransactionAsync(connection,tx,corporationId,token);
            var corporation = await GetAsync(connection,tx,corporationId,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.IsNpc || corporation.Deleted ||
                !await CanViewVotesAsync(connection,tx,corporationId,creatorId,token))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");

            long? suspendedCharacterId = null;
            long? priorCeoId = null;
            switch (voteType)
            {
                case VoteRunForCeo:
                    if (parameter != creatorId || parameter1 is not null || parameter2 is not null ||
                        !await CanRunForCEOInTransactionAsync(connection,tx,corporation,creatorId,token))
                        throw new CorporationError("CORPORATION_CEO_CANDIDATE_INELIGIBLE");
                    await using (var activeCeoVote = Command(connection,tx,$"""
                        SELECT EXISTS(
                            SELECT 1 FROM {_schema}.vote_cases v
                            WHERE v.corporation_id=$1 AND v.vote_type=$2 AND v.end_filetime>$3
                              AND NOT EXISTS(
                                  SELECT 1 FROM {_schema}.vote_case_resolutions r WHERE r.vote_case_id=v.id
                              )
                        )
                        """,corporationId,VoteRunForCeo,VoteNowFiletime))
                        if ((bool)(await activeCeoVote.ExecuteScalarAsync(token) ?? false))
                            throw new CorporationError("CORPORATION_CEO_VOTE_ALREADY_PENDING");
                    suspendedCharacterId = corporation.CeoId;
                    priorCeoId = corporation.CeoId;
                    break;
                case VoteCreateShares:
                    if (parameter is null or <= 0 || parameter1 is not null || parameter2 is not null)
                        throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");
                    break;
                case VoteExpelMember:
                    if (parameter is null or <= 0 || parameter1 is not null || parameter2 is not null ||
                        parameter == corporation.CeoId ||
                        !await IsAboveAverageShareholderAsync(connection,tx,corporationId,parameter.Value,token))
                        throw new CorporationError("CORPORATION_MEMBER_NOT_EXPULSION_ELIGIBLE");
                    var expelled = await MemberAsync(connection,tx,parameter.Value,token);
                    if (expelled is null || expelled.CorporationId != corporationId)
                        throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");
                    suspendedCharacterId = parameter.Value;
                    break;
                case VoteLockBlueprint:
                case VoteUnlockBlueprint:
                    if (parameter is null or <= 0 || parameter1 is null or <= 0 || parameter2 is null or <= 0)
                        throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");
                    if (voteType == VoteLockBlueprint && _setBlueprintLock is null)
                        throw new InvalidOperationException("CorporationStore requires setBlueprintLock for blueprint votes.");
                    break;
                case VoteWar:
                case VoteGeneral:
                    break;
            }

            if (suspendedCharacterId is { } characterId)
            {
                await using var existingSnapshot = Command(connection,tx,$"""
                    SELECT EXISTS(
                        SELECT 1 FROM {_schema}.vote_member_role_snapshots
                        WHERE corporation_id=$1 AND character_id=$2
                    )
                    """,corporationId,characterId);
                if ((bool)(await existingSnapshot.ExecuteScalarAsync(token) ?? false))
                    throw new CorporationError("CORPORATION_MEMBER_VOTE_ALREADY_PENDING");
            }

            var start = checked(VoteFiletimeEpoch + _now() * 10_000L);
            var end = checked(start + durationDays * VoteTicksPerDay);
            var expires = checked(end + 3650L * VoteTicksPerDay);
            var storedParameter = voteType == VoteRunForCeo ? creatorId : parameter;
            long voteCaseId;
            await using (var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.vote_cases
                    (corporation_id,creator_id,vote_case_text,description,vote_type,options_payload,option_count,
                     parameter,parameter1,parameter2,start_filetime,end_filetime,sanction_in_effect,sanction_expires_filetime)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,false,$13)
                RETURNING id
                """,corporationId,creatorId,voteCaseText,description,voteType,optionsMarshal,optionCount,
                    storedParameter,parameter1,parameter2,start,end,expires))
                voteCaseId = Convert.ToInt64(await insert.ExecuteScalarAsync(token),System.Globalization.CultureInfo.InvariantCulture);

            if (suspendedCharacterId is { } suspended)
            {
                await SnapshotAndSuspendVoteRolesAsync(connection,tx,voteCaseId,corporationId,suspended,priorCeoId,token);
                if (voteType == VoteRunForCeo)
                {
                    await using var suspendCeoRoles = Command(connection,tx,$"""
                        UPDATE {_schema}.corporations SET ceo_roles_suspended=true
                        WHERE id=$1 AND ceo_id=$2 AND NOT ceo_roles_suspended
                        """,corporationId,priorCeoId!.Value);
                    if (await suspendCeoRoles.ExecuteNonQueryAsync(token) != 1)
                        throw new CorporationError("CORPORATION_CEO_CHANGED");
                }
            }

            if (voteType == VoteLockBlueprint)
                await _setBlueprintLock!(connection,tx,corporationId,parameter!.Value,parameter1!.Value,parameter2!.Value,true,token);

            return voteCaseId;
        },token);
    }

    public async Task<IReadOnlyList<CorporationVoteCase>> VoteCasesAsync(long corporationId,long actorId,CancellationToken token = default)
    {
        await ResolveExpiredVotesAsync(corporationId,token);
        if (!await CanViewVotesAsync(corporationId,actorId,token)) return [];
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT {VoteCaseColumns} FROM {_schema}.vote_cases WHERE corporation_id=$1 ORDER BY id DESC",corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var cases = new List<CorporationVoteCase>();
        while (await reader.ReadAsync(token)) cases.Add(ReadVoteCase(reader));
        return cases;
    }

    public async Task<CorporationVoteCase?> VoteCaseAsync(long corporationId,long voteCaseId,long actorId,CancellationToken token = default)
    {
        await ResolveExpiredVotesAsync(corporationId,token);
        if (!await CanViewVotesAsync(corporationId,actorId,token)) return null;
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT {VoteCaseColumns} FROM {_schema}.vote_cases WHERE corporation_id=$1 AND id=$2",corporationId,voteCaseId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadVoteCase(reader) : null;
    }

    public Task<CorporationVoteBallot?> VoteBallotAsync(long corporationId,long voteCaseId,long actorId,CancellationToken token = default) =>
        VoteBallotAsyncCore(corporationId,voteCaseId,actorId,token);

    public Task<CorporationVoteBallot?> VoteBallotAsync(long voteCaseId,long actorId,CancellationToken token = default) =>
        VoteBallotAsyncCore(null,voteCaseId,actorId,token);

    private async Task<CorporationVoteBallot?> VoteBallotAsyncCore(long? expectedCorporationId,long voteCaseId,long actorId,CancellationToken token)
    {
        if (voteCaseId <= 0 || actorId <= 0) return null;

        long issuerCorporationId;
        await using (var lookupConnection = await _source.OpenConnectionAsync(token))
        {
            await using var lookup = Command(lookupConnection,null,
                $"SELECT corporation_id FROM {_schema}.vote_cases WHERE id=$1",voteCaseId);
            var found = await lookup.ExecuteScalarAsync(token);
            if (found is null || found is DBNull) return null;
            issuerCorporationId = Convert.ToInt64(found,System.Globalization.CultureInfo.InvariantCulture);
        }
        if (expectedCorporationId is { } expected && expected != issuerCorporationId) return null;

        await ResolveExpiredVotesAsync(issuerCorporationId,token);
        if (!await CanViewVotesAsync(issuerCorporationId,actorId,token)) return null;

        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT option_id,created_ms FROM {_schema}.vote_ballots
            WHERE vote_case_id=$1 AND shareholder_id=$2
            """,voteCaseId,actorId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var optionId = reader.GetInt64(0);
        var createdMs = reader.GetInt64(1);
        await reader.CloseAsync();
        var votingPower = await VoteWeightAsync(connection,null,issuerCorporationId,actorId,token);
        return new CorporationVoteBallot(actorId,optionId,votingPower,createdMs);
    }

    public async Task InsertVoteAsync(long corporationId,long voteCaseId,long actorId,long optionId,CancellationToken token = default)
    {
        if (optionId < 0) throw new CorporationError("CORPORATION_INVALID_VOTE_OPTION");
        await WriteAsync(async (connection,tx) =>
        {
            await ResolveExpiredVotesInTransactionAsync(connection,tx,corporationId,token);
            var corporation = await GetAsync(connection,tx,corporationId,true,token) ??
                throw new CorporationError("CORPORATION_NOT_FOUND");
            var character = await _characters.LockAndGetAsync(connection,tx,actorId,token);
            if (character is null || corporation.IsNpc || corporation.Deleted ||
                !await CanViewVotesAsync(connection,tx,corporationId,actorId,token))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");

            long end;
            int optionCount;
            await using (var vote = Command(connection,tx,$"""
                SELECT end_filetime,option_count FROM {_schema}.vote_cases
                WHERE corporation_id=$1 AND id=$2 FOR UPDATE
                """,corporationId,voteCaseId))
            await using (var reader = await vote.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new CorporationError("CORPORATION_VOTE_CASE_NOT_FOUND");
                end = reader.GetInt64(0);
                optionCount = reader.GetInt32(1);
            }

            if (end <= checked(VoteFiletimeEpoch + _now() * 10_000L))
                throw new CorporationError("CORPORATION_VOTE_CLOSED");
            if (optionId >= optionCount) throw new CorporationError("CORPORATION_INVALID_VOTE_OPTION");

            var votingPower = await VoteWeightAsync(connection,tx,corporationId,actorId,token);
            if (votingPower <= 0) throw new CorporationError("CORPORATION_NOT_A_SHAREHOLDER");

            await using var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.vote_ballots(vote_case_id,shareholder_id,option_id,voting_power,created_ms)
                VALUES($1,$2,$3,$4,$5)
                ON CONFLICT(vote_case_id,shareholder_id) DO UPDATE SET
                    option_id=EXCLUDED.option_id,
                    voting_power=EXCLUDED.voting_power,
                    created_ms=EXCLUDED.created_ms
                """,voteCaseId,actorId,optionId,votingPower,_now());
            await insert.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public async Task<IReadOnlyList<CorporationVoteSanction>> VoteSanctionedActionsAsync(long corporationId,long actorId,CancellationToken token = default)
    {
        var cases = await VoteCasesAsync(corporationId,actorId,token);
        var now = VoteNowFiletime;
        var actions = new List<CorporationVoteSanction>();
        await using var connection = await _source.OpenConnectionAsync(token);
        foreach (var vote in cases.Where(vote => vote.EndDateTime <= now &&
                     vote.VoteType is VoteCreateShares or VoteExpelMember or VoteUnlockBlueprint))
        {
            await using var resolution = Command(connection,null,$"""
                SELECT winning_option_id,passed
                FROM {_schema}.vote_case_resolutions
                WHERE vote_case_id=$1 AND corporation_id=$2
                """,vote.VoteCaseId,corporationId);
            await using var reader = await resolution.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.GetInt64(0) != 0 || !reader.GetBoolean(1))
                continue;
            actions.Add(new(vote.VoteCaseId,vote.CorporationId,vote.VoteType,vote.Parameter ?? 0L,
                vote.Parameter1 ?? 0L,vote.Parameter2 ?? 0L,vote.SanctionInEffect,vote.SanctionExpires));
        }
        return actions;
    }

private async Task<(long OptionId,decimal WinningWeight,decimal TotalCastWeight)> WinningVoteOptionAsync(
        NpgsqlConnection connection,NpgsqlTransaction? tx,long voteCaseId,CancellationToken token = default)
    {
        await using var command = Command(connection,tx,$"""
            WITH ballot_weights AS (
                SELECT b.option_id,
                    COALESCE(personal.shares,0)+COALESCE(corporate.shares,0) AS weight
                FROM {_schema}.vote_ballots b
                JOIN {_schema}.vote_cases vote ON vote.id=b.vote_case_id
                JOIN {_schema}.corporations issuer ON issuer.id=vote.corporation_id
                LEFT JOIN {_schema}.vote_shareholders personal
                    ON personal.corporation_id=vote.corporation_id
                   AND personal.shareholder_id=b.shareholder_id
                LEFT JOIN LATERAL (
                    SELECT owner_shares.shares
                    FROM {_schema}.vote_shareholders owner_shares
                    WHERE owner_shares.corporation_id=vote.corporation_id
                      AND owner_shares.shareholder_id=CASE
                          WHEN issuer.ceo_id=b.shareholder_id AND NOT issuer.ceo_roles_suspended THEN issuer.id
                          ELSE (
                              SELECT member_corporation.id
                              FROM {_schema}.members member
                              JOIN {_schema}.corporations member_corporation
                                ON member_corporation.id=member.corporation_id
                               AND member_corporation.ceo_id=b.shareholder_id AND NOT member_corporation.ceo_roles_suspended
                              WHERE member.character_id=b.shareholder_id
                              LIMIT 1
                          )
                      END
                ) corporate ON true
                WHERE b.vote_case_id=$1
            ),
            option_weights AS (
                SELECT option_id,SUM(weight) AS weight
                FROM ballot_weights
                WHERE weight>0
                GROUP BY option_id
            )
            SELECT option_id,weight,SUM(weight) OVER ()
            FROM option_weights
            ORDER BY weight DESC,option_id ASC
            LIMIT 1
            """,voteCaseId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? (reader.GetInt64(0),reader.GetDecimal(1),reader.GetDecimal(2))
            : (0L,0m,0m);
    }

    public async Task ActivateSanctionedVoteAsync(long corporationId,long actorId,long voteCaseId,CancellationToken token = default)
    {
        await WriteAsync<bool>(async (connection,tx) =>
        {
            await ResolveExpiredVotesInTransactionAsync(connection,tx,corporationId,token);
            var corporation = await GetAsync(connection,tx,corporationId,true,token) ??
                throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.CeoId != actorId || corporation.CeoRolesSuspended)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");

            int voteType;
            long endFiletime;
            long sanctionExpires;
            long? parameter;
            long? parameter1;
            long? parameter2;
            bool inEffect;
            await using (var query = Command(connection,tx,$"""
                SELECT vote_type,end_filetime,sanction_expires_filetime,sanction_in_effect,
                       parameter,parameter1,parameter2
                FROM {_schema}.vote_cases
                WHERE corporation_id=$1 AND id=$2 FOR UPDATE
                """,corporationId,voteCaseId))
            await using (var reader = await query.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token))
                    throw new CorporationError("CORPORATION_VOTE_CASE_NOT_FOUND");
                voteType = reader.GetInt32(0);
                endFiletime = reader.GetInt64(1);
                sanctionExpires = reader.GetInt64(2);
                inEffect = reader.GetBoolean(3);
                parameter = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                parameter1 = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                parameter2 = reader.IsDBNull(6) ? null : reader.GetInt64(6);
            }

            if (voteType is not (VoteCreateShares or VoteExpelMember or VoteUnlockBlueprint))
                throw new CorporationError("CORPORATION_VOTE_NOT_SANCTIONABLE");
            var now = VoteNowFiletime;
            if (endFiletime > now)
                throw new CorporationError("CORPORATION_SANCTION_NOT_AVAILABLE");
            if (sanctionExpires <= now)
                throw new CorporationError("CORPORATION_SANCTION_EXPIRED");
            if (inEffect)
                throw new CorporationError("CORPORATION_SANCTION_ALREADY_APPLIED");

            long winningOption;
            bool passed;
            await using (var result = Command(connection,tx,$"""
                SELECT winning_option_id,passed FROM {_schema}.vote_case_resolutions
                WHERE vote_case_id=$1 AND corporation_id=$2
                """,voteCaseId,corporationId))
            await using (var reader = await result.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token))
                    throw new CorporationError("CORPORATION_VOTE_NOT_PASSED");
                winningOption = reader.GetInt64(0);
                passed = reader.GetBoolean(1);
            }
            if (!passed || winningOption != 0)
                throw new CorporationError("CORPORATION_VOTE_NOT_PASSED");

            switch (voteType)
            {
                case VoteCreateShares:
                    if (parameter is null or <= 0)
                        throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");
                    await CreditSharesInTransactionAsync(connection,tx,corporationId,corporationId,parameter.Value,token);
                    break;
                case VoteExpelMember:
                    if (parameter is null or <= 0 || parameter == corporation.CeoId)
                        throw new CorporationError("CORPORATION_MEMBER_NOT_EXPULSION_ELIGIBLE");
                    var character = await _characters.LockAndGetAsync(connection,tx,parameter.Value,token)
                        ?? throw new CorporationError("CHARACTER_NOT_FOUND");
                    if (character.CorporationId != corporationId ||
                        await MemberAsync(connection,tx,parameter.Value,token) is null)
                        throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");
                    var npcCorporationId = Eve.Characters.CreationCatalog.ResolveSchool(
                        character.SchoolId,character.RaceId)?.CorporationId ?? 1000004;
                    var npcCorporation = await GetAsync(connection,tx,npcCorporationId,true,token);
                    if (npcCorporation is null || !npcCorporation.IsNpc || npcCorporation.Deleted)
                        throw new CorporationError("NPC_CORPORATION_REQUIRED");
                    await MoveAsync(connection,tx,character,npcCorporation.Id,null,_now(),false,token);
                    await DeleteVoteMemberRoleSnapshotAsync(connection,tx,voteCaseId,parameter.Value,token);
                    break;
                case VoteUnlockBlueprint:
                    if (parameter is null or <= 0 || parameter1 is null or <= 0 || parameter2 is null or <= 0 || _setBlueprintLock is null)
                        throw new InvalidOperationException("CorporationStore requires setBlueprintLock for blueprint votes.");
                    await _setBlueprintLock(connection,tx,corporationId,parameter.GetValueOrDefault(),parameter1.GetValueOrDefault(),parameter2.GetValueOrDefault(),false,token);
                    break;
            }

            await using var update = Command(connection,tx,$"""
                UPDATE {_schema}.vote_cases
                SET sanction_in_effect=true,sanctioned_by=$3,sanctioned_filetime=$4
                WHERE corporation_id=$1 AND id=$2 AND NOT sanction_in_effect
                """,corporationId,voteCaseId,actorId,now);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new CorporationError("CORPORATION_SANCTION_ALREADY_APPLIED");
            return true;
        },token);
    }

public async Task ResolveAllExpiredVotesAsync(CancellationToken token = default)
    {
        var now = VoteNowFiletime;
        var corporationIds = new List<long>();
        await using (var command = _source.CreateCommand($"""
            SELECT DISTINCT vote.corporation_id
            FROM {_schema}.vote_cases vote
            WHERE vote.end_filetime<=$1
              AND NOT EXISTS(
                  SELECT 1 FROM {_schema}.vote_case_resolutions resolved
                  WHERE resolved.vote_case_id=vote.id
              )
            ORDER BY vote.corporation_id
            """))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) corporationIds.Add(reader.GetInt64(0));
        }
        foreach (var corporationId in corporationIds)
            await ResolveExpiredVotesAsync(corporationId,token);
    }

private Task<bool> ResolveExpiredVotesAsync(long corporationId,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await ResolveExpiredVotesInTransactionAsync(connection,tx,corporationId,token);
            return true;
        },token);

    private async Task ResolveExpiredVotesInTransactionAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        long corporationId,CancellationToken token)
    {
        if (corporationId <= 0 || await GetAsync(connection,tx,corporationId,true,token) is null) return;
        var now = checked(VoteFiletimeEpoch + _now() * 10_000L);
        var due = new List<(long Id,int Type,long Creator,long? Parameter,long? Parameter1,long? Parameter2,long End)>();
        await using (var command = Command(connection,tx,$"""
            SELECT v.id,v.vote_type,v.creator_id,v.parameter,v.parameter1,v.parameter2,v.end_filetime
            FROM {_schema}.vote_cases v
            WHERE v.corporation_id=$1 AND v.end_filetime<=$2
              AND NOT EXISTS(
                  SELECT 1 FROM {_schema}.vote_case_resolutions r WHERE r.vote_case_id=v.id
              )
            ORDER BY v.id
            FOR UPDATE
            """,corporationId,now))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                due.Add((reader.GetInt64(0),reader.GetInt32(1),reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.GetInt64(6)));
        }

        foreach (var vote in due)
        {
            var outcome = await WinningVoteOptionAsync(connection,tx,vote.Id,token);
            var passed = outcome.OptionId == 0 && outcome.TotalCastWeight > 0m &&
                outcome.WinningWeight * 2m > outcome.TotalCastWeight;

            switch (vote.Type)
            {
                case VoteRunForCeo:
                {
                    var oldCeoId = await PriorVoteCeoAsync(connection,tx,vote.Id,token)
                        ?? throw new CorporationError("CORPORATION_VOTE_ROLE_SNAPSHOT_MISSING");
                    var current = await GetAsync(connection,tx,corporationId,false,token);
                    if (vote.Parameter != vote.Creator || current is null ||
                        current.CeoId != oldCeoId || !current.CeoRolesSuspended)
                        throw new CorporationError("CORPORATION_CEO_CHANGED");
                    if (passed)
                    {
                        var candidate = await _characters.LockAndGetAsync(connection,tx,vote.Creator,token);
                        var member = await MemberAsync(connection,tx,vote.Creator,token);
                        if (candidate is null || candidate.CorporationId != corporationId ||
                            member is null || member.CorporationId != corporationId)
                            passed = false;
                    }
                    if (passed)
                    {
                        await using (var leadership = Command(connection,tx,$"""
                            UPDATE {_schema}.corporations
                            SET ceo_id=$3,ceo_roles_suspended=false
                            WHERE id=$1 AND ceo_id=$2 AND ceo_roles_suspended
                            """,corporationId,oldCeoId,vote.Creator))
                            if (await leadership.ExecuteNonQueryAsync(token) != 1)
                                throw new CorporationError("CORPORATION_CEO_CHANGED");
                        await using (var director = Command(connection,tx,$"""
                            UPDATE {_schema}.members
                            SET roles=CASE WHEN character_id=$2 THEN roles | $4 ELSE roles & ~$4 END
                            WHERE corporation_id=$1 AND character_id IN($2,$3)
                            """,corporationId,vote.Creator,oldCeoId,CorporationRoles.Director))
                            if (await director.ExecuteNonQueryAsync(token) != 2)
                                throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");
                        await DeleteVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,oldCeoId,token);
                    }
                    else
                    {
                        await using (var restoreCeo = Command(connection,tx,$"""
                            UPDATE {_schema}.corporations SET ceo_roles_suspended=false
                            WHERE id=$1 AND ceo_id=$2 AND ceo_roles_suspended
                            """,corporationId,oldCeoId))
                            if (await restoreCeo.ExecuteNonQueryAsync(token) != 1)
                                throw new CorporationError("CORPORATION_CEO_CHANGED");
                        await RestoreVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,corporationId,oldCeoId,token);
                    }
                    break;
                }
                case VoteExpelMember:
                {
                    if (vote.Parameter is not { } expelCharacterId)
                    {
                        passed = false;
                        break;
                    }
                    var member = await MemberAsync(connection,tx,expelCharacterId,token);
                    if (passed && (member is null || member.CorporationId != corporationId))
                    {
                        passed = false;
                        await DeleteVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,expelCharacterId,token);
                    }
                    else if (!passed)
                    {
                        if (member?.CorporationId == corporationId)
                            await RestoreVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,corporationId,expelCharacterId,token);
                        else
                            await DeleteVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,expelCharacterId,token);
                    }
                    break;
                }
                case VoteLockBlueprint:
                    if (!passed)
                    {
                        if (_setBlueprintLock is null)
                            throw new InvalidOperationException("CorporationStore requires setBlueprintLock for blueprint votes.");
                        if (vote.Parameter is not { } itemId ||
                            vote.Parameter1 is not { } typeId || vote.Parameter2 is not { } locationId)
                            throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");
                        await _setBlueprintLock(connection,tx,corporationId,itemId,typeId,locationId,false,token);
                    }
                    break;
            }

            await using var resolved = Command(connection,tx,$"""
                INSERT INTO {_schema}.vote_case_resolutions
                    (vote_case_id,corporation_id,winning_option_id,passed,resolved_filetime)
                VALUES($1,$2,$3,$4,$5)
                """,vote.Id,corporationId,outcome.OptionId,passed,now);
            await resolved.ExecuteNonQueryAsync(token);
        }
    }

    private async Task<bool> CanRunForCEOInTransactionAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        Corporation corporation,long actorId,CancellationToken token)
    {
        if (actorId <= 0 || corporation.IsNpc || corporation.Deleted ||
            corporation.CeoId <= 0 || corporation.CeoRolesSuspended || corporation.CeoId == actorId)
            return false;
        var character = await _characters.LockAndGetAsync(connection,tx,actorId,token);
        var member = await MemberAsync(connection,tx,actorId,token);
        if (character?.CorporationId != corporation.Id || member?.CorporationId != corporation.Id)
            return false;

        decimal ownShares;
        decimal totalShares;
        await using (var command = Command(connection,tx,$"""
            SELECT COALESCE(SUM(shares) FILTER (WHERE shareholder_id=$2),0)::numeric,
                   COALESCE(SUM(shares),0)::numeric
            FROM {_schema}.vote_shareholders
            WHERE corporation_id=$1
            """,corporation.Id,actorId))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return false;
            ownShares = reader.GetDecimal(0);
            totalShares = reader.GetDecimal(1);
        }
        if (totalShares <= 0m || ownShares * 20m <= totalShares) return false;
        const int corporationManagementSkillTypeId = 3363;
        return await _skills.LevelAsync(connection,tx,actorId,corporationManagementSkillTypeId,token) >= 1;
    }

    private async Task<bool> IsAboveAverageShareholderAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        long corporationId,long shareholderId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            SELECT COALESCE((
                SELECT shareholder.shares::numeric*totals.holder_count>totals.total_shares
                FROM {_schema}.vote_shareholders shareholder
                CROSS JOIN (
                    SELECT COALESCE(SUM(shares),0)::numeric AS total_shares,COUNT(*)::numeric AS holder_count
                    FROM {_schema}.vote_shareholders WHERE corporation_id=$1
                ) totals
                WHERE shareholder.corporation_id=$1 AND shareholder.shareholder_id=$2
                  AND totals.holder_count>0
            ),false)
            """,corporationId,shareholderId);
        return (bool)(await command.ExecuteScalarAsync(token) ?? false);
    }

    private async Task SnapshotAndSuspendVoteRolesAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long voteCaseId,
        long corporationId,long characterId,long? priorCeoId,CancellationToken token)
    {
        await using (var snapshot = Command(connection,tx,$"""
            INSERT INTO {_schema}.vote_member_role_snapshots
                (vote_case_id,corporation_id,character_id,prior_ceo_id,roles,grantable_roles,
                 roles_hq,grantable_hq,roles_base,grantable_base,roles_other,grantable_other,title_mask,role_stasis_ms)
            SELECT $1,$2,character_id,$4,roles,grantable_roles,
                   roles_hq,grantable_hq,roles_base,grantable_base,roles_other,grantable_other,title_mask,role_stasis_ms
            FROM {_schema}.members
            WHERE corporation_id=$2 AND character_id=$3
            """,voteCaseId,corporationId,characterId,priorCeoId))
            if (await snapshot.ExecuteNonQueryAsync(token) != 1)
                throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");

        await using var suspend = Command(connection,tx,$"""
            UPDATE {_schema}.members
            SET roles=0,grantable_roles=0,roles_hq=0,grantable_hq=0,
                roles_base=0,grantable_base=0,roles_other=0,grantable_other=0,title_mask=0
            WHERE corporation_id=$1 AND character_id=$2
            """,corporationId,characterId);
        if (await suspend.ExecuteNonQueryAsync(token) != 1)
            throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");
    }

    private async Task<long?> PriorVoteCeoAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long voteCaseId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            SELECT prior_ceo_id FROM {_schema}.vote_member_role_snapshots
            WHERE vote_case_id=$1
            """,voteCaseId);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task RestoreVoteMemberRoleSnapshotAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long voteCaseId,
        long corporationId,long characterId,CancellationToken token)
    {
        await using (var restore = Command(connection,tx,$"""
            UPDATE {_schema}.members m
            SET roles=s.roles,grantable_roles=s.grantable_roles,
                roles_hq=s.roles_hq,grantable_hq=s.grantable_hq,
                roles_base=s.roles_base,grantable_base=s.grantable_base,
                roles_other=s.roles_other,grantable_other=s.grantable_other,
                title_mask=s.title_mask,role_stasis_ms=s.role_stasis_ms
            FROM {_schema}.vote_member_role_snapshots s
            WHERE s.vote_case_id=$1 AND s.corporation_id=$2 AND s.character_id=$3
              AND m.corporation_id=s.corporation_id AND m.character_id=s.character_id
            """,voteCaseId,corporationId,characterId))
            await restore.ExecuteNonQueryAsync(token);
        await DeleteVoteMemberRoleSnapshotAsync(connection,tx,voteCaseId,characterId,token);
    }

    private async Task DeleteVoteMemberRoleSnapshotAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long voteCaseId,
        long characterId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            DELETE FROM {_schema}.vote_member_role_snapshots
            WHERE vote_case_id=$1 AND character_id=$2
            """,voteCaseId,characterId);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<long> VoteWeightAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long corporationId,
        long actorId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            WITH issuer AS (
                SELECT id,ceo_id,ceo_roles_suspended FROM {_schema}.corporations WHERE id=$1
            ),
            corporate_owner AS (
                SELECT CASE WHEN issuer.ceo_id=$2 AND NOT issuer.ceo_roles_suspended THEN issuer.id ELSE (
                    SELECT owned.id
                    FROM {_schema}.members member
                    JOIN {_schema}.corporations owned
                      ON owned.id=member.corporation_id AND owned.ceo_id=$2 AND NOT owned.ceo_roles_suspended
                    WHERE member.character_id=$2
                    LIMIT 1
                ) END AS owner_id
                FROM issuer
            )
            SELECT COALESCE(personal.shares,0)+COALESCE(corporate.shares,0)
            FROM issuer
            LEFT JOIN {_schema}.vote_shareholders personal
              ON personal.corporation_id=issuer.id AND personal.shareholder_id=$2
            LEFT JOIN corporate_owner ON true
            LEFT JOIN {_schema}.vote_shareholders corporate
              ON corporate.corporation_id=issuer.id AND corporate.shareholder_id=corporate_owner.owner_id
            """,corporationId,actorId);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? 0L : Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task CreditSharesInTransactionAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long corporationId,
        long shareholderId,long amount,CancellationToken token)
    {
        if (amount <= 0) throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");
        var current = await ShareCountAsync(connection,tx,corporationId,shareholderId,token);
        if (current > long.MaxValue-amount) throw new CorporationError("CORPORATION_SHARE_LIMIT");
        await using var command = Command(connection,tx,$"""
            INSERT INTO {_schema}.vote_shareholders(corporation_id,shareholder_id,shares)
            VALUES($1,$2,$3)
            ON CONFLICT(corporation_id,shareholder_id)
            DO UPDATE SET shares={_schema}.vote_shareholders.shares+EXCLUDED.shares
            """,corporationId,shareholderId,amount);
        await command.ExecuteNonQueryAsync(token);
    }

    public Task<bool> CanRunForCEOAsync(long corporationId,long actorId,CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0) return Task.FromResult(false);
        return WriteAsync(async (connection,tx) =>
        {
            var corporation = await GetAsync(connection,tx,corporationId,false,token);
            return corporation is not null &&
                await CanRunForCEOInTransactionAsync(connection,tx,corporation,actorId,token);
        },token);
    }

    private static CorporationVoteCase ReadVoteCase(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetString(3),reader.GetString(4),
        reader.GetInt32(5),reader.GetFieldValue<byte[]>(6),reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10),reader.GetInt64(11),reader.GetInt64(12),
        reader.GetBoolean(13),reader.GetInt64(14));
}
