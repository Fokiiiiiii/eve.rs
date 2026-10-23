using System.Text.RegularExpressions;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Events;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Settles outstanding contracts at their persisted expiry time.</summary>
public sealed class ContractExpiryJobs(
    NpgsqlDataSource source,
    PgItemStore items,
    WalletTransactions wallets,
    DurableScheduler scheduler,
    string schema = PersistenceContractReadModel.DefaultSchema,
    EventBus? events = null)
{
    public const string JobKind = "contracts.expire";
    private const long EscrowOwnerId = 1;
    private const long EscrowLocationBase = 9_300_000_000;
    private const long FileTimeUnixEpoch = 116_444_736_000_000_000;
    private const long TicksPerMillisecond = 10_000;
    private const int ContractSettlementRef = 85;
    private readonly string _schema = Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$")
        ? schema
        : throw new ArgumentException("not a usable schema name", nameof(schema));

    private sealed record Payload(long ContractId);
    private sealed record ExpiredContract(
        long ContractId, short Type, long IssuerId, long IssuerCorporationId, bool CorporationIssued,
        long StartStationId, long ExpiredFileTime, decimal RewardEscrow, decimal Deposit,
        int IssuerWalletKey, int IssuerItemFlag, long WrapItemId);

    public void Register() => scheduler.Register(JobKind, async (job, token) =>
    {
        if (await SettleAsync(job.Payload<Payload>()!.ContractId, job.Connection, job.Transaction, job.NowMs, job.ScheduleAsync, token))
            events?.Wake();
    });

    public static long ToUnixMs(long fileTime) => checked((fileTime - FileTimeUnixEpoch) / TicksPerMillisecond);

    public async Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long contractId, long expiredFileTime, CancellationToken cancellationToken)
    {
        await scheduler.ScheduleAsync(connection, transaction, new JobSpec(JobKind, ToUnixMs(expiredFileTime))
        {
            Key = contractId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Payload = new Payload(contractId),
        }, cancellationToken);
    }

    public Task<bool> CancelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long contractId, CancellationToken cancellationToken) =>
        scheduler.CancelAsync(connection, transaction,
            JobKind, contractId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    public async Task<bool> SettleExpiredAsync(long contractId, long nowMs, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settled = await SettleAsync(contractId, connection, transaction, nowMs, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (settled) events?.Wake();
        return settled;
    }

    private async Task<bool> SettleAsync(long contractId, NpgsqlConnection connection, NpgsqlTransaction transaction,
        long nowMs, Func<JobSpec, CancellationToken, Task<long>>? schedule, CancellationToken cancellationToken)
    {
        ExpiredContract contract;
        await using (var command = new NpgsqlCommand($"""
            SELECT contract_type, issuer_id, issuer_corporation_id, is_corporation_issued, start_station_id,
                   date_expired_filetime, reward_escrow, deposit_isk, issuer_wallet_account_key,
                   issuer_item_flag, plastic_wrap_item_id
            FROM {_schema}.contract_records WHERE contract_id = $1 AND status = 0 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            contract = new ExpiredContract(contractId, reader.GetInt16(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetBoolean(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetDecimal(6), reader.GetDecimal(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt64(10));
        }

        var nowFileTime = checked(FileTimeUnixEpoch + nowMs * TicksPerMillisecond);
        if (contract.ExpiredFileTime > nowFileTime)
        {
            if (schedule is not null)
                await schedule(new JobSpec(JobKind, ToUnixMs(contract.ExpiredFileTime))
                {
                    Key = contractId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Payload = new Payload(contractId),
                }, cancellationToken);
            return false;
        }

        var issuerOwner = contract.CorporationIssued ? contract.IssuerCorporationId : contract.IssuerId;
        var escrowLocation = checked(EscrowLocationBase + contractId);
        var winningBid = contract.Type == 2 ? await ReadWinningBidAsync(connection, transaction, contractId, cancellationToken) : null;
        var offeredItems = new List<long>();
        await using (var command = new NpgsqlCommand($"SELECT item_id FROM {_schema}.contract_items WHERE contract_id = $1 AND in_crate ORDER BY item_id", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) offeredItems.Add(reader.GetInt64(0));
        }

        if (winningBid is { } bid)
        {
            foreach (var itemId in offeredItems)
                await items.MoveToOwnerAsync(connection, transaction, itemId, bid.BidderId, contract.StartStationId,
                    bid.IsCorporation ? 62 : 4, cancellationToken);
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, contract.IssuerWalletKey),
                bid.Amount, bid.BidderId, ContractSettlementRef, "Contract auction settled at expiry", nowMs,
                $"contract:{contractId}:expiry:auction-payout", $"contract:{contractId}", cancellationToken, referenceId: contractId);
            if (contract.Deposit > 0)
                await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, contract.IssuerWalletKey),
                    contract.Deposit, 0, ContractSettlementRef, "Contract deposit returned", nowMs,
                    $"contract:{contractId}:expiry:deposit-return", $"contract:{contractId}", cancellationToken, referenceId: contractId);
            await using var delivered = new NpgsqlCommand($"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction);
            delivered.Parameters.AddWithValue(contractId);
            await delivered.ExecuteNonQueryAsync(cancellationToken);
            await using var completed = new NpgsqlCommand($"""
                UPDATE {_schema}.contract_records SET status = 4, acceptor_id = $2, date_accepted_filetime = $3,
                    date_completed_filetime = $3, reward_escrow = 0
                WHERE contract_id = $1 AND status = 0
                """, connection, transaction);
            completed.Parameters.AddWithValue(contractId);
            completed.Parameters.AddWithValue(bid.BidderId);
            completed.Parameters.AddWithValue(Eve.Services.Py.FileTimeFromUnixMs(nowMs));
            await completed.ExecuteNonQueryAsync(cancellationToken);
            await PublishNotificationAsync(connection, transaction, contract, "OnContractCompleted", contractId, cancellationToken);
            return true;
        }

        foreach (var itemId in offeredItems)
            await items.MoveToOwnerAsync(connection, transaction, itemId, issuerOwner, contract.StartStationId,
                contract.IssuerItemFlag, cancellationToken);
        if (contract.WrapItemId > 0)
            await items.DeleteAsync(connection, transaction, contract.WrapItemId, cancellationToken);
        if (contract.RewardEscrow > 0)
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, contract.IssuerWalletKey),
                contract.RewardEscrow, 0, 83, "Contract reward returned at expiry", nowMs,
                $"contract:{contractId}:expiry:reward-return", $"contract:{contractId}", cancellationToken, referenceId: contractId);
        await using (var returned = new NpgsqlCommand($"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction))
        {
            returned.Parameters.AddWithValue(contractId);
            await returned.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var expired = new NpgsqlCommand($"""
            UPDATE {_schema}.contract_records SET status = 3, date_completed_filetime = $2,
                reward_escrow = 0, plastic_wrap_item_id = 0
            WHERE contract_id = $1 AND status = 0
            """, connection, transaction))
        {
            expired.Parameters.AddWithValue(contractId);
            expired.Parameters.AddWithValue(Eve.Services.Py.FileTimeFromUnixMs(nowMs));
            await expired.ExecuteNonQueryAsync(cancellationToken);
        }
        await PublishNotificationAsync(connection, transaction, contract, "OnContractFailed", contractId, cancellationToken);
        return true;
    }

    private Task<long> PublishNotificationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ExpiredContract contract, string name, long contractId, CancellationToken cancellationToken) =>
        events is null ? Task.FromResult(0L) : events.PublishAsync(connection, transaction, ContractNotifications.Topic,
            new ContractNotifications.Payload(name, contractId, contract.CorporationIssued ? 0 : contract.IssuerId,
                contract.CorporationIssued ? contract.IssuerCorporationId : 0, 0),
            $"contract:{contractId}:{name}:expiry", cancellationToken);

    private async Task<WinningBid?> ReadWinningBidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long contractId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT bidder_id, amount, bidder_is_corporation
            FROM {_schema}.contract_bids
            WHERE contract_id = $1 AND NOT is_refunded
            ORDER BY amount DESC, created_at_filetime DESC, bid_id DESC LIMIT 1 FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue(contractId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WinningBid(reader.GetInt64(0), reader.GetDecimal(1), reader.GetBoolean(2))
            : null;
    }

    private sealed record WinningBid(long BidderId, decimal Amount, bool IsCorporation);
}
