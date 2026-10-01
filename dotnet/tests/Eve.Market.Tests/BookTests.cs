using Eve.Data;
using Npgsql;

namespace Eve.Market.Tests;

public class BookTests
{
    [PgFact]
    public async Task Npc_book_configuration_replays_stably_and_invalid_batches_roll_back_completely()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "market_book_" + Guid.NewGuid().ToString("N");
        await MarketStore.MigrateAsync(source,schema);
        var store = new MarketStore(source,schema);
        var here = new MarketLocation(10,1,100,1000);
        try
        {
            MarketNpcSeed Seed(string key,decimal price,bool bid = false) => new(key,34,bid,price,100,32767,here,0);
            var rows = await store.SeedNpcAsync([Seed("ask-hi",12),Seed("ask-low",10),Seed("bid-low",7,true),Seed("bid-hi",9,true)]);
            var book = await store.BookAsync(1000,34,10000000000000);
            Assert.Equal([10m,12m],book.Asks.Select(o => o.Price));
            Assert.Equal([9m,7m],book.Bids.Select(o => o.Price));
            Assert.Equal(rows.Select(o => o.Id), (await store.SeedNpcAsync([Seed("ask-hi",12),Seed("ask-low",10),Seed("bid-low",7,true),Seed("bid-hi",9,true)])).Select(o => o.Id));
            var low = book.Asks.First();
            await using (var consumed = source.CreateCommand($"UPDATE {schema}.orders SET vol_remaining=20 WHERE id=$1"))
            {
                consumed.Parameters.Add(new NpgsqlParameter { Value=low.Id });
                await consumed.ExecuteNonQueryAsync();
            }
            await store.SeedNpcAsync([Seed("ask-low",10)]);
            Assert.Equal(20,(await store.BookAsync(1000,34,0)).Asks.First().VolumeRemaining);
            await Assert.ThrowsAsync<PostgresException>(() => store.SeedNpcAsync([Seed("new-good",3),Seed("bad",0)]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SeedNpcAsync([Seed("ask-low",10) with { TypeId=35 }]));
            Assert.DoesNotContain((await store.BookAsync(1000,34,0)).Asks,o => o.Price == 3);
            var reopened = new MarketStore(source,schema);
            Assert.Equal(20,(await reopened.BookAsync(1000,34,0)).Asks.First().VolumeRemaining);
            Assert.Empty((await store.BookAsync(2000,34,0)).Asks);
            Assert.Equal([low.Id],MarketMatching.Plan(book.Asks,new MarketTopology([]),34,true,here,10,2,-1,0).Select(f => f.Order.Id));
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
