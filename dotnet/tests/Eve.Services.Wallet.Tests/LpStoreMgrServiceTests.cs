using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Wallet.Tests;

/// <summary>LPStoreMgr answers the offers of a corporation's store as the client reads them; taking an offer is refused by name.</summary>
public class LpStoreMgrServiceTests
{
    private const long Corporation = 1000125;

    [LpPgFact]
    public async Task The_offers_are_answered_as_the_reference_rows_and_a_take_is_refused_by_name()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(url));
        var schema = "lpm_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgWalletStore.MigrateAsync(source, schema);
            var store = new LpStoreOfferStore(source, schema);
            await store.ReplaceAsync(Corporation, [new LpStoreOffer(Corporation, 20001, 1234, 1, 5000, 100000, 3,
                [new LpStoreRequirement(34, 10)])], DateTimeOffset.UtcNow);
            var service = new LpStoreMgrService(new LpStoreOfferCache(store, null, () => DateTimeOffset.UtcNow));
            var session = new ClientSession(new Sink()) { UserId = 7, CharacterId = 140000031 };

            var rows = await service.CallAsync(Call("GetAvailableOffersFromCorp", session, new PyInt(Corporation)));

            var offer = Assert.IsType<PyObject>(Assert.Single(Assert.IsType<PyList>(rows).Items));
            var fields = Assert.IsType<PyDict>(offer.Args).Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
            Assert.Equal(1234L, ((PyInt)fields["typeID"]).Value);
            Assert.Equal(20001L, ((PyInt)fields["offerID"]).Value);
            Assert.Equal(5000L, ((PyInt)fields["lpCost"]).Value);
            Assert.Equal(100000L, ((PyInt)fields["iskCost"]).Value);
            Assert.Equal(Corporation, ((PyInt)fields["corpID"]).Value);
            Assert.IsType<PyNone>(fields["requiredStandings"]);
            var required = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(fields["reqItems"]).Items));
            Assert.Equal((34L, 10L), (((PyInt)required.Items[0]).Value, ((PyInt)required.Items[1]).Value));

            var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
                service.CallAsync(Call("TakeOfferForCharacter", session, new PyInt(Corporation), new PyInt(20001), new PyInt(1))).AsTask());
            Assert.IsType<MachoWrappedException>(refused);
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session, CallId = new PyInt(1), Kwargs = PyNone.Instance };

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }
}
