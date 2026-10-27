using System.Text;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private const long InsuranceWireCharacterId = 90_000_101;
    private const long InsuranceWireCorporationId = 98_000_101;
    private const long InsuranceWireStationId = 60_000_101;
    private const long InsuranceWireStructureId = 65_000_101;
    private const long InsuranceWireSystemId = 30_000_101;
    private const long InsuranceWireShipId = 7_000_000_101;
    private const long InsuranceWireCorpShipId = 7_000_000_102;
    private const long InsuranceWirePoorShipId = 7_000_000_103;
    private const long InsuranceWireTypeId = 587;
    private const long InsuranceWireFullPriceCents = 10_000_000;

    private sealed class InsuranceSocketState : IInsuranceReadModel, IInsuranceWriteModel
    {
        private readonly Dictionary<long, long> _prices = new() { [InsuranceWireTypeId] = InsuranceWireFullPriceCents };
        private readonly Dictionary<long, long> _shipTypes = new()
        {
            [InsuranceWireShipId] = InsuranceWireTypeId,
            [InsuranceWirePoorShipId] = InsuranceWireTypeId,
        };
        private readonly Dictionary<long, InsuranceContractSnapshot> _contracts = new();
        private long _nextContractId = 10;

        public bool CorpInsuranceAllowed { get; set; } = true;

        public void Seed(InsuranceContractSnapshot contract) => _contracts.Add(contract.ShipId, contract);

        public ValueTask<IReadOnlyDictionary<long, long>> GetInsurancePricesCentsAsync(
            IReadOnlyCollection<long> typeIds, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<long, long>>(
                typeIds.Distinct().Where(_prices.ContainsKey).ToDictionary(typeId => typeId, typeId => _prices[typeId]));

        public ValueTask<IReadOnlyList<InsuranceContractSnapshot>> GetContractsAsync(
            long ownerId, InsuranceOwnerKind ownerKind, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<InsuranceContractSnapshot>>(
                _contracts.Values.Where(contract => contract.OwnerId == ownerId && contract.OwnerKind == ownerKind).ToArray());

        public ValueTask<InsuranceContractSnapshot?> GetContractForShipAsync(long shipId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_contracts.GetValueOrDefault(shipId));

        public ValueTask<InsuranceMutationResult> InsureShipAsync(
            InsurancePurchaseCommand command, CancellationToken cancellationToken)
        {
            if (_contracts.TryGetValue(command.ShipId, out var existing) &&
                existing.Status == "active" && !command.ReplaceExisting)
                return ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.AlreadyInsured, existing.OwnerId));

            if (command.ShipId == InsuranceWirePoorShipId)
                return ValueTask.FromResult(new InsuranceMutationResult(
                    InsuranceMutationStatus.InsufficientFunds, command.Actor.CharacterId,
                    command.PremiumCents, 1_234m));

            if (!_shipTypes.TryGetValue(command.ShipId, out var typeId) ||
                !_prices.TryGetValue(typeId, out var fullPriceCents) ||
                InsuranceQuotes.ForPremium(fullPriceCents, command.PremiumCents) is not { } quote)
                return ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.NotEligible));

            _contracts[command.ShipId] = new InsuranceContractSnapshot(
                _nextContractId++, command.ShipId, typeId,
                command.IsCorporation ? command.Actor.CorporationId : command.Actor.CharacterId,
                command.IsCorporation ? InsuranceOwnerKind.Corporation : InsuranceOwnerKind.Character,
                quote.FractionPercent / 100d, command.StartDateFileTime, command.EndDateFileTime, "active");
            return ValueTask.FromResult(new InsuranceMutationResult(
                InsuranceMutationStatus.Applied,
                command.IsCorporation ? command.Actor.CorporationId : command.Actor.CharacterId,
                command.PremiumCents));
        }

        public ValueTask<InsuranceMutationResult> UnInsureShipAsync(
            InsuranceCancellationCommand command, CancellationToken cancellationToken)
        {
            if (!_contracts.TryGetValue(command.ShipId, out var contract))
                return ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.NoActiveContract));
            if (contract.Status != "active")
                return ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.AlreadyApplied, contract.OwnerId));
            _contracts[command.ShipId] = contract with { Status = "void" };
            return ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.Applied, contract.OwnerId));
        }

        public ValueTask<InsuranceMutationResult> SettleShipLossAsync(
            InsuranceLossSettlementRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.NotEligible));

        public ValueTask<InsuranceMutationResult> InvalidateShipAsync(
            InsuranceInvalidationRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InsuranceMutationResult(InsuranceMutationStatus.NotEligible));
    }

    [Fact]
    public async Task Insurance_service_moniker_wire_covers_all_client_calls_errors_and_post_response_notification()
    {
        var nowFileTime = Py.FileTimeFromUnixMs(FixedNowMs);
        var state = new InsuranceSocketState();
        state.Seed(new InsuranceContractSnapshot(
            9, InsuranceWireCorpShipId, InsuranceWireTypeId, InsuranceWireCorporationId,
            InsuranceOwnerKind.Corporation, 0.5, nowFileTime - 60_000_000_000, nowFileTime + 60_000_000_000, "active"));
        var service = new InsuranceService(
            state,
            () => FixedNowMs,
            state,
            (_, _, _) => ValueTask.FromResult(state.CorpInsuranceAllowed));
        ClientSession? insuranceSession = null;
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                insuranceSession = session;
                session.CharacterId = InsuranceWireCharacterId;
                session.Set("corpid", new PyLong(InsuranceWireCorporationId));
                session.Set("corprole", new PyLong(InsuranceService.CorporationAccountantRole));
                session.Set("stationid", new PyLong(InsuranceWireStationId));
                session.Set("solarsystemid2", new PyInt(InsuranceWireSystemId));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        static PyDict Kwargs(params (string Name, PyValue Value)[] values) =>
            new([.. values.Select(value =>
                new KeyValuePair<PyValue, PyValue>(new PyStr(value.Name), value.Value))]);
        static PyTuple Args(params PyValue[] values) => new(values);
        static MachoPacket Decode(Send send) =>
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
        static PyValue Field(PyObject row, string name) =>
            Assert.IsType<PyDict>(row.Args).Entries.Single(entry => JsSemantics.PlainText(entry.Key) == name).Value;

        var version = Kwargs(("machoVersion", new PyInt(1)));
        async Task<Send> Remote(string method, PyValue args, PyValue kwargs) =>
            await SendCall(Packets.CallWithKwargs(open, 1, "insuranceSvc", method, args, kwargs));
        async Task<Send> Bound(string oid, long callId, string method, PyValue args, PyValue kwargs) =>
            await SendCall(Packets.BoundCall(open, callId, oid, method, args, kwargs));
        async Task<Send> SendCall(PyValue request)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }
        async Task AssertInsuranceNotification()
        {
            var notification = Decode(Assert.IsType<Send>(await front.ReceiveAsync()));
            Assert.Equal(MachoMessage.Notification, notification.Type);
            Assert.Equal("OnShipInsured", notification.Dest.BroadcastId);
            Assert.Equal("clientID", notification.Dest.IdType);
            var notificationPacket = Assert.IsType<PyTuple>(notification.Payload);
            var notificationCall = Assert.IsType<PyTuple>(Assert.Single(notificationPacket.Items));
            Assert.Equal(new PyInt(0), notificationCall.Items[0]);
            var inner = Assert.IsType<PyTuple>(
                MarshalCodec.Decode(Assert.IsType<PyBuffer>(notificationCall.Items[1]).Value));
            Assert.Equal(new PyInt(0), inner.Items[0]);
            var eventCall = Assert.IsType<PyTuple>(inner.Items[1]);
            Assert.Equal(new PyInt(1), eventCall.Items[0]);
            Assert.Empty(Assert.IsType<PyTuple>(eventCall.Items[1]).Items);
        }
        static PyDict AssertWrappedUserError(Send send, string userError, long? ownerName = null)
        {
            Assert.Equal(MachoMessage.ErrorResponse, Decode(send).Type);
            var errorResponse = Assert.IsType<PyTuple>(Decode(send).Payload);
            var wrapped = Assert.IsType<PyObjectEx>(
                Assert.IsType<PySubStream>(
                    Assert.IsType<PyTuple>(errorResponse.Items[2]).Items[0]).Decoded);
            var header = Assert.IsType<PyTuple>(wrapped.Header);
            Assert.Equal(MachoErrors.UserErrorClass, Assert.IsType<PyToken>(header.Items[0]).Value);
            var arguments = Assert.IsType<PyTuple>(header.Items[1]);
            Assert.Equal(userError, JsSemantics.PlainText(arguments.Items[0]));
            var substitutions = Assert.IsType<PyDict>(arguments.Items[1]);
            if (ownerName is { } id)
            {
                Assert.Equal(new PyInt(id), Assert.Single(substitutions.Entries,
                    entry => JsSemantics.PlainText(entry.Key) == "ownerName").Value);
            }
            return substitutions;
        }

        var remotePrice = await Remote("GetInsurancePrice", Args(new PyInt(InsuranceWireTypeId)), version);
        Assert.Equal(MachoMessage.CallRsp, Decode(remotePrice).Type);
        Assert.Equal(new PyInt(InsuranceWireFullPriceCents / 100), Result(MarshalCodec.Decode(remotePrice.Payload)));
        var remotePrices = await Remote("GetInsurancePrices",
            Args(new PyList([new PyLong(InsuranceWireTypeId)])), version);
        Assert.Single(Assert.IsType<PyDict>(Result(MarshalCodec.Decode(remotePrices.Payload))).Entries);
        var largeTypeIds = new PyList([.. Enumerable.Range(1, 501).Select(typeId => (PyValue)new PyLong(typeId))]);
        var largePrices = await Remote("GetInsurancePrices", Args(largeTypeIds), version);
        Assert.Equal(501, Assert.IsType<PyDict>(Result(MarshalCodec.Decode(largePrices.Payload))).Entries.Count);
        var remoteContracts = await Remote("GetContracts", Args(), version);
        Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(remoteContracts.Payload))).Items);
        var remoteCorpContracts = await Remote("GetContracts", Args(new PyInt(1)), version);
        Assert.Single(Assert.IsType<PyList>(Result(MarshalCodec.Decode(remoteCorpContracts.Payload))).Items);
        var remoteContract = await Remote("GetContractForShip", Args(new PyLong(InsuranceWireCorpShipId)), version);
        Assert.Equal(new PyInt(InsuranceWireCorporationId),
            Field(Assert.IsType<PyObject>(Result(MarshalCodec.Decode(remoteContract.Payload))), "ownerID"));

        var bindParameters = Args(
            new PyLong(InsuranceWireStationId),
            new PyLong(InsuranceWireStationId),
            new PyInt(15));
        var resolve = await Remote("MachoResolveObject", Args(bindParameters), version);
        Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), Result(MarshalCodec.Decode(resolve.Payload)));

        var nestedCall = Args(
            new PyStr("GetInsurancePrice"),
            Args(new PyLong(InsuranceWireTypeId)),
            version);
        var bind = await Remote("MachoBindObject", Args(bindParameters, nestedCall), version);
        Assert.Equal(MachoMessage.CallRsp, Decode(bind).Type);
        var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bind.Payload)));
        Assert.Equal(new PyInt(InsuranceWireFullPriceCents / 100), bindResult.Items[1]);
        var boundSubstruct = Assert.IsType<PySubStruct>(bindResult.Items[0]);
        var boundReference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(boundSubstruct.Value).Decoded);
        var oid = JsSemantics.StringValue(boundReference.Items[0])!;
        Assert.IsType<PyLong>(boundReference.Items[1]);
        var released = await Bound("N=999:released-insurance-object", 20,
            "GetInsurancePrice", Args(new PyInt(InsuranceWireTypeId)), version);
        AssertWrappedUserError(released, "UnMachoDestination");

        var pricesReply = await Bound(oid, 2, "GetInsurancePrices",
            Args(new PyList([new PyLong(InsuranceWireTypeId), new PyLong(999_999)])), version);
        var prices = Assert.IsType<PyDict>(Result(MarshalCodec.Decode(pricesReply.Payload)));
        Assert.Equal(new PyInt(InsuranceWireFullPriceCents / 100),
            Assert.Single(prices.Entries, entry => Equals(entry.Key, new PyInt(InsuranceWireTypeId))).Value);
        Assert.Equal(new PyInt(0),
            Assert.Single(prices.Entries, entry => Equals(entry.Key, new PyInt(999_999))).Value);

        var personalBefore = await Bound(oid, 3, "GetContracts", Args(), version);
        Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(personalBefore.Payload))).Items);
        var corporationContracts = await Bound(oid, 4, "GetContracts", Args(new PyInt(1)), version);
        var corpList = Assert.IsType<PyList>(Result(MarshalCodec.Decode(corporationContracts.Payload)));
        Assert.Single(corpList.Items);
        var corpContract = Assert.IsType<PyObject>(corpList.Items[0]);
        Assert.Equal(new PyInt(InsuranceWireCorporationId), Field(corpContract, "ownerID"));

        var ownedContract = await Bound(oid, 5, "GetContractForShip", Args(new PyInt(InsuranceWireCorpShipId)), version);
        Assert.Equal(new PyInt(InsuranceWireCorporationId),
            Field(Assert.IsType<PyObject>(Result(MarshalCodec.Decode(ownedContract.Payload))), "ownerID"));
        var missingContract = await Bound(oid, 6, "GetContractForShip", Args(new PyLong(InsuranceWireShipId)), version);
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(missingContract.Payload)));

        var quote = Assert.Single(
            InsuranceQuotes.ForFullPayoutValue(InsuranceWireFullPriceCents),
            item => item.FractionPercent == 50);
        var quoteAmount = new PyReal(quote.PremiumCents / 100d);
        var missingPurchase = await Bound(oid, 7, "InsureShip",
            Args(new PyLong(999_001), quoteAmount, PyBool.False), version);
        AssertWrappedUserError(missingPurchase, "InsCouldNotFindItem");
        var insufficient = await Bound(oid, 16, "InsureShip",
            Args(new PyLong(InsuranceWirePoorShipId), quoteAmount, PyBool.False), version);
        var insufficientValues = AssertWrappedUserError(insufficient, "NotEnoughMoney");
        Assert.Equal(Py.Number(quote.PremiumCents / 100d),
            Assert.Single(insufficientValues.Entries, entry => JsSemantics.PlainText(entry.Key) == "amount").Value);
        Assert.Equal(Py.Number(1_234d),
            Assert.Single(insufficientValues.Entries, entry => JsSemantics.PlainText(entry.Key) == "balance").Value);
        state.CorpInsuranceAllowed = false;
        var corpDenied = await Bound(oid, 17, "InsureShip",
            Args(new PyLong(InsuranceWireCorpShipId), quoteAmount, PyBool.True), version);
        AssertWrappedUserError(corpDenied, "CrpAccessDenied");
        state.CorpInsuranceAllowed = true;
        var firstPurchase = await Bound(oid, 18, "InsureShip",
            Args(new PyLong(InsuranceWireShipId), quoteAmount, PyBool.False),
            Kwargs(("machoVersion", new PyInt(1)), ("voidOld", PyNone.Instance)));
        Assert.Equal(MachoMessage.CallRsp, Decode(firstPurchase).Type);
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(firstPurchase.Payload)));
        await AssertInsuranceNotification();

        var insuredReply = await Bound(oid, 11, "GetContractForShip", Args(new PyInt(InsuranceWireShipId)), version);
        var insured = Assert.IsType<PyObject>(Result(MarshalCodec.Decode(insuredReply.Payload)));
        Assert.Equal(new PyInt(10), Field(insured, "contractID"));
        Assert.IsType<PyLong>(Field(insured, "shipID"));
        Assert.Equal(new PyInt(InsuranceWireTypeId), Field(insured, "typeID"));
        Assert.Equal(new PyInt(InsuranceWireCharacterId), Field(insured, "ownerID"));
        Assert.Equal(new PyReal(0.5), Field(insured, "fraction"));
        Assert.IsType<PyLong>(Field(insured, "startDate"));
        Assert.IsType<PyLong>(Field(insured, "endDate"));
        Assert.Equal(
            new[] { "contractID", "shipID", "typeID", "ownerID", "fraction", "startDate", "endDate" },
            Assert.IsType<PyDict>(insured.Args).Entries.Select(entry => JsSemantics.PlainText(entry.Key)));

        var duplicate = await Bound(oid, 12, "InsureShip",
            Args(new PyLong(InsuranceWireShipId), quoteAmount, PyBool.False), version);
        AssertWrappedUserError(duplicate, "InsureShipFailedSingleContract", InsuranceWireCharacterId);

        var replaceByKeyword = await Bound(oid, 13, "InsureShip",
            Args(new PyLong(InsuranceWireShipId), quoteAmount, PyBool.False),
            Kwargs(("machoVersion", new PyInt(1)), ("voidOld", PyBool.True)));
        Assert.Equal(MachoMessage.CallRsp, Decode(replaceByKeyword).Type);
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(replaceByKeyword.Payload)));
        await AssertInsuranceNotification();

        var replaceByPosition = await Bound(oid, 14, "InsureShip",
            Args(new PyLong(InsuranceWireShipId), quoteAmount, PyBool.False, new PyLong(1)), version);
        Assert.Equal(MachoMessage.CallRsp, Decode(replaceByPosition).Type);
        await AssertInsuranceNotification();

        var cancel = await Bound(oid, 15, "UnInsureShip", Args(new PyLong(InsuranceWireShipId)), version);
        Assert.Equal(MachoMessage.CallRsp, Decode(cancel).Type);
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(cancel.Payload)));
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(
            (await Bound(oid, 16, "GetContractForShip", Args(new PyLong(InsuranceWireShipId)), version)).Payload)));
        Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(
            (await Bound(oid, 17, "GetContracts", Args(), version)).Payload))).Items);

        var failedCancel = await Bound(oid, 18, "UnInsureShip", Args(new PyLong(InsuranceWireShipId)), version);
        AssertWrappedUserError(failedCancel, "InsCouldNotFindItem");

        insuranceSession!.Set("stationid", PyNone.Instance);
        insuranceSession.Set("structureid", new PyLong(InsuranceWireStructureId));
        var structureBindParameters = Args(
            new PyLong(InsuranceWireStructureId),
            new PyLong(InsuranceWireSystemId),
            new PyInt(5));
        var structureResolve = await Remote("MachoResolveObject", Args(structureBindParameters), version);
        Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), Result(MarshalCodec.Decode(structureResolve.Payload)));
        var structureBind = await Remote("MachoBindObject",
            Args(structureBindParameters, nestedCall), version);
        var structureBinding = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(structureBind.Payload)));
        Assert.Equal(new PyInt(InsuranceWireFullPriceCents / 100), structureBinding.Items[1]);
        var structureReference = Assert.IsType<PyTuple>(
            Assert.IsType<PySubStream>(Assert.IsType<PySubStruct>(structureBinding.Items[0]).Value).Decoded);
        var structureOid = JsSemantics.StringValue(structureReference.Items[0])!;
        Assert.IsType<PyLong>(structureReference.Items[1]);
        var structureContracts = await Bound(structureOid, 19, "GetContracts", Args(), version);
        Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(structureContracts.Payload))).Items);
    }
}
