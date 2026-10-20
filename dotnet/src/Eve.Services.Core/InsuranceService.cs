using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public enum InsuranceOwnerKind
{
    Character,
    Corporation
}

/// <summary>Read-only projection of one persisted ship insurance contract.</summary>
/// <param name="StartDateFileTime">Contract start as a Windows FILETIME.</param>
/// <param name="EndDateFileTime">Contract end as a Windows FILETIME.</param>
public sealed record InsuranceContractSnapshot(
    long ContractId,
    long ShipId,
    long TypeId,
    long OwnerId,
    InsuranceOwnerKind OwnerKind,
    double Fraction,
    long StartDateFileTime,
    long EndDateFileTime,
    string Status);

public sealed record InsuranceActorSnapshot(
    long CharacterId,
    long CorporationId,
    long CorporationRoleMask,
    long StationId,
    long StructureId,
    long SolarSystemId)
{
    public bool IsDocked => StationId > 0 || StructureId > 0;
}

public sealed record InsuranceQuote(int FractionPercent, long PremiumCents, long PayoutCents);

/// <summary>
    /// Maps the authoritative full payout value to CCP's published insurance tiers.
    /// Premiums follow the published schedule: each 10-point increase above the 40% base
    /// costs 5% of the full payout value.
    /// </summary>
    public static class InsuranceQuotes
    {
        private static readonly int[] FractionPercents = [50, 60, 70, 80, 90, 100];

        public static IReadOnlyList<InsuranceQuote> ForFullPayoutValue(long fullPayoutValueCents)
        {
            if (fullPayoutValueCents <= 0)
            {
                return [];
            }

            return FractionPercents.Select(fractionPercent => new InsuranceQuote(
                fractionPercent,
                RoundCents((decimal)fullPayoutValueCents * (fractionPercent - 40) / 200m),
                RoundCents((decimal)fullPayoutValueCents * fractionPercent / 100m))).ToArray();
        }

        public static InsuranceQuote? ForPremium(long fullPayoutValueCents, long premiumCents)
        {
            if (premiumCents < 0)
            {
                return null;
            }

            var matches = ForFullPayoutValue(fullPayoutValueCents)
                .Where(quote => quote.PremiumCents == premiumCents)
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        public static long PayoutForLoss(
            long fullPayoutValueCents,
            InsuranceContractSnapshot? contract,
            long lossDateFileTime,
            bool isConcordDestruction)
        {
            if (fullPayoutValueCents <= 0 || lossDateFileTime <= 0 || isConcordDestruction)
            {
                return 0;
            }

            var fraction = IsActiveAt(contract, lossDateFileTime) ? contract!.Fraction : 0.4d;
            return RoundCents((decimal)fullPayoutValueCents * (decimal)fraction);
        }

        internal static bool IsCoverageFraction(double fraction) =>
            double.IsFinite(fraction) &&
            FractionPercents.Any(percent => Math.Abs(fraction - percent / 100d) < 1e-9);

        internal static bool IsActiveAt(InsuranceContractSnapshot? contract, long fileTime) =>
            fileTime > 0 &&
            contract is { ShipId: > 0, TypeId: > 0, OwnerId: > 0 } &&
            Enum.IsDefined(contract.OwnerKind) &&
            IsCoverageFraction(contract.Fraction) &&
            string.Equals(contract.Status, "active", StringComparison.OrdinalIgnoreCase) &&
            contract.StartDateFileTime > 0 &&
            contract.StartDateFileTime <= fileTime &&
            contract.EndDateFileTime > fileTime;

        private static long RoundCents(decimal cents) =>
            checked((long)decimal.Round(cents, 0, MidpointRounding.AwayFromZero));
    }

public sealed record InsurancePurchaseCommand(
    InsuranceActorSnapshot Actor,
    long ShipId,
    long PremiumCents,
    bool IsCorporation,
    bool ReplaceExisting,
    long StartDateFileTime,
    long EndDateFileTime);

public sealed record InsuranceCancellationCommand(
    InsuranceActorSnapshot Actor,
    long ShipId,
    long RequestedAtFileTime);

public sealed record InsuranceLossSettlementRequest(
    long LossId,
    long ShipId,
    long LossDateFileTime,
    bool IsConcordDestruction,
    long FinalOwnerId,
    long FinalOwnerCorporationId);

public enum InsuranceMutationStatus
{
    Applied,
    AlreadyApplied,
    AlreadyInsured,
    NoActiveContract,
    NotAuthorized,
    InvalidRequest,
    NotEligible
}

public enum InsuranceInvalidationReason
{
    Repackaged,
    OwnerChanged,
    PlacedInCorporationHangar,
    Boarded
}

/// <summary>
/// Reports a ship lifecycle event that can void an active insurance contract.
/// For <see cref="InsuranceInvalidationReason.Boarded"/>, the actor corporation is
/// compared with the insured corporation; a same-corporation or character-owned
/// contract must not be invalidated.
/// </summary>

public sealed record InsuranceInvalidationRequest(
    long ShipId,
    InsuranceInvalidationReason Reason,
    long InvalidatedAtFileTime,
    long ActorCharacterId = 0,
    long? ActorCorporationId = null);

public sealed record InsuranceMutationResult(
    InsuranceMutationStatus Status,
    long RelatedOwnerId = 0,
    long AmountCents = 0);

/// <summary>
/// Host callbacks must perform each mutation in one database transaction. Purchase debits the


/// transaction. Each callback must re-read and validate the authoritative ship, price, and contract
/// rows under the write transaction. No parallel insurance ledger is introduced.
/// </summary>
/// <summary>
    /// Implements insurance changes against existing GameStore/wallet authorities. Each mutation
    /// must read, validate, update the contract/history/payout maps, and write any wallet journal
    /// entry in one database transaction; a journal or persistence failure must roll the entire
    /// operation back.
    ///

    /// inside the transaction, map PremiumCents with InsuranceQuotes.ForPremium, debit the
    /// character or corporation wallet, and update insuranceContracts.contractsByShipID and
    /// contractHistoryByID. Replacement with voidOld must void the old policy and buy the new one
    /// in that same transaction.
    ///

    /// not publish a manual-cancellation refund formula, so implementations must not invent a
    /// prorated credit.
    ///
    /// Loss settlement must use payoutLedgerByLossID as the idempotency authority, enforce the
    /// existing contract's term and void conditions (including CONCORD and corporation boarding),

    /// The insuranceContracts grouped table already provides all three maps; no parallel insurance
    /// table or SQL migration is required.
    /// </summary>
    /// <summary>
    /// Implements insurance changes against existing GameStore/wallet authorities. Each mutation
    /// must read, validate, update the contract/history/payout maps, and write any wallet journal
    /// entry in one database transaction; a journal or persistence failure must roll the entire
    /// operation back. Use WalletTransactions.AdjustAsync with that same connection and transaction.
    ///

    /// inside the transaction, map PremiumCents with InsuranceQuotes.ForPremium, debit the
    /// character or corporation wallet, and update insuranceContracts.contractsByShipID and
    /// contractHistoryByID. Replacement with voidOld must void the old policy and buy the new one
    /// in that same transaction.
    ///

    /// not publish a manual-cancellation refund formula, so implementations must not invent a
    /// prorated credit.
    ///
    /// Loss settlement must use payoutLedgerByLossID as the idempotency authority, enforce the
    /// existing contract's term and void conditions (including CONCORD and corporation boarding),

    /// The insuranceContracts grouped table already provides all three maps; no parallel insurance
    /// table or SQL migration is required.
    /// </summary>
    public interface IInsuranceWriteModel
    {
        ValueTask<InsuranceMutationResult> InsureShipAsync(
            InsurancePurchaseCommand command,
            CancellationToken cancellationToken);

        ValueTask<InsuranceMutationResult> UnInsureShipAsync(
            InsuranceCancellationCommand command,
            CancellationToken cancellationToken);

        ValueTask<InsuranceMutationResult> SettleShipLossAsync(
            InsuranceLossSettlementRequest request,
            CancellationToken cancellationToken);

        ValueTask<InsuranceMutationResult> InvalidateShipAsync(
            InsuranceInvalidationRequest request,
            CancellationToken cancellationToken);
    }

/// <summary>Host-owned read model for insurance price authority and live contracts.</summary>
public interface IInsuranceReadModel
{
    ValueTask<IReadOnlyDictionary<long, long>> GetInsurancePricesCentsAsync(
        IReadOnlyCollection<long> typeIds,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<InsuranceContractSnapshot>> GetContractsAsync(
        long ownerId,
        InsuranceOwnerKind ownerKind,
        CancellationToken cancellationToken);

    ValueTask<InsuranceContractSnapshot?> GetContractForShipAsync(
        long shipId,
        CancellationToken cancellationToken);
}

/// <summary>Read-only insurance price and contract RPCs matching the EVE client wire contract.</summary>
[Service("insuranceSvc")]
public sealed partial class InsuranceService
{
    public const long CorporationAccountantRole = 256;
    public const long CorporationJuniorAccountantRole = 4_503_599_627_370_496;

    private static readonly Regex PickledIntegers = new(@"(?:^|\n)(?:I(-?\d+)\na|L(-?\d+)L\na)", RegexOptions.CultureInvariant);

private readonly IInsuranceWriteModel? _writeModel;
    private readonly IInsuranceReadModel _readModel;
    private readonly Func<long> _nowUnixMs;

    public InsuranceService(
        IInsuranceReadModel readModel,
        Func<long> nowUnixMs,
        IInsuranceWriteModel? writeModel = null)
    {
        _readModel = readModel;
        _writeModel = writeModel;
        _nowUnixMs = nowUnixMs;
    }

    private const int MaximumInsurancePriceTypeIds = 500;

    [Method("GetInsurancePrice")]
    public async ValueTask<PyValue> GetInsurancePrice(ServiceCall call)
    {
        var typeId = PositiveId(call.Arg(0));
        if (typeId == 0)
        {
            return Py.Number(0);
        }

        var prices = await _readModel.GetInsurancePricesCentsAsync([typeId], call.Cancellation);
        return Py.Number(ToIsk(prices.GetValueOrDefault(typeId)));
    }

    [Method("GetInsurancePrices")]
    public async ValueTask<PyValue> GetInsurancePrices(ServiceCall call)
    {
        var typeIds = ReadTypeIds(call.Arg(0));
        if (typeIds.Length == 0)
        {
            return new PyDict([]);
        }

        var prices = await _readModel.GetInsurancePricesCentsAsync(typeIds, call.Cancellation);
        return new PyDict([.. typeIds.Select(typeId =>
            new KeyValuePair<PyValue, PyValue>(
                Py.Number(typeId),
                Py.Number(ToIsk(prices.GetValueOrDefault(typeId)))))]);
    }

    [Method("GetContracts")]
    public async ValueTask<PyValue> GetContracts(ServiceCall call)
    {
        var isCorporation = IntegerValue(call.Arg(0)) > 0;
        var ownerKind = isCorporation ? InsuranceOwnerKind.Corporation : InsuranceOwnerKind.Character;
        var ownerId = SessionOwnerId(call.Session, ownerKind);
        if (ownerId == 0 || (isCorporation && !HasCorporationInsuranceRole(call.Session)))
        {
            return new PyList([]);
        }

        var nowFileTime = Py.FileTimeFromUnixMs(_nowUnixMs());
        var contracts = await _readModel.GetContractsAsync(ownerId, ownerKind, call.Cancellation);
        return new PyList([.. contracts
            .Select(NormalizeContract)
            .Where(contract => contract is not null &&
                contract.OwnerId == ownerId &&
                contract.OwnerKind == ownerKind &&
                IsActive(contract, nowFileTime))
            .Select(contract => contract!)
            .DistinctBy(contract => contract.ShipId)
            .OrderBy(contract => contract.ShipId)
            .Select(ToClientContract)]);
    }

    [Method("GetContractForShip")]
    public async ValueTask<PyValue> GetContractForShip(ServiceCall call)
    {
        var shipId = PositiveId(call.Arg(0));
        if (shipId == 0)
        {
            return PyNone.Instance;
        }

        var contract = NormalizeContract(await _readModel.GetContractForShipAsync(shipId, call.Cancellation));
        if (contract is null || contract.ShipId != shipId || !IsActive(contract, Py.FileTimeFromUnixMs(_nowUnixMs())))
        {
            return PyNone.Instance;
        }

        var ownerId = SessionOwnerId(call.Session, contract.OwnerKind);
        if (ownerId != contract.OwnerId ||
            (contract.OwnerKind == InsuranceOwnerKind.Corporation && !HasCorporationInsuranceRole(call.Session)))
        {
            return PyNone.Instance;
        }

        return ToClientContract(contract);
    }


    [Method("InsureShip")]
    public async ValueTask<PyValue> InsureShip(ServiceCall call)
    {
        var shipId = PositiveId(call.Arg(0));
        if (shipId == 0 || !TryIskAmountCents(call.Arg(1), out var premiumCents) ||
            !TryBoolean(call.Arg(2), out var isCorporation))
        {
            throw Eve.Services.MachoErrors.ThrowUserError("InsureShipFailed");
        }

        var replaceValue = call.Kwarg("voidOld") ?? call.Arg(3);
        var replaceExisting = false;
        if (replaceValue is not null && replaceValue is not PyNone &&
            !TryBoolean(replaceValue, out replaceExisting))
        {
            throw Eve.Services.MachoErrors.ThrowUserError("InsureShipFailed");
        }

        var actor = Actor(call.Session);
        if (!actor.IsDocked || actor.CharacterId <= 0 ||
            (isCorporation && (actor.CorporationId <= 0 || !HasCorporationInsuranceRole(call.Session))))
        {
            throw Eve.Services.MachoErrors.ThrowUserError("InsureShipFailed");
        }

        var nowUnixMs = _nowUnixMs();
        var nowFileTime = Py.FileTimeFromUnixMs(nowUnixMs);
        var endFileTime = Py.FileTimeFromUnixMs(checked(nowUnixMs + 84L * 24 * 60 * 60 * 1000));
        var result = await WriteModel().InsureShipAsync(
            new InsurancePurchaseCommand(
                actor,
                shipId,
                premiumCents,
                isCorporation,
                replaceExisting,
                nowFileTime,
                endFileTime),
            call.Cancellation);
        return PurchaseResult(result);
    }

    [Method("UnInsureShip")]
    public async ValueTask<PyValue> UnInsureShip(ServiceCall call)
    {
        var shipId = PositiveId(call.Arg(0));
        var actor = Actor(call.Session);
        if (shipId == 0 || !actor.IsDocked || actor.CharacterId <= 0)
        {
            throw Eve.Services.MachoErrors.ThrowUserError("UnInsureShipFailed");
        }

        var result = await WriteModel("UnInsureShipFailed").UnInsureShipAsync(
            new InsuranceCancellationCommand(actor, shipId, Py.FileTimeFromUnixMs(_nowUnixMs())),
            call.Cancellation);
        return result.Status is InsuranceMutationStatus.Applied or InsuranceMutationStatus.AlreadyApplied
            ? PyNone.Instance
            : throw Eve.Services.MachoErrors.ThrowUserError("UnInsureShipFailed");
    }

    /// <summary>Called by the authoritative ship-loss path; not exposed as a client RPC.</summary>
    public ValueTask<InsuranceMutationResult> SettleShipLossAsync(
        InsuranceLossSettlementRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LossId <= 0 || request.ShipId <= 0 || request.LossDateFileTime <= 0)
            return ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.InvalidRequest));
        if (request.IsConcordDestruction)
            return ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.NotEligible));

        return WriteModel().SettleShipLossAsync(request, cancellationToken);
    }

    public ValueTask<InsuranceMutationResult> InvalidateShipAsync(
        InsuranceInvalidationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ShipId <= 0 ||
            request.InvalidatedAtFileTime <= 0 ||
            !Enum.IsDefined<InsuranceInvalidationReason>(request.Reason) ||
            (request.Reason == InsuranceInvalidationReason.Boarded &&
                (request.ActorCharacterId <= 0 ||
                    request.ActorCorporationId is null ||
                    request.ActorCorporationId < 0)))
        {
            return ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.InvalidRequest));
        }

        return WriteModel().InvalidateShipAsync(request, cancellationToken);
    }

    private IInsuranceWriteModel WriteModel(string errorName = "InsureShipFailed") =>
        _writeModel ?? throw Eve.Services.MachoErrors.ThrowUserError(errorName);

    private static PyValue PurchaseResult(InsuranceMutationResult result) =>
        result.Status switch
        {
            InsuranceMutationStatus.Applied or InsuranceMutationStatus.AlreadyApplied => PyNone.Instance,
            InsuranceMutationStatus.AlreadyInsured => throw Eve.Services.MachoErrors.ThrowUserError(
                "InsureShipFailedSingleContract",
                ("ownerName", Py.Number(result.RelatedOwnerId))),
            _ => throw Eve.Services.MachoErrors.ThrowUserError("InsureShipFailed")
        };

    private static InsuranceActorSnapshot Actor(ClientSession session)
    {
        var roleMask = IntegerValue(session.Get("corprole"));
        if (roleMask == 0)
        {
            roleMask = IntegerValue(session.Get("corpRole"));
        }

        return new InsuranceActorSnapshot(
            PositiveSessionValue(session, "characterID", "charID", "charid"),
            PositiveSessionValue(session, "corporationID", "corpid"),
            roleMask,
            PositiveSessionValue(session, "stationid"),
            PositiveSessionValue(session, "structureid"),
            PositiveSessionValue(session, "solarsystemid2", "solarsystemid"));
    }

    private static bool TryBoolean(PyValue? value, out bool result)
    {
        switch (value)
        {
            case PyBool boolean:
                result = boolean.Value;
                return true;
            case PyInt integer when integer.Value is 0 or 1:
                result = integer.Value == 1;
                return true;
            case PyLong integer when integer.Value is 0 or 1:
                result = integer.Value == 1;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryIskAmountCents(PyValue? value, out long cents)
    {
        cents = 0;
        decimal isk;
        try
        {
            switch (value)
            {
                case PyInt integer:
                    isk = integer.Value;
                    break;
                case PyLong integer:
                    isk = integer.Value;
                    break;
                case PyReal real when double.IsFinite(real.Value):
                    isk = (decimal)real.Value;
                    break;
                default:
                    return false;
            }

            if (isk < 0)
            {
                return false;
            }

            var amount = decimal.Round(isk * 100m, 0, MidpointRounding.AwayFromZero);
            if (amount > long.MaxValue)
            {
                return false;
            }

            cents = (long)amount;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static PyValue ToClientContract(InsuranceContractSnapshot contract) => Py.KeyVal(
        ("contractID", Py.Number(contract.ContractId)),
        ("shipID", Py.Number(contract.ShipId)),
        ("typeID", Py.Number(contract.TypeId)),
        ("ownerID", Py.Number(contract.OwnerId)),
        ("fraction", Py.Number(contract.Fraction)),
        ("startDate", Py.FileTimeLong(contract.StartDateFileTime)),
        ("endDate", Py.FileTimeLong(contract.EndDateFileTime)));

    private static InsuranceContractSnapshot? NormalizeContract(InsuranceContractSnapshot? contract)
    {
        if (contract is null || contract.ShipId <= 0 || contract.TypeId <= 0 || contract.OwnerId <= 0)
        {
            return null;
        }

        var status = string.IsNullOrWhiteSpace(contract.Status)
            ? "active"
            : contract.Status.Trim().ToLowerInvariant();
        if (!InsuranceQuotes.IsCoverageFraction(contract.Fraction) ||
            !Enum.IsDefined(contract.OwnerKind) ||
            status is not ("active" or "paid" or "void"))
        {
            return null;
        }

        return contract with
        {
            ContractId = contract.ContractId > 0 ? contract.ContractId : contract.ShipId,
            Status = status
        };
    }

    private static bool IsActive(InsuranceContractSnapshot contract, long nowFileTime) =>
        InsuranceQuotes.IsActiveAt(contract, nowFileTime);

    private static double ToIsk(long cents) => Math.Max(0, cents) / 100d;

    private static long SessionOwnerId(ClientSession session, InsuranceOwnerKind kind) =>
        kind == InsuranceOwnerKind.Corporation
            ? PositiveSessionValue(session, "corporationID", "corpid")
            : PositiveSessionValue(session, "characterID", "charID", "charid");

    private static long PositiveSessionValue(ClientSession session, params string[] names)
    {
        foreach (var name in names)
        {
            var value = PositiveId(session.Get(name));
            if (value > 0)
            {
                return value;
            }
        }
        return 0;
    }

    private static bool HasCorporationInsuranceRole(ClientSession session)
    {
        var roleMask = IntegerValue(session.Get("corprole"));
        if (roleMask == 0)
        {
            roleMask = IntegerValue(session.Get("corpRole"));
        }
        return (roleMask & (CorporationAccountantRole | CorporationJuniorAccountantRole)) != 0;
    }

    private static long[] ReadTypeIds(PyValue value)
    {
        var values = Values(value).Take(MaximumInsurancePriceTypeIds + 1).ToArray();
        if (values.Length > MaximumInsurancePriceTypeIds)
        {
            throw Eve.Services.MachoErrors.ThrowUserError("InsuranceBulkLimitExceeded");
        }

        return [.. values.Select(PositiveId).Where(id => id > 0).Distinct().OrderBy(id => id)];
    }

    private static IEnumerable<PyValue> Values(PyValue value)
    {
        switch (value)
        {
            case PyList list:
                return list.Items;
            case PyTuple tuple:
                return tuple.Items;
            case PyObjectEx { Kind: ObjectExKind.Ex1, Header: PyTuple header }
                when header.Items.Count > 1 && IsSetName(header.Items[0]):
                return header.Items.Skip(1).SelectMany(Values);
            case PyCPickled pickle:
                return PickledSetValues(pickle);
            default:
                return [];
        }
    }

    private static bool IsSetName(PyValue value) =>
        value is PyToken token && token.Value is "__builtin__.set" or "builtins.set";

    private static IEnumerable<PyValue> PickledSetValues(PyCPickled pickle)
    {
        var content = Encoding.ASCII.GetString(pickle.Value.Span);
        if (!content.Contains("set\n", StringComparison.Ordinal) ||
            (!content.Contains("c__builtin__\nset\n", StringComparison.Ordinal) &&
             !content.Contains("cbuiltins\nset\n", StringComparison.Ordinal)))
        {
            return [];
        }

        return PickledIntegers.Matches(content)
            .Select(match => long.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? (PyValue)new PyLong(value)
                : PyNone.Instance);
    }

    private static long PositiveId(PyValue? value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        PyReal number when double.IsFinite(number.Value) &&
            number.Value > 0 &&
            Math.Truncate(number.Value) == number.Value &&
            number.Value < 9.223372036854776E18 => (long)number.Value,
        _ => 0
    };

    private static long IntegerValue(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyBool boolean => boolean.Value ? 1 : 0,
        PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue => (long)number.Value,
        PyStr text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        PyWString text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        PyToken text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0
    };
}
