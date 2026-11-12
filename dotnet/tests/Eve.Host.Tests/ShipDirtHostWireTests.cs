using Eve.Data;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Reset_dirt_timestamp_persists_and_get_dirt_timestamp_reads_it_through_the_registered_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = $"ship_dirt_host_{Guid.NewGuid():N}";
        await PgShipConfigurationStore.MigrateAsync(source, schema);
        var store = new PgShipDirtStore(source, schema);
        const long shipId = 700002;
        Start(
            configure: registry => registry.Register(new ShipService(null!, () => FixedNowMs, shipDirts: store)),
            sessionOpened: opened =>
            {
                opened.Set("stationid", new PyInt(600));
                opened.Set("shipid", new PyLong(shipId));
            });

        try
        {
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            await front.ReceiveAsync();

            var nestedReset = new PyTuple(
            [
                new PyTuple([new PyInt(600), new PyInt(15)]),
                new PyTuple([new PyStr("ResetDirtTimestamp"), new PyTuple([new PyLong(shipId)]), PyNone.Instance]),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 1, "ship", "MachoBindObject", nestedReset, PyNone.Instance))));
            var reset = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
                Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
            var filetime = Py.FileTimeFromUnixMs(FixedNowMs);
            Assert.Equal(new PyLong(filetime), reset.Items[1]);
            Assert.Equal((long?)filetime, await store.GetAsync(shipId));

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, 2, "ship", "GetDirtTimestamp", new PyLong(shipId)))));
            var read = Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            Assert.Equal(new PyLong(filetime), read);
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
