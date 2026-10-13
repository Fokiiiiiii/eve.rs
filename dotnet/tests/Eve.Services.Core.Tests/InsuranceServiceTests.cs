using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class InsuranceServiceTests
{
    private const long NowUnixMs = 1_800_000_000_000;

    [Fact]
    public async Task Service_exposes_read_and_write_reference_calls()
    {
        IService service = Create(new FakeInsuranceReadModel(), new FakeInsuranceWriteModel());

        Assert.Equal("insuranceSvc", service.Name);
        foreach (var method in new[]
        {
            "GetInsurancePrice", "GetInsurancePrices", "GetContracts", "GetContractForShip",
            "InsureShip", "UnInsureShip"
        })
        {
            Assert.True(service.HasMethod(method), method);
        }
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

    [Fact]
    public void Insurance_quotes_match_the_official_coverage_levels_and_wiki_example()
    {
        var quotes = InsuranceQuotes.ForFullPayoutValue(712_023_300);

        Assert.Equal(new[] { 50, 60, 70, 80, 90, 100 }, quotes.Select(quote => quote.FractionPercent));
        Assert.Equal(35_601_165, quotes[0].PremiumCents);
        Assert.Equal(356_011_650, quotes[0].PayoutCents);
        Assert.Equal(213_606_990, quotes[^1].PremiumCents);
        Assert.Equal(712_023_300, quotes[^1].PayoutCents);
        Assert.Equal(quotes[0], InsuranceQuotes.ForPremium(712_023_300, 35_601_165));
        Assert.Null(InsuranceQuotes.ForPremium(712_023_300, 1));
    }

    [Fact]
    public void Loss_payout_uses_active_coverage_and_suppresses_concord_losses()
    {
        var lossDate = Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs);
        const long fullPayoutValueCents = 712_023_300;
        var activeBasic = Contract(
            1, 500, 587, 100, InsuranceOwnerKind.Character, 0.5, lossDate - 1, lossDate + 1);

        Assert.Equal(
            356_011_650,
            InsuranceQuotes.PayoutForLoss(fullPayoutValueCents, activeBasic, lossDate, false));
        Assert.Equal(
            284_809_320,
            InsuranceQuotes.PayoutForLoss(
                fullPayoutValueCents,
                activeBasic with { EndDateFileTime = lossDate },
                lossDate,
                false));
        Assert.Equal(
            284_809_320,
            InsuranceQuotes.PayoutForLoss(
                fullPayoutValueCents,
                activeBasic with { Fraction = 0.55 },
                lossDate,
                false));
        Assert.Equal(
            284_809_320,
            InsuranceQuotes.PayoutForLoss(fullPayoutValueCents, null, lossDate, false));
        Assert.Equal(
            0,
            InsuranceQuotes.PayoutForLoss(fullPayoutValueCents, activeBasic, lossDate, true));
    }

    [Fact]
    public async Task InsureShip_forwards_the_wire_amount_owner_kind_and_replace_flag()
    {
        var writer = new FakeInsuranceWriteModel();
        IService service = Create(new FakeInsuranceReadModel(), writer);
        var session = Session(
            ("charid", new PyInt(100)),
            ("corpid", new PyInt(200)),
            ("corprole", new PyLong(256)),
            ("stationid", new PyInt(300)),
            ("solarsystemid2", new PyInt(400)));

        var result = await service.CallAsync(Call(
            session,
            "InsureShip",
            new PyInt(500),
            new PyReal(356_011.65),
            new PyInt(0),
            new PyInt(1)));

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(new InsurancePurchaseCommand(
            new InsuranceActorSnapshot(100, 200, 256, 300, 0, 400),
            500,
            35_601_165,
            false,
            true,
            Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs),
            Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs + 84L * 24 * 60 * 60 * 1000)), writer.Purchase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsureShip_forwards_voidOld_keyword_with_unchanged_purchase_terms(bool isCorporation)
    {
        var writer = new FakeInsuranceWriteModel();
        IService service = Create(new FakeInsuranceReadModel(), writer);
        var session = Session(
            ("charid", new PyInt(100)),
            ("corpid", new PyInt(200)),
            ("corprole", new PyLong(256)),
            ("stationid", new PyInt(300)),
            ("solarsystemid2", new PyInt(400)));
        var call = new ServiceCall
        {
            Method = "InsureShip",
            Args = [new PyInt(500), new PyReal(356_011.65), PyValue.Of(isCorporation)],
            Kwargs = new PyDict([new(new PyStr("voidOld"), PyBool.True)]),
            Session = session
        };

        var result = await service.CallAsync(call);

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(new InsurancePurchaseCommand(
            new InsuranceActorSnapshot(100, 200, 256, 300, 0, 400),
            500,
            35_601_165,
            isCorporation,
            true,
            Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs),
            Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs + 84L * 24 * 60 * 60 * 1000)), writer.Purchase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task InsureShip_voidOld_keyword_takes_precedence_over_positional_flag(bool? replaceExisting)
    {
        var writer = new FakeInsuranceWriteModel();
        IService service = Create(new FakeInsuranceReadModel(), writer);
        PyValue keyword = replaceExisting is { } flag ? PyValue.Of(flag) : PyNone.Instance;
        var call = new ServiceCall
        {
            Method = "InsureShip",
            Args = [new PyInt(500), new PyReal(356_011.65), PyBool.False,
                PyValue.Of(replaceExisting != true)],
            Kwargs = new PyDict([new(new PyStr("voidOld"), keyword)]),
            Session = Session(("charid", new PyInt(100)), ("stationid", new PyInt(300)))
        };

        var result = await service.CallAsync(call);

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(replaceExisting == true, writer.Purchase!.ReplaceExisting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsureShip_invalid_voidOld_keyword_is_rejected_before_writer(bool useString)
    {
        var writer = new FakeInsuranceWriteModel();
        IService service = Create(new FakeInsuranceReadModel(), writer);
        PyValue invalid = useString ? new PyStr("true") : new PyInt(2);
        var call = new ServiceCall
        {
            Method = "InsureShip",
            Args = [new PyInt(500), new PyReal(356_011.65), PyBool.False, PyBool.True],
            Kwargs = new PyDict([new(new PyStr("voidOld"), invalid)]),
            Session = Session(("charid", new PyInt(100)), ("stationid", new PyInt(300)))
        };

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(call));

        Assert.Equal("InsureShipFailed", error.Error.UserErrorMessage);
        Assert.Null(writer.Purchase);
    }

    [Theory]
    [InlineData(100, 0, false)]
    [InlineData(100, 300, true)]
    [InlineData(0, 300, false)]
    public async Task InsureShip_voidOld_keyword_does_not_bypass_actor_authority(
        long characterId, long stationId, bool isCorporation)
    {
        var writer = new FakeInsuranceWriteModel();
        IService service = Create(new FakeInsuranceReadModel(), writer);
        var call = new ServiceCall
        {
            Method = "InsureShip",
            Args = [new PyInt(500), new PyReal(356_011.65), PyValue.Of(isCorporation)],
            Kwargs = new PyDict([new(new PyStr("voidOld"), PyBool.True)]),
            Session = Session(
                ("charid", new PyInt(characterId)),
                ("corpid", new PyInt(200)),
                ("corprole", new PyLong(0)),
                ("stationid", new PyInt(stationId)))
        };

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(call));

        Assert.Equal("InsureShipFailed", error.Error.UserErrorMessage);
        Assert.Null(writer.Purchase);
    }

    [Fact]
    public async Task InsureShip_recontract_confirmation_preserves_refusal_then_forwards_voidOld()
    {
        var writer = new FakeInsuranceWriteModel
        {
            PurchaseResult = new InsuranceMutationResult(InsuranceMutationStatus.AlreadyInsured, 100)
        };
        IService service = Create(new FakeInsuranceReadModel(), writer);
        var session = Session(("charid", new PyInt(100)), ("stationid", new PyInt(300)));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(
            Call(session, "InsureShip", new PyInt(500), new PyReal(356_011.65), PyBool.False)));

        Assert.Equal("InsureShipFailedSingleContract", error.Error.UserErrorMessage);
        var payload = Assert.IsType<PyObjectEx>(error.Error.Payload);
        var header = Assert.IsType<PyTuple>(payload.Header);
        var arguments = Assert.IsType<PyTuple>(header.Items[1]);
        Assert.Equal(100L, Long(Field(Assert.IsType<PyDict>(arguments.Items[1]), "ownerName")));
        var original = writer.Purchase!;
        Assert.False(original.ReplaceExisting);
        writer.PurchaseResult = new InsuranceMutationResult(InsuranceMutationStatus.Applied);

        var result = await service.CallAsync(new ServiceCall
        {
            Method = "InsureShip",
            Args = [new PyInt(500), new PyReal(356_011.65), PyBool.False],
            Kwargs = new PyDict([new(new PyStr("voidOld"), PyBool.True)]),
            Session = session
        });

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(new InsurancePurchaseCommand(
            original.Actor, original.ShipId, original.PremiumCents, original.IsCorporation, true,
            original.StartDateFileTime, original.EndDateFileTime), writer.Purchase);
    }

    [Fact]
    public async Task InsureShip_voidOld_keyword_preserves_writer_rejection()
    {
        var writer = new FakeInsuranceWriteModel
        {
            PurchaseResult = new InsuranceMutationResult(InsuranceMutationStatus.NotEligible)
        };
        IService service = Create(new FakeInsuranceReadModel(), writer);
        var call = new ServiceCall
        {
            Method = "InsureShip",
            Args = [new PyInt(500), new PyReal(356_011.65), PyBool.False],
            Kwargs = new PyDict([new(new PyStr("voidOld"), PyBool.True)]),
            Session = Session(("charid", new PyInt(100)), ("stationid", new PyInt(300)))
        };

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(call));

        Assert.Equal("InsureShipFailed", error.Error.UserErrorMessage);
        Assert.True(writer.Purchase!.ReplaceExisting);
    }

    [Fact]
    public async Task Corporation_insurance_forwards_corporation_owner_and_role_context()
    {
        var writer = new FakeInsuranceWriteModel();
        IService service = Create(new FakeInsuranceReadModel(), writer);
        var session = Session(
            ("charid", new PyInt(100)),
            ("corpid", new PyInt(200)),
            ("corprole", new PyLong(256)),
            ("stationid", new PyInt(300)),
            ("solarsystemid2", new PyInt(400)));

        var result = await service.CallAsync(Call(
            session,
            "InsureShip",
            new PyInt(500),
            new PyReal(356_011.65),
            PyBool.True));

        Assert.Same(PyNone.Instance, result);
        Assert.True(writer.Purchase!.IsCorporation);
        Assert.Equal(200L, writer.Purchase.Actor.CorporationId);
        Assert.Equal(100L, writer.Purchase.Actor.CharacterId);
    }

    [Fact]
    public async Task UnInsureShip_forwards_the_ship_and_actor_to_the_atomic_writer()
    {
        var writer = new FakeInsuranceWriteModel();
        IService service = Create(new FakeInsuranceReadModel(), writer);
        var session = Session(
            ("charid", new PyInt(100)),
            ("corpid", new PyInt(200)),
            ("stationid", new PyInt(300)),
            ("solarsystemid2", new PyInt(400)));

        var result = await service.CallAsync(Call(session, "UnInsureShip", new PyInt(500)));

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(new InsuranceCancellationCommand(
            new InsuranceActorSnapshot(100, 200, 0, 300, 0, 400),
            500,
            Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs)), writer.Cancellation);
    }

    [Fact]
    public async Task Ship_loss_settlement_is_delegated_to_the_same_atomic_insurance_writer()
    {
        var writer = new FakeInsuranceWriteModel();
        var service = new InsuranceService(new FakeInsuranceReadModel(), () => NowUnixMs, writer);
        var loss = new InsuranceLossSettlementRequest(
            900, 500, Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs), false, 100, 200);

        await service.SettleShipLossAsync(loss, CancellationToken.None);

        Assert.Equal(loss, writer.Loss);
    }

    [Fact]
    public async Task Concord_losses_are_not_sent_to_insurance_settlement_even_for_the_base_payout()
    {
        var writer = new FakeInsuranceWriteModel();
        var service = new InsuranceService(new FakeInsuranceReadModel(), () => NowUnixMs, writer);
        var loss = new InsuranceLossSettlementRequest(
            901, 500, Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs), true, 100, 200);

        var result = await service.SettleShipLossAsync(loss, CancellationToken.None);

        Assert.Equal(InsuranceMutationStatus.NotEligible, result.Status);
        Assert.Null(writer.Loss);
    }

    [Theory]
    [InlineData(InsuranceInvalidationReason.Repackaged)]
    [InlineData(InsuranceInvalidationReason.OwnerChanged)]
    [InlineData(InsuranceInvalidationReason.PlacedInCorporationHangar)]
    [InlineData(InsuranceInvalidationReason.Boarded)]
    public async Task Insurance_invalidation_events_forward_reason_time_and_actor_to_writer(
        InsuranceInvalidationReason reason)
    {
        var writer = new FakeInsuranceWriteModel();
        var service = new InsuranceService(new FakeInsuranceReadModel(), () => NowUnixMs, writer);
        var request = new InsuranceInvalidationRequest(
            500, reason, Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs), 101, 200);

        var result = await service.InvalidateShipAsync(request, CancellationToken.None);

        Assert.Equal(InsuranceMutationStatus.Applied, result.Status);
        Assert.Equal(request, writer.Invalidation);
    }

    [Fact]
    public async Task Board_invalidation_preserves_zero_for_a_boarder_without_a_corporation()
    {
        var writer = new FakeInsuranceWriteModel();
        var service = new InsuranceService(new FakeInsuranceReadModel(), () => NowUnixMs, writer);
        var request = new InsuranceInvalidationRequest(
            500,
            InsuranceInvalidationReason.Boarded,
            Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs),
            101,
            0);

        var result = await service.InvalidateShipAsync(request, CancellationToken.None);

        Assert.Equal(InsuranceMutationStatus.Applied, result.Status);
        Assert.Equal(request, writer.Invalidation);
        Assert.Equal(0L, writer.Invalidation!.ActorCorporationId);
    }

    [Fact]
    public async Task Invalid_insurance_invalidation_request_is_rejected_before_writer()
    {
        var writer = new FakeInsuranceWriteModel();
        var service = new InsuranceService(new FakeInsuranceReadModel(), () => NowUnixMs, writer);
        var timestamp = Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs);

        var invalidShip = await service.InvalidateShipAsync(
            new InsuranceInvalidationRequest(0, InsuranceInvalidationReason.Repackaged, timestamp),
            CancellationToken.None);
        var missingBoarderCharacter = await service.InvalidateShipAsync(
            new InsuranceInvalidationRequest(500, InsuranceInvalidationReason.Boarded, timestamp, 0, 200),
            CancellationToken.None);
        var missingBoarderCorporation = await service.InvalidateShipAsync(
            new InsuranceInvalidationRequest(500, InsuranceInvalidationReason.Boarded, timestamp, 101),
            CancellationToken.None);
        var negativeBoarderCorporation = await service.InvalidateShipAsync(
            new InsuranceInvalidationRequest(500, InsuranceInvalidationReason.Boarded, timestamp, 101, -1),
            CancellationToken.None);

        Assert.Equal(InsuranceMutationStatus.InvalidRequest, invalidShip.Status);
        Assert.Equal(InsuranceMutationStatus.InvalidRequest, missingBoarderCharacter.Status);
        Assert.Equal(InsuranceMutationStatus.InvalidRequest, missingBoarderCorporation.Status);
        Assert.Equal(InsuranceMutationStatus.InvalidRequest, negativeBoarderCorporation.Status);
        Assert.Null(writer.Invalidation);
    }

    [Fact]
    public async Task Invalid_loss_settlement_is_rejected_before_calling_the_writer()
    {
        var writer = new FakeInsuranceWriteModel();
        var service = new InsuranceService(new FakeInsuranceReadModel(), () => NowUnixMs, writer);
        var invalid = new InsuranceLossSettlementRequest(
            0, 500, Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs), false, 100, 200);

        var result = await service.SettleShipLossAsync(invalid, CancellationToken.None);

        Assert.Equal(InsuranceMutationStatus.InvalidRequest, result.Status);
        Assert.Null(writer.Loss);
    }

    [Fact]
    public async Task Contracts_are_not_active_before_start_or_with_unknown_status()
    {
        var now = Eve.Services.Py.FileTimeFromUnixMs(NowUnixMs);
        var model = new FakeInsuranceReadModel(contracts:
        [
            Contract(1, 500, 587, 100, InsuranceOwnerKind.Character, 0.7, now, now + 1),
            Contract(2, 501, 587, 100, InsuranceOwnerKind.Character, 0.7, now + 1, now + 10_000),
            Contract(3, 502, 587, 100, InsuranceOwnerKind.Character, 0.7, now - 1, now + 10_000, "pending"),
            Contract(4, 503, 587, 100, InsuranceOwnerKind.Character, 0.7, now - 1, now)
        ]);
        IService service = Create(model);
        var session = Session(("charid", new PyInt(100)));

        var contracts = Assert.IsType<PyList>(await service.CallAsync(Call(session, "GetContracts")));
        Assert.Equal([500L], contracts.Items.Select(item => Long(Field(KeyVal(item), "shipID"))));
        foreach (var shipId in new long[] { 501, 502, 503 })
        {
            Assert.Same(PyNone.Instance, await service.CallAsync(Call(session, "GetContractForShip", new PyInt(shipId))));
        }
    }

    private static IService Create(FakeInsuranceReadModel model, FakeInsuranceWriteModel? writer = null) =>
        (IService)(object)new InsuranceService(model, () => NowUnixMs, writer ?? new FakeInsuranceWriteModel());

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

