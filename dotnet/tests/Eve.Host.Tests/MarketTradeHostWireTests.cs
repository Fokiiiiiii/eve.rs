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
