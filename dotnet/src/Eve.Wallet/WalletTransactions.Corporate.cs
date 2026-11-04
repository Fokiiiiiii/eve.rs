using System.Text.Json;
using Npgsql;

namespace Eve.Wallet;

public readonly record struct WalletAccount(long OwnerId, int AccountKey = 1000);
public sealed record WalletAllocation(WalletAccount Account, decimal Amount);
public sealed record WalletLeg(WalletAccount Account, decimal Amount, long CounterpartyId, int RefTypeId, string Reason,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? ReferenceId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? ReferenceType = null);
public sealed record WalletSettlement(string OperationKey, IReadOnlyList<JournalEntry> Entries,
    [property: System.Text.Json.Serialization.JsonIgnore] bool Replayed = false);

public sealed partial class WalletTransactions
{
    public async Task OpenCorporationDivisionsAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,long corporationId,CancellationToken token = default)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {_schema}.balances(owner_id,account_key,isk,plex) SELECT $1,key,0,0 FROM generate_series(1001,1006) key ON CONFLICT DO NOTHING",connection,transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = corporationId });
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<decimal?> AccountBalanceAsync(WalletAccount account,CancellationToken token = default)
    {
        Validate(account);
        await using var command = _source.CreateCommand($"SELECT isk FROM {_schema}.balances WHERE owner_id=$1 AND account_key=$2");
        command.Parameters.Add(new NpgsqlParameter { Value = account.OwnerId });
        command.Parameters.Add(new NpgsqlParameter { Value = account.AccountKey });
        return await command.ExecuteScalarAsync(token) is decimal balance ? balance : null;
    }

    public async Task<IReadOnlyDictionary<WalletAccount,decimal>> LockBalancesAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,
        IEnumerable<WalletAccount> accounts,CancellationToken token = default) =>
        await LockAccountsAsync(connection,transaction,accounts,token);

    /// <summary>Credit, debit, reserve or refund a specific account; overflow is refused, never clipped.</summary>
    public Task<WalletSettlement> AdjustAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,WalletAccount account,
        decimal delta,long counterparty,int refTypeId,string reason,long timeMs,string operationKey,string reference,CancellationToken token = default,long? referenceId = null,int? referenceType = null) =>
        SettleAsync(connection,transaction,[new(account,delta,counterparty,refTypeId,reason,referenceId,referenceType)],timeMs,operationKey,"adjustment:" + reference,token,conserve:false);

    public Task<WalletSettlement> TransferAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,WalletAccount from,WalletAccount to,
        decimal amount,int refTypeId,string reason,long timeMs,string operationKey,string reference,CancellationToken token = default,long? referenceId = null,int? referenceType = null)
    {
        Positive(amount);
        if (from == to) throw new ArgumentException("a transfer needs two different accounts");
        return SettleAsync(connection,transaction,
            [new(from,-amount,to.OwnerId,refTypeId,reason,referenceId,referenceType),new(to,amount,from.OwnerId,refTypeId,reason,referenceId,referenceType)],
            timeMs,operationKey,"transfer:" + reference,token);
    }

    public Task<WalletSettlement> DistributeAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,WalletAccount source,
        IReadOnlyList<WalletAllocation> recipients,int refTypeId,string reason,long timeMs,string operationKey,string reference,CancellationToken token = default)
    {
        if (recipients.Count == 0) throw new ArgumentException("a distribution needs recipients",nameof(recipients));
        foreach (var recipient in recipients) Positive(recipient.Amount);
        var total = recipients.Sum(r => r.Amount);
        var legs = new List<WalletLeg> { new(source,-total,0,refTypeId,reason) };
        legs.AddRange(recipients.Select(r => new WalletLeg(r.Account,r.Amount,source.OwnerId,refTypeId,reason)));
        return SettleAsync(connection,transaction,legs,timeMs,operationKey,"distribution:" + reference,token);
    }

    public async Task<WalletSettlement> LiquidateAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,long corporationId,long characterId,
        int refTypeId,string reason,long timeMs,string operationKey,string reference,CancellationToken token = default)
    {
        if (corporationId == characterId) throw new ArgumentException("liquidation needs a different recipient");
        var context = JsonSerializer.Serialize(new { Kind="liquidation",corporationId,characterId,refTypeId,reason,reference });
        await LockOperationAsync(connection,transaction,operationKey,token);
        var previous = await ReceiptAsync(connection,transaction,operationKey,token);
        if (previous is not null)
        {
            if (previous.Value.Context != context) throw new WalletOperationConflictException(operationKey);
            return previous.Value.Result with { Replayed = true };
        }
        var accounts = Enumerable.Range(1000,7).Select(key => new WalletAccount(corporationId,key)).Append(new WalletAccount(characterId)).ToArray();
        var balances = await LockAccountsAsync(connection,transaction,accounts,token);
        var legs = new List<WalletLeg>();
        foreach (var account in accounts.Where(a => a.OwnerId == corporationId))
        {
            if (balances[account] < 0) throw new InsufficientFundsException(corporationId);
            if (balances[account] > 0) legs.Add(new WalletLeg(account,-balances[account],characterId,refTypeId,reason));
        }
        var payout = -legs.Sum(l => l.Amount);
        if (payout > 0) legs.Add(new WalletLeg(new WalletAccount(characterId),payout,corporationId,refTypeId,reason));
        return await SettleAsync(connection,transaction,legs,timeMs,operationKey,context,token);
    }

    private static void Validate(WalletAccount account)
    {
        if (account.OwnerId <= 0 || account.AccountKey is < 1000 or > 1006) throw new ArgumentOutOfRangeException(nameof(account));
    }
    private static void Positive(decimal amount)
    {
        if (amount <= 0 || decimal.Round(amount,2) != amount) throw new ArgumentOutOfRangeException(nameof(amount));
    }
    private async Task LockOperationAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string key,CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))",connection,transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = "wallet:" + _schema + ":" + key });
        await command.ExecuteNonQueryAsync(token);
    }
    private async Task<(string Context,string Fingerprint,WalletSettlement Result)?> ReceiptAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string key,CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT context,fingerprint,result FROM {_schema}.settlements WHERE operation_key=$1",connection,transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetString(0),reader.GetString(1),JsonSerializer.Deserialize<WalletSettlement>(reader.GetString(2))!) : null;
    }
    private async Task<Dictionary<WalletAccount,decimal>> LockAccountsAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,IEnumerable<WalletAccount> requested,CancellationToken token)
    {
        var accounts = requested.Distinct().OrderBy(a => a.OwnerId).ThenBy(a => a.AccountKey).ToArray();
        foreach (var account in accounts) Validate(account);
        await using var command = new NpgsqlCommand($"SELECT owner_id,account_key,isk FROM {_schema}.balances WHERE (owner_id,account_key) IN(SELECT * FROM unnest($1::bigint[],$2::integer[])) ORDER BY owner_id,account_key FOR UPDATE",connection,transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = accounts.Select(a => a.OwnerId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = accounts.Select(a => a.AccountKey).ToArray() });
        var balances = new Dictionary<WalletAccount,decimal>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) balances[new(reader.GetInt64(0),reader.GetInt32(1))] = reader.GetDecimal(2);
        foreach (var account in accounts)
            if (!balances.ContainsKey(account)) throw new WalletNotFoundException(account.OwnerId);
        return balances;
    }

    private async Task<WalletSettlement> SettleAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,IReadOnlyList<WalletLeg> legs,
        long timeMs,string key,string context,CancellationToken token,bool conserve = true)
    {
        foreach (var leg in legs)
        {
            Validate(leg.Account);
            if (leg.Amount == 0 || decimal.Round(leg.Amount,2) != leg.Amount) throw new ArgumentOutOfRangeException(nameof(legs));
        }
        if (conserve && legs.Sum(l => l.Amount) != 0) throw new ArgumentException("settlement must conserve ISK",nameof(legs));
        var fingerprint = JsonSerializer.Serialize(legs);
        await LockOperationAsync(connection,transaction,key,token);
        var previous = await ReceiptAsync(connection,transaction,key,token);
        if (previous is not null)
        {
            if (previous.Value.Context != context || !(JsonSerializer.Deserialize<WalletLeg[]>(previous.Value.Fingerprint)?.SequenceEqual(legs) ?? false)) throw new WalletOperationConflictException(key);
            return previous.Value.Result with { Replayed = true };
        }
        var balances = await LockAccountsAsync(connection,transaction,legs.Select(l => l.Account),token);
        // Check the whole request before any write, including multiple allocations to one account.
        var after = new Dictionary<WalletAccount,decimal>(balances);
        foreach (var leg in legs)
        {
            var balance = after[leg.Account] + leg.Amount;
            if (balance < 0) throw new InsufficientFundsException(leg.Account.OwnerId);
            if (balance > WalletLimits.MaximumIsk) throw new MoneyLimitException();
            after[leg.Account] = balance;
        }
        var savepoint = "wallet_" + Guid.NewGuid().ToString("N");
        await using (var command = new NpgsqlCommand("SAVEPOINT " + savepoint,connection,transaction)) await command.ExecuteNonQueryAsync(token);
        try
        {
            foreach (var (account,balance) in after)
            {
                await using var update = new NpgsqlCommand($"UPDATE {_schema}.balances SET isk=$3 WHERE owner_id=$1 AND account_key=$2",connection,transaction);
                update.Parameters.Add(new NpgsqlParameter { Value = account.OwnerId });
                update.Parameters.Add(new NpgsqlParameter { Value = account.AccountKey });
                update.Parameters.Add(new NpgsqlParameter { Value = balance });
                await update.ExecuteNonQueryAsync(token);
            }
            var entries = new List<JournalEntry>();
            for (var i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                balances[leg.Account] += leg.Amount;
                await using var journal = new NpgsqlCommand($"INSERT INTO {_schema}.journal(owner_id,account_key,counterparty_id,ref_type_id,amount,balance,reason,time_ms,operation_key,reference_id,reference_type) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) RETURNING id",connection,transaction);
                foreach (var value in new object?[] { leg.Account.OwnerId,leg.Account.AccountKey,leg.CounterpartyId,leg.RefTypeId,leg.Amount,balances[leg.Account],leg.Reason,timeMs,key + ":" + i,leg.ReferenceId,leg.ReferenceType })
                    journal.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
                entries.Add(new JournalEntry((long)(await journal.ExecuteScalarAsync(token))!,leg.Account.OwnerId,leg.CounterpartyId,leg.RefTypeId,leg.Amount,balances[leg.Account],leg.Reason,timeMs,key + ":" + i,leg.Account.AccountKey,leg.ReferenceId,leg.ReferenceType));
            }
            var result = new WalletSettlement(key,entries);
            await using var receipt = new NpgsqlCommand($"INSERT INTO {_schema}.settlements(operation_key,context,fingerprint,time_ms,result) VALUES($1,$2,$3,$4,$5::jsonb)",connection,transaction);
            foreach (var value in new object[] { key,context,fingerprint,timeMs,JsonSerializer.Serialize(result) }) receipt.Parameters.Add(new NpgsqlParameter { Value = value });
            await receipt.ExecuteNonQueryAsync(token);
            await using var release = new NpgsqlCommand("RELEASE SAVEPOINT " + savepoint,connection,transaction);
            await release.ExecuteNonQueryAsync(token);
            return result;
        }
        catch
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT " + savepoint,connection,transaction);
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }
    }
}
