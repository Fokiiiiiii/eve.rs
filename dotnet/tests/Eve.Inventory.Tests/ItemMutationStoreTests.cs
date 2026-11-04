using Eve.Data;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Npgsql;

namespace Eve.Inventory.Tests;

/// <summary>The provenance table, in a scratch schema of a real PostgreSQL (skipped without EVE_PG_URL; skipped is not passed).</summary>
public class ItemMutationStoreTests : IAsyncLifetime
{
    private const int Mutaplasmid = 47297;
    private const int Microwarpdrive = 12052;
    private const int Abyssal = 47408;
    private const long Character = 140_000_001;

    private readonly List<NpgsqlDataSource> _sources = [];
    private readonly List<string> _schemas = [];

    private NpgsqlDataSource Source()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(PgFactAttribute.Url!));
        _sources.Add(source);
        return source;
    }

    private string NewSchema()
    {
        var schema = $"im_{Guid.NewGuid():N}"[..20];
        _schemas.Add(schema);
        return schema;
    }

    /// <summary>Drops this test's schemas (a failed drop does not fail the test), then closes its sources.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            try
            {
                await using var drop = _sources[0].CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // Leaving a scratch schema behind is better than failing the test.
            }
        }
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }
    }

    private static bool SameBits(double expected, double actual) =>
        BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual);

    /// <summary>A mutated item made from a stored item, in the caller's transaction, with the given provenance.</summary>
    private static async Task<long> MakeItemAsync(NpgsqlDataSource source, PgItemStore items)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await items.CreateAsync(connection, transaction, new NewItem(Abyssal, Character, 60015249, ItemFlags.Hangar, 1, true));
        await transaction.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task Moving_items_uses_bounded_row_lookups_with_ten_thousand_items_present()
    {
        var source = Source();
        var schema = NewSchema();
        var items = new PgItemStore(source, schema);
        await PgItemStore.MigrateAsync(source, schema);

        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var sample = new long[100];
        for (var index = 0; index < 10_000; index++)
        {
            var id = await items.CreateAsync(connection, transaction,
                new NewItem(34, Character, 60_015_249, ItemFlags.Hangar, 1));
            if (index < sample.Length) sample[index] = id;
        }
        await transaction.CommitAsync();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        await using (var moveTransaction = await connection.BeginTransactionAsync())
        {
            foreach (var itemId in sample)
                await items.MoveAsync(connection, moveTransaction, itemId, 60_015_250, ItemFlags.Hangar);
            await moveTransaction.CommitAsync();
        }
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"100 row-locked moves among 10,000 items took {timer.Elapsed}");
        foreach (var itemId in sample)
            Assert.Equal(60_015_250, (await items.GetAsync(itemId))!.LocationId);
    }

    [PgFact]
    public async Task A_move_racing_a_merge_leaves_one_valid_item_transition()
    {
        var source = Source();
        var schema = NewSchema();
        var items = new PgItemStore(source, schema);
        await PgItemStore.MigrateAsync(source, schema);

        long sourceId;
        long destinationId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            sourceId = await items.CreateAsync(connection, transaction, new NewItem(34, Character, 60_015_249, ItemFlags.Hangar, 10));
            destinationId = await items.CreateAsync(connection, transaction, new NewItem(34, Character, 60_015_249, ItemFlags.Hangar, 10));
            await transaction.CommitAsync();
        }

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task MoveAsync()
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await start.Task;
            await items.MoveAsync(connection, transaction, sourceId, 60_015_250, ItemFlags.Hangar);
            await transaction.CommitAsync();
        }
        async Task<ItemMergeResult?> MergeAsync()
        {
            await start.Task;
            return await items.MergeAsync(sourceId, destinationId, 10, Character, 60_015_249, 60_015_249);
        }

        var move = MoveAsync();
        var merge = MergeAsync();
        start.SetResult(true);
        var merged = await merge.WaitAsync(TimeSpan.FromSeconds(5));
        await move.WaitAsync(TimeSpan.FromSeconds(5));

        var sourceAfter = await items.GetAsync(sourceId);
        var destinationAfter = await items.GetAsync(destinationId);
        Assert.NotNull(destinationAfter);
        if (merged is not null)
        {
            Assert.Null(sourceAfter);
            Assert.Equal(20, destinationAfter.Quantity);
        }
        else
        {
            Assert.NotNull(sourceAfter);
            Assert.Equal((60_015_250L, 10L), (sourceAfter.LocationId, sourceAfter.Quantity));
            Assert.Equal(10, destinationAfter.Quantity);
        }
    }

    [PgFact]
    public async Task The_provenance_table_is_applied_with_the_item_migrations()
    {
        var source = Source();
        var schema = NewSchema();

        var applied = await PgItemStore.MigrateAsync(source, schema);
        var again = await PgItemStore.MigrateAsync(source, schema);

        Assert.Contains("0005_item_mutation", applied);
        Assert.DoesNotContain("0005_item_mutation", again);
        await using var connection = await source.OpenConnectionAsync();
        var tables = (long)(await Sql.ScalarAsync(connection, null,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = $1 AND table_name = 'item_mutations'", CancellationToken.None, schema))!;
        Assert.Equal(1L, tables);
    }

    [PgFact]
    public async Task A_stored_record_reads_back_bit_for_bit_after_a_restart()
    {
        var source = Source();
        var schema = NewSchema();
        var items = new PgItemStore(source, schema);
        await PgItemStore.MigrateAsync(source, schema);
        var store = new ItemMutationStore(source, schema);
        var itemId = await MakeItemAsync(source, items);

        // Values that do not have short decimal forms: the stored double must be the rolled one, not a rounded copy.
        var rolls = new Dictionary<int, double> { [6] = 1.0 / 3.0, [30] = 0.8 + 0.75 * 0.7, [50] = 0.8 };
        var baseValues = new Dictionary<int, double> { [6] = 180, [30] = 150, [50] = 50 };
        var mutated = new Dictionary<int, double> { [6] = 180 * (1.0 / 3.0), [30] = 150 * (0.8 + 0.75 * 0.7), [50] = 50 * 0.8 };
        var record = new ItemMutationRecord(itemId, Mutaplasmid, 9_000_001, Microwarpdrive, 9_000_002, Character, 1_700_000_000_123, rolls, baseValues, mutated);

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.InsertAsync(connection, transaction, record);
            await transaction.CommitAsync();
        }

        // A new store instance reads what the first one wrote: nothing is recomputed or re-rolled on the way back.
        var reloaded = await new ItemMutationStore(Source(), schema).GetAsync(itemId);

        Assert.NotNull(reloaded);
        Assert.Equal(itemId, reloaded.ItemId);
        Assert.Equal(Mutaplasmid, reloaded.MutatorTypeId);
        Assert.Equal(9_000_001, reloaded.MutatorItemId);
        Assert.Equal(Microwarpdrive, reloaded.SourceTypeId);
        Assert.Equal(9_000_002, reloaded.SourceItemId);
        Assert.Equal(Character, reloaded.CharacterId);
        Assert.Equal(1_700_000_000_123, reloaded.CreatedAtMs);
        Assert.Equal(rolls.Keys.Order(), reloaded.Rolls.Keys.Order());
        foreach (var (attribute, value) in rolls)
        {
            Assert.True(SameBits(value, reloaded.Rolls[attribute]), $"roll of {attribute} changed on the way back");
        }
        foreach (var (attribute, value) in baseValues)
        {
            Assert.True(SameBits(value, reloaded.BaseValues[attribute]), $"base of {attribute} changed on the way back");
        }
        foreach (var (attribute, value) in mutated)
        {
            Assert.True(SameBits(value, reloaded.MutatedValues[attribute]), $"value of {attribute} changed on the way back");
        }
    }

    [PgFact]
    public async Task An_item_that_was_not_mutated_has_no_provenance()
    {
        var source = Source();
        var schema = NewSchema();
        var items = new PgItemStore(source, schema);
        await PgItemStore.MigrateAsync(source, schema);
        var plain = await MakeItemAsync(source, items);

        Assert.Null(await new ItemMutationStore(source, schema).GetAsync(plain));
        Assert.Null(await new ItemMutationStore(source, schema).GetAsync(987_654_321));
    }

    [PgFact]
    public async Task The_provenance_goes_with_its_item()
    {
        var source = Source();
        var schema = NewSchema();
        var items = new PgItemStore(source, schema);
        await PgItemStore.MigrateAsync(source, schema);
        var store = new ItemMutationStore(source, schema);
        var itemId = await MakeItemAsync(source, items);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.InsertAsync(connection, transaction, new ItemMutationRecord(itemId, Mutaplasmid, 1, Microwarpdrive, 2, Character, 1,
                new Dictionary<int, double> { [6] = 1.0 }, new Dictionary<int, double> { [6] = 180 }, new Dictionary<int, double> { [6] = 180 }));
            await transaction.CommitAsync();
        }

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await items.DeleteAsync(connection, transaction, itemId);
            await transaction.CommitAsync();
        }

        Assert.Null(await store.GetAsync(itemId));
    }

    [PgFact]
    public async Task A_record_must_name_one_roll_base_and_value_for_each_mutated_attribute()
    {
        var source = Source();
        var schema = NewSchema();
        var items = new PgItemStore(source, schema);
        await PgItemStore.MigrateAsync(source, schema);
        var store = new ItemMutationStore(source, schema);
        var itemId = await MakeItemAsync(source, items);

        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var mismatched = new ItemMutationRecord(itemId, Mutaplasmid, 1, Microwarpdrive, 2, Character, 1,
            new Dictionary<int, double> { [6] = 1.0, [20] = 0.95 }, new Dictionary<int, double> { [6] = 180 }, new Dictionary<int, double> { [6] = 180 });
        await Assert.ThrowsAsync<ArgumentException>(() => store.InsertAsync(connection, transaction, mismatched));
    }
}
