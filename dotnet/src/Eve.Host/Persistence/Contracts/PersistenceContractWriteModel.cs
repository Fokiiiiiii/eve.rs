using System.Text.RegularExpressions;
using Eve.Inventory;
using Eve.Services.Core;
using Eve.Events;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Creates a personal or corporation contract and stages its offered items and ISK atomically.</summary>
public sealed class PersistenceContractWriteModel(
    NpgsqlDataSource source,
    PgItemStore items,
    WalletTransactions wallets,
    Func<long> nowUnixMs,
    string schema = PersistenceContractReadModel.DefaultSchema,
    ContractExpiryJobs? expiryJobs = null,
    EventBus? events = null) : IContractWriteModel
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

        if (request.ContractType == 3)
        {
            var wrapItemId = await items.CreateAsync(connection, transaction,
                new NewItem(3468, EscrowOwnerId, escrowLocationId, 0, Quantity: 1, Singleton: true), cancellationToken);
            await using var wrapCommand = new NpgsqlCommand($"UPDATE {_schema}.contract_records SET plastic_wrap_item_id = $2 WHERE contract_id = $1", connection, transaction);
            wrapCommand.Parameters.AddWithValue(contractId);
            wrapCommand.Parameters.AddWithValue(wrapItemId);
            await wrapCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var wallet = new WalletAccount(ownerId, request.WalletAccountKey);
        var operationPrefix = $"contract:{contractId}:create";
        await DebitAsync(wallet, request.Reward, request.IsCorporationIssued ? ContractRewardAddedCorporationRef : ContractRewardAddedRef, $"{operationPrefix}:reward", contractId, nowMs, transaction, connection, cancellationToken);
        await DebitAsync(wallet, request.BrokerFee, request.IsCorporationIssued ? ContractBrokerFeeCorporationRef : ContractBrokerFeeRef, $"{operationPrefix}:broker", contractId, nowMs, transaction, connection, cancellationToken);
        await DebitAsync(wallet, request.SalesTax, ContractSalesTaxRef, $"{operationPrefix}:tax", contractId, nowMs, transaction, connection, cancellationToken);
        await DebitAsync(wallet, request.Deposit, ContractDepositRef, $"{operationPrefix}:deposit", contractId, nowMs, transaction, connection, cancellationToken);
        if (expiryJobs is not null)
            await expiryJobs.ScheduleAsync(connection, transaction, contractId, expiry, cancellationToken);
        await PublishNotificationAsync(connection, transaction,
            request.IsCorporationIssued ? 0 : request.IssuerCharacterId,
            request.IsCorporationIssued ? request.IssuerCorporationId : 0, 0,
            "OnContractCreated", contractId, cancellationToken);
        if (request.Availability is 1 or 2 or 3 && request.AssigneeId > 0)
            await PublishNotificationAsync(connection, transaction,
                request.Availability == 1 ? request.AssigneeId : 0,
                request.Availability == 2 ? request.AssigneeId : 0,
                request.Availability == 3 ? request.AssigneeId : 0,
                "OnContractAssigned", contractId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        events?.Wake();
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
        int numDays;
        decimal price, reward;
        int issuerWalletKey, issuerFlag;
        await using (var command = new NpgsqlCommand($"""
            SELECT contract_type, status, availability, issuer_id, issuer_corporation_id, is_corporation_issued,
                   assignee_id, start_station_id, end_station_id, date_expired_filetime, price_isk, reward_escrow,
                   issuer_wallet_account_key, issuer_item_flag, acceptor_id, date_accepted_filetime, num_days
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
            existingAcceptorId = reader.GetInt64(14); existingAcceptedAt = reader.GetInt64(15); numDays = reader.GetInt32(16);
        }
        if (type != 1) throw new ContractUserErrorException("ConIllegalType");
        if (status == 4 && existingAcceptorId == acceptorId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AcceptedContract(contractId, type, status, issuerId, issuerCorporationId, isCorporationIssued,
                existingAcceptorId, startStationId, endStationId, existingAcceptedAt, numDays);
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
        await PublishNotificationAsync(connection, transaction, isCorporationIssued ? 0 : issuerId,
            isCorporationIssued ? issuerCorporationId : 0, 0, "OnContractAccepted", contractId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        events?.Wake();
        return new AcceptedContract(contractId, type, 4, issuerId, issuerCorporationId, isCorporationIssued, acceptorId,
            startStationId, endStationId, nowFileTime, 0);
    }

    public async ValueTask<AcceptedContract> AcceptContractAsync(long contractId, long characterId, long corporationId,
        long allianceId, bool acceptForCorporation, int acceptorWalletKey, long currentStationId, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand($"SELECT contract_type FROM {_schema}.contract_records WHERE contract_id = $1");
        command.Parameters.AddWithValue(contractId);
        var type = await command.ExecuteScalarAsync(cancellationToken);
        if (type is null) throw new ContractUserErrorException("ConContractNotFound");
        return (short)type switch
        {
            1 => await AcceptItemExchangeAsync(contractId, characterId, corporationId, allianceId, acceptForCorporation,
                acceptorWalletKey, cancellationToken),
            3 => await AcceptCourierAsync(contractId, characterId, corporationId, allianceId, acceptForCorporation,
                acceptorWalletKey, currentStationId, cancellationToken),
            _ => throw new ContractUserErrorException("ConIllegalType"),
        };
    }

    private async ValueTask<AcceptedContract> AcceptCourierAsync(long contractId, long characterId, long corporationId,
        long allianceId, bool acceptForCorporation, int acceptorWalletKey, long currentStationId, CancellationToken cancellationToken)
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
        long issuerId, issuerCorporationId, assigneeId, startStationId, endStationId, expired, existingAcceptorId, existingAcceptedAt, wrapItemId;
        bool isCorporationIssued, existingAcceptorIsCorporation;
        decimal collateral;
        int issuerWalletKey, numDays, existingAcceptorWalletKey;
        await using (var command = new NpgsqlCommand($"""
            SELECT contract_type, status, availability, issuer_id, issuer_corporation_id, is_corporation_issued,
                   assignee_id, start_station_id, end_station_id, date_expired_filetime, collateral_isk,
                   issuer_wallet_account_key, acceptor_id, date_accepted_filetime, plastic_wrap_item_id, acceptor_is_corporation,
                   acceptor_wallet_account_key, num_days
            FROM {_schema}.contract_records WHERE contract_id = $1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ContractUserErrorException("ConContractNotFound");
            type = reader.GetInt16(0); status = reader.GetInt16(1); availability = reader.GetInt16(2);
            issuerId = reader.GetInt64(3); issuerCorporationId = reader.GetInt64(4); isCorporationIssued = reader.GetBoolean(5);
            assigneeId = reader.GetInt64(6); startStationId = reader.GetInt64(7); endStationId = reader.GetInt64(8);
            expired = reader.GetInt64(9); collateral = reader.GetDecimal(10); issuerWalletKey = reader.GetInt32(11);
            existingAcceptorId = reader.GetInt64(12); existingAcceptedAt = reader.GetInt64(13); wrapItemId = reader.GetInt64(14);
            existingAcceptorIsCorporation = reader.GetBoolean(15); existingAcceptorWalletKey = reader.GetInt32(16); numDays = reader.GetInt32(17);
        }
        if (type != 3) throw new ContractUserErrorException("ConIllegalType");
        if (status == 1 && existingAcceptorId == acceptorId && existingAcceptorIsCorporation == acceptForCorporation &&
            existingAcceptorWalletKey == (acceptForCorporation ? acceptorWalletKey : 1000))
        {
            await transaction.CommitAsync(cancellationToken);
            return new AcceptedContract(contractId, type, status, issuerId, issuerCorporationId, isCorporationIssued,
                existingAcceptorId, startStationId, endStationId, existingAcceptedAt, 0);
        }
        if (status != 0 || expired < nowFileTime) throw new ContractUserErrorException("ConContractNotAvailable");
        if (currentStationId != startStationId) throw new ContractUserErrorException("ConNotInStation");
        if (availability != 0 && assigneeId != characterId && assigneeId != corporationId && (allianceId == 0 || assigneeId != allianceId))
            throw new ContractUserErrorException("ConContractNotAvailable");
        if ((!isCorporationIssued && issuerId == characterId) || (isCorporationIssued && issuerCorporationId == corporationId))
            throw new ContractUserErrorException("ConCannotAcceptOwnContract");
        if (wrapItemId <= 0) throw new ContractUserErrorException("ConContractNotAvailable");
        var wrap = await items.LockAndGetAsync(connection, transaction, wrapItemId, cancellationToken);
        var escrowLocationId = checked(EscrowLocationBase + contractId);
        if (wrap is null || wrap.TypeId != 3468 || wrap.OwnerId != EscrowOwnerId || wrap.LocationId != escrowLocationId || !wrap.Singleton)
            throw new ContractUserErrorException("ConContractNotAvailable");
        await items.MoveToOwnerAsync(connection, transaction, wrapItemId, acceptorId, startStationId, acceptorFlag, cancellationToken);
        var acceptorWallet = new WalletAccount(acceptorId, acceptForCorporation ? acceptorWalletKey : 1000);
        if (collateral > 0)
            await wallets.AdjustAsync(connection, transaction, acceptorWallet, -collateral, isCorporationIssued ? issuerCorporationId : issuerId,
                85, "Courier contract collateral escrowed", nowMs, $"contract:{contractId}:courier:collateral-debit",
                $"contract:{contractId}", cancellationToken, referenceId: contractId);
        await using (var command = new NpgsqlCommand($"""
            UPDATE {_schema}.contract_records SET status = 1, acceptor_id = $2, date_accepted_filetime = $3,
                acceptor_is_corporation = $4, acceptor_wallet_account_key = $5, collateral_escrow = collateral_isk
            WHERE contract_id = $1 AND status = 0
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId); command.Parameters.AddWithValue(acceptorId);
            command.Parameters.AddWithValue(nowFileTime); command.Parameters.AddWithValue(acceptForCorporation);
            command.Parameters.AddWithValue(acceptForCorporation ? acceptorWalletKey : 1000);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ContractUserErrorException("ConContractNotAvailable");
        }
        await PublishNotificationAsync(connection, transaction, isCorporationIssued ? 0 : issuerId,
            isCorporationIssued ? issuerCorporationId : 0, 0, "OnContractAccepted", contractId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        events?.Wake();
        return new AcceptedContract(contractId, type, 1, issuerId, issuerCorporationId, isCorporationIssued,
            acceptorId, startStationId, endStationId, nowFileTime, numDays);
    }

    public async ValueTask<bool> CompleteContractAsync(long contractId, int targetStatus, long characterId, long corporationId,
        long currentStationId, bool hasCorpContractManager, CancellationToken cancellationToken)
    {
        if (contractId <= 0 || characterId <= 0 || corporationId <= 0 || targetStatus is not (4 or 5))
            throw new ContractUserErrorException("ConContractNotAvailable");
        var nowMs = nowUnixMs();
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        short type, status;
        long issuerId, issuerCorporationId, acceptorId, startStationId, endStationId, acceptedAt, wrapItemId;
        bool isCorporationIssued, acceptorIsCorporation;
        decimal rewardEscrow, collateralEscrow, deposit;
        int issuerWalletKey, issuerFlag, acceptorWalletKey, numDays;
        await using (var command = new NpgsqlCommand($"""
            SELECT contract_type, status, issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id,
                   start_station_id, end_station_id, date_accepted_filetime, plastic_wrap_item_id,
                   acceptor_is_corporation, reward_escrow, collateral_escrow, deposit_isk,
                   issuer_wallet_account_key, issuer_item_flag, acceptor_wallet_account_key, num_days
            FROM {_schema}.contract_records WHERE contract_id = $1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ContractUserErrorException("ConContractNotFound");
            type = reader.GetInt16(0); status = reader.GetInt16(1); issuerId = reader.GetInt64(2);
            issuerCorporationId = reader.GetInt64(3); isCorporationIssued = reader.GetBoolean(4); acceptorId = reader.GetInt64(5);
            startStationId = reader.GetInt64(6); endStationId = reader.GetInt64(7); acceptedAt = reader.GetInt64(8);
            wrapItemId = reader.GetInt64(9); acceptorIsCorporation = reader.GetBoolean(10); rewardEscrow = reader.GetDecimal(11);
            collateralEscrow = reader.GetDecimal(12); deposit = reader.GetDecimal(13); issuerWalletKey = reader.GetInt32(14);
            issuerFlag = reader.GetInt32(15); acceptorWalletKey = reader.GetInt32(16); numDays = reader.GetInt32(17);
        }
        if (type != 3) throw new ContractUserErrorException("ConIllegalType");
        if (status == targetStatus)
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        if (status != 1 || acceptorId <= 0 || wrapItemId <= 0) throw new ContractUserErrorException("ConContractNotAvailable");
        var actorOwnerId = acceptorIsCorporation ? corporationId : characterId;
        var actorIsAcceptor = actorOwnerId == acceptorId;
        var actorIsIssuer = isCorporationIssued ? corporationId == issuerCorporationId && hasCorpContractManager : characterId == issuerId;
        var deadline = checked(acceptedAt + (long)numDays * TimeSpan.TicksPerDay);
        if (targetStatus == 4 && !actorIsAcceptor) throw new ContractUserErrorException("ConContractNotAvailable");
        if (targetStatus == 5 && !actorIsAcceptor && !(actorIsIssuer && nowFileTime > deadline))
            throw new ContractUserErrorException("ConContractNotAvailable");
        var wrap = await items.LockAndGetAsync(connection, transaction, wrapItemId, cancellationToken);
        if (wrap is null || wrap.TypeId != 3468 || wrap.OwnerId != acceptorId)
            throw new ContractUserErrorException("ConContractNotAvailable");
        if (targetStatus == 4 && (currentStationId != endStationId || wrap.LocationId != endStationId))
            throw new ContractUserErrorException("ConContractNotAvailable");
        var issuerOwnerId = isCorporationIssued ? issuerCorporationId : issuerId;
        var destinationOwnerId = targetStatus == 4 ? issuerOwnerId : acceptorId;
        var destinationLocationId = targetStatus == 4 ? endStationId : wrap.LocationId;
        var destinationFlag = targetStatus == 4 ? issuerFlag : wrap.Flag;
        var escrowLocation = checked(EscrowLocationBase + contractId);
        var contentIds = new List<long>();
        await using (var command = new NpgsqlCommand($"SELECT item_id FROM {_schema}.contract_items WHERE contract_id = $1 AND in_crate ORDER BY item_id", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) contentIds.Add(reader.GetInt64(0));
        }
        foreach (var itemId in contentIds)
        {
            var content = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
            if (content is null || content.OwnerId != EscrowOwnerId || content.LocationId != escrowLocation)
                throw new ContractUserErrorException("ConContractNotAvailable");
            await items.MoveToOwnerAsync(connection, transaction, itemId, destinationOwnerId, destinationLocationId, destinationFlag, cancellationToken);
        }
        var acceptorWallet = new WalletAccount(acceptorId, acceptorIsCorporation ? acceptorWalletKey : 1000);
        var issuerWallet = new WalletAccount(issuerOwnerId, issuerWalletKey);
        if (targetStatus == 4)
        {
            if (collateralEscrow > 0)
                await wallets.AdjustAsync(connection, transaction, acceptorWallet, collateralEscrow, issuerOwnerId, 85,
                    "Courier contract collateral returned", nowMs, $"contract:{contractId}:courier:collateral-return", $"contract:{contractId}", cancellationToken, referenceId: contractId);
            if (rewardEscrow > 0)
                await wallets.AdjustAsync(connection, transaction, acceptorWallet, rewardEscrow, issuerOwnerId, 83,
                    "Courier contract reward received", nowMs, $"contract:{contractId}:courier:reward", $"contract:{contractId}", cancellationToken, referenceId: contractId);
            if (deposit > 0)
                await wallets.AdjustAsync(connection, transaction, issuerWallet, deposit, acceptorId, 85,
                    "Courier contract deposit returned", nowMs, $"contract:{contractId}:courier:deposit-return", $"contract:{contractId}", cancellationToken, referenceId: contractId);
        }
        else
        {
            if (collateralEscrow > 0)
                await wallets.AdjustAsync(connection, transaction, issuerWallet, collateralEscrow, acceptorId, 85,
                    "Courier contract collateral paid to issuer", nowMs, $"contract:{contractId}:courier:collateral-forfeit", $"contract:{contractId}", cancellationToken, referenceId: contractId);
            if (rewardEscrow > 0)
                await wallets.AdjustAsync(connection, transaction, issuerWallet, rewardEscrow, acceptorId, 83,
                    "Courier contract reward returned", nowMs, $"contract:{contractId}:courier:reward-return", $"contract:{contractId}", cancellationToken, referenceId: contractId);
        }
        await items.DeleteAsync(connection, transaction, wrapItemId, cancellationToken);
        await using (var command = new NpgsqlCommand($"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand($"""
            UPDATE {_schema}.contract_records SET status = $2, date_completed_filetime = $3,
                reward_escrow = 0, collateral_escrow = 0, plastic_wrap_item_id = 0
            WHERE contract_id = $1 AND status = 1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId); command.Parameters.AddWithValue((short)targetStatus); command.Parameters.AddWithValue(nowFileTime);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ContractUserErrorException("ConContractNotAvailable");
        }
        await PublishNotificationAsync(connection, transaction,
            targetStatus == 4 ? (isCorporationIssued ? 0 : issuerId) : (actorIsAcceptor ? (isCorporationIssued ? 0 : issuerId) : acceptorIsCorporation ? 0 : acceptorId),
            targetStatus == 4 || !actorIsAcceptor && acceptorIsCorporation ? (targetStatus == 4 ? issuerCorporationId : acceptorId) : 0,
            0, targetStatus == 4 ? "OnContractCompleted" : "OnContractFailed", contractId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        events?.Wake();
        return true;
    }

    public async ValueTask<bool> DeleteContractAsync(long contractId, long characterId, long corporationId,
        bool hasCorpContractManager, CancellationToken cancellationToken)
    {
        if (contractId <= 0 || characterId <= 0 || corporationId < 0)
            throw new ArgumentOutOfRangeException(nameof(contractId));
        var nowMs = nowUnixMs();
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        short status, contractType;
        long issuerId, issuerCorporationId, startStationId, wrapItemId;
        bool corporationIssued;
        decimal rewardEscrow;
        int issuerWalletKey, issuerItemFlag;
        await using (var command = new NpgsqlCommand($"""
            SELECT status, contract_type, issuer_id, issuer_corporation_id, is_corporation_issued,
                   start_station_id, plastic_wrap_item_id, reward_escrow, issuer_wallet_account_key, issuer_item_flag
            FROM {_schema}.contract_records WHERE contract_id = $1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            status = reader.GetInt16(0); contractType = reader.GetInt16(1); issuerId = reader.GetInt64(2);
            issuerCorporationId = reader.GetInt64(3); corporationIssued = reader.GetBoolean(4);
            startStationId = reader.GetInt64(5); wrapItemId = reader.GetInt64(6); rewardEscrow = reader.GetDecimal(7);
            issuerWalletKey = reader.GetInt32(8); issuerItemFlag = reader.GetInt32(9);
        }
        var issuerOwner = corporationIssued ? issuerCorporationId : issuerId;
        if (corporationIssued ? corporationId != issuerCorporationId || !hasCorpContractManager : characterId != issuerId)
            throw new ContractUserErrorException("ConContractNotOutstanding");
        if (status == 5)
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        if (status is not (0 or 6)) throw new ContractUserErrorException("ConContractNotOutstanding");

        var bids = new List<(long BidId, long BidderId, decimal Amount, bool IsCorporation, int WalletKey)>();
        if (contractType == 2)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT bid_id, bidder_id, amount, bidder_is_corporation, wallet_account_key
                FROM {_schema}.contract_bids WHERE contract_id = $1 AND NOT is_refunded ORDER BY bid_id FOR UPDATE
                """, connection, transaction);
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                bids.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetDecimal(2), reader.GetBoolean(3), reader.GetInt32(4)));
        }
        foreach (var bid in bids)
        {
            await wallets.AdjustAsync(connection, transaction,
                new WalletAccount(bid.BidderId, bid.IsCorporation ? bid.WalletKey : 1000), bid.Amount,
                issuerOwner, 85, "Contract auction bid returned on deletion", nowMs,
                $"contract:{contractId}:delete:bid:{bid.BidId}:refund", $"contract:{contractId}", cancellationToken,
                referenceId: contractId);
            await using var refunded = new NpgsqlCommand($"UPDATE {_schema}.contract_bids SET is_refunded = true WHERE bid_id = $1", connection, transaction);
            refunded.Parameters.AddWithValue(bid.BidId);
            await refunded.ExecuteNonQueryAsync(cancellationToken);
        }

        var offered = new List<long>();
        await using (var command = new NpgsqlCommand($"SELECT item_id FROM {_schema}.contract_items WHERE contract_id = $1 AND in_crate ORDER BY item_id", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) offered.Add(reader.GetInt64(0));
        }
        foreach (var itemId in offered)
            await items.MoveToOwnerAsync(connection, transaction, itemId, issuerOwner, startStationId, issuerItemFlag, cancellationToken);
        if (contractType == 3 && wrapItemId > 0)
            await items.DeleteAsync(connection, transaction, wrapItemId, cancellationToken);
        if (rewardEscrow > 0)
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, issuerWalletKey), rewardEscrow,
                0, ContractRewardAddedRef, "Contract reward escrow refunded", nowMs,
                $"contract:{contractId}:delete:reward-refund", $"contract:{contractId}", cancellationToken, referenceId: contractId);
        await using (var command = new NpgsqlCommand($"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand($"""
            UPDATE {_schema}.contract_records SET status = 5, date_completed_filetime = $2,
                reward_escrow = 0, collateral_escrow = 0, plastic_wrap_item_id = 0
            WHERE contract_id = $1 AND status IN (0, 6)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId); command.Parameters.AddWithValue(nowFileTime);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ContractUserErrorException("ConContractNotOutstanding");
        }
        if (expiryJobs is not null)
            await expiryJobs.CancelAsync(connection, transaction, contractId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> PlaceBidAsync(long contractId, long characterId, long corporationId, long allianceId,
        bool bidForCorporation, int bidderWalletKey, decimal amount, CancellationToken cancellationToken)
    {
        if (contractId <= 0 || characterId <= 0 || corporationId <= 0 || allianceId < 0 ||
            bidderWalletKey is < 1000 or > 1006 || amount <= 0 || decimal.Round(amount, 2) != amount || decimal.Truncate(amount) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var bidderId = bidForCorporation ? corporationId : characterId;
        var nowMs = nowUnixMs();
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        short type, status, availability;
        long issuerId, issuerCorporationId, assigneeId, startStationId, expired, currentAcceptor;
        bool isCorporationIssued;
        decimal startingPrice, buyoutPrice;
        int issuerWalletKey;
        await using (var command = new NpgsqlCommand($"""
            SELECT contract_type, status, availability, issuer_id, issuer_corporation_id, is_corporation_issued,
                   assignee_id, start_station_id, date_expired_filetime, price_isk, collateral_isk, acceptor_id,
                   issuer_wallet_account_key
            FROM {_schema}.contract_records WHERE contract_id = $1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ContractUserErrorException("ConContractNotFound");
            type = reader.GetInt16(0); status = reader.GetInt16(1); availability = reader.GetInt16(2);
            issuerId = reader.GetInt64(3); issuerCorporationId = reader.GetInt64(4); isCorporationIssued = reader.GetBoolean(5);
            assigneeId = reader.GetInt64(6); startStationId = reader.GetInt64(7); expired = reader.GetInt64(8);
            startingPrice = reader.GetDecimal(9); buyoutPrice = reader.GetDecimal(10); currentAcceptor = reader.GetInt64(11);
            issuerWalletKey = reader.GetInt32(12);
        }
        if (type != 2) throw new ContractUserErrorException("ConIllegalType");
        if (status == 4 && currentAcceptor == bidderId) return true;
        if (status != 0 || expired < nowFileTime) throw new ContractUserErrorException("ConContractNotAvailable");
        if (availability != 0 && assigneeId != characterId && assigneeId != corporationId && (allianceId == 0 || assigneeId != allianceId))
            throw new ContractUserErrorException("ConContractNotAvailable");
        if ((!isCorporationIssued && issuerId == characterId) || (isCorporationIssued && issuerCorporationId == corporationId))
            throw new ContractUserErrorException("ConCannotAcceptOwnContract");

        long previousBidId = 0, previousBidderId = 0;
        decimal previousAmount = 0;
        int previousWalletKey = 1000;
        bool previousForCorporation = false;
        await using (var command = new NpgsqlCommand($"""
            SELECT bid_id, bidder_id, amount, wallet_account_key, bidder_is_corporation
            FROM {_schema}.contract_bids WHERE contract_id = $1 AND NOT is_refunded
            ORDER BY amount DESC, created_at_filetime DESC, bid_id DESC LIMIT 1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                previousBidId = reader.GetInt64(0); previousBidderId = reader.GetInt64(1); previousAmount = reader.GetDecimal(2);
                previousWalletKey = reader.GetInt32(3); previousForCorporation = reader.GetBoolean(4);
            }
        }
        if (previousBidId > 0 && previousBidderId == bidderId && previousAmount == amount)
            return await CommitBidReplayAsync(transaction, cancellationToken);
        var increment = Math.Max(decimal.Truncate(startingPrice * 0.1m), 1000m);
        var nextBid = previousAmount + increment;
        if (buyoutPrice > 0) nextBid = Math.Min(nextBid, buyoutPrice);
        var minimum = Math.Max(startingPrice, nextBid);
        if (amount < minimum || buyoutPrice > 0 && amount > buyoutPrice)
            throw new ContractUserErrorException("ConBidTooLow");
        var isBuyout = buyoutPrice > 0 && amount == buyoutPrice;
        var issuerOwner = isCorporationIssued ? issuerCorporationId : issuerId;
        var bidderWallet = new WalletAccount(bidderId, bidForCorporation ? bidderWalletKey : 1000);
        if (previousBidId > 0)
        {
            var previousWalletOwner = previousBidderId;
            await wallets.AdjustAsync(connection, transaction,
                new WalletAccount(previousWalletOwner, previousForCorporation ? previousWalletKey : 1000), previousAmount,
                bidderId, 85, "Contract auction bid refunded", nowMs,
                $"contract:{contractId}:bid:{previousBidId}:refund", $"contract:{contractId}", cancellationToken,
                referenceId: contractId);
            await using var refund = new NpgsqlCommand($"UPDATE {_schema}.contract_bids SET is_refunded = true WHERE bid_id = $1", connection, transaction);
            refund.Parameters.AddWithValue(previousBidId);
            await refund.ExecuteNonQueryAsync(cancellationToken);
        }
        await wallets.AdjustAsync(connection, transaction, bidderWallet, -amount, issuerOwner, 85, "Contract auction bid escrowed", nowMs,
            $"contract:{contractId}:bid:{Guid.NewGuid():N}", $"contract:{contractId}", cancellationToken, referenceId: contractId);
        long bidId;
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.contract_bids (contract_id, bidder_id, amount, created_at_filetime, bidder_is_corporation, wallet_account_key)
            VALUES ($1, $2, $3, $4, $5, $6) RETURNING bid_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(contractId); command.Parameters.AddWithValue(bidderId); command.Parameters.AddWithValue(amount);
            command.Parameters.AddWithValue(nowFileTime); command.Parameters.AddWithValue(bidForCorporation);
            command.Parameters.AddWithValue(bidForCorporation ? bidderWalletKey : 1000);
            bidId = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("contract bid insert returned no id"));
        }
        if (isBuyout)
        {
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(issuerOwner, issuerWalletKey), amount, bidderId, 85,
                "Contract auction buyout", nowMs, $"contract:{contractId}:bid:{bidId}:buyout", $"contract:{contractId}", cancellationToken,
                referenceId: contractId);
            var offered = new List<long>();
            await using (var command = new NpgsqlCommand($"SELECT item_id FROM {_schema}.contract_items WHERE contract_id = $1 AND in_crate ORDER BY item_id", connection, transaction))
            {
                command.Parameters.AddWithValue(contractId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) offered.Add(reader.GetInt64(0));
            }
            foreach (var itemId in offered)
                await items.MoveToOwnerAsync(connection, transaction, itemId, bidderId, startStationId, bidForCorporation ? 62 : 4, cancellationToken);
            await using (var command = new NpgsqlCommand($"UPDATE {_schema}.contract_items SET in_crate = false WHERE contract_id = $1 AND in_crate", connection, transaction))
            {
                command.Parameters.AddWithValue(contractId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var command = new NpgsqlCommand($"UPDATE {_schema}.contract_records SET status = 4, acceptor_id = $2, date_accepted_filetime = $3, reward_escrow = 0 WHERE contract_id = $1 AND status = 0", connection, transaction))
            {
                command.Parameters.AddWithValue(contractId); command.Parameters.AddWithValue(bidderId); command.Parameters.AddWithValue(nowFileTime);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ContractUserErrorException("ConContractNotAvailable");
            }
        }
        if (previousBidId > 0)
            await PublishNotificationAsync(connection, transaction, previousForCorporation ? 0 : previousBidderId,
                previousForCorporation ? previousBidderId : 0, 0, "OnContractOutbid", contractId, cancellationToken);
        if (isBuyout)
            await PublishNotificationAsync(connection, transaction, isCorporationIssued ? 0 : issuerId,
                isCorporationIssued ? issuerCorporationId : 0, 0, "OnContractBuyout", contractId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        events?.Wake();
        return true;
    }

    public async ValueTask<bool> SplitStackAsync(long itemId, long quantity, long characterId, long corporationId,
        long stationId, bool forCorporation, int flag, CancellationToken cancellationToken)
    {
        if (itemId <= 0 || quantity <= 0 || characterId <= 0 || corporationId <= 0 || stationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(itemId));
        if (forCorporation ? !ItemFlags.IsCorporationHangar(flag) : flag != ItemFlags.Hangar)
            throw new ContractUserErrorException("ConContractNotAvailable");

        var ownerId = forCorporation ? corporationId : characterId;
        var item = await items.GetAsync(itemId, cancellationToken);
        if (item is null || item.OwnerId != ownerId || item.Flag != flag)
            throw new ContractUserErrorException("ConContractNotFound");
        if (item.LocationId != stationId)
            throw new ContractUserErrorException("ConNotInStation");
        if (item.Singleton || item.Quantity < quantity)
            throw new ContractUserErrorException("ConContractNotAvailable");

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await items.MoveStackAsync(connection, transaction, item, quantity, ownerId, stationId, flag, cancellationToken);
        }
        catch (InvalidOperationException error) when (error.Message is "ITEM_CHANGED" or "ITEM_LOCKED")
        {
            throw new ContractUserErrorException("ConContractNotAvailable");
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async ValueTask<bool> CommitBidReplayAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private Task<long> PublishNotificationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, long corporationId, long allianceId, string name, long contractId, CancellationToken cancellationToken) =>
        events is null ? Task.FromResult(0L) : events.PublishAsync(connection, transaction, ContractNotifications.Topic,
            new ContractNotifications.Payload(name, contractId, characterId, corporationId, allianceId),
            $"contract:{contractId}:{name}:{characterId}:{corporationId}:{allianceId}", cancellationToken);

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
