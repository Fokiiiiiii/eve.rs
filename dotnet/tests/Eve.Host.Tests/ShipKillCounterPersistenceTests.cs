using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.GameStore;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class ShipKillCounterPersistenceTests
{
    [Fact]
    public async Task Player_kill_counter_reads_the_persisted_shipKillCounters_record()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(connectionString!);
        var schema = "ship_kill_test_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await using var store = new PersistenceStore(source, schema);
            await store.EnsureSchemaAsync();
            await using var owner = await OwnerSession.StartAsync(
                store, PersistenceRoles.World, "ship-kill-test-" + Guid.NewGuid().ToString("N"));
            await using var cache = new GameStoreCache(store, owner,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await cache.LoadAsync("shipKillCounters");
            Assert.True(cache.Write("shipKillCounters", "/ships/9001", new JsonObject
            {
                ["itemID"] = 9001,
                ["playerKills"] = 42,
                ["npcKills"] = 3,
            }).Success);
            await cache.FlushTableAsync("shipKillCounters");

            await using var reader = new GameStoreCache(store, null,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.Reader });
            var service = new ShipKillCounterService(async cancellationToken =>
            {
                await reader.LoadAsync("shipKillCounters", cancellationToken);
                return reader.Read("shipKillCounters", "/").Data as JsonObject ?? new JsonObject();
            });
            IService rpc = service;
            var session = new ClientSession();
            var found = await rpc.CallAsync(new ServiceCall
            {
                Method = "GetItemKillCountPlayer",
                Args = [new PyInt(9001)],
                Session = session,
            });
            var missing = await rpc.CallAsync(new ServiceCall
            {
                Method = "GetItemKillCountPlayer",
                Args = [new PyInt(9002)],
                Session = session,
            });
            Assert.Equal(new PyInt(42), found);
            Assert.Equal(new PyInt(0), missing);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
