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
    public async Task SetVoteShareholderAsync(long corporationId,long shareholderId,long shares,CancellationToken token = default)
    {
        if (corporationId <= 0 || shareholderId <= 0 || shares < 0) throw new CorporationError("CORPORATION_INVALID_SHAREHOLDER");
        await WriteAsync(async (connection,tx) =>
        {
            if (await GetAsync(connection,tx,corporationId,true,token) is null) throw new CorporationError("CORPORATION_NOT_FOUND");
            if (shares == 0)
            {
                await using var delete = Command(connection,tx,$"DELETE FROM {_schema}.vote_shareholders WHERE corporation_id=$1 AND shareholder_id=$2",corporationId,shareholderId);
                await delete.ExecuteNonQueryAsync(token);
            }
            else
            {
                await using var upsert = Command(connection,tx,$"""
                    INSERT INTO {_schema}.vote_shareholders(corporation_id,shareholder_id,shares)
                    VALUES($1,$2,$3)
                    ON CONFLICT(corporation_id,shareholder_id) DO UPDATE SET shares=EXCLUDED.shares
                    """,corporationId,shareholderId,shares);
                await upsert.ExecuteNonQueryAsync(token);
            }
            return true;
        },token);
    }


    public async Task<IReadOnlyList<CorporationShareholding>> SharesByShareholderAsync(
        long ownerId,long? corporationFilter,CancellationToken token = default)
    {
        if (ownerId <= 0 || corporationFilter is <= 0)
            throw new CorporationError("CORPORATION_INVALID_SHAREHOLDER");

        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = corporationFilter is { } issuer
            ? Command(connection,null,$"""
                SELECT shareholder_id,corporation_id,shares
                FROM {_schema}.vote_shareholders
                WHERE shareholder_id=$1 AND corporation_id=$2
                ORDER BY corporation_id
                """,ownerId,issuer)
            : Command(connection,null,$"""
                SELECT shareholder_id,corporation_id,shares
                FROM {_schema}.vote_shareholders
                WHERE shareholder_id=$1
                ORDER BY corporation_id
                """,ownerId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<CorporationShareholding>();
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2)));
        return result;
    }

    public async Task<IReadOnlyList<CorporationShareholding>> ShareholdersAsync(
        long corporationId,CancellationToken token = default)
    {
        if (corporationId <= 0) throw new CorporationError("CORPORATION_INVALID_SHAREHOLDER");
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT shareholder_id,corporation_id,shares
            FROM {_schema}.vote_shareholders
            WHERE corporation_id=$1
            ORDER BY shareholder_id
            """,corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<CorporationShareholding>();
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2)));
        return result;
    }

    public async Task<Eve.Wallet.WalletSettlement> DistributeDividendAsync(
        long corporationId,IReadOnlyList<Eve.Wallet.WalletAllocation> recipients,string operationKey,CancellationToken token = default)
    {
        if (corporationId <= 0 || recipients.Count == 0 || string.IsNullOrWhiteSpace(operationKey))
            throw new CorporationError("CORPORATION_INVALID_DIVIDEND");
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var settlement = await _wallets.DistributeAsync(connection,transaction,new Eve.Wallet.WalletAccount(corporationId,1000),
            recipients,37,"Dividend",_now(),operationKey,$"corporation:{corporationId}",token);
        await transaction.CommitAsync(token);
        if (!settlement.Replayed)
        {
            foreach (var entry in settlement.Entries.GroupBy(entry => (entry.OwnerId,entry.AccountKey)).Select(group => group.Last()))
                _wallets.AnnounceCommitted(new Eve.Wallet.WalletBalanceChange(entry.OwnerId,entry.AccountKey,entry.Balance));
        }
        return settlement;
    }

    public Task MoveCompanySharesAsync(long corporationId,long actorId,long toOwnerId,long amount,string? operationKey,
        CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0 || toOwnerId <= 0 || amount <= 0)
            throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");
        var stableKey = ShareOperationKey(operationKey);
        var fingerprint = ShareOperationFingerprint("companyShares",corporationId,actorId,new { toOwnerId,amount });
        return WriteAsync(async (connection,tx) =>
        {
            if (await ShareOperationWasAppliedAsync(connection,tx,stableKey,fingerprint,token)) return true;
            var actor = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corporation = await GetAsync(connection,tx,corporationId,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.IsNpc || corporation.Deleted || actor.CorporationId != corporationId)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var member = await MemberAsync(connection,tx,actorId,token);
            if (member is null) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var roles = CorporationRoles.Session(corporation,member,actor.StationId,
                await TitlesAsync(connection,tx,corporationId,token)).AtAll;
            var administrator = corporation.CeoId == actorId && !corporation.CeoRolesSuspended;
            if (!administrator && (roles & CorporationRoles.Director) == 0)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            await ValidateShareOwnerAsync(connection,tx,toOwnerId,token);
            await MoveShareBalanceAsync(connection,tx,corporationId,corporationId,toOwnerId,amount,token);
            await RecordShareOperationAsync(connection,tx,stableKey,fingerprint,token);
            return true;
        },token);
    }

    public Task MovePrivateSharesAsync(long corporationId,long actorId,long toOwnerId,long amount,string? operationKey,
        CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0 || toOwnerId <= 0 || amount <= 0 || actorId == toOwnerId)
            throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");
        var stableKey = ShareOperationKey(operationKey);
        var fingerprint = ShareOperationFingerprint("privateShares",corporationId,actorId,new { toOwnerId,amount });
        return WriteAsync(async (connection,tx) =>
        {
            if (await ShareOperationWasAppliedAsync(connection,tx,stableKey,fingerprint,token)) return true;
            _ = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corporation = await GetAsync(connection,tx,corporationId,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.IsNpc || corporation.Deleted)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            await ValidateShareOwnerAsync(connection,tx,toOwnerId,token);
            await MoveShareBalanceAsync(connection,tx,corporationId,actorId,toOwnerId,amount,token);
            await RecordShareOperationAsync(connection,tx,stableKey,fingerprint,token);
            return true;
        },token);
    }

    public Task ExecuteMemberGrantsAsync(long corporationId,long actorId,IReadOnlyList<long> targetIds,
        IReadOnlyList<CorporationMemberGrant> grants,int walletAccountKey,string? operationKey,
        CancellationToken token = default)
    {
        if (targetIds.Count == 0 || grants.Count == 0) throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
        if (corporationId <= 0 || actorId <= 0 ||
            targetIds.Any(id => id <= 0) || targetIds.Distinct().Count() != targetIds.Count)
            throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
        var hasCash = grants.Any(grant => grant.Property == 6);
        if (hasCash && walletAccountKey is < 1000 or > 1006)
            throw new CorporationError("CORPORATION_INVALID_WALLET_ACCOUNT");
        foreach (var grant in grants)
        {
            if (grant.Property == 6)
            {
                if (grant.Amount <= 0 || decimal.Round(grant.Amount,2) != grant.Amount)
                    throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
            }
            else if (grant.Property == 7)
            {
                if (grant.Amount <= 0 || decimal.Truncate(grant.Amount) != grant.Amount || grant.Amount > long.MaxValue)
                    throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
            }
            else
                throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
        }

        var stableKey = ShareOperationKey(operationKey);
        var fingerprint = ShareOperationFingerprint("memberGrants",corporationId,actorId,
            new { targetIds,grants,walletAccountKey });
        return WriteAsync(async (connection,tx) =>
        {
            if (await ShareOperationWasAppliedAsync(connection,tx,stableKey,fingerprint,token)) return true;
            var actor = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corporation = await GetAsync(connection,tx,corporationId,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.IsNpc || corporation.Deleted || actor.CorporationId != corporationId)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var actorMember = await MemberAsync(connection,tx,actorId,token);
            if (actorMember is null) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var roles = CorporationRoles.Session(corporation,actorMember,actor.StationId,
                await TitlesAsync(connection,tx,corporationId,token)).AtAll;
            var ceoAdministrator = corporation.CeoId == actorId && !corporation.CeoRolesSuspended;
            var administrator = ceoAdministrator || (roles & CorporationRoles.Director) != 0;
            if (grants.Any(grant => grant.Property == 7) && !administrator)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            if (hasCash && !administrator &&
                (roles & CorporationRoles.WalletTakeRole(walletAccountKey)) == 0)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");

            foreach (var targetId in targetIds.Order())
            {
                var target = await _characters.LockAndGetAsync(connection,tx,targetId,token)
                    ?? throw new CorporationError("CHARACTER_NOT_FOUND");
                if (target.CorporationId != corporationId ||
                    await MemberAsync(connection,tx,targetId,token) is null)
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
            }

            var now = _now();
            for (var targetIndex = 0; targetIndex < targetIds.Count; targetIndex++)
            {
                var targetId = targetIds[targetIndex];
                for (var grantIndex = 0; grantIndex < grants.Count; grantIndex++)
                {
                    var grant = grants[grantIndex];
                    if (grant.Property == 6)
                    {
                        var key = $"{stableKey}:cash:{targetId}:{grantIndex}";
                        await _wallets.TransferAsync(connection,tx,new WalletAccount(corporationId,walletAccountKey),
                            new WalletAccount(targetId),grant.Amount,CorporationMemberCashGrantRefTypeId,"Corporation member grant",now,key,
                            $"member-grant:{corporationId}:{actorId}:{targetId}:{grantIndex}",token);
                    }
                    else
                    {
                        await MoveShareBalanceAsync(connection,tx,corporationId,corporationId,targetId,
                            checked((long)grant.Amount),token);
                    }
                }
            }
            await RecordShareOperationAsync(connection,tx,stableKey,fingerprint,token);
            return true;
        },token);
    }

private static string ShareOperationKey(string? operationKey) =>
        string.IsNullOrWhiteSpace(operationKey) ? Guid.NewGuid().ToString("N") : operationKey;

    private static string ShareOperationFingerprint(string kind,long corporationId,long actorId,object request) =>
        JsonSerializer.Serialize(new { kind,corporationId,actorId,request });

    private async Task<bool> ShareOperationWasAppliedAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        string operationKey,string fingerprint,CancellationToken token)
    {
        await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))",connection,tx))
        {
            advisory.Parameters.Add(new NpgsqlParameter { Value = "corporation-share:" + _schema + ":" + operationKey });
            await advisory.ExecuteNonQueryAsync(token);
        }
        await using var command = Command(connection,tx,$"""
            SELECT request_fingerprint FROM {_schema}.share_operation_receipts
            WHERE operation_key=$1 FOR UPDATE
            """,operationKey);
        var previous = await command.ExecuteScalarAsync(token);
        if (previous is null or DBNull) return false;
        if (!string.Equals((string)previous,fingerprint,StringComparison.Ordinal))
            throw new CorporationError("CORPORATION_OPERATION_CONFLICT");
        return true;
    }

    private async Task RecordShareOperationAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        string operationKey,string fingerprint,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            INSERT INTO {_schema}.share_operation_receipts(operation_key,request_fingerprint,created_ms)
            VALUES($1,$2,$3)
            """,operationKey,fingerprint,_now());
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task ValidateShareOwnerAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long ownerId,CancellationToken token)
    {
        if (await _characters.LockAndGetAsync(connection,tx,ownerId,token) is not null) return;
        var corporation = await GetAsync(connection,tx,ownerId,true,token);
        if (corporation is null || corporation.Deleted)
            throw new CorporationError("CORPORATION_SHAREHOLDER_NOT_FOUND");
    }

    private async Task<long> ShareCountAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long corporationId,long ownerId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            SELECT COALESCE((SELECT shares FROM {_schema}.vote_shareholders
                WHERE corporation_id=$1 AND shareholder_id=$2),0)::bigint
            """,corporationId,ownerId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token),System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task MoveShareBalanceAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long corporationId,
        long fromOwnerId,long toOwnerId,long amount,CancellationToken token)
    {
        if (corporationId <= 0 || fromOwnerId <= 0 || toOwnerId <= 0 || amount <= 0 || fromOwnerId == toOwnerId)
            throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");

        long fromShares = 0;
        long toShares = 0;
        await using (var command = Command(connection,tx,$"""
            SELECT shareholder_id,shares
            FROM {_schema}.vote_shareholders
            WHERE corporation_id=$1 AND shareholder_id IN ($2,$3)
            ORDER BY shareholder_id
            FOR UPDATE
            """,corporationId,fromOwnerId,toOwnerId))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                if (reader.GetInt64(0) == fromOwnerId) fromShares = reader.GetInt64(1);
                else toShares = reader.GetInt64(1);
            }
        }
        if (fromShares < amount) throw new CorporationError("CORPORATION_INSUFFICIENT_SHARES");
        if (toShares > long.MaxValue - amount) throw new CorporationError("CORPORATION_SHARE_LIMIT");

        if (fromShares == amount)
        {
            await using var remove = Command(connection,tx,$"""
                DELETE FROM {_schema}.vote_shareholders
                WHERE corporation_id=$1 AND shareholder_id=$2
                """,corporationId,fromOwnerId);
            await remove.ExecuteNonQueryAsync(token);
        }
        else
        {
            await using var debit = Command(connection,tx,$"""
                UPDATE {_schema}.vote_shareholders SET shares=shares-$3
                WHERE corporation_id=$1 AND shareholder_id=$2 AND shares >= $3
                """,corporationId,fromOwnerId,amount);
            if (await debit.ExecuteNonQueryAsync(token) != 1)
                throw new CorporationError("CORPORATION_INSUFFICIENT_SHARES");
        }

        await using var credit = Command(connection,tx,$"""
            INSERT INTO {_schema}.vote_shareholders(corporation_id,shareholder_id,shares)
            VALUES($1,$2,$3)
            ON CONFLICT (corporation_id,shareholder_id)
            DO UPDATE SET shares={_schema}.vote_shareholders.shares+EXCLUDED.shares
            """,corporationId,toOwnerId,amount);
        await credit.ExecuteNonQueryAsync(token);
    }

public async Task<bool> CanViewVotesAsync(long corporationId,long actorId,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        return await CanViewVotesAsync(connection,null,corporationId,actorId,token);
    }

    private async Task<bool> CanViewVotesAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long corporationId,long actorId,CancellationToken token)
    {
        if (corporationId <= 0 || actorId <= 0) return false;
        var corporation = await GetAsync(connection,tx,corporationId,false,token);
        if (corporation is null) return false;
        if (corporation.CeoId == actorId && !corporation.CeoRolesSuspended) return true;
        await using var access = Command(connection,tx,$"""
            SELECT EXISTS(
                SELECT 1 FROM {_schema}.members
                WHERE character_id=$1 AND corporation_id=$2 AND roles<>0)
               OR EXISTS(
                SELECT 1 FROM {_schema}.vote_shareholders
                WHERE corporation_id=$2 AND shareholder_id=$1 AND shares>0)
               OR EXISTS(
                SELECT 1
                FROM {_schema}.members m
                JOIN {_schema}.corporations owned ON owned.id=m.corporation_id AND owned.ceo_id=$1 AND NOT owned.ceo_roles_suspended
                JOIN {_schema}.vote_shareholders s ON s.corporation_id=$2 AND s.shareholder_id=owned.id AND s.shares>0
                WHERE m.character_id=$1)
            """,actorId,corporationId);
        return (bool)(await access.ExecuteScalarAsync(token) ?? false);
    }
}
