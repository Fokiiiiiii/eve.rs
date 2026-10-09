using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

sealed class PersistenceInsuranceWriteModel(
    NpgsqlDataSource source,
    PgItemStore items,
    PersistenceStore store,
    OwnerFence ownerFence,
    WalletTransactions wallets,
    Eve.Services.Core.IInsuranceReadModel readModel,
    Func<long> nowUnixMs) : Eve.Services.Core.IInsuranceWriteModel, Eve.Inventory.IShipInsuranceInvalidator
{
    private const string Table = "insuranceContracts";
    private const string ContractsRow = "contractsByShipID";
    private const string SettlementsRow = "settlementsByLossID";
    private const int InsuranceRefType = 0;
    // ponytail: serialize the single GameStore contract document; shard rows if claim volume requires concurrency.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<Eve.Services.Core.InsuranceMutationResult> InsureShipAsync(
        Eve.Services.Core.InsurancePurchaseCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ShipId <= 0 || command.PremiumCents <= 0 ||
            command.StartDateFileTime <= 0 || command.EndDateFileTime <= command.StartDateFileTime ||
            !command.Actor.IsDocked || command.Actor.CharacterId <= 0 ||
            (command.IsCorporation && !CanInsureForCorporation(command.Actor)))
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var ship = await items.LockAndGetAsync(connection, transaction, command.ShipId, cancellationToken);
            var ownerKind = command.IsCorporation
                ? Eve.Services.Core.InsuranceOwnerKind.Corporation
                : Eve.Services.Core.InsuranceOwnerKind.Character;
            var ownerId = command.IsCorporation ? command.Actor.CorporationId : command.Actor.CharacterId;
            if (ship is null || ship.OwnerId != ownerId || !AtActorLocation(ship.LocationId, command.Actor) ||
                !CorrectHangar(command.IsCorporation, ship.Flag))
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible, ownerId);

            await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
            var prices = await readModel.GetInsurancePricesCentsAsync([ship.TypeId], cancellationToken);
            if (!prices.TryGetValue(ship.TypeId, out var fullPayoutCents) || fullPayoutCents <= 0 ||
                Eve.Services.Core.InsuranceQuotes.ForPremium(fullPayoutCents, command.PremiumCents) is not { } quote)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible, ownerId);

            var state = await store.LoadTableAsync(Table, ct: cancellationToken);
            var contracts = state[ContractsRow] as JsonObject ?? new JsonObject();
            var shipKey = Key(command.ShipId);
            var current = contracts[shipKey] as JsonObject;
            var currentContract = Contract(current, command.ShipId);
            if (ActiveAt(currentContract, command.StartDateFileTime))
            {
                if (!command.ReplaceExisting)
                    return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyInsured, currentContract!.OwnerId);
                current!["status"] = "void";
            }

            var contractId = NextContractId(contracts);
            if (contractId <= 0)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible, ownerId);
            contracts[shipKey] = new JsonObject
            {
                ["contractID"] = contractId,
                ["shipID"] = command.ShipId,
                ["typeID"] = ship.TypeId,
                ["ownerID"] = ownerId,
                ["ownerKind"] = ownerKind == Eve.Services.Core.InsuranceOwnerKind.Corporation ? "corporation" : "character",
                ["fraction"] = quote.FractionPercent / 100d,
                ["startDate"] = command.StartDateFileTime,
                ["endDate"] = command.EndDateFileTime,
                ["status"] = "active",
            };

            var balance = await wallets.AdjustAsync(
                connection, transaction, ownerId, -command.PremiumCents / 100m, 0,
                InsuranceRefType, "Insurance premium", nowUnixMs(),
                $"insurance:premium:{command.ShipId}:{contractId}", cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, Table,
                [new RowUpsert(ContractsRow, contracts.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            wallets.AnnounceCommitted(new WalletBalanceChange(ownerId, 1000, balance));
            return Result(Eve.Services.Core.InsuranceMutationStatus.Applied, ownerId, command.PremiumCents);
        }
        catch (Exception error) when (error is Eve.Wallet.InsufficientFundsException or
                                      Eve.Wallet.WalletNotFoundException or
                                      Eve.Wallet.MoneyLimitException or
                                      Eve.Wallet.WalletOperationConflictException)
        {
            return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Eve.Services.Core.InsuranceMutationResult> UnInsureShipAsync(
        Eve.Services.Core.InsuranceCancellationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ShipId <= 0 || command.RequestedAtFileTime <= 0 || !command.Actor.IsDocked || command.Actor.CharacterId <= 0)
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var ship = await items.LockAndGetAsync(connection, transaction, command.ShipId, cancellationToken);
            if (ship is null || !AtActorLocation(ship.LocationId, command.Actor))
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);

            await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
            var state = await store.LoadTableAsync(Table, ct: cancellationToken);
            var contracts = state[ContractsRow] as JsonObject;
            var key = Key(command.ShipId);
            if (contracts?[key] is not JsonObject row || Contract(row, command.ShipId) is not { } contract)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NoActiveContract);
            if (string.Equals(contract.Status, "void", StringComparison.OrdinalIgnoreCase))
                return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyApplied, contract.OwnerId);
            if (!ActiveAt(contract, command.RequestedAtFileTime))
                return Result(Eve.Services.Core.InsuranceMutationStatus.NoActiveContract, contract.OwnerId);
            if (contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Character &&
                contract.OwnerId != command.Actor.CharacterId ||
                contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Corporation &&
                (contract.OwnerId != command.Actor.CorporationId || !CanInsureForCorporation(command.Actor)))
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotAuthorized, contract.OwnerId);

            row["status"] = "void";
            await store.ApplyChangesAsync(
                connection, transaction, Table,
                [new RowUpsert(ContractsRow, contracts!.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result(Eve.Services.Core.InsuranceMutationStatus.Applied, contract.OwnerId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Eve.Services.Core.InsuranceMutationResult> SettleShipLossAsync(
        Eve.Services.Core.InsuranceLossSettlementRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LossId <= 0 || request.ShipId <= 0 || request.LossDateFileTime <= 0)
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var ship = await items.LockAndGetAsync(connection, transaction, request.ShipId, cancellationToken);
            await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
            var state = await store.LoadTableAsync(Table, ct: cancellationToken);
            var contracts = state[ContractsRow] as JsonObject ?? new JsonObject();
            var settlements = state[SettlementsRow] as JsonObject ?? new JsonObject();
            var lossKey = Key(request.LossId);
            if (settlements[lossKey] is JsonObject settled)
                return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyApplied,
                    Number(settled["ownerID"]), Number(settled["amountCents"]));

            var shipKey = Key(request.ShipId);
            var contractRow = contracts[shipKey] as JsonObject;
            var contract = Contract(contractRow, request.ShipId);
            var typeId = ship?.TypeId ?? contract?.TypeId ?? 0;
            if (typeId <= 0)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);
            var prices = await readModel.GetInsurancePricesCentsAsync([typeId], cancellationToken);
            if (!prices.TryGetValue(typeId, out var fullPayoutCents) || fullPayoutCents <= 0)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);

            if (contract is not null && ship is not null &&
                (ship.OwnerId != contract.OwnerId ||
                 !CorrectHangar(contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Corporation, ship.Flag)))
                contract = null;
            var payoutCents = Eve.Services.Core.InsuranceQuotes.PayoutForLoss(
                fullPayoutCents, contract, request.LossDateFileTime, request.IsConcordDestruction);
            var ownerId = ship?.OwnerId ?? contract?.OwnerId ??
                (request.FinalOwnerId > 0 ? request.FinalOwnerId : request.FinalOwnerCorporationId);
            if (payoutCents > 0 && ownerId <= 0)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);

            decimal balance = 0;
            if (payoutCents > 0)
                balance = await wallets.AdjustAsync(
                    connection, transaction, ownerId, payoutCents / 100m, 0,
                    InsuranceRefType, "Insurance payout", nowUnixMs(),
                    $"insurance:payout:{request.LossId}", cancellationToken);

            if (contractRow is not null)
                contractRow["status"] = contract is not null && payoutCents > 0 ? "paid" : "void";
            settlements[lossKey] = new JsonObject
            {
                ["lossID"] = request.LossId,
                ["shipID"] = request.ShipId,
                ["ownerID"] = ownerId,
                ["amountCents"] = payoutCents,
                ["isConcordDestruction"] = request.IsConcordDestruction,
                ["settledAtFiletime"] = checked(116444736000000000L + nowUnixMs() * 10_000L),
                ["contractID"] = contract?.ContractId ?? 0,
            };
            await store.ApplyChangesAsync(
                connection, transaction, Table,
                [new RowUpsert(ContractsRow, contracts.ToJsonString()),
                 new RowUpsert(SettlementsRow, settlements.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (payoutCents > 0)
                wallets.AnnounceCommitted(new WalletBalanceChange(ownerId, 1000, balance));
            return Result(Eve.Services.Core.InsuranceMutationStatus.Applied, ownerId, payoutCents);
        }
        catch (Exception error) when (error is Eve.Wallet.WalletNotFoundException or
                                      Eve.Wallet.MoneyLimitException or
                                      Eve.Wallet.WalletOperationConflictException)
        {
            return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);
        }
        finally
        {
            _gate.Release();
        }
    }



    public async ValueTask InvalidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long itemId,
        Eve.Inventory.ShipContractInvalidationReason reason,
        CancellationToken cancellationToken)
    {
        var insuranceReason = reason switch
        {
            Eve.Inventory.ShipContractInvalidationReason.Repackaged => Eve.Services.Core.InsuranceInvalidationReason.Repackaged,
            Eve.Inventory.ShipContractInvalidationReason.OwnerChanged => Eve.Services.Core.InsuranceInvalidationReason.OwnerChanged,
            Eve.Inventory.ShipContractInvalidationReason.CorporationHangar => Eve.Services.Core.InsuranceInvalidationReason.PlacedInCorporationHangar,
            _ => (Eve.Services.Core.InsuranceInvalidationReason?)null,
        };
        if (itemId <= 0 || insuranceReason is not { } mappedReason) return;

        await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
        await InvalidateShipInTransactionAsync(
            connection,
            transaction,
            new Eve.Services.Core.InsuranceInvalidationRequest(
                itemId, mappedReason, Py.FileTimeFromUnixMs(nowUnixMs())),
            cancellationToken);
    }

    private static async Task LockInsuranceContractsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const long lockId = 0x455645494E535552L;
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction)
        {
            Parameters = { new() { Value = lockId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<Eve.Services.Core.InsuranceMutationResult> InvalidateShipInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Eve.Services.Core.InsuranceInvalidationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ShipId <= 0 || request.InvalidatedAtFileTime <= 0 ||
            !Enum.IsDefined(request.Reason) ||
            (request.Reason == Eve.Services.Core.InsuranceInvalidationReason.Boarded &&
             (request.ActorCharacterId <= 0 || request.ActorCorporationId is null)))
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        var state = await store.LoadTableAsync(Table, ct: cancellationToken);
        var contracts = state[ContractsRow] as JsonObject;
        var key = Key(request.ShipId);
        if (contracts?[key] is not JsonObject row || Contract(row, request.ShipId) is not { } contract)
            return Result(Eve.Services.Core.InsuranceMutationStatus.NoActiveContract);
        if (!string.Equals(contract.Status, "active", StringComparison.OrdinalIgnoreCase))
            return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyApplied, contract.OwnerId);

        var shouldInvalidate = request.Reason switch
        {
            Eve.Services.Core.InsuranceInvalidationReason.Repackaged => true,
            Eve.Services.Core.InsuranceInvalidationReason.OwnerChanged => true,
            Eve.Services.Core.InsuranceInvalidationReason.PlacedInCorporationHangar =>
                contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Character,
            Eve.Services.Core.InsuranceInvalidationReason.Boarded =>
                contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Corporation &&
                request.ActorCorporationId != contract.OwnerId,
            _ => false,
        };
        if (!shouldInvalidate)
            return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyApplied, contract.OwnerId);

        row["status"] = "void";
        row["invalidatedAt"] = request.InvalidatedAtFileTime;
        row["invalidationReason"] = request.Reason.ToString();
        await store.ApplyChangesAsync(
            connection, transaction, Table,
            [new RowUpsert(ContractsRow, contracts!.ToJsonString())], [], ownerFence, cancellationToken);
        return Result(Eve.Services.Core.InsuranceMutationStatus.Applied, contract.OwnerId);
    }

    public async ValueTask<Eve.Services.Core.InsuranceMutationResult> InvalidateShipAsync(
        Eve.Services.Core.InsuranceInvalidationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ShipId <= 0 || request.InvalidatedAtFileTime <= 0 ||
            !Enum.IsDefined(request.Reason) ||
            (request.Reason == Eve.Services.Core.InsuranceInvalidationReason.Boarded &&
             (request.ActorCharacterId <= 0 || request.ActorCorporationId is null)))
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
            var result = await InvalidateShipInTransactionAsync(connection, transaction, request, cancellationToken);
            if (result.Status == Eve.Services.Core.InsuranceMutationStatus.Applied)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Eve.Services.Core.InsuranceMutationResult Result(
        Eve.Services.Core.InsuranceMutationStatus status, long ownerId = 0, long amountCents = 0) =>
        new(status, ownerId, amountCents);

    private static bool CanInsureForCorporation(Eve.Services.Core.InsuranceActorSnapshot actor) =>
        actor.CorporationId > 0 &&
        (actor.CorporationRoleMask &
            (Eve.Corporations.CorporationRoles.Accountant | Eve.Corporations.CorporationRoles.JuniorAccountant)) != 0;

    private static bool AtActorLocation(long locationId, Eve.Services.Core.InsuranceActorSnapshot actor) =>
        locationId > 0 && (locationId == actor.StationId || locationId == actor.StructureId);


    private static bool CorrectHangar(bool corporationInsurance, int flag) =>
        Eve.Inventory.ItemFlags.IsCorporationHangar(flag) == corporationInsurance;

    private static long NextContractId(JsonObject contracts)
    {
        var highest = contracts.Select(pair => pair.Value as JsonObject)
            .Where(row => row is not null)
            .Select(row => Number(row!["contractID"]))
            .DefaultIfEmpty(0)
            .Max();
        return highest == long.MaxValue ? 0 : highest + 1;
    }

    private static Eve.Services.Core.InsuranceContractSnapshot? Contract(JsonObject? row, long fallbackShipId)
    {
        if (row is null)
            return null;
        var shipId = Number(row["shipID"]);
        if (shipId <= 0) shipId = fallbackShipId;
        var ownerKind = string.Equals(Text(row["ownerKind"]), "corporation", StringComparison.OrdinalIgnoreCase)
            ? Eve.Services.Core.InsuranceOwnerKind.Corporation
            : Eve.Services.Core.InsuranceOwnerKind.Character;
        var ownerId = Number(row["ownerID"]);
        var typeId = Number(row["typeID"]);
        if (shipId <= 0 || ownerId <= 0 || typeId <= 0)
            return null;
        return new Eve.Services.Core.InsuranceContractSnapshot(
            Number(row["contractID"]), shipId, typeId, ownerId, ownerKind,
            NumberDouble(row["fraction"]), Number(row["startDate"]), Number(row["endDate"]),
            Text(row["status"]) is { Length: > 0 } status ? status : "active");
    }

    private static bool ActiveAt(Eve.Services.Core.InsuranceContractSnapshot? contract, long fileTime) =>
        fileTime > 0 && contract is { ShipId: > 0, TypeId: > 0, OwnerId: > 0 } &&
        Enum.IsDefined(contract.OwnerKind) && contract.Fraction > 0 && contract.Fraction <= 1 &&
        string.Equals(contract.Status, "active", StringComparison.OrdinalIgnoreCase) &&
        contract.StartDateFileTime > 0 && contract.StartDateFileTime <= fileTime && contract.EndDateFileTime > fileTime;

    private static long Number(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static double NumberDouble(JsonNode? value) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";

    private static string Key(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
