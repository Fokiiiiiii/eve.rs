using Eve.Data;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Npgsql;
using Xunit;

namespace Eve.Services.Space.Tests;

public sealed class ShipDirtServiceTests
{
    private const long OwnShipId = 700003;
    private const long OtherShipId = 700004;

    [PgFact]
    public async Task Reset_stores_the_bound_own_ship_dirt_timestamp_and_get_reads_the_stored_value_back()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = $"ship_dirt_{Guid.NewGuid():N}";
        await PgShipConfigurationStore.MigrateAsync(source, schema);
        try
        {
            const long nowUnixMs = 1_700_000_000_000;
            var store = new PgShipDirtStore(source, schema);
            var service = new ShipService(null!, () => nowUnixMs, shipDirts: store);
            var session = Session(OwnShipId);

            var answer = Assert.IsType<PyTuple>(await CallAsync(service, session, "MachoBindObject", Bind("ResetDirtTimestamp", OwnShipId)));
            var filetime = 116444736000000000L + nowUnixMs * 10_000L;
            Assert.Equal(new PyLong(filetime), answer.Items[1]);
            Assert.Equal(filetime, await store.GetAsync(OwnShipId));

            // A later clock proves GetDirtTimestamp answers the stored value, not the current time.
            var later = new ShipService(null!, () => nowUnixMs + 5_000, shipDirts: store);
            Assert.Equal(new PyLong(filetime), await CallAsync(later, session, "GetDirtTimestamp", new PyLong(OwnShipId)));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Reset_rejects_a_ship_the_caller_does_not_own_and_leaves_its_timestamp_alone()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = $"ship_dirt_{Guid.NewGuid():N}";
        await PgShipConfigurationStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgShipDirtStore(source, schema);
            var service = new ShipService(null!, () => 1_700_000_000_000, shipDirts: store);
            var session = Session(OwnShipId);

            await Assert.ThrowsAnyAsync<Exception>(() => CallAsync(service, session, "MachoBindObject", Bind("ResetDirtTimestamp", OtherShipId)));
            Assert.Null(await store.GetAsync(OtherShipId));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    private static ClientSession Session(long shipId)
    {
        var session = new ClientSession();
        session.Set("stationid", new PyInt(600));
        session.Set("shipid", new PyLong(shipId));
        return session;
    }

    /// <summary>The client's <c>MachoBindObject((stationID, groupID), (method, args, kwargs))</c> for a nested call on the ship.</summary>
    private static PyValue[] Bind(string method, long shipId) =>
    [
        Py.Tuple(new PyInt(600), new PyInt(15)),
        Py.Tuple(new PyStr(method), Py.Tuple(new PyLong(shipId)), PyNone.Instance),
    ];

    private static async Task<PyValue> CallAsync(ShipService service, ClientSession session, string method, params PyValue[] args) =>
        await ((IService)service).CallAsync(new ServiceCall { Method = method, Args = [.. args], Session = session, CallId = new PyInt(1) });
}
