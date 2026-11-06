using System.Text.RegularExpressions;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Events;
using Eve.Wallet;
using Eve.Services.Core;
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
        int IssuerWalletKey, int IssuerItemFlag, long WrapItemId, short Status, long AcceptedAtFileTime,
        int NumDays, long AcceptorId, bool AcceptorIsCorporation, decimal CollateralEscrow);

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

    public async Task<bool> ExpireNowAsync(long contractId, long nowMs, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settled = await SettleAsync(contractId, connection, transaction, nowMs, null, cancellationToken, force: true);
        if (settled) await CancelAsync(connection, transaction, contractId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (settled) events?.Wake();
        return settled;
    }

    public async Task<bool> ClaimAuctionAsync(long contractId, long nowMs, long characterId, long corporationId,
        bool isIssuer, bool hasCorpContractManager, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        short type, status;
        long issuerId, issuerCorporationId, stationId, expired;
        bool corporationIssued;
        int issuerWalletKey, issuerItemFlag;
        decimal deposit;
        await using (var command = new NpgsqlCommand($"""
            SELECT contract_type, status, issuer_id, issuer_corporation_id, is_corporation_issued,
                   start_station_id, date_expired_filetime, issuer_wallet_account_key,
                   issuer_item_flag, deposit_isk
            FROM {_schema}.contract_records WHERE contract_id = $1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new ContractUserErrorException("ConContractNotFound");
            type = reader.GetInt16(0); status = reader.GetInt16(1);
            issuerId = reader.GetInt64(2); issuerCorporationId = reader.GetInt64(3);
            corporationIssued = reader.GetBoolean(4); stationId = reader.GetInt64(5);
            expired = reader.GetInt64(6); issuerWalletKey = reader.GetInt32(7);
            issuerItemFlag = reader.GetInt32(8); deposit = reader.GetDecimal(9);
        }
        if (type != 2)
            throw new ContractUserErrorException("ConIllegalType");
        var ownsIssuer = corporationIssued
            ? corporationId == issuerCorporationId && hasCorpContractManager
            : characterId == issuerId;
        if (isIssuer ? !ownsIssuer : false)
            throw new ContractUserErrorException("ConContractNotAvailable");
        if (expired > Eve.Services.Py.FileTimeFromUnixMs(nowMs))
            throw new ContractUserErrorException("ConContractNotAvailable");
        if (status is not (0 or 2 or 3 or 4))
            throw new ContractUserErrorException("ConContractNotAvailable");

        long bidderId = 0;
        bool bidderIsCorporation = false;
        decimal amount = 0;
        await using (var command = new NpgsqlCommand($"""
            SELECT bidder_id, bidder_is_corporation, amount
            FROM {_schema}.contract_bids
            WHERE contract_id = $1 AND NOT is_refunded
            ORDER BY amount DESC, created_at_filetime DESC, bid_id DESC LIMIT 1
            FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                bidderId = reader.GetInt64(0);
                bidderIsCorporation = reader.GetBoolean(1);
                amount = reader.GetDecimal(2);
            }
        }
        var ownsWinner = bidderId > 0 && (bidderIsCorporation
            ? corporationId == bidderId && hasCorpContractManager
            : characterId == bidderId);
        if (status == 4)
        {
            if (isIssuer ? ownsIssuer : ownsWinner)
            {
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            throw new ContractUserErrorException("ConContractNotAvailable");
        }
        if (isIssuer ? (status is 2 or 4) : (!ownsWinner || status is 3 or 4))
        {
            await transaction.CommitAsync(cancellationToken);
            if (isIssuer ? status is 2 or 4 : ownsWinner && status is 3 or 4)
                return true;
            throw new ContractUserErrorException("ConContractNotAvailable");
        }

        var issuerOwner = corporationIssued ? issuerCorporationId : issuerId;
        var nextStatus = (short)(status == 0 ? (isIssuer ? 2 : 3) : 4);
        if (bidderId == 0)
        {
            if (!isIssuer)
                throw new ContractUserErrorException("ConContractNotAvailable");
            var returnedItems = new List<long>();
            await using (var command = new NpgsqlCommand(
                $"SELECT item_id FROM {_schema}.contract_items WHERE contract_id = $1 AND in_crate ORDER BY item_id", connection, transaction))
            {
                command.Parameters.AddWithValue(contractId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) returnedItems.Add(reader.GetInt64(0));
            }
            foreach (var itemId in returnedItems)
                await items.MoveToOwnerAsync(connection, transaction, itemId, issuerOwner, stationId, issuerItemFlag, cancellationToken);
            if (deposit > 0)
                await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, issuerWalletKey),
                    deposit, 0, ContractSettlementRef, "Contract auction deposit returned", nowMs,
                    $"contract:{contractId}:auction:deposit-return", $"contract:{contractId}", cancellationToken,
                    referenceId: contractId);
            await using var returned = new NpgsqlCommand(
                $"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction);
            returned.Parameters.AddWithValue(contractId);
            await returned.ExecuteNonQueryAsync(cancellationToken);
            nextStatus = 4;
        }
        else if (isIssuer)
        {
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, issuerWalletKey),
                amount + deposit, bidderId, ContractSettlementRef, "Contract auction seller proceeds", nowMs,
                $"contract:{contractId}:auction:seller-payout", $"contract:{contractId}", cancellationToken,
                referenceId: contractId);
        }
        else
        {
            var offeredItems = new List<long>();
            await using (var command = new NpgsqlCommand(
                $"SELECT item_id FROM {_schema}.contract_items WHERE contract_id = $1 AND in_crate ORDER BY item_id", connection, transaction))
            {
                command.Parameters.AddWithValue(contractId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) offeredItems.Add(reader.GetInt64(0));
            }
            foreach (var itemId in offeredItems)
                await items.MoveToOwnerAsync(connection, transaction, itemId, bidderId, stationId,
                    bidderIsCorporation ? 62 : ItemFlags.Hangar, cancellationToken);
            await using var delivered = new NpgsqlCommand(
                $"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction);
            delivered.Parameters.AddWithValue(contractId);
            await delivered.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = new NpgsqlCommand($"""
            UPDATE {_schema}.contract_records
            SET status = $2, acceptor_id = $3, acceptor_is_corporation = $4,
                date_completed_filetime = CASE WHEN $2 = 4 THEN $5 ELSE date_completed_filetime END,
                deposit_isk = CASE WHEN $6 THEN 0 ELSE deposit_isk END
            WHERE contract_id = $1
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(contractId);
            update.Parameters.AddWithValue(nextStatus);
            update.Parameters.AddWithValue(bidderId);
            update.Parameters.AddWithValue(bidderIsCorporation);
            update.Parameters.AddWithValue(Eve.Services.Py.FileTimeFromUnixMs(nowMs));
            update.Parameters.AddWithValue(isIssuer || bidderId == 0);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        if (nextStatus == 4)
        {
            if (events is not null)
            {
                await events.PublishAsync(connection, transaction, ContractNotifications.Topic,
                    new ContractNotifications.Payload("OnContractCompleted", contractId,
                        corporationIssued ? 0 : issuerId, corporationIssued ? issuerCorporationId : 0, 0),
                    $"contract:{contractId}:auction:completed:issuer", cancellationToken);
                if (bidderId > 0)
                    await events.PublishAsync(connection, transaction, ContractNotifications.Topic,
                        new ContractNotifications.Payload("OnContractCompleted", contractId,
                            bidderIsCorporation ? 0 : bidderId, bidderIsCorporation ? bidderId : 0, 0),
                        $"contract:{contractId}:auction:completed:winner", cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        if (nextStatus == 4) events?.Wake();
        return true;
    }

    private async Task<bool> SettleAsync(long contractId, NpgsqlConnection connection, NpgsqlTransaction transaction,
        long nowMs, Func<JobSpec, CancellationToken, Task<long>>? schedule, CancellationToken cancellationToken,
        bool force = false)
    {
        ExpiredContract contract;
        await using (var command = new NpgsqlCommand($"""
            SELECT contract_type, issuer_id, issuer_corporation_id, is_corporation_issued, start_station_id,
                   date_expired_filetime, reward_escrow, deposit_isk, issuer_wallet_account_key,
                   issuer_item_flag, plastic_wrap_item_id, status, date_accepted_filetime, num_days,
                   acceptor_id, acceptor_is_corporation, collateral_escrow
            FROM {_schema}.contract_records
            WHERE contract_id = $1 AND (status = 0 OR (contract_type = 3 AND status = 1)) FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            contract = new ExpiredContract(contractId, reader.GetInt16(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetBoolean(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetDecimal(6), reader.GetDecimal(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt64(10), reader.GetInt16(11), reader.GetInt64(12),
                reader.GetInt32(13), reader.GetInt64(14), reader.GetBoolean(15), reader.GetDecimal(16));
        }

        var nowFileTime = checked(FileTimeUnixEpoch + nowMs * TicksPerMillisecond);
        var acceptedCourier = contract.Status == 1;
        var deadlineFileTime = acceptedCourier
            ? checked(contract.AcceptedAtFileTime + (long)contract.NumDays * TimeSpan.TicksPerDay)
            : contract.ExpiredFileTime;
        if (!force && deadlineFileTime > nowFileTime)
        {
            if (schedule is not null)
                await schedule(new JobSpec(JobKind, ToUnixMs(deadlineFileTime))
                {
                    Key = contractId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Payload = new Payload(contractId),
                }, cancellationToken);
            return false;
        }

        if (contract.Type == 2)
            return false;

        var issuerOwner = contract.CorporationIssued ? contract.IssuerCorporationId : contract.IssuerId;
        var escrowLocation = checked(EscrowLocationBase + contractId);
        if (acceptedCourier)
        {
            var wrap = await items.LockAndGetAsync(connection, transaction, contract.WrapItemId, cancellationToken);
            if (wrap is null || wrap.TypeId != 3468 || wrap.OwnerId != contract.AcceptorId)
                throw new InvalidOperationException($"Accepted courier {contractId} has no valid package.");
            var courierItems = new List<long>();
            await using (var command = new NpgsqlCommand($"SELECT item_id FROM {_schema}.contract_items WHERE contract_id = $1 AND in_crate ORDER BY item_id", connection, transaction))
            {
                command.Parameters.AddWithValue(contractId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) courierItems.Add(reader.GetInt64(0));
            }
            foreach (var itemId in courierItems)
            {
                var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
                if (item is null || item.OwnerId != EscrowOwnerId || item.LocationId != escrowLocation)
                    throw new InvalidOperationException($"Accepted courier {contractId} has invalid escrow contents.");
                await items.MoveToOwnerAsync(connection, transaction, itemId, contract.AcceptorId, wrap.LocationId,
                    wrap.Flag, cancellationToken);
            }
            if (contract.CollateralEscrow > 0)
                await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, contract.IssuerWalletKey),
                    contract.CollateralEscrow, contract.AcceptorId, 85, "Courier contract collateral paid to issuer", nowMs,
                    $"contract:{contractId}:courier:collateral-forfeit", $"contract:{contractId}", cancellationToken, referenceId: contractId);
            if (contract.RewardEscrow > 0)
                await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, contract.IssuerWalletKey),
                    contract.RewardEscrow, contract.AcceptorId, 83, "Courier contract reward returned", nowMs,
                    $"contract:{contractId}:courier:reward-return", $"contract:{contractId}", cancellationToken, referenceId: contractId);
            await items.DeleteAsync(connection, transaction, contract.WrapItemId, cancellationToken);
            await using (var completed = new NpgsqlCommand($"""
                UPDATE {_schema}.contract_records SET status = 7, date_completed_filetime = $2,
                    reward_escrow = 0, collateral_escrow = 0, plastic_wrap_item_id = 0
                WHERE contract_id = $1 AND status = 1
                """, connection, transaction))
            {
                completed.Parameters.AddWithValue(contractId);
                completed.Parameters.AddWithValue(Eve.Services.Py.FileTimeFromUnixMs(nowMs));
                if (await completed.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            }
            await using (var delivered = new NpgsqlCommand($"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction))
            {
                delivered.Parameters.AddWithValue(contractId);
                await delivered.ExecuteNonQueryAsync(cancellationToken);
            }
            await PublishNotificationAsync(connection, transaction, contract, "OnContractFailed", contractId, cancellationToken);
            if (events is not null)
            {
                var acceptor = contract.AcceptorIsCorporation
                    ? new ContractNotifications.Payload("OnContractFailed", contractId, 0, contract.AcceptorId, 0)
                    : new ContractNotifications.Payload("OnContractFailed", contractId, contract.AcceptorId, 0, 0);
                await events.PublishAsync(connection, transaction, ContractNotifications.Topic, acceptor,
                    $"contract:{contractId}:OnContractFailed:acceptor", cancellationToken);
            }
            return true;
        }
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