private sealed class FakeInsuranceWriteModel : IInsuranceWriteModel
    {
        public InsurancePurchaseCommand? Purchase { get; private set; }
        public InsuranceCancellationCommand? Cancellation { get; private set; }
        public InsuranceLossSettlementRequest? Loss { get; private set; }
        public InsuranceInvalidationRequest? Invalidation { get; private set; }
        public InsuranceMutationResult PurchaseResult { get; set; } = new(InsuranceMutationStatus.Applied);
        public InsuranceMutationResult CancellationResult { get; set; } = new(InsuranceMutationStatus.Applied);
        public InsuranceMutationResult LossResult { get; set; } = new(InsuranceMutationStatus.Applied);
        public InsuranceMutationResult InvalidationResult { get; set; } = new(InsuranceMutationStatus.Applied);

        public ValueTask<InsuranceMutationResult> InsureShipAsync(
            InsurancePurchaseCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Purchase = command;
            return ValueTask.FromResult(PurchaseResult);
        }

        public ValueTask<InsuranceMutationResult> UnInsureShipAsync(
            InsuranceCancellationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Cancellation = command;
            return ValueTask.FromResult(CancellationResult);
        }

        public ValueTask<InsuranceMutationResult> SettleShipLossAsync(
            InsuranceLossSettlementRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Loss = request;
            return ValueTask.FromResult(LossResult);
        }

        public ValueTask<InsuranceMutationResult> InvalidateShipAsync(
            InsuranceInvalidationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invalidation = request;
            return ValueTask.FromResult(InvalidationResult);
        }
    }

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
