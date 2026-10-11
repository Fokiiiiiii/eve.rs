using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The in-client PLEX checkout service the client calls (fastCheckoutClientService.py), with the catalog and a fake grant.</summary>
public sealed class FastCheckoutServiceTests
{
    private const long UsdPackOfferId = 400100;

    [Fact]
    public async Task The_checkout_is_enabled_and_its_testing_configuration_is_the_catalog_s()
    {
        var service = Service(NewEdenStoreCatalog.Default, new RecordingGrants());

        Assert.Equal(PyBool.True, await service.CallAsync(Call("IsFastCheckoutEnabledForUser")));
        Assert.Equal(PyBool.True, await service.CallAsync(Call("ClearFastCheckoutCacheForUser")));
        var configuration = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetTestingConfiguration")));
        Assert.Equal(PyBool.False, configuration.Items[0]);
        Assert.Equal(new PyStr(""), configuration.Items[1]);
        Assert.Equal(PyBool.True, configuration.Items[2]);
    }

    [Fact]
    public async Task The_offers_for_the_user_are_the_nine_plex_packs_with_their_listed_prices()
    {
        var service = Service(NewEdenStoreCatalog.Default, new RecordingGrants());

        var offers = Assert.IsType<PyDict>(await service.CallAsync(Call("GetOffersForUser")));
        var plex = Assert.IsType<PyList>(Field(offers, "plex")).Items;
        Assert.Equal(9, plex.Count);
        var first = Assert.IsType<PyObject>(plex[0]).Args as PyDict ?? throw new InvalidOperationException("no KeyVal fields");
        Assert.Equal(new PyInt(UsdPackOfferId), Field(first, "id"));
        Assert.Equal(new PyStr("USD"), Field(first, "currency"));
        Assert.Equal(new PyInt(100), Field(first, "quantity"));
        Assert.Equal(new PyReal(5), Field(first, "price"));
    }

    [Fact]
    public async Task Buy_offer_grants_the_pack_at_no_charge_and_answers_ok_for_the_client_to_confirm()
    {
        var grants = new RecordingGrants();
        var service = Service(NewEdenStoreCatalog.Default, grants);

        var result = Assert.IsType<PyDict>(await service.CallAsync(BuyCall(UsdPackOfferId, callId: 77)));

        Assert.Equal(new PyStr("OK"), Field(result, "Message"));
        Assert.Equal(new PyInt(UsdPackOfferId), Field(result, "offerID"));
        Assert.Equal(new PyStr("cash_plex_100_usd"), Field(result, "storeOfferID"));
        var grant = Assert.Single(grants.Calls);
        Assert.Equal((9001L, 100L, "cash_plex_100_usd", "USD", "77"), (grant.UserId, grant.CharacterId, grant.StoreOfferId, grant.Currency, grant.OperationId));
    }

    [Fact]
    public async Task Buy_offer_of_an_unknown_offer_is_refused_before_any_grant()
    {
        var grants = new RecordingGrants();
        var service = Service(NewEdenStoreCatalog.Default, grants);

        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(BuyCall(12345, callId: 1)));
        Assert.Empty(grants.Calls);
    }

    [Fact]
    public async Task Buy_offer_is_refused_while_fast_checkout_is_disabled_and_grants_nothing()
    {
        var root = JsonNode.Parse(CatalogText())!.AsObject();
        root["config"]!["fastCheckoutEnabled"] = false;
        var grants = new RecordingGrants();
        var service = Service(NewEdenStoreCatalog.Parse(root.ToJsonString()), grants);

        Assert.Equal(PyBool.False, await service.CallAsync(Call("IsFastCheckoutEnabledForUser")));
        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(BuyCall(UsdPackOfferId, callId: 2)));
        Assert.Empty(grants.Calls);
    }

    [Fact]
    public async Task Buy_offer_without_a_character_is_refused()
    {
        var grants = new RecordingGrants();
        var service = Service(NewEdenStoreCatalog.Default, grants);
        var call = BuyCall(UsdPackOfferId, callId: 3, session: new ClientSession { UserId = 9001 });

        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(call));
        Assert.Empty(grants.Calls);
    }

    private static IService Service(NewEdenStoreCatalog catalog, IFastCheckoutCallbacks grants) =>
        (IService)(object)new FastCheckoutService(catalog, grants);

    private static ServiceCall Call(string method) => new()
    {
        Method = method,
        Args = [],
        Session = new ClientSession { CharacterId = 100, UserId = 9001 },
    };

    private static ServiceCall BuyCall(long offerId, long callId, ClientSession? session = null) => new()
    {
        Method = "BuyOffer",
        Args = [],
        Kwargs = Py.Dict(
            ("offer", Py.KeyVal(("id", new PyInt(offerId)), ("storeOfferID", new PyStr("ignored")))),
            ("password", new PyStr("not checked, as in EVE.js")),
            ("purchaseTraceID", new PyStr("trace")),
            ("journeyID", new PyStr("journey"))),
        Session = session ?? new ClientSession { CharacterId = 100, UserId = 9001 },
        CallId = new PyInt(callId),
    };

    private static string CatalogText()
    {
        using var stream = typeof(NewEdenStoreCatalog).Assembly.GetManifestResourceStream("new-eden-store.v1.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static PyValue Field(PyDict dict, string name) =>
        dict.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

    private sealed record GrantCall(long UserId, long CharacterId, string StoreOfferId, string Currency, string OperationId);

    private sealed class RecordingGrants : IFastCheckoutCallbacks
    {
        public List<GrantCall> Calls { get; } = [];

        public ValueTask GrantFastCheckoutOfferAsync(
            long authenticatedUserId,
            long characterId,
            string storeOfferId,
            string currency,
            string operationId,
            CancellationToken cancellationToken)
        {
            Calls.Add(new GrantCall(authenticatedUserId, characterId, storeOfferId, currency, operationId));
            return ValueTask.CompletedTask;
        }
    }
}
