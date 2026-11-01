using Eve.Marshal;
using Eve.Data;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Ship_configuration_changes_persist_through_the_registered_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = $"ship_config_host_{Guid.NewGuid():N}";
        await PgShipConfigurationStore.MigrateAsync(source, schema);
        var store = new PgShipConfigurationStore(source, schema);
        const long shipId = 700001;
        Start(
            configure: registry => registry.Register(new ShipService(null!, () => FixedNowMs, shipConfigurations: store)),
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

            var settings = new PyDict(
            [
                new(new PyStr("SMB_AllowFleetAccess"), PyBool.True),
                new(new PyStr("SMB_AllowCorpAccess"), PyBool.True),
                new(new PyStr("FleetHangar_AllowFleetAccess"), PyBool.True),
                new(new PyStr("FleetHangar_AllowCorpAccess"), PyBool.False),
            ]);
            var nestedCall = new PyTuple(
            [
                new PyTuple([new PyInt(600), new PyInt(15)]),
                new PyTuple([new PyStr("ConfigureShip"), new PyTuple([settings]), PyNone.Instance]),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 1, "ship", "MachoBindObject", nestedCall, PyNone.Instance))));
            var configured = Assert.IsType<Send>(await front.ReceiveAsync());
            var configuredResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(configured.Payload)));
            Assert.Equal(PyNone.Instance, configuredResult.Items[1]);

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, 2, "ship", "GetShipConfiguration", new PyLong(shipId)))));
            var read = Assert.IsType<Send>(await front.ReceiveAsync());
            var entries = Assert.IsType<PyDict>(Result(MarshalCodec.Decode(read.Payload))).Entries;
            var config = entries.ToDictionary(entry => Eve.Macho.JsSemantics.StringValue(entry.Key)!, entry => entry.Value);

            Assert.Equal(PyBool.True, config["allowFleetSMBUsage"]);
            Assert.Equal(PyBool.True, config["SMB_AllowFleetAccess"]);
            Assert.Equal(PyBool.True, config["allowCorpSMBUsage"]);
            Assert.Equal(PyBool.True, config["SMB_AllowCorpAccess"]);
            Assert.Equal(PyBool.True, config["FleetHangar_AllowFleetAccess"]);
            Assert.Equal(PyBool.False, config["FleetHangar_AllowCorpAccess"]);
            Assert.Equal(new ShipConfiguration(true, true, true, false), await store.GetAsync(shipId));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
