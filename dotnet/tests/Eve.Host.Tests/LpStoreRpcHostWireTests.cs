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
}
