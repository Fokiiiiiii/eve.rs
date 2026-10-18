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

    [Theory]
    [InlineData("PLX", 4)]
    [InlineData("AUR", 4)]
    [InlineData("USD", 9)]
    [InlineData("EUR", 9)]
    public async Task Buy_offer_passes_the_full_request_to_the_grant_for_any_accepted_currency(string currency, long storeId)
    {
        var call = PurchaseCall(currency, storeId: storeId);
        var response = new PyStr("granted");
        StoreOfferPurchaseRequest? received = null;
        var providerCalls = 0;
        var service = Service(StoreState(currency, storeId: storeId), (request, _) =>
        {
            received = request;
            providerCalls++;
            return ValueTask.FromResult<PyValue>(response);
        });

        var result = await service.CallAsync(call);

        Assert.Same(response, result);
        Assert.Equal(1, providerCalls);
        var purchase = Assert.IsType<StoreOfferPurchaseRequest>(received);
        Assert.Equal(9001L, purchase.AuthenticatedUserId);
        Assert.Equal(new PyLong(781234), purchase.OperationId);
        Assert.Same(call.Args[0], purchase.Offer);
        Assert.Same(call.Args[1], purchase.Currency);
        Assert.Same(call.Args[2], purchase.Qty);
        Assert.Same(call.Kwarg("from_character_id"), purchase.Sender);
        Assert.Same(call.Kwarg("to_character_id"), purchase.Recipient);
        Assert.Same(call.Kwarg("message"), purchase.Message);
        Assert.Same(call.Kwarg("is_game_time"), purchase.GameTime);
        Assert.Same(call.Kwarg("store_id"), purchase.Store);
        Assert.Equal(new PyStr("cash-sku"), purchase.ThirdPartyGoodsId);
        var products = Assert.IsType<PyList>(purchase.Products);
        Assert.Equal(2, products.Items.Count);
        Assert.Equal(
            Py.Dict(
                ("id", new PyInt(8001)),
                ("typeId", new PyInt(34)),
                ("quantity", new PyInt(2)),
                ("productName", new PyStr("Tritanium")),
                ("imageUrl", new PyStr("image/34.png"))),
            products.Items[0]);
    }

    [Fact]
    public async Task Buy_offer_does_not_need_a_positive_price_row_because_nothing_is_charged()
    {
        // The price rows are the listed prices shown to the player; the purchase names a currency and is granted at no charge.
        var providerCalls = 0;
        var service = Service(StoreState("PLX", price: 0, storeId: 9), (_, _) =>
        {
            providerCalls++;
            return ValueTask.FromResult<PyValue>(new PyStr("granted"));
        });

        Assert.Equal(new PyStr("granted"), await service.CallAsync(PurchaseCall("USD", storeId: 9)));
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task Buy_offer_fails_when_the_grant_is_rejected()
    {
        var providerCalls = 0;
        var service = Service(StoreState("USD", storeId: 9), (_, _) =>
        {
            providerCalls++;
            return ValueTask.FromResult<PyValue>(PyBool.False);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.CallAsync(PurchaseCall("USD", storeId: 9));
        });
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task Buy_offer_rejects_missing_offer_or_invalid_products_before_fulfillment()
    {
        var providerCalls = 0;
        Func<StoreOfferPurchaseRequest, CancellationToken, ValueTask<PyValue>> provider = (_, _) =>
        {
            providerCalls++;
            return ValueTask.FromResult<PyValue>(new PyStr("redeem-queued"));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Service(null, provider).CallAsync(PurchaseCall("USD", storeId: 9)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Service(StoreState("USD", storeId: 9, includeProducts: false), provider)
                .CallAsync(PurchaseCall("USD", storeId: 9)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Service(StoreState("USD", storeId: 9, validProducts: false), provider)
                .CallAsync(PurchaseCall("USD", storeId: 9)));

        Assert.Equal(0, providerCalls);
    }

    [Fact]
    public async Task Buy_offer_requires_a_grant_provider_and_fails_when_the_grant_is_rejected()
    {
        var call = PurchaseCall("PLX");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await Service(StoreState("PLX")).CallAsync(call);
        });

        var providerCalls = 0;
        var rejectingProvider = Service(StoreState("PLX"), (_, _) =>
        {
            providerCalls++;
            return ValueTask.FromResult<PyValue>(PyBool.False);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await rejectingProvider.CallAsync(call);
        });

        Assert.Equal(1, providerCalls);
    }

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

    private static ServiceCall PurchaseCall(
        string currency,
        long? characterId = 100,
        long storeId = 4) => new()
    {
        Method = "buy_offer",
        Args = [new PyInt(100), new PyStr(currency), new PyInt(2)],
        Kwargs = Py.Dict(
            ("from_character_id", new PyInt(100)),
            ("to_character_id", new PyInt(456)),
            ("message", new PyStr("gift")),
            ("is_game_time", PyBool.True),
            ("store_id", new PyInt(storeId))),
        Session = new ClientSession { UserId = 9001, CharacterId = characterId },
        CallId = new PyLong(781234),
    };

    private static JsonObject StoreState(
        string currency,
        bool enabled = true,
        bool canPurchase = true,
        double price = 9.99,
        long storeId = 4,
        bool includeProducts = true,
        bool validProducts = true) => new()
    {
        ["config"] = new JsonObject { ["enabled"] = enabled },
        ["stores"] = new JsonObject
        {
            [storeId.ToString()] = new JsonObject
            {
                ["offers"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = 100,
                        ["storeOfferID"] = "VEVC-100",
                        ["canPurchase"] = canPurchase,
                        ["offerPricings"] = new JsonArray(
                            new JsonObject
                            {
                                ["currency"] = currency,
                                ["price"] = price,
                                ["basePrice"] = 12.99,
                            }),
                        ["thirdpartyinfo"] = new JsonObject { ["goodsid"] = "cash-sku" },
                        ["products"] = includeProducts
                            ? new JsonArray(
                                new JsonObject
                                {
                                    ["id"] = 8001,
                                    ["typeId"] = 34,
                                    ["quantity"] = validProducts ? 2 : 0,
                                    ["productName"] = "Tritanium",
                                    ["imageUrl"] = "image/34.png",
                                },
                                new JsonObject
                                {
                                    ["id"] = 8002,
                                    ["typeId"] = 35,
                                    ["quantity"] = 1,
                                })
                            : new JsonArray(),
                    }),
            },
        },
    };
}


