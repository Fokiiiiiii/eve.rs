using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Wallet;
using Eve.Wallet;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Registered_lp_store_and_wallet_routes_return_the_client_consumed_shapes()
    {
        const long characterId = 900_101;
        const long memberCorporationId = 980_101;
        const long storeCorporationId = 980_201;
        const long offerId = 700_301;
        const long stationId = 600_401;
        const int corporationAccountKey = 1001;
        var offer = new LpStoreOffer(
            storeCorporationId, offerId, TypeId: 34_001, Quantity: 2, LpCost: 1250, IskCost: 500_000, AkCost: 17,
            [new LpStoreRequirement(TypeId: 34_002, Quantity: 3)]);
        IReadOnlyList<LpStoreOffer> offerRows = [offer];
        IReadOnlyList<LoyaltyPointBalance> characterBalances =
            [new(500_002, 1_250), new(500_003, 75)];
        IReadOnlyList<LoyaltyPointBalance> corporationBalances = [new(500_002, 4_512)];
        var walletReads = new List<LoyaltyHolder>();
        var purchaseRequests = new List<LpStorePurchaseRequest>();
        var directorChecks = new List<(long CharacterId, long CorporationId)>();
        var corporationLookups = new List<long>();
        long offersReadFor = 0;

        Task<IReadOnlyList<LpStoreOffer>> ReadOffers(long corporationId, CancellationToken _)
        {
            offersReadFor = corporationId;
            return Task.FromResult(offerRows);
        }

        Task<LpStorePurchaseReceipt> TakePurchase(LpStorePurchaseRequest request, CancellationToken _)
        {
            purchaseRequests.Add(request);
            return Task.FromResult(new LpStorePurchaseReceipt("wire-test", 0, 0m, []));
        }

        Task<IReadOnlyList<LoyaltyPointBalance>> ReadBalances(LoyaltyHolder holder, CancellationToken _)
        {
            walletReads.Add(holder);
            return Task.FromResult(holder.Kind == LoyaltyHolderKind.Character ? characterBalances : corporationBalances);
        }

        var lpStoreMgr = new LpStoreMgrService(
            null!,
            null!,
            (buyerCharacterId, corporationId, _) =>
            {
                directorChecks.Add((buyerCharacterId, corporationId));
                return ValueTask.FromResult(true);
            },
            readOffers: ReadOffers,
            takePurchase: TakePurchase);
        var lpService = new LoyaltyPointsService(
            null!,
            (buyerCharacterId, _) =>
            {
                corporationLookups.Add(buyerCharacterId);
                return Task.FromResult<long?>(memberCorporationId);
            },
            ReadBalances);

        Start(
            configure: registry =>
            {
                registry.Register(lpStoreMgr);
                registry.Register(lpService);
            },
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyInt(characterId));
                session.Set("corporationID", new PyInt(memberCorporationId));
                session.Set("corpAccountKey", new PyInt(corporationAccountKey));
                session.Set("stationid", new PyInt(stationId));
            });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var offerReply = Assert.IsType<PyList>(await CallOverHost(
            front, open, 1, "LPStoreMgr", "GetAvailableOffersFromCorp", Py.Tuple(new PyInt(storeCorporationId))));
        var offerObject = Assert.IsType<PyObject>(Assert.Single(offerReply.Items));
        Assert.Equal("util.KeyVal", JsSemantics.StringValue(offerObject.Name));
        var offerFields = Assert.IsType<PyDict>(offerObject.Args);
        Assert.Equal(10, offerFields.Entries.Count);
        Assert.Equal(
            new[] { "typeID", "iskCost", "akCost", "reqItems", "offerID", "qty", "requiredStandings", "corpID", "lootItems", "lpCost" }.Order(),
            offerFields.Entries.Select(entry => JsSemantics.StringValue(entry.Key)!).Order());
        PyValue OfferField(string name) =>
            Assert.Single(offerFields.Entries, entry => JsSemantics.StringValue(entry.Key) == name).Value;
        Assert.Equal(new PyInt(offer.TypeId), OfferField("typeID"));
        Assert.Equal(new PyInt(offer.IskCost), OfferField("iskCost"));
        Assert.Equal(new PyInt(offer.AkCost), OfferField("akCost"));
        Assert.Equal(new PyInt(offer.OfferId), OfferField("offerID"));
        Assert.Equal(new PyInt(offer.Quantity), OfferField("qty"));
        Assert.Equal(PyNone.Instance, OfferField("requiredStandings"));
        Assert.Equal(new PyInt(offer.CorporationId), OfferField("corpID"));
        Assert.Equal(new PyInt(offer.LpCost), OfferField("lpCost"));
        var requiredItems = Assert.IsType<PyList>(OfferField("reqItems")).Items;
        var requiredItem = Assert.IsType<PyTuple>(Assert.Single(requiredItems)).Items;
        Assert.Equal(2, requiredItem.Count);
        Assert.Equal(new PyInt(34_002), requiredItem[0]);
        Assert.Equal(new PyInt(3), requiredItem[1]);
        Assert.Empty(Assert.IsType<PyList>(OfferField("lootItems")).Items);
        Assert.Equal(storeCorporationId, offersReadFor);

        Assert.Equal(PyBool.True, await CallOverHost(
            front, open, 2, "LPStoreMgr", "TakeOfferForCharacter",
            Py.Tuple(new PyInt(storeCorporationId), new PyInt(offerId), new PyInt(3))));
        Assert.Equal(PyBool.True, await CallOverHost(
            front, open, 3, "LPStoreMgr", "TakeOfferForCorporation",
            Py.Tuple(new PyInt(storeCorporationId), new PyInt(offerId), new PyInt(2))));
        Assert.Equal(
            new[]
            {
                new LpStorePurchaseRequest(characterId, 0, 1000, storeCorporationId, offerId, 3, stationId),
                new LpStorePurchaseRequest(characterId, memberCorporationId, corporationAccountKey, storeCorporationId, offerId, 2, stationId),
            },
            purchaseRequests);
        Assert.Equal([(characterId, memberCorporationId)], directorChecks);

        async Task AssertBalanceRows(
            string method, IReadOnlyList<LoyaltyPointBalance> expectedRows, uint callId)
        {
            var rowset = Assert.IsType<PyObject>(await CallOverHost(
                front, open, callId, "LPSvc", method, Py.Tuple([])));
            Assert.Equal("eve.common.script.sys.rowset.Rowset", JsSemantics.StringValue(rowset.Name));
            var fields = Assert.IsType<PyDict>(rowset.Args);
            Assert.Equal(4, fields.Entries.Count);
            PyValue RowsetField(string name) =>
                Assert.Single(fields.Entries, entry => JsSemantics.StringValue(entry.Key) == name).Value;
            var expectedColumns = new[] { "issuerCorpID", "loyaltyPoints" };
            Assert.Equal(expectedColumns, Assert.IsType<PyList>(RowsetField("header")).Items.Select(JsSemantics.StringValue));
            Assert.Equal(expectedColumns, Assert.IsType<PyList>(RowsetField("columns")).Items.Select(JsSemantics.StringValue));
            Assert.Equal(new PyToken("util.Row"), RowsetField("RowClass"));
            var lines = Assert.IsType<PyList>(RowsetField("lines")).Items;
            Assert.Equal(expectedRows.Count, lines.Count);
            for (var index = 0; index < expectedRows.Count; index++)
            {
                var values = Assert.IsType<PyTuple>(lines[index]).Items;
                Assert.Equal(2, values.Count);
                Assert.Equal(new PyInt(expectedRows[index].IssuerCorpId), values[0]);
                Assert.Equal(new PyInt(expectedRows[index].Amount), values[1]);
            }
        }

        await AssertBalanceRows("GetAllMyCharacterWalletLPBalances", characterBalances, 4);
        await AssertBalanceRows("GetAllMyCorporationWalletLPBalances", corporationBalances, 5);
        Assert.Equal(
            [new LoyaltyHolder(LoyaltyHolderKind.Character, characterId), new LoyaltyHolder(LoyaltyHolderKind.Corporation, memberCorporationId)],
            walletReads);
        Assert.Equal([characterId], corporationLookups);
    }

    [Fact]
    public async Task Structure_lp_store_checks_service_access_and_sends_item_change_before_reply()
    {
        const long characterId = 900_501;
        const long corporationId = 980_501;
        const long storeCorporationId = 980_601;
        const long offerId = 700_601;
        const long structureId = 7_000_000_601;
        const long itemId = 8_000_000_601;
        var allowStructure = true;
        var allowDirector = true;
        var offerReads = 0;
        var serviceChecks = new List<(long CharacterId, long StructureId)>();
        var purchases = new List<LpStorePurchaseRequest>();
        var offer = new LpStoreOffer(storeCorporationId, offerId, 34_101, 2, 100, 500, 0, []);
        IReadOnlyList<LpStoreOffer> offers = [offer];

        var service = new LpStoreMgrService(
            null!,
            null!,
            (_, _, _) => ValueTask.FromResult(allowDirector),
            readOffers: (_, _) =>
            {
                offerReads++;
                return Task.FromResult(offers);
            },
            takePurchase: (request, _) =>
            {
                purchases.Add(request);
                if (request.NumberOfOffers <= 0)
                    throw new LpStorePurchaseRefusedException("INVALID_QUANTITY");
                IReadOnlyList<LpStoreItemChange> changes =
                [
                    new(itemId, 34_102, 0, 0, characterId, structureId, 5, 2, false, 0, 0, true, false),
                ];
                return Task.FromResult(new LpStorePurchaseReceipt("wire-structure", 100, 500m, changes));
            },
            hasLoyaltyStoreService: (checkedCharacter, checkedStructure, _) =>
            {
                serviceChecks.Add((checkedCharacter, checkedStructure));
                return ValueTask.FromResult(allowStructure);
            });

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("corporationID", new PyLong(corporationId));
                session.Set("corpAccountKey", new PyInt(1001));
                session.Set("structureid", new PyLong(structureId));
            });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var machoVersion = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        long callId = 1;

        async Task<PyValue> Call(string method, PyValue args)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(open, callId++, "LPStoreMgr", method, args, machoVersion))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            return MarshalCodec.Decode(response.Payload);
        }

        static long Number(PyValue value) => value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => throw new InvalidOperationException($"Expected an integer, got {value.GetType().Name}."),
        };

        static (string ErrorClass, string Code, PyDict Arguments) UserError(PyValue response)
        {
            var packet = MachoPacket.Decode(response);
            Assert.NotNull(packet);
            Assert.Equal(MachoMessage.ErrorResponse, packet!.Type);
            var state = Assert.IsType<PyTuple>(packet.Payload).Items;
            Assert.Equal(MachoErrorType.WrappedException, (int)Number(state[1]));
            var wrapped = Assert.IsType<PyTuple>(state[2]).Items;
            var error = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
            var header = Assert.IsType<PyTuple>(error.Header).Items;
            var errorClass = Assert.IsType<PyToken>(header[0]).Value;
            var args = Assert.IsType<PyTuple>(header[1]).Items;
            var code = System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(args[0]).Value.Span);
            return (errorClass, code, Assert.IsType<PyDict>(args[1]));
        }

        static string ErrorArgument(PyDict arguments, string name)
        {
            var value = Assert.Single(arguments.Entries,
                entry => JsSemantics.StringValue(entry.Key) == name).Value;
            return value switch
            {
                PyStr text => text.Value,
                PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
                _ => throw new InvalidOperationException($"Expected a user-error string, got {value.GetType().Name}."),
            };
        }

        var offersReply = await Call("GetAvailableOffersFromCorp", Py.Tuple(new PyLong(storeCorporationId)));
        Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(offersReply)!.Type);
        var offerRows = Assert.IsType<PyList>(Result(offersReply));
        Assert.Single(offerRows.Items);
        Assert.Equal(1, offerReads);
        Assert.Contains((characterId, structureId), serviceChecks);

        var noneOfferReply = await Call("GetAvailableOffersFromCorp", Py.Tuple(PyNone.Instance));
        Assert.Empty(Assert.IsType<PyList>(Result(noneOfferReply)).Items);
        Assert.Equal(1, offerReads);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, callId++, "LPStoreMgr", "TakeOfferForCharacter",
            Py.Tuple(new PyLong(storeCorporationId), new PyLong(offerId), new PyInt(2)), machoVersion))));
        var notification = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.Notification, MachoPacket.Decode(notification)!.Type);
        var notificationState = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(notification).Args).Items;
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(notificationState[2]).Args).Items;
        Assert.Equal("OnItemChange", JsSemantics.StringValue(address[1]));
        var packedBody = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(
            Assert.IsType<PyTuple>(notificationState[4]).Items[0]).Items[1]);
        var eventValue = Assert.IsType<PyTuple>(MarshalCodec.Decode(packedBody.Value.ToArray())).Items;
        var eventPayload = Assert.IsType<PyTuple>(eventValue[1]).Items;
        var itemChangeArgs = Assert.IsType<PyTuple>(eventPayload[1]).Items;
        var changedItem = Assert.IsType<PyPackedRow>(itemChangeArgs[0]).Values;
        Assert.Equal(itemId, Number(changedItem[0]));
        Assert.Equal(2, Number(changedItem[5]));
        Assert.IsType<PyDict>(itemChangeArgs[1]);
        Assert.Equal(PyNone.Instance, itemChangeArgs[2]);

        var purchaseReply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(purchaseReply)!.Type);
        Assert.Equal(PyBool.True, Result(purchaseReply));
        Assert.Equal(structureId, Assert.Single(purchases).LocationId);

        var invalidQuantity = await Call("TakeOfferForCharacter",
            Py.Tuple(new PyLong(storeCorporationId), new PyLong(offerId), PyNone.Instance));
        var invalidQuantityError = UserError(invalidQuantity);
        Assert.Equal(MachoErrors.UserErrorClass, invalidQuantityError.ErrorClass);
        Assert.Equal("CustomNotify", invalidQuantityError.Code);
        Assert.Equal("That offer quantity is not valid.", ErrorArgument(invalidQuantityError.Arguments, "notify"));
        Assert.Equal(0, purchases.Last().NumberOfOffers);

        allowDirector = false;
        var directorDenied = await Call("TakeOfferForCorporation",
            Py.Tuple(new PyInt(storeCorporationId), new PyInt(offerId), new PyInt(1)));
        var directorError = UserError(directorDenied);
        Assert.Equal(MachoErrors.UserErrorClass, directorError.ErrorClass);
        Assert.Equal("CustomNotify", directorError.Code);
        Assert.Equal("You do not have permission to spend your corporation's funds.",
            ErrorArgument(directorError.Arguments, "notify"));
        Assert.Equal(2, purchases.Count);

        allowStructure = false;
        var structureDenied = await Call("GetAvailableOffersFromCorp", Py.Tuple(new PyInt(storeCorporationId)));
        var structureError = UserError(structureDenied);
        Assert.Equal(MachoErrors.UserErrorClass, structureError.ErrorClass);
        Assert.Equal("CustomNotify", structureError.Code);
        Assert.Equal("That loyalty store service is not available.",
            ErrorArgument(structureError.Arguments, "notify"));
        Assert.Equal(1, offerReads);

        var deniedPurchase = await Call("TakeOfferForCharacter",
            Py.Tuple(new PyInt(storeCorporationId), new PyInt(offerId), new PyInt(1)));
        var deniedPurchaseError = UserError(deniedPurchase);
        Assert.Equal(MachoErrors.UserErrorClass, deniedPurchaseError.ErrorClass);
        Assert.Equal("CustomNotify", deniedPurchaseError.Code);
        Assert.Equal("That loyalty store service is not available.",
            ErrorArgument(deniedPurchaseError.Arguments, "notify"));
        Assert.Equal(2, purchases.Count);
    }

    [Fact]
    public async Task Lp_wallet_mutation_routes_return_their_exact_not_available_user_error()
    {
        const long characterId = 900_701;
        const long corporationId = 980_701;
        const long issuerCorporationId = 500_701;
        const long receiverCorporationId = 980_702;
        var service = new LoyaltyPointsService(null!);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyInt(characterId));
                session.Set("corporationID", new PyInt(corporationId));
            });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        long callId = 1;

        async Task<PyValue> Call(string method, PyValue args)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(open, callId++, "LPSvc", method, args, kwargs))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            return MarshalCodec.Decode(response.Payload);
        }

        static (string ErrorClass, string Code, PyDict Arguments) UserError(PyValue response)
        {
            var packet = MachoPacket.Decode(response);
            Assert.NotNull(packet);
            Assert.Equal(MachoMessage.ErrorResponse, packet!.Type);
            var state = Assert.IsType<PyTuple>(packet.Payload).Items;
            Assert.Equal(MachoErrorType.WrappedException, Assert.IsType<PyInt>(state[1]).Value);
            var wrapped = Assert.IsType<PyTuple>(state[2]).Items;
            var error = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
            var header = Assert.IsType<PyTuple>(error.Header).Items;
            var errorClass = Assert.IsType<PyToken>(header[0]).Value;
            var args = Assert.IsType<PyTuple>(header[1]).Items;
            var code = System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(args[0]).Value.Span);
            return (errorClass, code, Assert.IsType<PyDict>(args[1]));
        }

        static void AssertInfo((string ErrorClass, string Code, PyDict Arguments) error)
        {
            Assert.Equal(MachoErrors.UserErrorClass, error.ErrorClass);
            Assert.Equal("CustomInfo", error.Code);
            var info = Assert.Single(error.Arguments.Entries,
                entry => JsSemantics.StringValue(entry.Key) == "info").Value;
            Assert.Equal("This loyalty point operation is not available on this server yet.", info switch
            {
                PyStr text => text.Value,
                PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
                _ => throw new InvalidOperationException($"Expected a user-error string, got {info.GetType().Name}."),
            });
        }

        AssertInfo(UserError(await Call("ExchangeConcordLP", Py.Tuple(new PyLong(issuerCorporationId), new PyInt(500)))));
        AssertInfo(UserError(await Call("TransferLPFromMyWalletToOtherCorp",
            Py.Tuple(new PyLong(receiverCorporationId), new PyLong(issuerCorporationId), new PyLong(100)))));
        AssertInfo(UserError(await Call("TransferLPFromMyCorpWalletToOtherCorp",
            Py.Tuple(new PyInt(receiverCorporationId), new PyInt(issuerCorporationId), new PyInt(100)))));
    }

}
