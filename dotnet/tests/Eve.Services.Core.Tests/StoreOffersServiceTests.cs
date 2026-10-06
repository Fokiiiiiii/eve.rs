using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class StoreOffersServiceTests
{
    [Fact]
    public void Advertises_the_reference_rpcs_without_local_vgs_wrappers()
    {
        var service = Service(null);

        Assert.Equal("storeManager", service.Name);
        Assert.All(
            new[] { "get_offers", "GetOffers", "get_categories", "GetCategories", "get_products", "GetProducts", "can_purchase_offer", "CanPurchaseOffer" },
            method => Assert.True(service.HasMethod(method)));
        Assert.True(service.HasMethod("buy_offer"));
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

    [Fact]
    public async Task Buy_offer_forwards_exact_args_and_authenticated_caller_and_returns_raw_response()
    {
        var args = PurchaseArgs();
        var response = new PyStr("provider-receipt");
        StoreOfferPurchaseRequest? received = null;
        var service = Service(null, (request, _) =>
        {
            received = request;
            return ValueTask.FromResult<PyValue>(response);
        });

        var result = await service.CallAsync(Request("buy_offer", args));

        Assert.Same(response, result);
        var purchase = Assert.IsType<StoreOfferPurchaseRequest>(received);
        Assert.Equal(100L, purchase.AuthenticatedCharacterId);
        Assert.Same(args[0], purchase.Offer);
        Assert.Same(args[1], purchase.Currency);
        Assert.Same(args[2], purchase.Qty);
        Assert.Same(args[3], purchase.Sender);
        Assert.Same(args[4], purchase.Recipient);
        Assert.Same(args[5], purchase.Message);
        Assert.Same(args[6], purchase.GameTime);
        Assert.Same(args[7], purchase.Store);
    }

    [Fact]
    public async Task Buy_offer_retry_identity_includes_every_field_including_game_time()
    {
        var args = PurchaseArgs();
        var changedGameTime = PurchaseArgs();
        changedGameTime[6] = new PyLong(123457);
        var requests = new List<StoreOfferPurchaseRequest>();
        var service = Service(null, (request, _) =>
        {
            requests.Add(request);
            return ValueTask.FromResult<PyValue>(new PyStr("provider-receipt"));
        });

        await service.CallAsync(Request("buy_offer", args));
        await service.CallAsync(Request("buy_offer", args));
        await service.CallAsync(Request("buy_offer", changedGameTime));

        Assert.Equal(requests[0], requests[1]);
        Assert.NotEqual(requests[0], requests[2]);
        Assert.Same(args[6], requests[0].GameTime);
    }

    [Fact]
    public async Task Buy_offer_rejects_missing_or_nonpositive_authenticated_character_before_provider()
    {
        var providerCalls = 0;
        var service = Service(null, (_, _) =>
        {
            providerCalls++;
            return ValueTask.FromResult<PyValue>(new PyStr("provider-receipt"));
        });

        var missingCharacter = Request("buy_offer", PurchaseArgs());
        missingCharacter.Session.CharacterId = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await service.CallAsync(missingCharacter);
        });

        var nonpositiveCharacter = Request("buy_offer", PurchaseArgs());
        nonpositiveCharacter.Session.CharacterId = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await service.CallAsync(nonpositiveCharacter);
        });

        Assert.Equal(0, providerCalls);
    }

    [Fact]
    public async Task Buy_offer_fails_when_provider_is_absent_or_returns_false()
    {
        var request = Request("buy_offer", PurchaseArgs());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await Service(null).CallAsync(request);
        });

        var rejecting = Service(null, (_, _) => ValueTask.FromResult<PyValue>(PyBool.False));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await rejecting.CallAsync(request);
        });
    }

    private static PyValue[] PurchaseArgs() =>
    [
        new PyStr("VEVC-100"),
        new PyStr("ISK"),
        new PyInt(2),
        new PyInt(999),
        new PyInt(456),
        new PyStr("gift"),
        new PyLong(123456),
        new PyInt(4),
    ];

    private static StoreOffersService Service(
        JsonObject? state,
        Func<StoreOfferPurchaseRequest, CancellationToken, ValueTask<PyValue>>? purchaseWriter = null) =>
        new(_ => ValueTask.FromResult<JsonObject?>(state), purchaseWriter);

    private static ServiceCall Request(string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = new ClientSession { CharacterId = 100 },
    };
}
