using Eve.Data;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class RafflePriceRuntimeTests
{
    private sealed class Sink : IPacketSink { public List<PyValue> Packets { get; } = []; public void Send(PyValue value) => Packets.Add(value); }
    private static PyValue Request(int id, PyValue data)
    {
        var from = new PyTuple([new PyInt(MachoAddress.TypeClient),new PyLong(7),new PyInt(id),PyNone.Instance]);
        var body = new PyTuple([new PyInt(1),new PyStr("CreateRaffle"),new PyTuple([data]),PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0),new PySubStream(body)])]);
        var head = new PyValue[] {new PyInt(6),from,MachoAddress.Any("raffleProxy").Encode(),new PyInt(90000002),payload,PyNone.Instance};
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),new PyTuple([..head,..Enumerable.Repeat<PyValue>(PyNone.Instance,14-head.Length)]));
    }
    private static PyValue ConfigRequest(int id)
    {
        var from = new PyTuple([new PyInt(MachoAddress.TypeClient),new PyLong(7),new PyInt(id),PyNone.Instance]);
        var body = new PyTuple([new PyInt(1),new PyStr("GetAverageMarketPrices"),new PyTuple([]),PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0),new PySubStream(body)])]);
        var head = new PyValue[] {new PyInt(6),from,MachoAddress.Any("config").Encode(),new PyInt(90000002),payload,PyNone.Instance};
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),new PyTuple([..head,..Enumerable.Repeat<PyValue>(PyNone.Instance,14-head.Length)]));
    }
    [Fact]
    public async Task RuntimeGlobalPriceAndFractionalDivisionDetermineActualTokenConsumption()
    {
        var databaseUrl = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL required");
        string configSchema = "rf_price_" + Guid.NewGuid().ToString("N")[..12]; string marketSchema = configSchema + "_market";
        string[] names = ["EVE_PG_URL","EVE_SIM","EVE_MARKET_SEED","EVE_WORMHOLES","EVE_GLOBAL_CONFIG_SCHEMA","EVE_MARKET_SCHEMA"];
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        HostRuntime? host = null;
        try
        {
            Environment.SetEnvironmentVariable("EVE_PG_URL", databaseUrl); Environment.SetEnvironmentVariable("EVE_SIM","off");
            Environment.SetEnvironmentVariable("EVE_MARKET_SEED","skip"); Environment.SetEnvironmentVariable("EVE_WORMHOLES","off");
            Environment.SetEnvironmentVariable("EVE_GLOBAL_CONFIG_SCHEMA", configSchema); Environment.SetEnvironmentVariable("EVE_MARKET_SCHEMA", marketSchema);
            host = await HostComposition.BuildAsync(new HostSettings("", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" }, new AccountOptions { AutoCreate = true }, StrictRpc: true));
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(databaseUrl));
            var directory = new PgAccountDirectory(source,new AccountOptions { AutoCreate = true });
            var login = Assert.IsType<LoggedIn>(await directory.LoginAsync(new LoginAttempt("raffle-price-" + Guid.NewGuid().ToString("N"),new byte[] {9,9,9},"en","127.0.0.1"),CancellationToken.None));
            long owner = Random.Shared.NextInt64(90_000_000,99_999_999); const long station = 60003760;
            var items = new PgItemStore(source); var sink = new Sink();
            var player = new ClientSession(sink) { CharacterId = owner, UserId = login.AccountId, SessionId = 7 }; player.Set("stationid",new PyLong(station));
            var admin = new ClientSession { CharacterId = owner, UserId = login.AccountId, SessionId = 8 }; admin.Set("accountRole",new PyLong(1L<<56));
            var macho = Assert.IsAssignableFrom<IService>(host.Registry.Lookup("machoNet"));
            async Task SetPrice(long value) => await macho.CallAsync(new ServiceCall { Method = "SetGlobalConfigValue", Session = admin, Args = [new PyStr("HyperNetPlexPriceOverride"),new PyLong(value)] });
            var dispatcher = new PacketDispatcher(host.Registry) { StrictRpc = true };
            async Task Check(int id, double ticketPrice, long remaining)
            {
                long prize,token;
                await using (var conn = await source.OpenConnectionAsync()) await using (var tx = await conn.BeginTransactionAsync())
                { prize = await items.CreateAsync(conn,tx,new NewItem(587,owner,station,ItemFlags.Hangar,1)); token = await items.CreateAsync(conn,tx,new NewItem(52568,owner,station,ItemFlags.Hangar,100)); await tx.CommitAsync(); }
                var data = Py.KeyVal(("owner_id",new PyLong(owner)),("location_id",new PyLong(station)),("solar_system_id",new PyLong(30000142)),("item_id",new PyLong(prize)),("type_id",new PyInt(587)),("token_id",new PyLong(token)),("token_location_id",new PyLong(station)),("ticket_count",new PyInt(8)),("ticket_price",new PyReal(ticketPrice)));
                sink.Packets.Clear(); await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(Request(id,data))),player);
                Assert.Contains(sink.Packets, packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp);
                Assert.Equal(remaining,(await items.GetAsync(token))!.Quantity);
            }
            await SetPrice(11);
            await Check(10,125.00,55); // int(1000 / 1.1 * .05) = 45
            await Check(11,137.49,51); // int(1099.92 / 1.1 * .05) = 49
            await Check(12,137.50,50); // int(1100 / 1.1 * .05) = 50
            await SetPrice(-1);
            await Check(13,125.00,50); // max(1, -1 / 10) = 1
            await macho.CallAsync(new ServiceCall { Method = "SetGlobalConfigValue", Session = admin, Args = [new PyStr("HyperNetPlexPriceOverride"),new PyReal(11.5)] });
            await Check(18,125.00,55); // Python int(11.5) = 11
            await macho.CallAsync(new ServiceCall { Method = "SetGlobalConfigValue", Session = admin, Args = [new PyStr("HyperNetPlexPriceOverride"),new PyReal(-1.5)] });
            await Check(19,125.00,50); // Python int(-1.5) = -1
            await SetPrice(0);
            // No price was advertised to this session: refuse before consuming inventory.
            long unsoldPrize,unsoldToken;
            await using (var conn = await source.OpenConnectionAsync()) await using (var tx = await conn.BeginTransactionAsync())
            { unsoldPrize = await items.CreateAsync(conn,tx,new NewItem(587,owner,station,ItemFlags.Hangar,1)); unsoldToken = await items.CreateAsync(conn,tx,new NewItem(52568,owner,station,ItemFlags.Hangar,100)); await tx.CommitAsync(); }
            var unpriced = Py.KeyVal(("owner_id",new PyLong(owner)),("location_id",new PyLong(station)),("item_id",new PyLong(unsoldPrize)),("type_id",new PyInt(587)),("token_id",new PyLong(unsoldToken)),("token_location_id",new PyLong(station)),("ticket_count",new PyInt(8)),("ticket_price",new PyReal(125)));
            sink.Packets.Clear(); await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(Request(14,unpriced))),player);
            Assert.Equal(MachoMessage.ErrorResponse,Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets))).Type);
            Assert.Equal(100,(await items.GetAsync(unsoldToken))!.Quantity); Assert.Equal(ItemFlags.Hangar,(await items.GetAsync(unsoldPrize))!.Flag);
            async Task AddFill(decimal price, long time, string key)
            {
                await using var conn = await source.OpenConnectionAsync();
                await using var order = new NpgsqlCommand($"INSERT INTO {marketSchema}.orders(type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,solar_system_id,constellation_id,region_id,issued_ms,duration_days,is_npc) VALUES(44992,false,$1,0,1,1,0,60003760,30000142,20000020,10000002,$2,1,true) RETURNING id",conn);
                order.Parameters.AddWithValue(price); order.Parameters.AddWithValue(time); long orderId = (long)(await order.ExecuteScalarAsync())!;
                await using var fill = new NpgsqlCommand($"INSERT INTO {marketSchema}.fills(order_id,type_id,buyer_id,seller_id,buyer_wallet,seller_wallet,buyer_account,seller_account,station_id,region_id,quantity,price,gross,tax,time_ms,operation_key,fill_index) VALUES($1,44992,1,2,1,2,1000,1000,60003760,10000002,1,$2,$2,0,$3,$4,0)",conn);
                fill.Parameters.AddWithValue(orderId); fill.Parameters.AddWithValue(price); fill.Parameters.AddWithValue(time); fill.Parameters.AddWithValue(key); await fill.ExecuteNonQueryAsync();
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await AddFill(11m,now,"fixture-a");
            sink.Packets.Clear(); await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(ConfigRequest(15))),player);
            Assert.Equal(MachoMessage.CallRsp,Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets))).Type);
            await AddFill(20m,now+86_400_000,"fixture-b");
            await Check(16,125.00,55); // client saw A=11, although market now reports B=20
            var otherSink = new Sink(); var other = new ClientSession(otherSink) { CharacterId=owner,UserId=login.AccountId,SessionId=17 }; other.Set("stationid",new PyLong(station));
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(Request(17,unpriced))),other);
            Assert.Equal(MachoMessage.ErrorResponse,Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(otherSink.Packets))).Type);
            Assert.Equal(100,(await items.GetAsync(unsoldToken))!.Quantity);
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            foreach (var (name,value) in saved) Environment.SetEnvironmentVariable(name,value);
            await using var cleanupSource = NpgsqlDataSource.Create(PgConnection.ForTest(databaseUrl));
            await using var clean = cleanupSource.CreateCommand($"DROP SCHEMA IF EXISTS {configSchema} CASCADE; DROP SCHEMA IF EXISTS {marketSchema} CASCADE"); await clean.ExecuteNonQueryAsync();
        }
    }
}