public sealed class RedeemServiceTests
{
    [Fact]
    public void User_svc_exposes_the_pinned_redeem_rpcs()
    {
        var service = (IService)(object)new RedeemService(new FakeRedeemCallbacks());

        Assert.Equal("userSvc", service.Name);
        Assert.All(
            new[] { "GetRedeemTokens", "ClaimRedeemTokens", "ReverseRedeem", "TrashRedeemTokens" },
            method => Assert.True(service.HasMethod(method)));
    }

    [Fact]
    public async Task Get_and_claim_redeem_tokens_preserve_account_scope_and_wire_payloads()
    {
        var callbacks = new FakeRedeemCallbacks();
        var tokens = new PyList(
        [
            Py.Dict(
                ("tokenID", new PyLong(101)),
                ("massTokenID", new PyLong(0)),
                ("typeID", new PyInt(34)),
                ("quantity", new PyInt(2)),
                ("label", new PyStr("redeem.label")),
                ("description", new PyStr("redeem.description"))),
        ]);
        callbacks.TokenResult = tokens;
        var service = (IService)(object)new RedeemService(callbacks);
        var session = new ClientSession { UserId = 9001, CharacterId = 9002 };

        var listed = await service.CallAsync(new ServiceCall
        {
            Method = "GetRedeemTokens",
            Args = [],
            Session = session,
        });
        var rowset = Assert.IsType<PyObjectEx>(listed);
        Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
        var rowsetHeader = Assert.IsType<PyTuple>(rowset.Header).Items;
        Assert.Equal(new PyToken("carbon.common.script.sys.crowset.CRowset"),
            Assert.IsType<PyTuple>(rowsetHeader[0]).Items[0]);
        Assert.IsType<PyPackedRow>(Assert.Single(rowset.List));
        Assert.Equal(9001L, callbacks.LastUserId);

        var redeemInfo = Py.Dict(
            ("errors", new PyList([])),
            ("itemsCreated", new PyList([Py.Dict(("itemID", new PyLong(7001)), ("typeID", new PyInt(34)), ("quantity", new PyInt(2)))])),
            ("stationID", new PyLong(6001)));
        callbacks.ClaimResult = redeemInfo;
        var call = new ServiceCall
        {
            Method = "ClaimRedeemTokens",
            Args = [tokens, new PyLong(9002)],
            Kwargs = Py.Dict(("useHomeStation", PyBool.True)),
            Session = session,
        };

        var claimed = await service.CallAsync(call);

        Assert.Same(redeemInfo, claimed);
        Assert.Equal(9001L, callbacks.LastUserId);
        Assert.Same(tokens, callbacks.ClaimedTokens);
        Assert.Equal(9002L, callbacks.TargetCharacterId);
        Assert.True(callbacks.UseHomeStation);
    }

    [Fact]
    public async Task Redeem_mutations_are_account_scoped_and_return_EveJS_success_values()
    {
        var callbacks = new FakeRedeemCallbacks();
        var service = (IService)(object)new RedeemService(callbacks);
        var session = new ClientSession { UserId = 9001, CharacterId = 9002 };
        var itemId = new PyLong(7001);

        var reversed = await service.CallAsync(new ServiceCall
        {
            Method = "ReverseRedeem",
            Args = [itemId],
            Session = session,
        });

        Assert.Equal(PyBool.True, reversed);
        Assert.Equal(9001L, callbacks.LastUserId);
        Assert.Same(itemId, callbacks.ReversedItemId);

        var tokens = new PyList([Py.Dict(("tokenID", new PyLong(101)), ("massTokenID", new PyLong(0)))]);
        var trashed = await service.CallAsync(new ServiceCall
        {
            Method = "TrashRedeemTokens",
            Args = [tokens],
            Session = session,
        });

        Assert.Equal(PyBool.True, trashed);
        Assert.Equal(9001L, callbacks.LastUserId);
        Assert.Same(tokens, callbacks.TrashedTokens);
    }

