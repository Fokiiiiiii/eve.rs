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

    private readonly IInsuranceReadModel _readModel;
    private readonly Func<long> _nowUnixMs;

    public InsuranceService(IInsuranceReadModel readModel, Func<long> nowUnixMs)
    {
        _readModel = readModel;
        _nowUnixMs = nowUnixMs;
    }

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

        var fraction = Math.Floor(contract.Fraction * 10 + 0.5) / 10;
        if (!double.IsFinite(fraction) || fraction < 0.5 || fraction > 1.0 || !Enum.IsDefined(contract.OwnerKind))
        {
            return null;
        }

        return contract with
        {
            ContractId = contract.ContractId > 0 ? contract.ContractId : contract.ShipId,
            Fraction = fraction,
            Status = contract.Status is "paid" or "void" ? contract.Status : "active"
        };
    }

    private static bool IsActive(InsuranceContractSnapshot contract, long nowFileTime) =>
        string.Equals(contract.Status, "active", StringComparison.Ordinal) &&
        contract.EndDateFileTime > nowFileTime;

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

    private static long[] ReadTypeIds(PyValue value) =>
        [.. Values(value).Select(PositiveId).Where(id => id > 0).Distinct().OrderBy(id => id)];

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

    private static long PositiveId(PyValue? value)
    {
        var id = IntegerValue(value);
        return id > 0 ? id : 0;
    }

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
