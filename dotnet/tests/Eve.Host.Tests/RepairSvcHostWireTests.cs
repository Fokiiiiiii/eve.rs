using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Repair_service_binds_quotes_and_commits_station_repairs_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var itemTable = $"rhi_{suffix}";
        var walletTable = $"rhw_{suffix}";
        var moduleTable = $"rhm_{suffix}";
        var marketTable = $"rhk_{suffix}";
        await Eve.Inventory.PgItemStore.MigrateAsync(source, itemTable);
        await Eve.Wallet.PgWalletStore.MigrateAsync(source, walletTable);
        await Eve.Fitting.PgModuleStateStore.MigrateAsync(source, moduleTable);
        await Eve.Market.MarketStore.MigrateAsync(source, marketTable);

        var sde = new Eve.Sde.SdeReader(source);
        var statics = await Eve.Dogma.DogmaStatic.LoadAsync(sde);
        var stations = new Eve.Universe.StationCatalog([
            new Eve.Universe.Station(60_000_011, 1531, "Repair Station", 1_000_002, "Owner", null, "", 0, 0, 30_000_142, "Jita", 20_000_020, "Kimotoro", 10_000_002, "The Forge", 0.9, 0.05, 4),
        ]);
        var items = new Eve.Inventory.PgItemStore(source, itemTable);
        var modules = new Eve.Fitting.PgModuleStateStore(source, moduleTable);
        var wallets = new Eve.Wallet.PgWalletStore(source, walletTable);
        var prices = new Eve.Market.AdjustedPriceStore(source, marketTable);
        var now = FixedNowMs;
        await prices.ReplaceAsync([new Eve.Market.AdjustedPriceRow(587, 1000, null)], now);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await wallets.OpenAsync(connection, transaction, 9411, 100m, 0);
            var ship = await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(587, 9411, 60_000_011, Eve.Inventory.ItemFlags.Hangar, 1, Singleton: true));
            await modules.SetShipConditionAsync(connection, transaction, ship, new Eve.Fitting.ShipCondition(0.5, 1.0, 0.5, 0.2, false));
            await transaction.CommitAsync();

            var service = new Eve.Services.Station.RepairService(
                source, items, new Eve.Inventory.ItemTypeCatalog(sde), stations, statics, modules,
                new Eve.Market.AdjustedPriceCache(prices, esi: null, () => now),
                new Eve.Wallet.WalletTransactions(source, walletTable), wallets, () => now);
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.CharacterId = 9411;
                    session.Set("stationid", new PyInt(60_000_011));
                });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(
                open, 1, "repairSvc", "MachoBindObject", new PyInt(60_000_011), PyNone.Instance))));
            var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload)));
            var bound = Assert.IsType<PySubStruct>(bindResult.Items[0]);
            var reference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            var objectId = Assert.IsType<string>(JsSemantics.StringValue(reference.Items[0]));

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 2, objectId, "GetRepairQuotes", new PyTuple([new PyList([new PyLong(ship)])]), PyNone.Instance))));
            var quoteReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var quotes = Assert.IsType<PyDict>(Result(MarshalCodec.Decode(quoteReply.Payload)));
            var quoteRows = Assert.IsType<PyList>(Assert.Single(quotes.Entries).Value);
            Assert.Single(quoteRows.Items);

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 3, objectId, "RepairItemsInStation",
                new PyTuple([new PyList([new PyLong(ship)]), PyNone.Instance]), PyNone.Instance))));
            var damageChanged = Assert.IsType<Send>(await front.ReceiveAsync());
            var damagePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(damageChanged.Payload)));
            Assert.Equal(MachoMessage.Notification, damagePacket.Type);
            Assert.Equal("OnModuleAttributeChanges", damagePacket.Dest.BroadcastId);
            var itemChanged = Assert.IsType<Send>(await front.ReceiveAsync());
            var itemPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(itemChanged.Payload)));
            Assert.Equal(MachoMessage.Notification, itemPacket.Type);
            Assert.Equal("OnItemChange", itemPacket.Dest.BroadcastId);
            var repairReply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(repairReply.Payload))).Type);
            Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(repairReply.Payload)));

            Assert.Equal(Eve.Fitting.ShipCondition.Full, await modules.ShipConditionAsync(ship));
            Assert.Equal(60.8m, (await wallets.GetAsync(9411))!.Isk);
            await using var check = await source.OpenConnectionAsync();
            await using var journal = new NpgsqlCommand($"SELECT count(*) FROM {walletTable}.journal WHERE owner_id = $1 AND ref_type_id = $2", check);
            journal.Parameters.AddWithValue(9411L);
            journal.Parameters.AddWithValue(Eve.Services.Station.RepairService.RepairBillRefType);
            Assert.Equal(1L, (long)(await journal.ExecuteScalarAsync())!);
        }
    }
}
