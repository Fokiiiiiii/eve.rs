using System.Text;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Skills;
using Eve.Services.Space;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private static PyValue NsField(PyDict value, string key) =>
        value.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == key).Value;

    private async Task<string> NsHostRpcErrorAsync(
        TestFront front,
        SessionOpen open,
        long callId,
        string service,
        string method,
        PyTuple args,
        PyDict? kwargs = null)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, service, method, args, kwargs ?? TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
        return Encoding.Latin1.GetString(send.Payload.Span);
    }

    private async Task<PyValue> NsHostRpcKwargsAsync(
        TestFront front,
        SessionOpen open,
        long callId,
        string service,
        string method,
        PyTuple args,
        PyDict kwargs)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, service, method, args, kwargs))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
        return Result(decoded)!;
    }

    [Fact]
    public async Task Random_jump_rejection_keeps_the_client_error_envelope_through_host_wire()
    {
        Start(configure: registry => registry.Register(new RandomJumpMgrService()));
        var (front, open) = await OpenFrontAsync();

        var body = await NsHostRpcErrorAsync(
            front, open, 1, "randomJumpMgr", "ActivateRandomJumpFilament",
            new PyTuple([new PyInt(0)]));

        Assert.Contains("RandomJumpError", body);
    }

    [Fact]
    public async Task Season_manager_no_data_reads_and_unclaimable_rewards_keep_client_shapes_over_host_wire()
    {
        Start(configure: registry => registry.Register(new SeasonManagerService()));
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(PyBool.True, await HostRpcAsync(front, open, 1, "seasonManager", "challenge_is_dormant", new PyInt(12)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 2, "seasonManager", "get_challenge", new PyInt(12)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 3, "seasonManager", "get_challenge_expiration_date", new PyInt(12)));
        Assert.Empty(Assert.IsType<PyDict>(await HostRpcAsync(
            front, open, 4, "seasonManager", "fill_up_and_return_challenges_for_character")).Entries);
        Assert.Equal(PyBool.False, await HostRpcAsync(
            front, open, 5, "seasonManager", "claim_challenge_rewards", new PyInt(12), new PyStr("Challenge Card")));
        Assert.Equal(PyBool.False, await HostRpcAsync(
            front, open, 6, "seasonManager", "claim_goal_reward", new PyInt(0)));
    }

    [Fact]
    public async Task Season_loot_table_lookup_keeps_the_client_list_shape_over_host_wire()
    {
        // seasons/client/seasonservice.py:167 get_next_reward_loot iterates the returned list.
        Start(configure: registry => registry.Register(new SeasonManagerService()));
        var (front, open) = await OpenFrontAsync();

        var loot = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 1, "seasonManager", "get_loot_table_by_typeID", new PyInt(34)));
        Assert.Empty(loot.Items);
    }

    [Fact]
    public async Task Season_event_uses_the_client_keyword_fields_over_host_wire()
    {
        SeasonClientEvent? observed = null;
        Start(configure: registry => registry.Register(new SeasonManagerService((clientEvent, _, _) =>
        {
            observed = clientEvent;
            return ValueTask.CompletedTask;
        })));
        var (front, open) = await OpenFrontAsync();
        var eventType = new PyInt(19);
        var extra = Py.Dict(("typeID", new PyInt(34)), ("objectID", new PyLong(90_000_001)));
        var kwargs = Py.Dict(
            ("machoVersion", new PyInt(1)),
            ("extra_data", extra),
            ("amount", new PyInt(3)));

        Assert.Same(PyNone.Instance, await NsHostRpcKwargsAsync(
            front, open, 1, "seasonManager", "process_client_event",
            new PyTuple([eventType]), kwargs));
        Assert.Equal(eventType, observed!.EventType);
        var observedExtra = Assert.IsType<PyDict>(observed.ExtraData);
        Assert.Equal(new PyInt(34), NsField(observedExtra, "typeID"));
        Assert.Equal(new PyLong(90_000_001), NsField(observedExtra, "objectID"));
        Assert.Equal(new PyInt(3), observed.Amount);
    }

    [Fact]
    public async Task Season_event_without_progress_runtime_fails_closed_over_host_wire()
    {
        Start(configure: registry => registry.Register(new SeasonManagerService()));
        var (front, open) = await OpenFrontAsync();
        var kwargs = Py.Dict(("machoVersion", new PyInt(1)), ("extra_data", Py.Dict()));

        var body = await NsHostRpcErrorAsync(
            front, open, 1, "seasonManager", "process_client_event",
            new PyTuple([new PyInt(19)]), kwargs);

        Assert.Contains("CustomInfo", body);
        Assert.Contains("Season progression is unavailable.", body);
    }

    [Fact]
    public async Task Inventory_asset_holding_qa_toggles_keep_defaults_and_session_scope_over_host_wire()
    {
        Start(sessionOpened: session => session.Set("role", new PyLong(4_503_599_627_370_496L)));
        var (firstFront, firstOpen) = await OpenFrontAsync();
        var (secondFront, secondOpen) = await OpenFrontAsync();

        Assert.Equal(PyBool.False, await HostRpcAsync(firstFront, firstOpen, 1,
            "inventoryAssetHoldingMgr", "is_item_failure_enabled"));
        Assert.Equal(PyBool.True, await HostRpcAsync(firstFront, firstOpen, 2,
            "inventoryAssetHoldingMgr", "is_item_validation_enabled"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(firstFront, firstOpen, 3,
            "inventoryAssetHoldingMgr", "set_item_failure_enabled", PyBool.True));
        Assert.Equal(PyBool.True, await HostRpcAsync(firstFront, firstOpen, 4,
            "inventoryAssetHoldingMgr", "is_item_failure_enabled"));
        Assert.Equal(PyBool.False, await HostRpcAsync(secondFront, secondOpen, 1,
            "inventoryAssetHoldingMgr", "is_item_failure_enabled"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(firstFront, firstOpen, 5,
            "inventoryAssetHoldingMgr", "set_item_validation_enabled", PyBool.False));
        Assert.Equal(PyBool.False, await HostRpcAsync(firstFront, firstOpen, 6,
            "inventoryAssetHoldingMgr", "is_item_validation_enabled"));
        Assert.Equal(PyBool.True, await HostRpcAsync(secondFront, secondOpen, 2,
            "inventoryAssetHoldingMgr", "is_item_validation_enabled"));
    }

    [Fact]
    public async Task Season_reload_fsd_data_returns_the_reference_false_over_host_wire()
    {
        IReadOnlyList<PyValue>? auditedArguments = null;
        Start(configure: registry => registry.Register(new SeasonManagerService(
            auditReloadFsdData: (_, arguments) => auditedArguments = arguments.ToArray())));
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(PyBool.False, await HostRpcAsync(front, open, 1,
            "seasonManager", "reload_fsd_data"));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<PyValue>>(auditedArguments));
    }

    [Fact]
    public async Task Abyssal_content_tool_calls_keep_the_evejs_rejected_method_none_reply_over_host_wire()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 1,
            "abyssal_content_manager", "GetContentDetailsForShipLocation"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 2,
            "abyssal_content_manager", "GenerateContentFromExistingPlan", new PyStr("{}")));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 3,
            "abyssal_content_manager", "GenerateAbyssEncounterContent",
            new PyInt(172), new PyInt(1), new PyInt(0), new PyInt(0), new PyInt(0), new PyInt(1)));
    }

    [Fact]
    public async Task Ship_cosmetics_reads_keep_empty_and_none_shapes_and_mutations_fail_closed_without_providers()
    {
        var service = new ShipCosmeticsMgrRpcService();
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(90_000_001)));
        var (front, open) = await OpenFrontAsync();

        Assert.Empty(Assert.IsType<PyDict>(await HostRpcAsync(
            front, open, 1, "shipCosmeticsMgr", "GetEnabledCosmetics", new PyInt(44))).Entries);
        Assert.Empty(Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 2, "shipCosmeticsMgr", "GetLicencedSkins")).Items);
        Assert.Empty(Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 3, "shipCosmeticsMgr", "GetLicencedSkinsForShipType",
            new PyInt(90_000_001), new PyInt(587))).Items);
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 4, "shipCosmeticsMgr", "GetFirstPartySkinData",
            new PyInt(90_000_001), new PyInt(7001)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 5, "shipCosmeticsMgr", "ActivateSkinLicense", new PyList([])));

        var apply = await NsHostRpcErrorAsync(
            front, open, 6, "shipCosmeticsMgr", "ApplySkinToShip",
            new PyTuple([new PyInt(44), new PyInt(7001)]));
        Assert.Contains("SKIN application is unavailable.", apply);

        var activate = await NsHostRpcErrorAsync(
            front, open, 7, "shipCosmeticsMgr", "ActivateSkinLicense",
            new PyTuple([new PyList([new PyInt(8001)])]));
        Assert.Contains("SKIN license activation is unavailable.", activate);
    }

    [Fact]
    public async Task Store_manager_offers_products_and_availability_keep_vgs_fields_over_host_wire()
    {
        var state = JsonNode.Parse(
            """{"stores":{"4":{"offers":[{"id":7,"name":"Skill bundle","description":"Two skills","href":"/offers/7","offerPricings":[{"currency":"PLEX","price":100,"basePrice":120}],"imageUrl":"/offer.png","products":[{"id":701,"typeId":34,"quantity":2,"productName":"Skillbook","imageUrl":"/skill.png"}],"categories":[{"id":9}],"label":{"name":"Bundle","description":"Limited offer","url":"/bundle"},"thirdpartyinfo":{"goodsid":"goods-7"},"canPurchase":false,"singlePurchase":true}],"products":[{"id":701,"name":"Skillbook","href":"/products/701"}]}}}""")!.AsObject();
        Start(configure: registry => registry.Register(
            new StoreOffersService(_ => ValueTask.FromResult<JsonObject?>(state))));
        var (front, open) = await OpenFrontAsync();

        var offers = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 1, "storeManager", "get_offers", new PyInt(4)));
        var offer = Assert.IsType<PyDict>(Assert.Single(offers.Items));
        Assert.Equal(new PyInt(7), NsField(offer, "id"));
        Assert.Equal("Skill bundle", JsSemantics.StringValue(NsField(offer, "name")));
        Assert.Equal("Two skills", JsSemantics.StringValue(NsField(offer, "description")));
        Assert.Equal("/offers/7", JsSemantics.StringValue(NsField(offer, "href")));
        Assert.Equal("/offer.png", JsSemantics.StringValue(NsField(offer, "imageUrl")));
        Assert.Equal(PyBool.False, NsField(offer, "canPurchase"));
        Assert.Equal(PyBool.True, NsField(offer, "singlePurchase"));
        var label = Assert.IsType<PyDict>(NsField(offer, "label"));
        Assert.Equal("Bundle", JsSemantics.StringValue(NsField(label, "name")));
        Assert.Equal("Limited offer", JsSemantics.StringValue(NsField(label, "description")));
        Assert.Equal("/bundle", JsSemantics.StringValue(NsField(label, "url")));
        var pricing = Assert.IsType<PyDict>(Assert.Single(Assert.IsType<PyList>(NsField(offer, "offerPricings")).Items));
        Assert.Equal("PLEX", JsSemantics.StringValue(NsField(pricing, "currency")));
        Assert.Equal(new PyInt(100), NsField(pricing, "price"));
        Assert.Equal(new PyInt(120), NsField(pricing, "basePrice"));
        var product = Assert.IsType<PyDict>(Assert.Single(Assert.IsType<PyList>(NsField(offer, "products")).Items));
        Assert.Equal(new PyInt(701), NsField(product, "id"));
        Assert.Equal(new PyInt(34), NsField(product, "typeId"));
        Assert.Equal(new PyInt(2), NsField(product, "quantity"));
        Assert.Equal("Skillbook", JsSemantics.StringValue(NsField(product, "productName")));
        Assert.Equal("/skill.png", JsSemantics.StringValue(NsField(product, "imageUrl")));
        var category = Assert.IsType<PyDict>(Assert.Single(Assert.IsType<PyList>(NsField(offer, "categories")).Items));
        Assert.Equal(new PyInt(9), NsField(category, "id"));
        Assert.Equal("goods-7", JsSemantics.StringValue(NsField(
            Assert.IsType<PyDict>(NsField(offer, "thirdpartyinfo")), "goodsid")));

        var products = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 2, "storeManager", "get_products", new PyInt(4)));
        var row = Assert.IsType<PyDict>(Assert.Single(products.Items));
        Assert.Equal(new PyInt(701), NsField(row, "id"));
        Assert.Equal("Skillbook", JsSemantics.StringValue(NsField(row, "name")));
        Assert.Equal("/products/701", JsSemantics.StringValue(NsField(row, "href")));

        var availability = Assert.IsType<PyDict>(await HostRpcAsync(
            front, open, 3, "storeManager", "can_purchase_offer", new PyStr("VEVC-7")));
        Assert.Equal(PyBool.False, NsField(availability, "canPurchase"));
        Assert.Same(PyNone.Instance, NsField(availability, "offerID"));
        Assert.Same(PyNone.Instance, NsField(availability, "storeOfferID"));
        var reason = Assert.IsType<PyBytes>(NsField(availability, "purchaseLimitedReason"));
        Assert.Equal("payment_unavailable", System.Text.Encoding.UTF8.GetString(reason.Value.Span));

        var buyKwargs = Py.Dict(
            ("machoVersion", new PyInt(1)),
            ("from_character_id", new PyInt(90_000_001)),
            ("to_character_id", PyNone.Instance),
            ("message", PyNone.Instance),
            ("is_game_time", PyBool.False),
            ("store_id", new PyInt(4)));
        var checkout = await NsHostRpcKwargsAsync(
            front, open, 4, "storeManager", "buy_offer",
            new PyTuple([new PyInt(7), new PyStr("PLEX"), new PyInt(1)]), buyKwargs);
        Assert.Same(PyNone.Instance, checkout);
    }

    [Fact]
    public async Task Skill_handler_purchase_skills_fails_closed_on_the_registered_bound_host_route()
    {
        const long characterId = 90_000_001;
        const string objectId = "N=ProxyNode:skill-purchase-test";
        var handler = new SkillHandlerService(
            null!, null!, null!, null!, null!, null!, null!, () => FixedNowMs, null!);
        Start(sessionOpened: session =>
            {
                session.Set("charid", new PyInt(characterId));
                _testRegistry.RegisterBoundObjectForSession(objectId, handler, session);
            });
        var (front, open) = await OpenFrontAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 1, objectId, "PurchaseSkills",
                new PyTuple([new PyList([new PyInt(34)])]), TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
        var body = Encoding.Latin1.GetString(send.Payload.Span);
        Assert.Contains("SkillPurchaseDisabled", body);
    }

    [PgFact]
    public async Task Skill_handler_purchase_skills_debits_the_wallet_and_grants_the_skill_over_host_wire()
    {
        // characterskills/client/purchase.py:100 extends its list with the reply, so the reply is the tuple of granted type ids.
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var owned = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character)).Select(skill => skill.TypeId).ToHashSet();
        var skill = catalog.Skills.First(candidate => candidate.Published && candidate.AvailableForPurchase && candidate.BasePrice > 0 && !owned.Contains(candidate.TypeId));
        var price = decimal.Round(skill.BasePrice * 1.3m, 2, MidpointRounding.AwayFromZero);
        var startingBalance = price + 1_000_000m;
        await rig.FundAsync(character, startingBalance);
        var handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        const string objectId = "N=ProxyNode:skill-purchase-success-test";
        Start(sessionOpened: session =>
        {
            session.Set("charid", new PyInt(character));
            _testRegistry.RegisterBoundObjectForSession(objectId, handler, session);
        });
        var (front, open) = await OpenFrontAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 1, objectId, "PurchaseSkills",
                new PyTuple([new PyList([new PyInt(skill.TypeId)])]), TransportKwargs()))));

        PyValue? answer = null;
        for (var received = 0; answer is null && received < 16; received++)
        {
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.NotEqual(MachoMessage.ErrorResponse, packet.Type);
            if (packet.Type == MachoMessage.CallRsp)
                answer = Result(decoded);
        }

        var granted = Assert.IsType<PyTuple>(answer);
        Assert.Equal(new PyInt(skill.TypeId), Assert.Single(granted.Items));
        Assert.Contains(await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character),
            saved => saved.TypeId == skill.TypeId && saved.Level == 0 && saved.SkillPoints == 0);
        Assert.Single(await new WalletTransactions(rig.Source, rig.WalletSchema).JournalAsync(character));
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT isk FROM {rig.WalletSchema}.balances WHERE owner_id = $1 AND account_key = 1000", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        Assert.Equal(startingBalance - price, (decimal)(await command.ExecuteScalarAsync())!);
    }
}
