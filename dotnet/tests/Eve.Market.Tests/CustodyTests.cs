using Eve.Data;
using Eve.Inventory;
using Eve.Sde;
using Npgsql;

namespace Eve.Market.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip="EVE_PG_URL is not set"; }
}

public class CustodyTests
{
    [PgFact]
    public async Task Stack_split_moves_are_atomic_and_concurrent_custody_cannot_consume_one_stack_twice()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "market_custody_" + Guid.NewGuid().ToString("N");
        await PgItemStore.MigrateAsync(source,schema);
        var items = new PgItemStore(source,schema);
        try
        {
            long id;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                id = await items.CreateAsync(connection,tx,new NewItem(34,1,60000001,ItemFlags.Hangar,10));
                await tx.CommitAsync();
            }
            var original = (await items.GetAsync(id))!;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                var split = await items.MoveStackAsync(connection,tx,original,4,1,9200000001,ItemFlags.Hangar);
                Assert.NotEqual(id,split.Id);
                Assert.Equal(4,split.Quantity);
                await tx.RollbackAsync();
                Assert.Null(await items.GetAsync(split.Id));
            }
            Assert.Equal(original,await items.GetAsync(id));
            await using (var constraint = source.CreateCommand($"ALTER TABLE {schema}.items ADD CONSTRAINT no_owner_77 CHECK(owner_id <> 77)"))
                await constraint.ExecuteNonQueryAsync();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await tx.SaveAsync("failed_split");
                await Assert.ThrowsAsync<PostgresException>(() => items.MoveStackAsync(connection,tx,original,4,77,9200000001,ItemFlags.Hangar));
                await tx.RollbackAsync("failed_split");
                await tx.CommitAsync(); // catching a failed split cannot commit a smaller source stack
            }
            Assert.Equal(original,await items.GetAsync(id));
            async Task<bool> Take()
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var tx = await connection.BeginTransactionAsync();
                try
                {
                    await items.MoveStackAsync(connection,tx,original,6,1,9200000001,ItemFlags.Hangar);
                    await tx.CommitAsync();
                    return true;
                }
                catch (InvalidOperationException error) when (error.Message == "ITEM_CHANGED") { return false; }
            }
            Assert.Equal(1,(await Task.WhenAll(Take(),Take())).Count(x => x));
            var remaining = (await items.GetAsync(id))!;
            Assert.Equal(4,remaining.Quantity);
            var escrow = Assert.Single(await items.InLocationAsync(9200000001,1));
            Assert.Equal(6,escrow.Quantity);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                var delivered = await items.MoveStackAsync(connection,tx,escrow,6,2,60000002,ItemFlags.Hangar);
                Assert.Equal(escrow.Id,delivered.Id);
                await tx.CommitAsync();
            }
            Assert.Empty(await items.InLocationAsync(9200000001,1));
            var buyerItem = Assert.Single(await items.InLocationAsync(60000002,2));
            Assert.Equal(10,remaining.Quantity+buyerItem.Quantity);
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Corporate_blueprint_lock_blocks_item_mutations_until_unlocked()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "market_blueprint_lock_" + Guid.NewGuid().ToString("N");
        await PgItemStore.MigrateAsync(source, schema);
        var items = new PgItemStore(source, schema);
        try
        {
            const long corporationId = 98_000_001;
            const long locationId = 60_000_001;
            long blueprintId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                blueprintId = await items.CreateAsync(connection, transaction,
                    new NewItem(810, corporationId, locationId, ItemFlags.CorpSAG1, Singleton: true));
                await transaction.CommitAsync();
            }

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await items.SetBlueprintLockedAsync(connection, transaction, corporationId, blueprintId, locked: true);
                await transaction.CommitAsync();
            }
            Assert.True((await items.GetAsync(blueprintId))!.BlueprintLocked);

            async Task AssertBlocked(Func<NpgsqlConnection, NpgsqlTransaction, Task> mutation)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => mutation(connection, transaction));
                await transaction.RollbackAsync();
            }

            await AssertBlocked((connection, transaction) =>
                items.MoveAsync(connection, transaction, blueprintId, locationId + 1, ItemFlags.CorpSAG2));
            await AssertBlocked((connection, transaction) =>
                items.MoveToOwnerAsync(connection, transaction, blueprintId, 98_000_002, locationId, ItemFlags.CorpSAG1));
            await AssertBlocked((connection, transaction) =>
                items.SetQuantityAsync(connection, transaction, blueprintId, 2));
            await AssertBlocked((connection, transaction) =>
                items.DeleteAsync(connection, transaction, blueprintId));
            var lockedItem = (await items.GetAsync(blueprintId))!;
            await AssertBlocked(async (connection, transaction) =>
            {
                await items.MoveStackAsync(connection, transaction, lockedItem, 1, corporationId,
                    locationId + 1, ItemFlags.CorpSAG1);
            });

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await items.SetBlueprintLockedAsync(connection, transaction, corporationId, blueprintId, locked: false);
                await items.MoveAsync(connection, transaction, blueprintId, locationId + 1, ItemFlags.CorpSAG1);
                await transaction.CommitAsync();
            }
            Assert.False((await items.GetAsync(blueprintId))!.BlueprintLocked);

            long copyId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                copyId = await items.CreateAsync(connection, transaction,
                    new NewItem(810, corporationId, locationId, ItemFlags.CorpSAG1, Singleton: true, BlueprintCopy: true));
                await transaction.CommitAsync();
            }
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    items.SetBlueprintLockedAsync(connection, transaction, corporationId, copyId, locked: true));
                Assert.Equal("ITEM_NOT_CORPORATION_BLUEPRINT_ORIGINAL", error.Message);
                await transaction.RollbackAsync();
            }
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task The_pinned_sde_stargates_load_as_market_routes()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var topology = await MarketTopology.LoadAsync(sde);
        var gate = (await sde.ReadAsync("mapStargates")).First(r => r.Doc.GetProperty("solarSystemID").GetInt32() != r.Doc.GetProperty("destination").GetProperty("solarSystemID").GetInt32());
        Assert.Equal(1,topology.Jumps(gate.Doc.GetProperty("solarSystemID").GetInt32(),gate.Doc.GetProperty("destination").GetProperty("solarSystemID").GetInt32()));
    }
}

