using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class StoreOffersServiceTests
{
    [Fact]
    public void Advertises_the_reference_read_rpcs_and_no_purchase_writes()
    {
        var service = Service(null);

        Assert.Equal("storeManager", service.Name);
        Assert.All(
            new[] { "get_offers", "GetOffers", "get_categories", "GetCategories", "get_products", "GetProducts", "can_purchase_offer", "CanPurchaseOffer" },
            method => Assert.True(service.HasMethod(method)));
        Assert.False(service.HasMethod("buy_offer"));
        Assert.False(service.HasMethod("BuyOffer"));
        Assert.False(service.HasMethod("GetOffer"));
    }

    [Fact]
    public async Task Missing_store_returns_the_reference_empty_catalog_lists()
    {
        var service = Service(null);

        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("GetOffers"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("GetCategories"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("GetProducts"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("get_offers", new PyInt(999)))).Items);
    }

    [Fact]
    public async Task Missing_offer_returns_the_reference_unavailable_detail()
    {
        var service = Service(null);

        var expected = Py.Dict(
            ("canPurchase", PyBool.False),
            ("offerID", PyNone.Instance),
            ("storeOfferID", PyNone.Instance),
            ("purchaseLimitedReason", new PyStr("offer_unavailable")));

        Assert.Equal(expected, await service.CallAsync(Request("CanPurchaseOffer", new PyStr("VEVC-100"))));
    }

    private static StoreOffersService Service(JsonObject? state) =>
        new(_ => ValueTask.FromResult<JsonObject?>(state));

    private static ServiceCall Request(string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = new ClientSession { CharacterId = 100 },
    };
}
