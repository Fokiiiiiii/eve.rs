using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class RaffleStationValidationTests
{
    [Fact]
    public async Task CreationRequiresAnNpcStationAndItsAuthoritativeNonTriglavianSystem()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12]; string inventory = "rf_si_" + suffix, wallet = "rf_sw_" + suffix, raffle = "rf_sr_" + suffix;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        try
        {
            await PgItemStore.MigrateAsync(source, inventory); await PgWalletStore.MigrateAsync(source, wallet);
            var items = new PgItemStore(source, inventory); var store = new RaffleProxyStore(source, items, new WalletTransactions(source, wallet), schema: raffle);
            await store.MigrateAsync(); const long owner = 90000101, jita = 60003760, jitaSystem = 30000142, pochven = 60000355, pochvenSystem = 30001372, structure = 1030000000001;
            async Task<(long Prize, long Token, ServiceCall Call)> Prepare(long station, long? declaredSystem)
            {
                await using var connection = await source.OpenConnectionAsync(); await using var tx = await connection.BeginTransactionAsync();
                long prize = await items.CreateAsync(connection, tx, new NewItem(587, owner, station, ItemFlags.Hangar, 1));
                long token = await items.CreateAsync(connection, tx, new NewItem(52568, owner, station, ItemFlags.Hangar, 2));
                await tx.CommitAsync();
                var data = Py.KeyVal(("owner_id",new PyLong(owner)), ("location_id",new PyLong(station)), ("solar_system_id",declaredSystem is long system ? new PyLong(system) : PyNone.Instance),
                    ("item_id",new PyLong(prize)), ("token_id",new PyLong(token)), ("token_location_id",new PyLong(station)),
                    ("type_id",new PyInt(587)), ("ticket_count",new PyInt(8)), ("ticket_price",new PyInt(125)));
                var session = new ClientSession { CharacterId = owner }; session.Set("stationid",new PyLong(station));
                return (prize, token, new ServiceCall { Method = "CreateRaffle", Session = session, Args = [data] });
            }
            foreach (var (station, system) in new[] { (structure, jitaSystem), (pochven, pochvenSystem), (jita, pochvenSystem) })
            {
                var (prize, token, call) = await Prepare(station, system);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateRaffleAsync(call, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 3500000));
                Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(prize))!.Flag);
                Assert.Equal(2, (await items.GetAsync(token))!.Quantity);
            }
            var valid = await Prepare(jita, null);
            long id = await store.CreateRaffleAsync(valid.Call, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 3500000);
            await using var query = source.CreateCommand($"SELECT solar_system_id FROM {raffle}.raffle_records WHERE raffle_id=$1");
            query.Parameters.AddWithValue(id); Assert.Equal(jitaSystem, (long)(await query.ExecuteScalarAsync())!);
        }
        finally { await using var clean = source.CreateCommand($"DROP SCHEMA IF EXISTS {raffle} CASCADE; DROP SCHEMA IF EXISTS {inventory} CASCADE; DROP SCHEMA IF EXISTS {wallet} CASCADE"); await clean.ExecuteNonQueryAsync(); }
    }
}