    [Fact]
    public async Task Redeem_rpcs_reject_a_missing_account_identity()
    {
        var callbacks = new FakeRedeemCallbacks();
        var service = (IService)(object)new RedeemService(callbacks);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.CallAsync(new ServiceCall
            {
                Method = "GetRedeemTokens",
                Args = [],
                Session = new ClientSession { CharacterId = 9002 },
            }));

        Assert.Equal(0, callbacks.Calls);
    }

    private sealed class FakeRedeemCallbacks : IRedeemCallbacks
    {
        public PyValue TokenResult { get; set; } = new PyList([]);
        public PyValue ClaimResult { get; set; } = PyNone.Instance;
        public long LastUserId { get; private set; }
        public PyValue? ClaimedTokens { get; private set; }
        public long TargetCharacterId { get; private set; }
        public bool UseHomeStation { get; private set; }
        public PyValue? ReversedItemId { get; private set; }
        public PyValue? TrashedTokens { get; private set; }
        public int Calls { get; private set; }

        public ValueTask<PyValue> GetRedeemTokensAsync(long authenticatedUserId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastUserId = authenticatedUserId;
            return ValueTask.FromResult(TokenResult);
        }

        public ValueTask<PyValue> ClaimRedeemTokensAsync(
            long authenticatedUserId,
            PyValue tokens,
            long characterId,
            bool useHomeStation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastUserId = authenticatedUserId;
            ClaimedTokens = tokens;
            TargetCharacterId = characterId;
            UseHomeStation = useHomeStation;
            return ValueTask.FromResult(ClaimResult);
        }

        public ValueTask ReverseRedeemAsync(
            long authenticatedUserId,
            PyValue itemId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastUserId = authenticatedUserId;
            ReversedItemId = itemId;
            return ValueTask.CompletedTask;
        }

        public ValueTask TrashRedeemTokensAsync(
            long authenticatedUserId,
            PyValue tokens,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastUserId = authenticatedUserId;
            TrashedTokens = tokens;
            return ValueTask.CompletedTask;
        }
    }
}



public sealed class VaultManagerServiceTests
{
    [Fact]
    public async Task Get_account_balance_rejects_unsupported_currency_without_calling_the_provider()
    {
        var callbacks = new FakeVaultManagerCallbacks(10);
        IService service = (IService)(object)new VaultManagerService(callbacks);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await service.CallAsync(new ServiceCall
            {
                Method = "get_account_balance",
                Args = [new PyStr("GMS")],
                Session = new ClientSession { UserId = 9001 },
            });
        });

        Assert.Null(callbacks.AuthenticatedUserId);
    }

    [Fact]
    public async Task Get_account_balance_rejects_a_missing_account_without_calling_the_provider()
    {
        var callbacks = new FakeVaultManagerCallbacks(10);
        IService service = (IService)(object)new VaultManagerService(callbacks);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await service.CallAsync(new ServiceCall
            {
                Method = "get_account_balance",
                Args = [new PyStr("PLX")],
                Session = new ClientSession(),
            });
        });

        Assert.Null(callbacks.AuthenticatedUserId);
    }

    [Fact]
    public async Task Get_account_balance_reads_the_currency_the_client_sends_as_a_byte_string()
    {
        var callbacks = new FakeVaultManagerCallbacks(4_294_967_321);
        IService service = (IService)(object)new VaultManagerService(callbacks);

        var result = await service.CallAsync(new ServiceCall
        {
            Method = "get_account_balance",
            Args = [new PyBytes("PLX"u8.ToArray())],
            Session = new ClientSession { UserId = 9001 },
        });

        Assert.Equal(new PyLong(4_294_967_321), result);
        Assert.Equal(9001L, callbacks.AuthenticatedUserId);
    }

    [Fact]
    public async Task Get_account_balance_returns_the_authenticated_accounts_plex_balance()
    {
        var callbacks = new FakeVaultManagerCallbacks(4_294_967_321);
        IService service = (IService)(object)new VaultManagerService(callbacks);

        Assert.Equal("vaultManager", service.Name);
        Assert.True(service.HasMethod("get_account_balance"));
        var result = await service.CallAsync(new ServiceCall
        {
            Method = "get_account_balance",
            Args = [new PyStr("PLX")],
            Session = new ClientSession { UserId = 9001 },
        });

        Assert.Equal(new PyLong(4_294_967_321), result);
        Assert.Equal(9001L, callbacks.AuthenticatedUserId);
    }

    private sealed class FakeVaultManagerCallbacks(long plexBalance) : IVaultManagerCallbacks
    {
        public long? AuthenticatedUserId { get; private set; }

        public ValueTask<long> GetPlexBalanceAsync(long authenticatedUserId, CancellationToken cancellationToken)
        {
            AuthenticatedUserId = authenticatedUserId;
            return ValueTask.FromResult(plexBalance);
        }
    }
}
