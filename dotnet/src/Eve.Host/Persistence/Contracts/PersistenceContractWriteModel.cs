using System.Text.RegularExpressions;
using Eve.Inventory;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Creates a personal or corporation item exchange and stages its offered items and ISK atomically.</summary>
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

    public async ValueTask<CreatedContract> CreateItemExchangeAsync(
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
        long contractId;
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.contract_records
                (issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id, assignee_id,
                 contract_type, status, start_station_id, end_station_id, date_accepted_filetime,
                 date_expired_filetime, num_days, reward_escrow, created_at_filetime,
                 availability, price_isk, collateral_isk, creation_fee_isk, deposit_isk, sales_tax_isk,
                 title, description)
            VALUES ($1, $2, $3, 0, $4, 1, 0, $5, 0, 0, $6, 0, $7, $8,
                    $9, $10, 0, $11, $12, $13, $14, $15)
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
                throw new InvalidOperationException("CONTRACT_ITEM_UNAVAILABLE");

            var moved = await items.MoveStackAsync(connection, transaction, item, offered.Quantity,
                EscrowOwnerId, escrowLocationId, 0, cancellationToken);
            escrowItemIds.Add(moved.Id);
            await using var itemCommand = new NpgsqlCommand($"""
                INSERT INTO {_schema}.contract_items (contract_id, item_id, type_id, quantity, in_crate, is_package)
                VALUES ($1, $2, $3, $4, true, false)
                """, connection, transaction);
            itemCommand.Parameters.AddWithValue(contractId);
            itemCommand.Parameters.AddWithValue(moved.Id);
            itemCommand.Parameters.AddWithValue(moved.TypeId);
            itemCommand.Parameters.AddWithValue(moved.Singleton ? 1L : moved.Quantity);
            await itemCommand.ExecuteNonQueryAsync(cancellationToken);
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
