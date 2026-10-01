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
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
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
    public async Task The_pinned_sde_stargates_load_as_market_routes()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var topology = await MarketTopology.LoadAsync(sde);
        var gate = (await sde.ReadAsync("mapStargates")).First(r => r.Doc.GetProperty("solarSystemID").GetInt32() != r.Doc.GetProperty("destination").GetProperty("solarSystemID").GetInt32());
        Assert.Equal(1,topology.Jumps(gate.Doc.GetProperty("solarSystemID").GetInt32(),gate.Doc.GetProperty("destination").GetProperty("solarSystemID").GetInt32()));
    }
}
