using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Market;
using Eve.Services;
using Eve.Services.Station;
using Eve.Sde;
using Eve.Ships;
using Eve.Structures;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class MarketTradeHostWireTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA",
        "EVE_WALLET_SCHEMA", "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA",
        "EVE_CORPORATIONS_SCHEMA", "EVE_MARKET_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA",
        "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA", "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
    ];

    private readonly string prefix = "mtw" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> saved = [];
    private readonly List<string> schemas = [];
    private HostRuntime? host;

    private string Schema(string variable) => variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant() is var name ? $"{prefix}_{name}" : "";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (host is not null)
        {
            await host.ShutdownAsync();
            host = null;
        }
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private async Task<long> CreateTraderAsync(NpgsqlDataSource source, Station station, string name, decimal isk = 10000m)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var actor = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(connection, transaction,
            new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, actor, isk, 0);
        await new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA")).GrantAsync(connection, transaction, actor, [new CharacterSkill(3363, 1, 0)]);
        await using (var membership = new NpgsqlCommand($"""
            INSERT INTO {Schema("EVE_RELATIONS_SCHEMA")}.relations
                (kind, subject, object, value, attrs, since_ms, until_ms, updated_ms)
            VALUES ($1, $2, $3, 1, $4::jsonb, $5, NULL, $5)
            """, connection, transaction))
        {
            membership.Parameters.Add(new NpgsqlParameter { Value = Eve.Relations.RelationKinds.CorporationMember.Name });
            membership.Parameters.Add(new NpgsqlParameter { Value = actor });
            membership.Parameters.Add(new NpgsqlParameter { Value = 1000044L });
            membership.Parameters.Add(new NpgsqlParameter { Value = "{}" });
            membership.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await membership.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return actor;
    }

    private async Task<(long CharacterId, long CorporationId)> CreateCorporationBuyerAsync(NpgsqlDataSource source, Station station, string name)
    {
        var founder = await CreateTraderAsync(source, station, name, 10_000_000m);
        var walletStore = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
        var wallets = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
        var corporations = new CorporationStore(source, new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")),
            walletStore, wallets, new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA")), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Schema("EVE_CORPORATIONS_SCHEMA"));
        var suffix = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var corporation = await corporations.CreateAsync(founder,
            new NewCorporation($"Market Delivery {suffix}", $"M{suffix}", "", "", 0), $"market-delivery-{suffix}");
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await wallets.TransferAsync(connection, transaction, new WalletAccount(founder), new WalletAccount(corporation.Id, 1000),
            1_000m, 10, "Market test deposit", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), $"market-delivery-deposit-{suffix}", "test");
        await transaction.CommitAsync();
        return (founder, corporation.Id);
    }

    [PgFact]
    public async Task Registered_market_buys_notify_committed_items_for_immediate_and_corporation_fills()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var inventory = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var itemTypes = new ItemTypeCatalog(new SdeReader(source));

        async Task<long> Stock(long owner, long quantity)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var itemId = await inventory.CreateAsync(connection, transaction,
                new NewItem(34, owner, station.Id, ItemFlags.Hangar, quantity));
            await transaction.CommitAsync();
            return itemId;
        }

        var buyerId = await CreateTraderAsync(source, station, "Market Delivery Immediate Buyer");
        var firstSellerId = await CreateTraderAsync(source, station, "Market Delivery First Seller");
        var secondSellerId = await CreateTraderAsync(source, station, "Market Delivery Second Seller");
        var buyerSink = new Sink();
        var firstSellerSink = new Sink();
        var secondSellerSink = new Sink();
        var buyerSession = DockedSession(buyerId, 801, station, buyerSink, 801);
        var firstSellerSession = DockedSession(firstSellerId, 802, station, firstSellerSink, 802);
        var secondSellerSession = DockedSession(secondSellerId, 803, station, secondSellerSink, 803);
        sessions.Enter(buyerId, buyerSession);
        sessions.Enter(firstSellerId, firstSellerSession);
        sessions.Enter(secondSellerId, secondSellerSession);

        var firstItemId = await Stock(firstSellerId, 2);
        var secondItemId = await Stock(secondSellerId, 2);
        await market.CallAsync(Call(firstSellerSession, "PlaceMultiSellOrder", 1,
            new PyList([SellEntry(firstItemId, station, 2, 9m)]), PyBool.False, new PyInt(30), PyNone.Instance));
        await market.CallAsync(Call(secondSellerSession, "PlaceMultiSellOrder", 2,
            new PyList([SellEntry(secondItemId, station, 2, 10m)]), PyBool.False, new PyInt(30), PyNone.Instance));
        buyerSink.Packets.Clear();
        firstSellerSink.Packets.Clear();
        secondSellerSink.Packets.Clear();

        Assert.IsType<PyNone>(await market.CallAsync(Call(buyerSession, "PlaceBuyOrder", 3,
            new PyInt(station.Id), new PyInt(34), new PyReal(10), new PyInt(3), new PyInt(-1), new PyInt(1),
            new PyInt(0), PyBool.False, PyNone.Instance)));

        var immediateIds = await AssertBuyerDeliveryAsync(buyerSink, inventory, itemTypes, buyerId, station.Id, expectedCount: 2);
        Assert.Contains(firstItemId, immediateIds);
        Assert.DoesNotContain(secondItemId, immediateIds);
        Assert.Equal(2, (await inventory.GetAsync(firstItemId))!.Quantity);
        Assert.Equal(1, (await inventory.GetAsync(secondItemId))!.Quantity);
        Assert.Empty(NamedNotifications(firstSellerSink, "OnItemsChanged"));
        Assert.Empty(NamedNotifications(firstSellerSink, "OnMarketItemReceived"));
        Assert.Empty(NamedNotifications(secondSellerSink, "OnItemsChanged"));
        Assert.Empty(NamedNotifications(secondSellerSink, "OnMarketItemReceived"));
        AssertSettlementNoticesPrecedeDelivery(buyerSink);
        var marketStore = new MarketStore(source, Schema("EVE_MARKET_SCHEMA"));
        var secondSellerOrder = Assert.Single(await marketStore.OwnOrdersAsync(secondSellerId, secondSellerId, false)).Order;
        await market.CallAsync(Call(secondSellerSession, "CancelCharOrder", 4,
            new PyLong(secondSellerOrder.Id), new PyInt(station.RegionId)));

        var (corporationBuyerId, corporationId) = await CreateCorporationBuyerAsync(source, station, "Market Delivery Corporate Buyer");
        var corporationSellerId = await CreateTraderAsync(source, station, "Market Delivery Corporate Seller");
        var corporationBuyerSink = new Sink();
        var corporationSellerSink = new Sink();
        var corporationBuyerSession = DockedSession(corporationBuyerId, 804, station, corporationBuyerSink, 804, corporationId);
        var corporationSellerSession = DockedSession(corporationSellerId, 805, station, corporationSellerSink, 805);
        sessions.Enter(corporationBuyerId, corporationBuyerSession);
        sessions.Enter(corporationSellerId, corporationSellerSession);

        Assert.IsType<PyNone>(await market.CallAsync(Call(corporationBuyerSession, "PlaceBuyOrder", 4,
            new PyInt(station.Id), new PyInt(34), new PyReal(12), new PyInt(1), new PyInt(-1), new PyInt(1),
            new PyInt(30), PyBool.True, PyNone.Instance)));
        corporationBuyerSink.Packets.Clear();
        var corporationItemId = await Stock(corporationSellerId, 1);
        await market.CallAsync(Call(corporationSellerSession, "PlaceMultiSellOrder", 5,
            new PyList([SellEntry(corporationItemId, station, 1, 12m)]), PyBool.False, new PyInt(0), PyNone.Instance));

        var corporationDeliveryIds = await AssertBuyerDeliveryAsync(
            corporationBuyerSink, inventory, itemTypes, corporationBuyerId, station.Id, expectedCount: 1);
        Assert.Equal(corporationItemId, Assert.Single(corporationDeliveryIds));
        Assert.Empty(NamedNotifications(corporationSellerSink, "OnItemsChanged"));
        Assert.Empty(NamedNotifications(corporationSellerSink, "OnMarketItemReceived"));
    }

    [PgFact]
    public async Task Registered_market_accepts_consecutive_identical_buys_as_distinct_client_calls()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Market Repeat Buyer");
        var sink = new Sink();
        var session = DockedSession(actor, 809, station, sink, 809);
        (host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered")).Enter(actor, session);
        var marketStore = new MarketStore(source, Schema("EVE_MARKET_SCHEMA"));
        await marketStore.SeedNpcAsync([
            new("market-repeat-asks", 34, false, 10m, 2, 32767,
                new MarketLocation(station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        ]);
        var inventory = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var itemTypes = new ItemTypeCatalog(new SdeReader(source));

        async Task<IReadOnlyList<long>> Buy(long callId)
        {
            Assert.IsType<PyNone>(await market.CallAsync(Call(session, "PlaceBuyOrder", callId,
                new PyLong(station.Id), new PyInt(34), new PyReal(10), new PyInt(1), new PyInt(-1), new PyInt(1),
                new PyInt(0), PyBool.False, PyNone.Instance)));
            var delivered = await AssertBuyerDeliveryAsync(sink, inventory, itemTypes, actor, station.Id, expectedCount: 1);
            AssertSettlementNoticesPrecedeDelivery(sink);
            sink.Packets.Clear();
            return delivered;
        }

        var firstItems = await Buy(1);
        var secondItems = await Buy(2);

        Assert.NotEqual(Assert.Single(firstItems), Assert.Single(secondItems));
        Assert.Equal(9980m, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(actor))!.Isk);
        Assert.Equal(2, (await marketStore.WalletFillsAsync(actor, 1000, 0)).Count);
        Assert.Equal(2, (await inventory.OwnedByAsync(actor)).Where(item => item.TypeId == 34 && item.LocationId == station.Id)
            .Sum(item => item.Quantity));
        Assert.Empty((await marketStore.BookAsync(station.RegionId, 34, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Asks);
    }

    [PgFact]
    public async Task Registered_buy_multi_returns_each_transient_order_and_expires_the_same_rows()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Market Multi Buyer");
        var sink = new Sink();
        var session = DockedSession(actor, 808, station, sink, 808);
        (host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered")).Enter(actor, session);
        var location = new MarketLocation(station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId);
        await new MarketStore(source, Schema("EVE_MARKET_SCHEMA")).SeedNpcAsync([
            new("market-multi-buy-asks", 34, false, 10m, 5, 32767, location, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        ]);
        var requests = new PyList([
            MultiBuyEntry(2, 10),
            MultiBuyEntry(5, 10),
            MultiBuyEntry(4, 9),
        ]);

        var response = MarshalCodec.Decode(MarshalCodec.Encode(await market.CallAsync(Call(session, "BuyMultipleItems", 1,
            new PyLong(station.Id), requests, PyBool.False))));

        var returnedRows = ImmediateOrderRows(response);
        Assert.Equal(3, returnedRows.Count);
        Assert.Equal(3, returnedRows.Select(row => row.Id).Distinct().Count());
        Assert.Equal(new long[] { 2, 5, 4 }, returnedRows.Select(row => row.Entered));
        Assert.Equal(new long[] { 0, 2, 4 }, returnedRows.Select(row => row.Remaining));
        Assert.Equal(9950m, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(actor))!.Isk);
        Assert.Empty(await new MarketStore(source, Schema("EVE_MARKET_SCHEMA")).OwnOrdersAsync(actor, actor, false));

        var notice = Assert.Single(NamedNotifications(sink, "OnOwnOrdersChanged"));
        Assert.Equal("charid", NotificationIdType(notice));
        var args = NotificationArguments(notice);
        Assert.Equal("Expiry", JsSemantics.PlainText(args.Items[1]));
        Assert.Equal(new PyInt(0), args.Items[2]);
        var notifiedRows = Assert.IsType<PyList>(args.Items[0]).Items.Select(ImmediateOrderRow).ToArray();
        Assert.Equal(returnedRows, notifiedRows);
    }

    [PgFact]
    public async Task Registered_market_sell_escrow_notifies_personal_hangar_stack_and_full_move()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var seller = await CreateTraderAsync(source, station, "Market Escrow Seller");
        var sink = new Sink();
        var session = DockedSession(seller, 809, station, sink, 809);
        (host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered")).Enter(seller, session);
        var inventory = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        long partialItemId;
        long fullItemId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            partialItemId = await inventory.CreateAsync(connection, transaction,
                new NewItem(34, seller, station.Id, ItemFlags.Hangar, 10));
            fullItemId = await inventory.CreateAsync(connection, transaction,
                new NewItem(34, seller, station.Id, ItemFlags.Hangar, 5));
            await transaction.CommitAsync();
        }

        await market.CallAsync(Call(session, "PlaceMultiSellOrder", 1,
            new PyList([SellEntry(partialItemId, station, 5, 10m), SellEntry(fullItemId, station, 5, 10m)]),
            PyBool.False, new PyInt(30), PyNone.Instance));

        var partialAfter = await inventory.GetAsync(partialItemId);
        var fullAfter = await inventory.GetAsync(fullItemId);
        Assert.NotNull(partialAfter);
        Assert.NotNull(fullAfter);
        Assert.Equal(station.Id, partialAfter.LocationId);
        Assert.Equal(ItemFlags.Hangar, partialAfter.Flag);
        Assert.Equal(5, partialAfter.Quantity);
        Assert.NotEqual(station.Id, fullAfter.LocationId);
        Assert.Equal(ItemFlags.Hangar, fullAfter.Flag);
        Assert.Equal(5, fullAfter.Quantity);

        var changes = NamedNotifications(sink, "OnItemChange");
        Assert.Equal(2, changes.Count);
        Assert.Empty(NamedNotifications(sink, "OnMarketItemReceived"));
        Assert.Empty(NamedNotifications(sink, "OnItemsChanged"));
        var packedRows = changes.Select(packet => Assert.IsType<PyPackedRow>(NotificationArguments(packet).Items[0]))
            .ToDictionary(row => IntegerValue(row.Values[0]));
        Assert.Equal(2, packedRows.Count);
        var partialPacket = Assert.Single(changes, packet => IntegerValue(
            Assert.IsType<PyPackedRow>(NotificationArguments(packet).Items[0]).Values[0]) == partialItemId);
        var partialArgs = NotificationArguments(partialPacket);
        var partialRow = Assert.IsType<PyPackedRow>(partialArgs.Items[0]);
        Assert.Equal(station.Id, IntegerValue(partialRow.Values[3]));
        Assert.Equal(5, IntegerValue(partialRow.Values[5]));
        var partialDifferences = Assert.IsType<PyDict>(partialArgs.Items[1]);
        Assert.Equal(10, IntegerValue(partialDifferences.Entries.Single(entry => IntegerValue(entry.Key) == 9).Value));

        var fullPacket = Assert.Single(changes, packet => IntegerValue(
            Assert.IsType<PyPackedRow>(NotificationArguments(packet).Items[0]).Values[0]) == fullItemId);
        var fullArgs = NotificationArguments(fullPacket);
        var fullRow = Assert.IsType<PyPackedRow>(fullArgs.Items[0]);
        Assert.Equal(fullAfter.LocationId, IntegerValue(fullRow.Values[3]));
        Assert.Equal(5, IntegerValue(fullRow.Values[5]));
        var fullDifferences = Assert.IsType<PyDict>(fullArgs.Items[1]);
        Assert.Equal(station.Id, IntegerValue(fullDifferences.Entries.Single(entry => IntegerValue(entry.Key) == 3).Value));
        Assert.True(sink.Packets.FindIndex(packet => NotificationName(packet) == "OnOwnOrdersChanged") <
            sink.Packets.FindIndex(packet => NotificationName(packet) == "OnItemChange"),
            "order and cache invalidations must precede the seller's inventory rows");

        var marketStore=new MarketStore(source,Schema("EVE_MARKET_SCHEMA"));
        var canceled=(await marketStore.OwnOrdersAsync(seller,seller,false)).Single(order => fullAfter.LocationId==9_200_000_000L+order.Order.Id).Order;
        sink.Packets.Clear();
        await market.CallAsync(Call(session,"CancelCharOrder",2,new PyLong(canceled.Id),new PyInt(station.RegionId)));
        var returned=await inventory.GetAsync(fullItemId);
        Assert.NotNull(returned);
        Assert.Equal(station.Id,returned.LocationId);
        var returnChange=Assert.Single(NamedNotifications(sink,"OnItemChange"));
        var returnArgs=NotificationArguments(returnChange);
        var returnRow=Assert.IsType<PyPackedRow>(returnArgs.Items[0]);
        Assert.Equal(fullItemId,IntegerValue(returnRow.Values[0]));
        Assert.Equal(station.Id,IntegerValue(returnRow.Values[3]));
        Assert.Equal(fullAfter.LocationId,IntegerValue(Assert.IsType<PyDict>(returnArgs.Items[1]).Entries
            .Single(entry => IntegerValue(entry.Key)==3).Value));
        Assert.Empty(NamedNotifications(sink,"OnMarketItemReceived"));
    }

    [PgFact]
    public async Task Registered_corporation_sell_notifies_only_authorized_bound_hangar_viewers()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var inventoryBroker = host.Registry.Lookup("invbroker") ?? throw new InvalidOperationException("invbroker is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var (seller, corporationId) = await CreateCorporationBuyerAsync(source, station, "Market Corp Inventory Seller");
        var observer = await CreateTraderAsync(source, station, "Market Corp Inventory Observer");
        var wrongDivisionObserver = await CreateTraderAsync(source, station, "Market Corp Wrong Division Observer");
        var corporations = new CorporationStore(source,
            new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")),
            new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")),
            new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA")),
            new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA")),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Schema("EVE_CORPORATIONS_SCHEMA"));
        var application = await corporations.ApplyAsync(observer, corporationId, "Market hangar observer");
        await corporations.RespondAsync(seller, application, 6, "Market hangar observer", "");
        await corporations.RespondAsync(observer, application, 2, "Market hangar observer", "");
        var wrongDivisionApplication=await corporations.ApplyAsync(wrongDivisionObserver,corporationId,"Market wrong division observer");
        await corporations.RespondAsync(seller,wrongDivisionApplication,6,"Market wrong division observer","");
        await corporations.RespondAsync(wrongDivisionObserver,wrongDivisionApplication,2,"Market wrong division observer","");
        await using (var roles = source.CreateCommand($"UPDATE {Schema("EVE_CORPORATIONS_SCHEMA")}.members SET roles_hq=$2 WHERE character_id=$1"))
        {
            roles.Parameters.AddWithValue(observer);
            roles.Parameters.AddWithValue(1L << 20); // flagCorpSAG1 Query at this corporation's HQ.
            Assert.Equal(1, await roles.ExecuteNonQueryAsync());
        }
        await using (var roles = source.CreateCommand($"UPDATE {Schema("EVE_CORPORATIONS_SCHEMA")}.members SET roles_hq=$2 WHERE character_id=$1"))
        {
            roles.Parameters.AddWithValue(wrongDivisionObserver);
            roles.Parameters.AddWithValue(1L << 21); // flagCorpSAG2 Query, but not flagCorpSAG1.
            Assert.Equal(1, await roles.ExecuteNonQueryAsync());
        }
        var itemStore = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var officeFolder = await itemStore.CreateAsync(connection, transaction,
                new NewItem(CorporationOfficeStore.OfficeFolderTypeId, corporationId, station.Id, ItemFlags.Hangar, Singleton: true));
            var offices = new CorporationOfficeStore(source, Schema("EVE_CORPORATIONS_SCHEMA"));
            await offices.RentAsync(connection, transaction, corporationId, officeFolder,
                new CorporationOfficeStation(station.Id, station.TypeId, station.OwnerId, station.SolarSystemId, station.Security),
                CorporationOfficeStore.MinimumRentIsk, now);
            await transaction.CommitAsync();
        }
        long corporationStackId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            corporationStackId = await itemStore.CreateAsync(connection, transaction,
                new NewItem(34, corporationId, station.Id, ItemFlags.CorpSAG1, 5));
            await transaction.CommitAsync();
        }

        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var sellerSink = new Sink();
        var observerSink = new Sink();
        var outsiderSink = new Sink();
        var sellerSession = DockedSession(seller, 810, station, sellerSink, 810, corporationId);
        var observerSession = DockedSession(observer, 811, station, observerSink, 811, corporationId);
        var outsiderSession = DockedSession(wrongDivisionObserver, 812, station, outsiderSink, 812, corporationId);
        sessions.Enter(seller, sellerSession);
        sessions.Enter(observer, observerSession);
        sessions.Enter(wrongDivisionObserver, outsiderSession);

        var boundHangarValue = await inventoryBroker.CallAsync(new ServiceCall
        {
            Session = observerSession, Method = "GetInventoryFromId", Args = [new PyLong(station.Id)],
        });
        var boundHangar = JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)boundHangarValue).Value).Decoded!).Items[0])!;
        var initialList = await inventoryBroker.CallAsync(new ServiceCall
        {
            Session = observerSession, Method = "List", BoundObjectId = boundHangar,
            Args = [new PyInt(ItemFlags.CorpSAG1)],
        });
        Assert.Contains(corporationStackId, Assert.IsType<PyList>(initialList).Items
            .OfType<PyPackedRow>().Select(row => IntegerValue(row.Values[0])));
        var wrongDivisionBoundValue=await inventoryBroker.CallAsync(new ServiceCall
        {
            Session=outsiderSession,Method="GetInventoryFromId",Args=[new PyLong(station.Id)],
        });
        var wrongDivisionBound=JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)wrongDivisionBoundValue).Value).Decoded!).Items[0])!;
        var wrongDivisionList=await inventoryBroker.CallAsync(new ServiceCall
        {
            Session=outsiderSession,Method="List",BoundObjectId=wrongDivisionBound,Args=[new PyInt(ItemFlags.CorpSAG2)],
        });
        Assert.Equal(InventoryWire.EmptySet(),wrongDivisionList);

        await market.CallAsync(Call(sellerSession, "PlaceMultiSellOrder", 1,
            new PyList([SellEntry(corporationStackId, station, 5, 10m)]), PyBool.True, new PyInt(30), PyNone.Instance));

        var sellerChange = Assert.Single(NamedNotifications(sellerSink, "OnItemChange"));
        var observerChange = Assert.Single(NamedNotifications(observerSink, "OnItemChange"));
        Assert.Empty(NamedNotifications(outsiderSink, "OnItemChange"));
        Assert.Equal(corporationStackId, IntegerValue(Assert.IsType<PyPackedRow>(NotificationArguments(observerChange).Items[0]).Values[0]));
        Assert.Equal(CharacterShips.JunkLocation,
            IntegerValue(Assert.IsType<PyPackedRow>(NotificationArguments(observerChange).Items[0]).Values[3]));
        Assert.NotEqual(CharacterShips.JunkLocation,
            IntegerValue(Assert.IsType<PyPackedRow>(NotificationArguments(sellerChange).Items[0]).Values[3]));
    }

    private static PyValue MultiBuyEntry(long quantity, double price) => Py.KeyVal(
        ("typeID", new PyInt(34)), ("quantity", new PyLong(quantity)), ("price", new PyReal(price)), ("minVolume", new PyInt(1)));

    private static IReadOnlyList<(long Id, long Entered, long Remaining)> ImmediateOrderRows(PyValue rowset)
    {
        return [.. Assert.IsType<PyList>(rowset).Items.Select(ImmediateOrderRow)];
    }

    private static (long Id, long Entered, long Remaining) ImmediateOrderRow(PyValue row)
    {
        var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args);
        return (IntegerValue(Field(fields, "orderID")), IntegerValue(Field(fields, "volEntered")),
            IntegerValue(Field(fields, "volRemaining")));
    }

    private static PyValue SellEntry(long itemId, Station station, long quantity, decimal price) => Py.KeyVal(
        ("typeID", new PyInt(34)), ("itemID", new PyLong(itemId)), ("price", new PyReal((double)price)),
        ("quantity", new PyInt(quantity)), ("stationID", new PyInt(station.Id)));

    private static async Task<IReadOnlyList<long>> AssertBuyerDeliveryAsync(
        Sink buyerSink, PgItemStore inventory, ItemTypeCatalog itemTypes, long buyerId, long locationId, int expectedCount)
    {
        var changes = Assert.Single(NamedNotifications(buyerSink, "OnItemsChanged"));
        Assert.Equal("charid", NotificationIdType(changes));
        var changeArgs = NotificationArguments(changes);
        Assert.Equal(3, changeArgs.Items.Count);
        var rows = Assert.IsType<PyList>(changeArgs.Items[0]);
        var differences = Assert.IsType<PyDict>(changeArgs.Items[1]);
        Assert.Equal(2, differences.Entries.Count);
        Assert.Equal(new PyInt(0), differences.Entries.Single(entry => Assert.IsType<PyInt>(entry.Key).Value == 3).Value);
        Assert.Equal(new PyInt(0), differences.Entries.Single(entry => Assert.IsType<PyInt>(entry.Key).Value == 9).Value);
        Assert.Equal(PyNone.Instance, changeArgs.Items[2]);
        Assert.Equal(expectedCount, rows.Items.Count);

        var received = NamedNotifications(buyerSink, "OnMarketItemReceived");
        Assert.Equal(expectedCount, received.Count);
        var ids = new List<long>(expectedCount);
        for (var index = 0; index < received.Count; index++)
        {
            var packet = received[index];
            Assert.Equal("clientID", NotificationIdType(packet));
            var args = NotificationArguments(packet);
            Assert.Equal(4, args.Items.Count);
            var itemId = IntegerValue(args.Items[0]);
            var item = await inventory.GetAsync(itemId);
            Assert.NotNull(item);
            Assert.Equal(buyerId, item.OwnerId);
            Assert.Equal(locationId, item.LocationId);
            Assert.Equal(ItemFlags.Hangar, item.Flag);
            Assert.Equal(new PyInt(34), args.Items[1]);
            Assert.Equal(new PyInt(ItemFlags.Hangar), args.Items[2]);
            Assert.Equal(InventoryWire.Id(locationId), args.Items[3]);

            var type = (await itemTypes.GetAsync([item.TypeId]))[item.TypeId];
            var expectedRow = Assert.IsType<PyPackedRow>(InventoryWire.PackedRow(new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag,
                item.Singleton ? -1 : item.Quantity, type.GroupId, type.CategoryId)));
            var actualRow = Assert.IsType<PyPackedRow>(rows.Items[index]);
            var expectedColumns = expectedRow.Descriptor.Columns.Select(column => (column.Name, column.DbType)).ToArray();
            var actualColumns = actualRow.Descriptor.Columns.Select(column => (column.Name, column.DbType)).ToArray();
            Assert.Equal(expectedColumns, actualColumns);
            Assert.Equal(expectedRow.Values.Count, actualRow.Values.Count);
            for (var column = 0; column < actualRow.Values.Count; column++)
            {
                var expectedValue = expectedRow.Values[column];
                var actualValue = actualRow.Values[column];
                if ((expectedValue is PyInt or PyLong) && (actualValue is PyInt or PyLong))
                    Assert.Equal(IntegerValue(expectedValue), IntegerValue(actualValue));
                else if (expectedValue is PyStr { Value.Length: 0 } && actualValue is PyBytes { Value.IsEmpty: true })
                    continue;
                else
                    Assert.Equal(expectedValue, actualValue);
            }
            ids.Add(itemId);
        }

        var changedIndex = buyerSink.Packets.FindIndex(packet => NotificationName(packet) == "OnItemsChanged");
        var firstReceivedIndex = buyerSink.Packets.FindIndex(packet => NotificationName(packet) == "OnMarketItemReceived");
        Assert.True(changedIndex >= 0 && firstReceivedIndex > changedIndex,
            "inventory cache updates must be sent before market item received notices");
        return ids;
    }

    private static void AssertSettlementNoticesPrecedeDelivery(Sink sink)
    {
        var walletIndex = sink.Packets.FindIndex(packet => NotificationName(packet) == "OnAccountChange");
        var cacheIndex = sink.Packets.FindIndex(packet => ClientCallMethod(packet) == "InvalidateCachedMethodCall");
        var orderIndex = sink.Packets.FindIndex(packet => NotificationName(packet) == "OnOwnOrdersChanged");
        var inventoryIndex = sink.Packets.FindIndex(packet => NotificationName(packet) == "OnItemsChanged");
        var receivedIndex = sink.Packets.FindIndex(packet => NotificationName(packet) == "OnMarketItemReceived");
        Assert.True(walletIndex >= 0 && cacheIndex > walletIndex && orderIndex > cacheIndex &&
                    inventoryIndex > orderIndex && receivedIndex > inventoryIndex,
            "wallet, cache, and own-order notices must precede delivery notifications");
    }

    [PgFact]
    public async Task A_rolled_back_market_settlement_emits_no_buyer_delivery_notifications()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var inventory = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var sellerId = await CreateTraderAsync(source, station, "Market Delivery Rollback Seller");
        var buyerId = await CreateTraderAsync(source, station, "Market Delivery Rollback Buyer");
        var sellerSink = new Sink();
        var buyerSink = new Sink();
        var sellerSession = DockedSession(sellerId, 806, station, sellerSink, 806);
        var buyerSession = DockedSession(buyerId, 807, station, buyerSink, 807);
        sessions.Enter(sellerId, sellerSession);
        sessions.Enter(buyerId, buyerSession);

        long itemId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            itemId = await inventory.CreateAsync(connection, transaction, new NewItem(34, sellerId, station.Id, ItemFlags.Hangar, 1));
            await transaction.CommitAsync();
        }
        await market.CallAsync(Call(sellerSession, "PlaceMultiSellOrder", 1,
            new PyList([SellEntry(itemId, station, 1, 8m)]), PyBool.False, new PyInt(30), PyNone.Instance));
        buyerSink.Packets.Clear();
        sellerSink.Packets.Clear();

        var schema = Schema("EVE_INVENTORY_SCHEMA");
        await using (var command = source.CreateCommand($"""
            CREATE FUNCTION {schema}.reject_market_delivery() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN
                RAISE EXCEPTION 'market delivery test failure';
            END;
            $body$;
            CREATE TRIGGER reject_market_delivery
            BEFORE UPDATE OF owner_id ON {schema}.items
            FOR EACH ROW WHEN (OLD.owner_id <> NEW.owner_id)
            EXECUTE FUNCTION {schema}.reject_market_delivery();
            """))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<PostgresException>(() => market.CallAsync(Call(buyerSession, "PlaceBuyOrder", 2,
            new PyInt(station.Id), new PyInt(34), new PyReal(8), new PyInt(1), new PyInt(-1), new PyInt(1),
            new PyInt(0), PyBool.False, PyNone.Instance)).AsTask());

        Assert.Empty(NamedNotifications(buyerSink, "OnItemsChanged"));
        Assert.Empty(NamedNotifications(buyerSink, "OnMarketItemReceived"));
        var itemAfterRollback = await inventory.GetAsync(itemId);
        Assert.NotNull(itemAfterRollback);
        Assert.Equal(sellerId, itemAfterRollback.OwnerId);
        Assert.Equal(1, itemAfterRollback.Quantity);
        var restingOrder = Assert.Single(await new MarketStore(source, Schema("EVE_MARKET_SCHEMA")).OwnOrdersAsync(sellerId, sellerId, false));
        Assert.Equal(1, restingOrder.Order.VolumeRemaining);
        Assert.Empty(buyerSink.Packets);
    }

    [PgFact]
    public async Task Registered_market_and_trade_calls_reply_and_update_persisted_state()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");
        foreach (var method in new[] { "StartupCheck", "PlaceBuyOrder", "ModifyCharOrder", "PlacePlexSellOrder", "ModifyPlexCharOrder" })
            Assert.True(market.HasMethod(method), method);
        Assert.True(trade.HasMethod("InitiateTrade"));
        Assert.True(trade.HasMethod("MachoBindObject"));

        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Market Trade RPC Pilot");
        var targetId = await CreateTraderAsync(source, station, "Market Trade RPC Peer");
        var actorSink = new Sink();
        var targetSink = new Sink();
        var session = DockedSession(actor, 801, station, actorSink);
        var target = DockedSession(targetId, 802, station, targetSink);
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        sessions.Enter(actor, session);
        sessions.Enter(targetId, target);

        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "StartupCheck", 1)));

        // Buy and modify a resting personal order using the same terms the client sends in marketsvc.py.
        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "PlaceBuyOrder", 2,
            new PyInt(station.Id), new PyInt(35), new PyReal(1234), new PyInt(1), new PyInt(-1), new PyInt(1),
            new PyInt(30), PyBool.False, PyNone.Instance)));
        var marketStore = new MarketStore(source, Schema("EVE_MARKET_SCHEMA"));
        var bid = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false)).Order;
        Assert.True(bid.Bid);
        await BackdateAsync(source, bid.Id);
        bid = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false)).Order;
        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "ModifyCharOrder", 3,
            new PyLong(bid.Id), new PyReal(1235), PyBool.True, new PyLong(station.Id), new PyInt(station.SolarSystemId),
            new PyReal((double)bid.Price), new PyInt(bid.Range), new PyInt(bid.VolumeRemaining),
            new PyLong(Py.FileTimeFromUnixMs(bid.IssuedMs)))));
        Assert.Equal(1235m, Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false)).Order.Price);
        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "CancelCharOrder", 31,
            new PyLong(bid.Id), new PyInt(station.RegionId))));
        Assert.DoesNotContain(await marketStore.OwnOrdersAsync(actor, actor, false), owned => owned.Order.Id == bid.Id);

        // PLEX sell returns the boolean consumed by sellMulti.py; the dedicated modify RPC updates the same persisted order.
        long plexItem;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            plexItem = await new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA")).CreateAsync(connection, transaction,
                new NewItem(44992, actor, station.Id, ItemFlags.Hangar, 1));
            await transaction.CommitAsync();
        }
        var sellEntry = Py.KeyVal(
            ("typeID", new PyInt(44992)), ("itemID", new PyLong(plexItem)), ("price", new PyReal(1234)),
            ("quantity", new PyInt(1)), ("stationID", new PyLong(station.Id)));
        Assert.Equal(PyBool.True, await market.CallAsync(Call(session, "PlacePlexSellOrder", 4,
            sellEntry, PyBool.False, new PyInt(30), PyNone.Instance)));
        var plexAsk = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false), owned => owned.Order.TypeId == 44992).Order;
        Assert.False(plexAsk.Bid);
        await BackdateAsync(source, plexAsk.Id);
        plexAsk = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false), owned => owned.Order.TypeId == 44992).Order;
        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "ModifyPlexCharOrder", 5,
            new PyLong(plexAsk.Id), new PyReal(1233), PyBool.False, new PyLong(station.Id), new PyInt(station.SolarSystemId),
            new PyReal((double)plexAsk.Price), new PyInt(plexAsk.Range), new PyInt(plexAsk.VolumeRemaining),
            new PyLong(Py.FileTimeFromUnixMs(plexAsk.IssuedMs)))));
        Assert.Equal(1233m, Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false),
            owned => owned.Order.TypeId == 44992).Order.Price);
        Assert.NotEmpty(actorSink.Packets);

        // InitiateTrade returns a moniker, not the trade row. The client's next List call binds that moniker and reads the row.
        var moniker = Assert.IsType<PyObject>(await trade.CallAsync(Call(session, "InitiateTrade", 6, new PyLong(targetId))));
        var monikerArgs = Assert.IsType<PyTuple>(moniker.Args);
        Assert.Equal(new PyStr("trademgr"), monikerArgs.Items[0]);
        var bindParameters = Assert.IsType<PyTuple>(monikerArgs.Items[2]);
        var nestedList = Py.Tuple(new PyStr("List"), new PyTuple([]), new PyDict([]));
        var bindReply = Assert.IsType<PyTuple>(await trade.CallAsync(Call(session, "MachoBindObject", 7, bindParameters, nestedList)));
        var row = Assert.IsType<PyObject>(bindReply.Items[1]);
        var fields = Assert.IsType<PyDict>(row.Args);
        static PyValue Field(PyDict fields, string name) =>
            fields.Entries.Single(entry => JsSemantics.PlainText(entry.Key) == name).Value;
        var header = Assert.IsType<PyList>(Field(fields, "header")).Items.Select(JsSemantics.PlainText).ToArray();
        Assert.Equal(new[] { "tradeContainerID", "traders", "state", "money", "items" }, header);
        var line = Assert.IsType<PyList>(Field(fields, "line"));
        var traders = Assert.IsType<PyList>(line.Items[1]);
        Assert.Equal(2, traders.Items.Count);
        Assert.Equal(new PyInt(actor), traders.Items[0]);
        Assert.Equal(new PyInt(targetId), traders.Items[1]);
        Assert.Single(targetSink.Packets);

        var tradeId = Assert.IsType<PyLong>(bindParameters.Items[0]).Value;
        await using (var command = source.CreateCommand($"SELECT status FROM {Schema("EVE_TRADE_SCHEMA")}.sessions WHERE trade_id=$1"))
        {
            command.Parameters.AddWithValue(tradeId);
            Assert.Equal("open", await command.ExecuteScalarAsync());
        }
    }

    [PgFact]
    public async Task Registered_dogma_trade_unload_stages_a_fitted_charge_and_abort_restores_its_slot()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Trade fitted charge abort pilot");
        var target = await CreateTraderAsync(source, station, "Trade fitted charge abort peer");
        var (shipId, moduleId, chargeId) = await AddFittedRailgunAsync(source, station, actor, 28);
        var inventory = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var actorSink = new Sink();
        var targetSink = new Sink();
        var actorSession = DockedSession(actor, 811, station, actorSink);
        var targetSession = DockedSession(target, 812, station, targetSink);
        actorSession.Set("shipid", new PyLong(shipId));
        sessions.Enter(actor, actorSession);
        sessions.Enter(target, targetSession);

        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");
        var dogma = host.Registry.Lookup("dogmaIM") ?? throw new InvalidOperationException("dogmaIM is not registered");
        var (tradeId, tradeObject) = await BindTradeAsync(trade, actorSession, target, 1);
        var dogmaObject = await BindDogmaAsync(dogma, actorSession, station.Id, 2);

        var unload = await dogma.CallAsync(BoundCall(actorSession, "UnloadAmmo", 3, dogmaObject,
            new PyLong(shipId), new PyList([new PyLong(moduleId)]),
            Py.Tuple(new PyLong(tradeId), new PyLong(actor), PyNone.Instance), PyNone.Instance));

        Assert.Equal([chargeId], Assert.IsType<PyList>(unload).Items.Select(IntegerValue));
        Assert.Equal([chargeId], await TradeItemIdsAsync(trade, actorSession, tradeObject, 4));
        Assert.DoesNotContain((await inventory.InLocationAsync(shipId, actor)).Select(item => item.Id), id => id == chargeId);
        var priming = Assert.Single(NamedNotifications(actorSink, "OnPrimingNeededForTradeItems"));
        Assert.Equal([chargeId], Assert.IsType<PyList>(Assert.Single(NotificationArguments(priming).Items))
            .Items.Select(IntegerValue));
        Assert.Single(NamedNotifications(targetSink, "OnPrimingNeededForTradeItems"));
        Assert.NotEmpty(NamedNotifications(actorSink, "OnModuleAttributeChanges"));
        Assert.Equal(2, NamedNotifications(actorSink, "OnItemChange").Count);

        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(actorSession, "Abort", 5, tradeObject)));
        var restored = await inventory.GetAsync(chargeId);
        Assert.NotNull(restored);
        Assert.Equal((actor, shipId, 28), (restored.OwnerId, restored.LocationId, restored.Flag));
        await using var status = source.CreateCommand($"SELECT status FROM {Schema("EVE_TRADE_SCHEMA")}.sessions WHERE trade_id=$1");
        status.Parameters.AddWithValue(tradeId);
        Assert.Equal("aborted", await status.ExecuteScalarAsync());
    }

    [PgFact]
    public async Task Registered_trade_restart_restores_a_staged_fitted_charge_to_its_slot()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Trade fitted charge restart pilot");
        var target = await CreateTraderAsync(source, station, "Trade fitted charge restart peer");
        var (shipId, moduleId, chargeId) = await AddFittedRailgunAsync(source, station, actor, 30);
        var inventory = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var actorSession = DockedSession(actor, 823, station, new Sink());
        var targetSession = DockedSession(target, 824, station, new Sink());
        actorSession.Set("shipid", new PyLong(shipId));
        sessions.Enter(actor, actorSession);
        sessions.Enter(target, targetSession);
        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");
        var dogma = host.Registry.Lookup("dogmaIM") ?? throw new InvalidOperationException("dogmaIM is not registered");
        var (tradeId, _) = await BindTradeAsync(trade, actorSession, target, 1);
        var dogmaObject = await BindDogmaAsync(dogma, actorSession, station.Id, 2);

        var unloaded = await dogma.CallAsync(BoundCall(actorSession, "UnloadAmmo", 3, dogmaObject,
            new PyLong(shipId), new PyList([new PyLong(moduleId)]),
            Py.Tuple(new PyLong(tradeId), new PyLong(actor), PyNone.Instance), PyNone.Instance));
        Assert.Equal([chargeId], Assert.IsType<PyList>(unloaded).Items.Select(IntegerValue));
        Assert.Equal((actor, tradeId), ((await inventory.GetAsync(chargeId))!.OwnerId,
            (await inventory.GetAsync(chargeId))!.LocationId));

        await host.ShutdownAsync();
        host = null;
        host = await HostComposition.BuildAsync(Settings());

        var restored = await inventory.GetAsync(chargeId);
        Assert.NotNull(restored);
        Assert.Equal((actor, shipId, 30), (restored.OwnerId, restored.LocationId, restored.Flag));
        Assert.Equal("aborted", await TradeStatusAsync(source, tradeId));
    }

    [PgFact]
    public async Task Registered_dogma_unloads_fitted_charge_before_trade_stages_module_and_both_items_settle()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Trade fitted module pilot");
        var target = await CreateTraderAsync(source, station, "Trade fitted module peer");
        var (shipId, moduleId, chargeId) = await AddFittedRailgunAsync(source, station, actor, 29);
        var inventory = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var actorSink = new Sink();
        var targetSink = new Sink();
        var actorSession = DockedSession(actor, 813, station, actorSink);
        var targetSession = DockedSession(target, 814, station, targetSink);
        actorSession.Set("shipid", new PyLong(shipId));
        sessions.Enter(actor, actorSession);
        sessions.Enter(target, targetSession);

        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");
        var dogma = host.Registry.Lookup("dogmaIM") ?? throw new InvalidOperationException("dogmaIM is not registered");
        var (tradeId, actorTradeObject) = await BindTradeAsync(trade, actorSession, target, 1);
        var (_, targetTradeObject) = await BindTradeAsync(trade, targetSession, actor, 2, tradeId);
        var dogmaObject = await BindDogmaAsync(dogma, actorSession, station.Id, 3);

        var unloaded = await dogma.CallAsync(BoundCall(actorSession, "UnloadAmmo", 4, dogmaObject,
            new PyLong(shipId), new PyList([new PyLong(moduleId)]), new PyLong(tradeId), PyNone.Instance));
        Assert.Equal([chargeId], Assert.IsType<PyList>(unloaded).Items.Select(IntegerValue));
        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(actorSession, "Add", 5, actorTradeObject,
            new PyLong(moduleId), new PyLong(shipId))));

        Assert.Equal(new[] { chargeId, moduleId }.Order(),
            (await TradeItemIdsAsync(trade, actorSession, actorTradeObject, 6)).Order());
        Assert.Equal((actor, tradeId),
            ((await inventory.GetAsync(chargeId))!.OwnerId, (await inventory.GetAsync(chargeId))!.LocationId));
        Assert.Equal((actor, tradeId),
            ((await inventory.GetAsync(moduleId))!.OwnerId, (await inventory.GetAsync(moduleId))!.LocationId));

        await AcceptTradeAsync(trade, actorSession, actorTradeObject, 7);
        await AcceptTradeAsync(trade, targetSession, targetTradeObject, 8);

        Assert.Equal((target, station.Id, ItemFlags.Hangar),
            ((await inventory.GetAsync(chargeId))!.OwnerId, (await inventory.GetAsync(chargeId))!.LocationId,
                (await inventory.GetAsync(chargeId))!.Flag));
        Assert.Equal((target, station.Id, ItemFlags.Hangar),
            ((await inventory.GetAsync(moduleId))!.OwnerId, (await inventory.GetAsync(moduleId))!.LocationId,
                (await inventory.GetAsync(moduleId))!.Flag));
        Assert.NotEmpty(NamedNotifications(targetSink, "OnTradeComplete"));
    }

    [PgFact]
    public async Task Registered_trade_mutation_aborts_and_restores_items_when_the_peer_is_no_longer_docked()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Trade peer dock pilot");
        var target = await CreateTraderAsync(source, station, "Trade peer dock peer");
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        long itemId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            itemId = await items.CreateAsync(connection, transaction,
                new NewItem(34, actor, station.Id, ItemFlags.Hangar, 5));
            await transaction.CommitAsync();
        }
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var actorSink = new Sink();
        var targetSink = new Sink();
        var actorSession = DockedSession(actor, 815, station, actorSink);
        var targetSession = DockedSession(target, 816, station, targetSink);
        sessions.Enter(actor, actorSession);
        sessions.Enter(target, targetSession);
        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");
        var (tradeId, actorTradeObject) = await BindTradeAsync(trade, actorSession, target, 1);
        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(actorSession, "Add", 2, actorTradeObject,
            new PyLong(itemId), new PyLong(station.Id))));

        targetSession.Change(("stationid", PyNone.Instance));
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await trade.CallAsync(BoundCall(actorSession, "OfferMoney", 3, actorTradeObject, new PyReal(1))));

        Assert.Equal("aborted", await TradeStatusAsync(source, tradeId));
        Assert.Equal((actor, station.Id, ItemFlags.Hangar),
            ((await items.GetAsync(itemId))!.OwnerId, (await items.GetAsync(itemId))!.LocationId,
                (await items.GetAsync(itemId))!.Flag));
        Assert.Single(NamedNotifications(actorSink, "OnTradeCancel"));
        Assert.Single(NamedNotifications(targetSink, "OnTradeCancel"));
    }

    [PgFact]
    public async Task Registered_trade_stages_corporation_office_items_and_restores_or_transfers_the_original_owner()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var (actor, corporationId) = await CreateCorporationBuyerAsync(source, station, "Trade office item CEO");
        var target = await CreateTraderAsync(source, station, "Trade office item peer");
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        long officeItemId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var officeFolderId = await items.CreateAsync(connection, transaction,
                new NewItem(CorporationOfficeStore.OfficeFolderTypeId, corporationId, station.Id, ItemFlags.Hangar, 1, true));
            await using (var office = new NpgsqlCommand($"""
                INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rentals
                    (office_folder_id,corporation_id,station_id,solar_system_id,rent_isk,rented_at_ms,expires_at_ms)
                VALUES ($1,$2,$3,$4,10000,1,9999999999999)
                """, connection, transaction))
            {
                office.Parameters.AddWithValue(officeFolderId);
                office.Parameters.AddWithValue(corporationId);
                office.Parameters.AddWithValue(station.Id);
                office.Parameters.AddWithValue(station.SolarSystemId);
                await office.ExecuteNonQueryAsync();
            }
            officeItemId = await items.CreateAsync(connection, transaction,
                new NewItem(34, corporationId, station.Id, ItemFlags.CorpSAG1, 5));
            await using (var locked = new NpgsqlCommand($"""
                INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.item_locks
                    (item_id,corporation_id,type_id,location_id,locked_filetime)
                VALUES ($1,$2,34,$3,$4)
                """, connection, transaction))
            {
                locked.Parameters.AddWithValue(officeItemId);
                locked.Parameters.AddWithValue(corporationId);
                locked.Parameters.AddWithValue(station.Id);
                locked.Parameters.AddWithValue(DateTime.UtcNow.ToFileTimeUtc());
                await locked.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var actorSink = new Sink();
        var actorSession = DockedSession(actor, 821, station, actorSink, corporationId: corporationId);
        actorSession.Set("corporationID", new PyLong(corporationId));
        var targetSession = DockedSession(target, 822, station, new Sink());
        sessions.Enter(actor, actorSession);
        sessions.Enter(target, targetSession);
        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");

        var (_, unauthorizedTradeObject) = await BindTradeAsync(trade, targetSession, actor, 1);
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await trade.CallAsync(BoundCall(targetSession, "Add", 2, unauthorizedTradeObject,
                new PyLong(officeItemId), new PyLong(station.Id))));
        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(targetSession, "Abort", 3, unauthorizedTradeObject)));

        var (_, lockedTradeObject) = await BindTradeAsync(trade, actorSession, target, 4);
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await trade.CallAsync(BoundCall(actorSession, "Add", 5, lockedTradeObject,
                new PyLong(officeItemId), new PyLong(station.Id))));
        Assert.Equal((corporationId, station.Id, ItemFlags.CorpSAG1),
            ((await items.GetAsync(officeItemId))!.OwnerId, (await items.GetAsync(officeItemId))!.LocationId,
                (await items.GetAsync(officeItemId))!.Flag));
        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(actorSession, "Abort", 6, lockedTradeObject)));
        await using (var unlock = source.CreateCommand($"DELETE FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.item_locks WHERE item_id=$1"))
        {
            unlock.Parameters.AddWithValue(officeItemId);
            Assert.Equal(1, await unlock.ExecuteNonQueryAsync());
        }

        var (abortTradeId, abortTradeObject) = await BindTradeAsync(trade, actorSession, target, 7);
        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(actorSession, "Add", 8, abortTradeObject,
            new PyLong(officeItemId), new PyLong(station.Id))));
        Assert.Equal((actor, abortTradeId), ((await items.GetAsync(officeItemId))!.OwnerId,
            (await items.GetAsync(officeItemId))!.LocationId));
        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(actorSession, "Abort", 9, abortTradeObject)));
        Assert.Equal((corporationId, station.Id, ItemFlags.CorpSAG1),
            ((await items.GetAsync(officeItemId))!.OwnerId, (await items.GetAsync(officeItemId))!.LocationId,
                (await items.GetAsync(officeItemId))!.Flag));

        var (completeTradeId, completeActorObject) = await BindTradeAsync(trade, actorSession, target, 10);
        var (_, completeTargetObject) = await BindTradeAsync(trade, targetSession, actor, 11, completeTradeId);
        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(actorSession, "Add", 12, completeActorObject,
            new PyLong(officeItemId), new PyLong(station.Id))));
        await AcceptTradeAsync(trade, actorSession, completeActorObject, 13);
        await AcceptTradeAsync(trade, targetSession, completeTargetObject, 14);
        Assert.Equal((target, station.Id, ItemFlags.Hangar),
            ((await items.GetAsync(officeItemId))!.OwnerId, (await items.GetAsync(officeItemId))!.LocationId,
                (await items.GetAsync(officeItemId))!.Flag));
    }

    [PgFact]
    public async Task Registered_trade_priming_includes_nested_inventory_items()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Trade contained-item pilot");
        var target = await CreateTraderAsync(source, station, "Trade contained-item peer");
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var containerId = await items.CreateAsync(connection, transaction,
            new NewItem(23, actor, station.Id, ItemFlags.Hangar, 1, true));
        var childId = await items.CreateAsync(connection, transaction,
            new NewItem(34, actor, containerId, ItemFlags.Hangar, 10));
        await transaction.CommitAsync();
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var actorSink = new Sink();
        var actorSession = DockedSession(actor, 817, station, actorSink);
        var targetSession = DockedSession(target, 818, station, new Sink());
        sessions.Enter(actor, actorSession);
        sessions.Enter(target, targetSession);
        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");
        var (_, tradeObject) = await BindTradeAsync(trade, actorSession, target, 1);

        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(actorSession, "Add", 2, tradeObject,
            new PyLong(containerId), new PyLong(station.Id))));

        var priming = Assert.Single(NamedNotifications(actorSink, "OnPrimingNeededForTradeItems"));
        Assert.Equal([containerId, childId], Assert.IsType<PyList>(Assert.Single(NotificationArguments(priming).Items))
            .Items.Select(IntegerValue));
    }

    [PgFact]
    public async Task Registered_trade_self_item_uses_the_default_station_type_at_a_structure()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await StructureStore.MigrateAsync(source, Schema("EVE_STRUCTURES_SCHEMA"));
        const long structureId = 9000000000001234;
        const int structureTypeId = 35832;
        await using (var structure = source.CreateCommand($"""
            INSERT INTO {Schema("EVE_STRUCTURES_SCHEMA")}.structures
                (structure_id,type_id,solar_system_id,owner_corporation_id,state,destroyed_ms)
            VALUES ({structureId},{structureTypeId},30000142,98000001,110,0)
            """))
            await structure.ExecuteNonQueryAsync();
        host = await HostComposition.BuildAsync(Settings());
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Trade structure type pilot");
        var target = await CreateTraderAsync(source, station, "Trade structure type peer");
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        var actorSession = DockedSession(actor, 819, station, new Sink());
        var targetSession = DockedSession(target, 820, station, new Sink());
        actorSession.Change(("stationid", PyNone.Instance), ("structureid", new PyLong(structureId)));
        targetSession.Change(("stationid", PyNone.Instance), ("structureid", new PyLong(structureId)));
        sessions.Enter(actor, actorSession);
        sessions.Enter(target, targetSession);
        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");
        var (_, tradeObject) = await BindTradeAsync(trade, actorSession, target, 1);

        var selfItem = await trade.CallAsync(BoundCall(actorSession, "GetSelfInvItem", 2, tradeObject));

        Assert.Equal(new PyInt(1529), KeyValField(selfItem, "typeID"));
        Assert.Equal(new PyLong(structureId), KeyValField(selfItem, "locationID"));
    }

    private static PyValue KeyValField(PyValue value, string name)
    {
        var entries = value switch
        {
            PyObjectEx ex => ex.Dict,
            PyObject { Args: PyDict dict } => dict.Entries,
            PyDict dict => dict.Entries,
            _ => throw new InvalidOperationException($"not a KeyVal: {value}"),
        };
        return entries.Single(pair => JsSemantics.PlainText(pair.Key) == name).Value;
    }

    private async Task<string> TradeStatusAsync(NpgsqlDataSource source, long tradeId)
    {
        await using var command = source.CreateCommand($"SELECT status FROM {Schema("EVE_TRADE_SCHEMA")}.sessions WHERE trade_id=$1");
        command.Parameters.AddWithValue(tradeId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(long ShipId, long ModuleId, long ChargeId)> AddFittedRailgunAsync(
        NpgsqlDataSource source, Station station, long characterId, int slot)
    {
        var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var shipId = await items.CreateAsync(connection, transaction,
            new NewItem(603, characterId, station.Id, ItemFlags.Hangar, 1, true));
        await characters.SetShipAsync(connection, transaction, characterId, shipId);
        var moduleId = await items.CreateAsync(connection, transaction,
            new NewItem(10678, characterId, shipId, slot, 1, true));
        var chargeId = await items.CreateAsync(connection, transaction,
            new NewItem(222, characterId, shipId, slot, 80));
        await transaction.CommitAsync();
        return (shipId, moduleId, chargeId);
    }

    private static async Task<(long TradeId, string ObjectId)> BindTradeAsync(
        IService trade, ClientSession session, long otherCharacterId, long callId, long? tradeId = null)
    {
        PyTuple parameters;
        if (tradeId is { } knownTradeId)
        {
            parameters = new PyTuple([new PyLong(knownTradeId)]);
        }
        else
        {
            var moniker = Assert.IsType<PyObject>(await trade.CallAsync(
                Call(session, "InitiateTrade", callId, new PyLong(otherCharacterId))));
            var monikerArgs = Assert.IsType<PyTuple>(moniker.Args);
            parameters = Assert.IsType<PyTuple>(monikerArgs.Items[2]);
        }
        var nested = Py.Tuple(new PyStr("List"), new PyTuple([]), new PyDict([]));
        var boundResult = Assert.IsType<PyTuple>(await trade.CallAsync(
            Call(session, "MachoBindObject", callId + 100, parameters, nested)));
        var bound = Assert.IsType<PySubStruct>(boundResult.Items[0]);
        var encoded = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
        var objectId = Assert.IsType<PyStr>(encoded.Items[0]).Value;
        return (IntegerValue(parameters.Items[0]), objectId);
    }

    private static async Task<string> BindDogmaAsync(IService dogma, ClientSession session, long locationId, long callId)
    {
        var boundResult = Assert.IsType<PyTuple>(await dogma.CallAsync(
            Call(session, "MachoBindObject", callId, new PyTuple([new PyLong(locationId), new PyInt(15)]))));
        var bound = Assert.IsType<PySubStruct>(boundResult.Items[0]);
        var encoded = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
        return Assert.IsType<PyStr>(encoded.Items[0]).Value;
    }

    private static ServiceCall BoundCall(ClientSession session, string method, long callId, string objectId, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(callId), BoundObjectId = objectId };

    private static async Task<IReadOnlyList<long>> TradeItemIdsAsync(
        IService trade, ClientSession session, string objectId, long callId)
    {
        var row = Assert.IsType<PyObject>(await trade.CallAsync(BoundCall(session, "List", callId, objectId)));
        var fields = Assert.IsType<PyDict>(row.Args);
        var line = Assert.IsType<PyList>(Field(fields, "line"));
        var itemSet = Assert.IsType<PyObjectEx>(line.Items[4]);
        var setHeader = Assert.IsType<PyTuple>(itemSet.Header);
        var itemRows = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyTuple>(setHeader.Items[1]).Items));
        return itemRows.Items.Select(item => IntegerValue(Assert.IsType<PyPackedRow>(item).Values[0])).ToArray();
    }

    private static async Task AcceptTradeAsync(IService trade, ClientSession session, string objectId, long callId)
    {
        var row = Assert.IsType<PyObject>(await trade.CallAsync(BoundCall(session, "List", callId, objectId)));
        var fields = Assert.IsType<PyDict>(row.Args);
        var line = Assert.IsType<PyList>(Field(fields, "line"));
        var offer = Py.KeyVal(
            ("tradeContainerID", line.Items[0]),
            ("money", line.Items[3]),
            ("tradeItems", line.Items[4]));
        Assert.Equal(PyBool.True, await trade.CallAsync(BoundCall(session, "MakeOffer", callId + 100, objectId, offer)));
    }

    private static PyTuple PacketFields(PyValue packet) =>
        Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args);

    private static PyTuple PacketAddress(PyValue packet) =>
        Assert.IsType<PyTuple>(Assert.IsType<PyObject>(PacketFields(packet).Items[2]).Args);

    private static string? NotificationName(PyValue packet)
    {
        if (packet is not PyObject { Name: var className } ||
            JsSemantics.PlainText(className) != ClientNotification.ClassName) return null;
        return PacketAddress(packet).Items[1] is PyStr name ? JsSemantics.PlainText(name) : null;
    }

    private static string NotificationIdType(PyValue packet) =>
        JsSemantics.PlainText(PacketAddress(packet).Items[3])!;

    private static PyTuple NotificationArguments(PyValue packet)
    {
        var envelope = Assert.IsType<PyTuple>(PacketFields(packet).Items[4]);
        var chunk = Assert.IsType<PyTuple>(envelope.Items[0]);
        var buffer = Assert.IsType<PyBuffer>(chunk.Items[1]);
        var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(buffer.Value));
        var payload = Assert.IsType<PyTuple>(inner.Items[1]);
        return Assert.IsType<PyTuple>(payload.Items[1]);
    }

    private static string? ClientCallMethod(PyValue packet)
    {
        if (packet is not PyObject { Name: var className } ||
            JsSemantics.PlainText(className) != ClientCallRequest.ClassName) return null;
        var fields = PacketFields(packet);
        var destination = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(fields.Items[2]).Args);
        if (destination.Items.Count != 4 || JsSemantics.PlainText(destination.Items[3]) != "objectCaching")
            return null;
        var envelope = Assert.IsType<PyTuple>(fields.Items[4]);
        var chunk = Assert.IsType<PyTuple>(envelope.Items[0]);
        var stream = Assert.IsType<PySubStream>(chunk.Items[1]);
        var call = Assert.IsType<PyTuple>(stream.Decoded ?? MarshalCodec.Decode(stream.Raw));
        return JsSemantics.PlainText(call.Items[1]);
    }

    private static IReadOnlyList<PyValue> NamedNotifications(Sink sink, string name) =>
        [.. sink.Packets.Where(packet => NotificationName(packet) == name)];

    private static long IntegerValue(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException($"not an integer: {value}"),
    };

    private static PyValue Field(PyDict fields, string name) =>
        fields.Entries.Single(entry => JsSemantics.PlainText(entry.Key) == name).Value;

    private static ClientSession DockedSession(long characterId, long sessionId, Station station, Sink sink, long clientId = 0, long? corporationId = null)
    {
        var session = new ClientSession(sink)
        {
            CharacterId = characterId,
            SessionId = sessionId,
            ClientId = clientId,
            UserId = clientId > 0 ? clientId + 100_000 : null,
        };
        session.Set("charid", new PyInt(characterId));
        session.Set("stationid", new PyInt(station.Id));
        session.Set("solarsystemid2", new PyInt(station.SolarSystemId));
        session.Set("regionid", new PyInt(station.RegionId));
        session.Set("corpid", new PyInt(corporationId ?? 1000044));
        return session;
    }

    private async Task BackdateAsync(NpgsqlDataSource source, long orderId)
    {
        await using var command = source.CreateCommand(
            $"UPDATE {Schema("EVE_MARKET_SCHEMA")}.orders SET issued_ms=$2 WHERE id=$1");
        command.Parameters.AddWithValue(orderId);
        command.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - MarketRules.ModificationDelayMs - 1_000);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