public sealed class MarketSeedV4StoreTests
{
    [PgFact]
    public async Task V4_bulk_seed_preserves_tq_volume_baseline_and_retires_old_rows_atomically()
    {
        await using var source=NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema="market_seed_v4_"+Guid.NewGuid().ToString("N");
        await MarketStore.MigrateAsync(source,schema);
        var store=new MarketStore(source,schema);
        var location=new MarketLocation(60000001,30000001,20000001,10000001);
        const long now=1_800_000_000_000;
        try
        {
            await store.SeedNpcAsync([new("jita:34:ask",34,false,10m,100,MarketRules.RangeRegion,location,now)]);
            var ask=new MarketNpcSeed("tq:v4:1001",34,false,3.78m,100,MarketRules.RangeRegion,location,now,90,5,60);
            var bid=new MarketNpcSeed("tq:v4:1002",34,true,3.2m,50,MarketRules.RangeRegion,location,now,90,1,15);
            var empty=new MarketNpcSeed("tq:v4:1003",36,false,1m,10,MarketRules.RangeRegion,location,now,90,1,0);
            Assert.Equal(3,await store.SeedNpcV4Async(Stream(ask,bid,empty),new string('a',64),3396210,now,()=>now+10));
            await using (var seedTime=source.CreateCommand($"SELECT captured_at_ms,imported_at_ms FROM {schema}.market_seed_state WHERE version=4"))
            await using (var seedTimeReader=await seedTime.ExecuteReaderAsync())
            {
                Assert.True(await seedTimeReader.ReadAsync());
                Assert.Equal(now,seedTimeReader.GetInt64(0));
                Assert.Equal(now+10,seedTimeReader.GetInt64(1));
            }
            Assert.Equal((0,0L),await SeedState("tq:v4:1003"));
            Assert.Empty((await store.BookAsync(location.RegionId,36,now)).Asks);

            var book=await store.BookAsync(location.RegionId,34,now);
            var seededAsk=Assert.Single(book.Asks);
            Assert.Equal(100,seededAsk.VolumeEntered);
            Assert.Equal(60,seededAsk.VolumeRemaining);
            Assert.Equal(5,seededAsk.MinimumVolume);
            Assert.Equal(3.78m,seededAsk.Price);
            Assert.Equal(1,Assert.Single(book.Bids).MinimumVolume);
            Assert.Equal((2,0L),await SeedState("jita:34:ask"));

            var enumerated=false;
            async IAsyncEnumerable<MarketNpcSeed> Unread()
            {
                enumerated=true;
                await Task.Yield();
                yield break;
            }
            Assert.Equal(0,await store.SeedNpcV4Async(Unread(),new string('a',64),3396210,now,()=>now+20));
            Assert.False(enumerated);

            await using (var consume=source.CreateCommand($"UPDATE {schema}.orders SET vol_remaining=50 WHERE seed_key=$1"))
            {
                consume.Parameters.Add(new NpgsqlParameter { Value="tq:v4:1001" });
                await consume.ExecuteNonQueryAsync();
            }
            var refreshedAsk=ask with { VolumeRemaining=55 };
            var newAsk=new MarketNpcSeed("tq:v4:1004",35,false,20m,25,MarketRules.RangeRegion,location,now,30,2,25);
            Assert.Equal(2,await store.SeedNpcV4Async(Stream(refreshedAsk,newAsk),new string('b',64),3396210,now+1000,()=>now+1100));
            var refreshed=Assert.Single((await store.BookAsync(location.RegionId,34,now)).Asks);
            Assert.Equal(45,refreshed.VolumeRemaining); // New TQ remaining (55) less local consumption (10).
            Assert.Equal((2,0L),await SeedState("tq:v4:1002"));
            Assert.Equal((2,0L),await SeedState("tq:v4:1003"));
            Assert.Equal((2,0L),await SeedState("jita:34:ask"));
            Assert.Equal(2,Assert.Single((await store.BookAsync(location.RegionId,35,now)).Asks).MinimumVolume);

            var invalid=newAsk with { Key="tq:v4:invalid",VolumeRemaining=-1 };
            var error=await Assert.ThrowsAsync<ArgumentException>(()=>store.SeedNpcV4Async(Stream(newAsk,invalid),new string('c',64),3396210,now+2000,()=>now+2100));
            Assert.Contains("volume_remain",error.Message);
            await using var state=source.CreateCommand($"SELECT fingerprint,order_count FROM {schema}.market_seed_state WHERE version=4");
            await using var stateReader=await state.ExecuteReaderAsync();
            Assert.True(await stateReader.ReadAsync());
            Assert.Equal(new string('b',64),stateReader.GetString(0));
            Assert.Equal(2L,stateReader.GetInt64(1));
            Assert.DoesNotContain((await store.BookAsync(location.RegionId,35,now)).Asks,o=>o.VolumeEntered==26);
        }
        finally
        {
            await using var drop=source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }

        static async IAsyncEnumerable<MarketNpcSeed> Stream(params MarketNpcSeed[] seeds)
        {
            foreach(var seed in seeds)
            {
                yield return seed;
                await Task.Yield();
            }
        }

        async Task<(int State,long Remaining)> SeedState(string key)
        {
            await using var command=source.CreateCommand($"SELECT state,vol_remaining FROM {schema}.orders WHERE seed_key=$1");
            command.Parameters.Add(new NpgsqlParameter { Value=key });
            await using var reader=await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0),reader.GetInt64(1));
        }
    }
}
