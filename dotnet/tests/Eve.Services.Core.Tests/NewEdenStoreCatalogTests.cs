using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The New Eden Store catalog: the embedded seed the server serves, and the store service reading it.</summary>
public sealed class NewEdenStoreCatalogTests
{
    private const long OmegaClone = 29143;
    private const long MultipleTraining = 63188;

    [Fact]
    public void The_store_4_offers_are_the_15_seeded_offers_with_real_type_ids_for_every_product()
    {
        var offers = StoreOffers(NewEdenStoreCatalog.Default.Root, "4");

        Assert.Equal(15, offers.Count);
        Assert.All(offers, offer => Assert.All(
            offer["products"]!.AsArray(), product => Assert.True(product!["typeId"]!.GetValue<long>() > 0)));
        Assert.Equal(OmegaClone, FirstProductTypeId(offers, "omega_30_days"));
        Assert.Equal(MultipleTraining, FirstProductTypeId(offers, "mct_slot_30_days"));
    }

    [Fact]
    public void Every_store_offer_has_a_public_offer_whose_fulfillment_the_server_can_grant()
    {
        var root = NewEdenStoreCatalog.Default.Root;
        var publicOffers = root["publicOffers"]!.AsObject();
        foreach (var storeId in new[] { "4", "9" })
        {
            foreach (var offer in StoreOffers(root, storeId))
            {
                var fulfillment = publicOffers[offer["storeOfferID"]!.GetValue<string>()]!["fulfillment"]!;
                Assert.Contains(fulfillment["kind"]!.GetValue<string>(), new[] { "omega", "mct", "item", "grant_plex", "bundle" });
                Assert.DoesNotContain("skill_points", fulfillment.ToJsonString());
            }
        }
    }

    [Fact]
    public void The_real_money_store_lists_the_plex_packs_the_fast_checkout_sells_and_the_cash_omega_and_mct_offers()
    {
        var offers = StoreOffers(NewEdenStoreCatalog.Default.Root, "9");

        Assert.Equal(21, offers.Count);
        Assert.Contains(offers, offer => offer["storeOfferID"]!.GetValue<string>() == "cash_plex_500_usd");
        Assert.Contains(offers, offer => offer["storeOfferID"]!.GetValue<string>() == "cash_omega_365_days");
        Assert.Contains(offers, offer => offer["storeOfferID"]!.GetValue<string>() == "cash_mct_6");
        var listedPrice = offers.Single(offer => offer["storeOfferID"]!.GetValue<string>() == "cash_plex_500_usd")["offerPricings"]!.AsArray().Single()!;
        Assert.Equal("USD", listedPrice["currency"]!.GetValue<string>());
        Assert.Equal(25, listedPrice["price"]!.GetValue<double>());
    }

    [Fact]
    public void The_client_flags_and_the_fast_checkout_pack_list_are_derived_from_the_catalog()
    {
        var catalog = NewEdenStoreCatalog.Default;

        Assert.True(catalog.StoreEnabled);
        Assert.False(catalog.FastCheckoutEnabled);
        Assert.False(catalog.FakeVgsEnabled);
        Assert.False(catalog.ChinaFunnelEnabled);
        Assert.Equal("", catalog.FakeBuyPlexOfferUrl);
        Assert.True(catalog.UseShellExecuteToBuyPlexOffer);
        Assert.Equal("FAST_CHECKOUT_UNAVAILABLE", catalog.FakeFastCheckoutResponse);
        var packs = JsonNode.Parse(catalog.FastCheckoutPlexOffers)!.AsArray();
        Assert.Empty(packs);
    }

    [Fact]
    public void A_catalog_with_another_schema_is_refused()
    {
        var root = JsonNode.Parse(ReadCatalogText())!.AsObject();
        root["meta"]!["schema"] = "eve.rs.other";

        Assert.Throws<InvalidDataException>(() => NewEdenStoreCatalog.Parse(root.ToJsonString()));
    }

    [Fact]
    public async Task The_store_lists_the_catalog_offers_and_answers_can_purchase_from_it()
    {
        var purchaseCalls = 0;
        var service = new StoreOffersService(
            _ => ValueTask.FromResult<JsonObject?>(NewEdenStoreCatalog.Default.Root),
            (_, _) =>
            {
                purchaseCalls++;
                throw new InvalidOperationException("Availability reads must not start a purchase.");
            });

        Assert.Equal(15, Assert.IsType<PyList>(await service.CallAsync(Call("get_offers", new PyInt(4)))).Items.Count);
        Assert.Equal(21, Assert.IsType<PyList>(await service.CallAsync(Call("get_offers", new PyInt(9)))).Items.Count);
        Assert.Equal(15, Assert.IsType<PyList>(await service.CallAsync(Call("get_products", new PyInt(4)))).Items.Count);

        var available = Assert.IsType<PyDict>(await service.CallAsync(Call("can_purchase_offer", new PyStr("omega_30_days"))));
        Assert.Equal(PyBool.True, Field(available, "canPurchase"));
        var unknown = Assert.IsType<PyDict>(await service.CallAsync(Call("can_purchase_offer", new PyStr("no_such_offer"))));
        Assert.Equal(PyBool.False, Field(unknown, "canPurchase"));
        Assert.Equal(0, purchaseCalls);
    }

    private static List<JsonObject> StoreOffers(JsonObject root, string storeId) =>
        root["stores"]!.AsObject()[storeId]!["offers"]!.AsArray().OfType<JsonObject>().ToList();

    private static long FirstProductTypeId(IEnumerable<JsonObject> offers, string storeOfferId) =>
        offers.Single(offer => offer["storeOfferID"]!.GetValue<string>() == storeOfferId)["products"]!.AsArray()[0]!["typeId"]!.GetValue<long>();

    private static string ReadCatalogText()
    {
        using var stream = typeof(NewEdenStoreCatalog).Assembly.GetManifestResourceStream("new-eden-store.v1.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Eve.Services.ServiceCall Call(string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = new Eve.Services.ClientSession { CharacterId = 100, UserId = 9001 },
    };

    private static PyValue Field(PyDict dict, string name) =>
        dict.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;
}
