using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Invalid_non_ship_fit_type_returns_its_SDE_name_through_the_registered_host_rpc()
    {
        const long characterId = 140_300_001;
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var dogma = await DogmaStatic.LoadAsync(sde);
        var schema = "fitwire_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgModuleStateStore.MigrateAsync(source, schema);
            var store = new PgSavedFittingStore(source, schema);
            var calls = new FittingMgrCalls(store,
                new FittingHost(dogma, null!, new CharacterSessions()), () => FixedNowMs);
            var service = new CharFittingMgrService(calls);
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.CharacterId = characterId;
                    session.Set("charid", new PyInt(characterId));
                });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            var fitting = Py.KeyVal(
                ("name", new PyStr("not a ship")),
                ("description", new PyStr("text")),
                ("shipTypeID", new PyInt(2046)),
                ("fitData", new PyList([Py.Tuple(new PyInt(2046), new PyInt(11), new PyInt(1))])));
            var kwargs = Py.Dict(("machoVersion", new PyInt(3396210)));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 1, "charFittingMgr", "SaveFitting",
                    new PyTuple([new PyInt(characterId), fitting]), kwargs))));

            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload)));
            Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
            var responsePayload = Assert.IsType<PyTuple>(packet.Payload);
            var wrapped = Assert.IsType<PyTuple>(responsePayload.Items[2]);
            var userError = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(Assert.Single(wrapped.Items)).Decoded);
            var error = new MachoError(MachoErrorType.WrappedException, userError);
            Assert.Equal("InvalidFittingDataShipNotShip", error.UserErrorMessage);
            var values = ((PyTuple)((PyTuple)userError.Header).Items[1]).Items;
            var arguments = ((PyDict)values[1]).Entries
                .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
            Assert.Equal("Damage Control I", JsSemantics.StringValue(arguments["typeName"]));
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
