using Eve.Data;
using Eve.Host.Persistence;
using Eve.Marshal;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class PgAccruedTimeStoreTests
{
    [PgFact]
    public async Task Account_session_history_is_durable_and_closes_only_open_rows()
    {
        var schema = "login_" + Guid.NewGuid().ToString("N")[..12];
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        try
        {
            var store = new PgAccruedTimeStore(source, schema);
            await store.MigrateAsync();
            var accountId = 77_000_001L;
            var started = DateTime.UtcNow.Ticks;
            var rowId = await store.OpenAsync(accountId, started);

            var active = await store.ReadWireValueAsync(accountId, started + 50);
            Assert.Equal(new PyList([new PyTuple([new PyLong(rowId), new PyLong(started), new PyLong(started + 50)])]), active);

            var ended = started + 100;
            await store.CloseAsync(rowId, ended);
            await store.CloseAsync(rowId, ended + 1);
            var restarted = new PgAccruedTimeStore(source, schema);
            Assert.Equal(new PyList([new PyTuple([new PyLong(rowId), new PyLong(started), new PyLong(ended)])]),
                await restarted.ReadWireValueAsync(accountId, ended + 2));
            Assert.Equal(new PyList([]), await restarted.ReadWireValueAsync(accountId + 1, ended + 2));
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
