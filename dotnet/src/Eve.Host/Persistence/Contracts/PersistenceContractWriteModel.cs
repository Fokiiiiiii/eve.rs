using System.Text.RegularExpressions;
using Eve.Inventory;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Creates a personal or corporation contract and stages its offered items and ISK atomically.</summary>
public sealed class PersistenceContractWriteModel(
    NpgsqlDataSource source,
    PgItemStore items,
    WalletTransactions wallets,
    Func<long> nowUnixMs,
    string schema = PersistenceContractReadModel.DefaultSchema) : IContractWriteModel
{
    private const long EscrowOwnerId = 1;
    private const long EscrowLocationBase = 9_300_000_000;
    private const int ContractBrokerFeeRef = 72;
    private const int ContractBrokerFeeCorporationRef = 80;
    private const int ContractSalesTaxRef = 73;
    private const int ContractDepositRef = 74;
    private const int ContractRewardAddedRef = 83;
    private const int ContractRewardAddedCorporationRef = 84;
    private readonly string _schema = Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$")
        ? schema
        : throw new ArgumentException("not a usable schema name", nameof(schema));

    public ValueTask<CreatedContract> CreateItemExchangeAsync(
        CreateItemExchangeRequest request,
        CancellationToken cancellationToken) => CreateContractAsync(request, cancellationToken);

    public async ValueTask<CreatedContract> CreateContractAsync(
        CreateItemExchangeRequest request,
        CancellationToken cancellationToken)
    {
        ContractCreationRules.ValidateItemExchangeTerms(request);
        var ownerId = request.IsCorporationIssued ? request.IssuerCorporationId : request.IssuerCharacterId;
        var nowMs = nowUnixMs();
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        var expiry = checked(nowFileTime + (long)request.ExpiryMinutes * TimeSpan.TicksPerMinute);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // serialises creations for one issuer so the outstanding count cannot race past the limit
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue($"contract-limit:{(request.IsCorporationIssued ? "corporation" : "character")}:{ownerId}");
            await lockCommand.ExecuteScalarAsync(cancellationToken);
        }
        await using (var countCommand = new NpgsqlCommand(request.IsCorporationIssued
            ? $"SELECT count(*) FROM {_schema}.contract_records WHERE status = 0 AND date_expired_filetime >= $1 AND is_corporation_issued AND issuer_corporation_id = $2"
            : $"SELECT count(*) FROM {_schema}.contract_records WHERE status = 0 AND date_expired_filetime >= $1 AND NOT is_corporation_issued AND issuer_id = $2",
            connection, transaction))
        {
            countCommand.Parameters.AddWithValue(nowFileTime);
            countCommand.Parameters.AddWithValue(ownerId);
            var outstanding = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (outstanding >= request.OutstandingLimit)
                throw new ContractUserErrorException("CustomInfo", ("info", new Eve.Marshal.PyStr(
                    $"You have reached the maximum of {request.OutstandingLimit} outstanding contracts.")));
        }
        long contractId;
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.contract_records
                (issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id, assignee_id,
                 contract_type, status, start_station_id, end_station_id, date_accepted_filetime,
                 date_expired_filetime, num_days, reward_escrow, reward_isk, created_at_filetime,
                 availability, price_isk, collateral_isk, creation_fee_isk, deposit_isk, sales_tax_isk,
                 title, description, issuer_wallet_account_key, issuer_item_flag)
            VALUES ($1, $2, $3, 0, $4, $16, 0, $5, $17, 0, $6, $18, $7, $7, $8,
                    $9, $10, $19, $11, $12, $13, $14, $15, $20, $21)
            RETURNING contract_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(request.IssuerCharacterId);
            command.Parameters.AddWithValue(request.IssuerCorporationId);
            command.Parameters.AddWithValue(request.IsCorporationIssued);
            command.Parameters.AddWithValue(request.AssigneeId);
            command.Parameters.AddWithValue(request.StartStationId);
            command.Parameters.AddWithValue(expiry);
            command.Parameters.AddWithValue(request.Reward);
            command.Parameters.AddWithValue(nowFileTime);
            command.Parameters.AddWithValue((short)request.Availability);
            command.Parameters.AddWithValue(request.Price);
            command.Parameters.AddWithValue(request.BrokerFee);
            command.Parameters.AddWithValue(request.Deposit);
            command.Parameters.AddWithValue(request.SalesTax);
            command.Parameters.AddWithValue(request.Title);
            command.Parameters.AddWithValue(request.Description);
            command.Parameters.AddWithValue(request.ContractType);
            command.Parameters.AddWithValue(request.EndStationId);
            command.Parameters.AddWithValue(request.NumDays);
            command.Parameters.AddWithValue(request.Collateral);
            command.Parameters.AddWithValue(request.WalletAccountKey);
            command.Parameters.AddWithValue(request.StartStationDivision);
            contractId = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("contract insert returned no id"));
        }

        var escrowLocationId = checked(EscrowLocationBase + contractId);
        var division = request.StartStationDivision;
        var escrowItemIds = new List<long>(request.Items.Count);
        foreach (var offered in request.Items.OrderBy(item => item.ItemId))
        {
            var item = await items.LockAndGetAsync(connection, transaction, offered.ItemId, cancellationToken);
            if (item is null || item.OwnerId != ownerId || item.LocationId != request.StartStationId ||
                item.Flag != division || offered.Quantity > (item.Singleton ? 1 : item.Quantity) ||
                offered.Quantity <= 0 || item.Singleton && offered.Quantity != 1)
                throw new ContractUserErrorException("ConNotInStation");

            var moved = await items.MoveStackAsync(connection, transaction, item, offered.Quantity,
                EscrowOwnerId, escrowLocationId, 0, cancellationToken);
            escrowItemIds.Add(moved.Id);
            await using var itemCommand = new NpgsqlCommand($"""
                INSERT INTO {_schema}.contract_items (contract_id, item_id, type_id, quantity, in_crate, is_package)
                VALUES ($1, $2, $3, $4, true, $5)
                """, connection, transaction);
            itemCommand.Parameters.AddWithValue(contractId);
            itemCommand.Parameters.AddWithValue(moved.Id);
            itemCommand.Parameters.AddWithValue(moved.TypeId);
            itemCommand.Parameters.AddWithValue(moved.Singleton ? 1L : moved.Quantity);
            itemCommand.Parameters.AddWithValue(request.ContractType == 3);
            await itemCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var requested in request.RequestedItems ?? [])
        {
            await using var requestCommand = new NpgsqlCommand($"""
                INSERT INTO {_schema}.contract_requested_items (contract_id, type_id, quantity)
                VALUES ($1, $2, $3)
                """, connection, transaction);
            requestCommand.Parameters.AddWithValue(contractId);
            requestCommand.Parameters.AddWithValue(requested.TypeId);
            requestCommand.Parameters.AddWithValue(requested.Quantity);
            await requestCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var wallet = new WalletAccount(ownerId, request.WalletAccountKey);
        var operationPrefix = $"contract:{contractId}:create";
        await DebitAsync(wallet, request.Reward, request.IsCorporationIssued ? ContractRewardAddedCorporationRef : ContractRewardAddedRef, $"{operationPrefix}:reward", contractId, nowMs, transaction, connection, cancellationToken);
        await DebitAsync(wallet, request.BrokerFee, request.IsCorporationIssued ? ContractBrokerFeeCorporationRef : ContractBrokerFeeRef, $"{operationPrefix}:broker", contractId, nowMs, transaction, connection, cancellationToken);
        await DebitAsync(wallet, request.SalesTax, ContractSalesTaxRef, $"{operationPrefix}:tax", contractId, nowMs, transaction, connection, cancellationToken);
        await DebitAsync(wallet, request.Deposit, ContractDepositRef, $"{operationPrefix}:deposit", contractId, nowMs, transaction, connection, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedContract(contractId, escrowItemIds);
    }

    public async ValueTask<AcceptedContract> AcceptItemExchangeAsync(long contractId, long characterId, long corporationId,
        long allianceId, bool acceptForCorporation, int acceptorWalletKey, CancellationToken cancellationToken)
    {
        if (contractId <= 0 || characterId <= 0 || corporationId <= 0 || allianceId < 0 || acceptorWalletKey is < 1000 or > 1006)
            throw new ArgumentOutOfRangeException(nameof(contractId));
        var acceptorId = acceptForCorporation ? corporationId : characterId;
        var acceptorFlag = acceptForCorporation ? 62 : 4;
        var nowMs = nowUnixMs();
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        short type, status, availability;
        long issuerId, issuerCorporationId, assigneeId, startStationId, endStationId, expired, existingAcceptorId, existingAcceptedAt;
        bool isCorporationIssued;
        decimal price, reward;
        int issuerWalletKey, issuerFlag;
        await using (var command = new NpgsqlCommand($"""
            SELECT contract_type, status, availability, issuer_id, issuer_corporation_id, is_corporation_issued,
                   assignee_id, start_station_id, end_station_id, date_expired_filetime, price_isk, reward_escrow,
                   issuer_wallet_account_key, issuer_item_flag, acceptor_id, date_accepted_filetime
            FROM {_schema}.contract_records WHERE contract_id = $1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ContractUserErrorException("ConContractNotFound");
            type = reader.GetInt16(0); status = reader.GetInt16(1); availability = reader.GetInt16(2);
            issuerId = reader.GetInt64(3); issuerCorporationId = reader.GetInt64(4); isCorporationIssued = reader.GetBoolean(5);
            assigneeId = reader.GetInt64(6); startStationId = reader.GetInt64(7); endStationId = reader.GetInt64(8);
            expired = reader.GetInt64(9); price = reader.GetDecimal(10); reward = reader.GetDecimal(11);
            issuerWalletKey = reader.GetInt32(12); issuerFlag = reader.GetInt32(13);
            existingAcceptorId = reader.GetInt64(14); existingAcceptedAt = reader.GetInt64(15);
        }
        if (type != 1) throw new ContractUserErrorException("ConIllegalType");
        if (status == 4 && existingAcceptorId == acceptorId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AcceptedContract(contractId, type, status, issuerId, issuerCorporationId, isCorporationIssued,
                existingAcceptorId, startStationId, endStationId, existingAcceptedAt, 0);
        }
        if (status != 0 || expired < nowFileTime) throw new ContractUserErrorException("ConContractNotAvailable");
        if (availability != 0 && assigneeId != characterId && assigneeId != corporationId && (allianceId == 0 || assigneeId != allianceId))
            throw new ContractUserErrorException("ConContractNotAvailable");
        if ((!isCorporationIssued && issuerId == characterId) || (isCorporationIssued && issuerCorporationId == corporationId))
            throw new ContractUserErrorException("ConCannotAcceptOwnContract");

        var requested = new List<(int TypeId, long Quantity)>();
        await using (var command = new NpgsqlCommand($"SELECT type_id, quantity FROM {_schema}.contract_requested_items WHERE contract_id = $1 ORDER BY type_id", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) requested.Add((reader.GetInt32(0), reader.GetInt64(1)));
        }
        var requestedMoves = new List<(Item Item, long Quantity)>();
        if (requested.Count > 0)
        {
            var candidates = new List<Item>();
            IEnumerable<int> flags = acceptForCorporation ? new[] { 62 }.Concat(Enumerable.Range(115, 7)) : new[] { 4 };
            foreach (var flag in flags)
                candidates.AddRange(await items.LockInLocationAsync(connection, transaction, startStationId, acceptorId, flag,
                    requested.Select(row => row.TypeId).ToArray(), cancellationToken));
            var remaining = requested.ToDictionary(row => row.TypeId, row => row.Quantity);
            foreach (var item in candidates.OrderBy(item => item.Id))
            {
                if (!remaining.TryGetValue(item.TypeId, out var need) || need <= 0) continue;
                var quantity = item.Singleton ? 1 : Math.Min(need, item.Quantity);
                requestedMoves.Add((item, quantity));
                remaining[item.TypeId] = need - quantity;
            }
            if (remaining.Values.Any(quantity => quantity > 0)) throw new ContractUserErrorException("ConNotEnoughItems");
            var issuerOwner = isCorporationIssued ? issuerCorporationId : issuerId;
            foreach (var (item, quantity) in requestedMoves)
            {
                if (item.Singleton || quantity == item.Quantity)
                    await items.MoveToOwnerAsync(connection, transaction, item.Id, issuerOwner, startStationId, issuerFlag, cancellationToken);
                else
                    await items.MoveStackAsync(connection, transaction, item, quantity, issuerOwner, startStationId, issuerFlag, cancellationToken);
            }
        }

        var escrowItemIds = new List<long>();
        await using (var command = new NpgsqlCommand($"SELECT item_id FROM {_schema}.contract_items WHERE contract_id = $1 AND in_crate ORDER BY item_id", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) escrowItemIds.Add(reader.GetInt64(0));
        }
        foreach (var itemId in escrowItemIds)
            await items.MoveToOwnerAsync(connection, transaction, itemId, acceptorId, startStationId, acceptorFlag, cancellationToken);

        var issuerOwnerId = isCorporationIssued ? issuerCorporationId : issuerId;
        var acceptorWallet = new WalletAccount(acceptorId, acceptForCorporation ? acceptorWalletKey : 1000);
        if (price > 0)
        {
            await wallets.AdjustAsync(connection, transaction, acceptorWallet, -price, issuerOwnerId, 85, "Contract price paid", nowMs,
                $"contract:{contractId}:accept:price-debit", $"contract:{contractId}", cancellationToken, referenceId: contractId);
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwnerId, issuerWalletKey), price, acceptorId, 85,
                "Contract price received", nowMs, $"contract:{contractId}:accept:price-credit", $"contract:{contractId}", cancellationToken, referenceId: contractId);
        }
        if (reward > 0)
            await wallets.AdjustAsync(connection, transaction, acceptorWallet, reward, issuerOwnerId, 83, "Contract reward received", nowMs,
                $"contract:{contractId}:accept:reward", $"contract:{contractId}", cancellationToken, referenceId: contractId);
        await using (var command = new NpgsqlCommand($"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand($"UPDATE {_schema}.contract_records SET status = 4, acceptor_id = $2, date_accepted_filetime = $3, reward_escrow = 0 WHERE contract_id = $1 AND status = 0", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId); command.Parameters.AddWithValue(acceptorId); command.Parameters.AddWithValue(nowFileTime);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ContractUserErrorException("ConContractNotAvailable");
        }
        await transaction.CommitAsync(cancellationToken);
        return new AcceptedContract(contractId, type, 4, issuerId, issuerCorporationId, isCorporationIssued, acceptorId,
            startStationId, endStationId, nowFileTime, 0);
    }

    private async Task DebitAsync(WalletAccount wallet, decimal amount, int refType, string operationKey,
        long contractId, long nowMs, NpgsqlTransaction transaction, NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (amount <= 0) return;
        await wallets.AdjustAsync(connection, transaction, wallet, -amount, 0, refType,
            "Contract creation", nowMs, operationKey, $"contract:{contractId}", cancellationToken,
            referenceId: contractId);
    }

}
