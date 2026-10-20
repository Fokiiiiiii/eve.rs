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
                 date_expired_filetime, num_days, reward_escrow, created_at_filetime,
                 availability, price_isk, collateral_isk, creation_fee_isk, deposit_isk, sales_tax_isk,
                 title, description)
            VALUES ($1, $2, $3, 0, $4, $16, 0, $5, $17, 0, $6, $18, $7, $8,
                    $9, $10, $19, $11, $12, $13, $14, $15)
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
