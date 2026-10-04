using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class InsuranceServiceTests
{
    private const long NowUnixMs = 1_800_000_000_000;

    [Fact]
    public async Task Service_exposes_the_four_read_only_reference_calls()
    {
        IService service = Create(new FakeInsuranceReadModel());

        Assert.Equal("insuranceSvc", service.Name);
        foreach (var method in new[]
        {
            "GetInsurancePrice", "GetInsurancePrices", "GetContracts", "GetContractForShip"
        })
        {
            Assert.True(service.HasMethod(method), method);
        }
        Assert.False(service.HasMethod("InsureShip"));
        Assert.False(service.HasMethod("UnInsureShip"));
        Assert.Same(PyNone.Instance, await service.CallAsync(Call(new ClientSession(), "GetContractForShip", new PyInt(10))));
    }

    [Fact]
    public async Task Prices_convert_from_cents_and_unknown_types_return_zero()
    {
        var model = new FakeInsuranceReadModel(new Dictionary<long, long> { [34] = 12_345 });
        var service = Create(model);

        Assert.Equal(new PyReal(123.45), await service.CallAsync(Call(new ClientSession(), "GetInsurancePrice", new PyInt(34))));
        Assert.Equal(new PyInt(0), await service.CallAsync(Call(new ClientSession(), "GetInsurancePrice", new PyInt(999))));

        var prices = Assert.IsType<PyDict>(await service.CallAsync(Call(
            new ClientSession(),
            "GetInsurancePrices",
            new PyList([new PyInt(34), new PyInt(999), new PyInt(34), new PyInt(-1)]))));
        Assert.Equal(2, prices.Entries.Count);
        Assert.Equal(new PyReal(123.45), Field(prices, 34));
        Assert.Equal(new PyInt(0), Field(prices, 999));
        Assert.Equal([34L, 999L], model.LastPriceRequest);
    }

    [Fact]
    public async Task Contracts_are_owner_scoped_role_gated_sorted_and_marshaled_as_keyvals()
    {
        var now = Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs);
        var model = new FakeInsuranceReadModel(contracts:
        [
            Contract(1, 600, 587, 100, InsuranceOwnerKind.Character, 0.7, now - 20_000, now + 70_000),
            Contract(2, 500,  hullTypeId: 588, ownerId: 100, InsuranceOwnerKind.Character, 0.8, now - 10_000, now + 80_000),
            Contract(3, 700,  hullTypeId: 589, ownerId: 200, InsuranceOwnerKind.Corporation, 1.0, now - 5_000, now + 90_000),
            Contract(4, 800, 587, 100, InsuranceOwnerKind.Character, 0.9, now - 100_000, now - 1),
            Contract(5, 900, 587, 100, InsuranceOwnerKind.Character, 0.6, now - 100_000, now + 1, "void")
        ]);
        var service = Create(model);
        var character = Session(("charid", new PyInt(100)));

        var personal = Assert.IsType<PyList>(await service.CallAsync(Call(character, "GetContracts")));
        Assert.Equal([500L, 600L], personal.Items.Select(item => Long(Field(KeyVal(item), "shipID"))));
        var personalContract = KeyVal(personal.Items[0]);
        Assert.Equal(
            ["contractID", "shipID", "typeID", "ownerID", "fraction", "startDate", "endDate"],
            FieldNames(personalContract));
        Assert.Equal(new PyInt(2), Field(personalContract, "contractID"));
        Assert.Equal(new PyInt(588), Field(personalContract, "typeID"));
        Assert.Equal(new PyReal(0.8), Field(personalContract, "fraction"));
        Assert.Equal(new PyLong(now - 10_000), Field(personalContract, "startDate"));
        Assert.Equal(new PyLong(now + 80_000), Field(personalContract, "endDate"));

        Assert.Same(PyNone.Instance, await service.CallAsync(Call(character, "GetContractForShip", new PyInt(700))));
        var visiblePersonal = KeyVal(await service.CallAsync(Call(character, "GetContractForShip", new PyInt(500))));
        Assert.Equal(new PyInt(2), Field(visiblePersonal, "contractID"));
        Assert.Same(PyNone.Instance, await service.CallAsync(Call(character, "GetContractForShip", new PyInt(800))));

        var corporation = Session(("corpid", new PyInt(200)));
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call(corporation, "GetContracts", new PyInt(1)))).Items);
        Assert.Same(PyNone.Instance, await service.CallAsync(Call(corporation, "GetContractForShip", new PyInt(700))));

        corporation.Set("corprole", new PyLong(256));
        var corporateContracts = Assert.IsType<PyList>(await service.CallAsync(Call(corporation, "GetContracts", new PyInt(1))));
        Assert.Single(corporateContracts.Items);
        Assert.Equal(new PyInt(3), Field(KeyVal(corporateContracts.Items[0]), "contractID"));
        Assert.Equal(new PyInt(3), Field(KeyVal(await service.CallAsync(Call(corporation, "GetContractForShip", new PyInt(700)))), "contractID"));

        corporation.Set("corprole", new PyLong(1L << 52));
        Assert.Single(Assert.IsType<PyList>(await service.CallAsync(Call(corporation, "GetContracts", new PyInt(1)))).Items);
    }

    private static IService Create(FakeInsuranceReadModel model) => new InsuranceService(model, () => NowUnixMs);

    private static ServiceCall Call(ClientSession session, string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session
    };

    private static ClientSession Session(params (string Key, PyValue Value)[] entries)
    {
        var session = new ClientSession();
        foreach (var (key, value) in entries)
        {
            session.Set(key, value);
        }
        return session;
    }

    private static PyValue Field(PyDict fields, string name) =>
        fields.Entries.Single(entry => entry.Key == new PyStr(name)).Value;

    private static PyValue Field(PyDict fields, long id) =>
        fields.Entries.Single(entry => Long(entry.Key) == id).Value;

    private static PyDict KeyVal(PyValue value)
    {
        var keyval = Assert.IsType<PyObject>(value);
        Assert.Equal(new PyStr("util.KeyVal"), keyval.Name);
        return Assert.IsType<PyDict>(keyval.Args);
    }

    private static IReadOnlyList<string> FieldNames(PyDict fields) =>
        fields.Entries.Select(entry => Assert.IsType<PyStr>(entry.Key).Value).ToArray();

    private static long Long(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected integer, received {value.GetType().Name}.")
    };

    private static InsuranceContractSnapshot Contract(
        long contractId,
        long shipId,
        long hullTypeId,
        long ownerId,
        InsuranceOwnerKind ownerKind,
        double fraction,
        long startDate,
        long endDate,
        string status = "active") =>
        new(contractId, shipId, hullTypeId, ownerId, ownerKind, fraction, startDate, endDate, status);

    private sealed class FakeInsuranceReadModel(
        IReadOnlyDictionary<long, long>? prices = null,
        IReadOnlyList<InsuranceContractSnapshot>? contracts = null) : IInsuranceReadModel
    {
        private readonly IReadOnlyDictionary<long, long> _prices = prices ?? new Dictionary<long, long>();
        private readonly IReadOnlyList<InsuranceContractSnapshot> _contracts = contracts ?? [];
        public IReadOnlyList<long> LastPriceRequest { get; private set; } = [];

        public ValueTask<IReadOnlyDictionary<long, long>> GetInsurancePricesCentsAsync(
            IReadOnlyCollection<long> typeIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPriceRequest = typeIds.ToArray();
            return ValueTask.FromResult<IReadOnlyDictionary<long, long>>(
                _prices.Where(pair => typeIds.Contains(pair.Key)).ToDictionary());
        }

        public ValueTask<IReadOnlyList<InsuranceContractSnapshot>> GetContractsAsync(
            long ownerId,
            InsuranceOwnerKind ownerKind,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<InsuranceContractSnapshot>>(
                _contracts.Where(contract => contract.OwnerId == ownerId && contract.OwnerKind == ownerKind).ToArray());
        }

        public ValueTask<InsuranceContractSnapshot?> GetContractForShipAsync(long shipId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_contracts.FirstOrDefault(contract => contract.ShipId == shipId));
        }
    }
}
