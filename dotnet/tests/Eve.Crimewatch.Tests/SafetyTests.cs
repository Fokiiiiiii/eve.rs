using Eve.Data;
using Npgsql;

namespace Eve.Crimewatch.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

public class SafetyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 2)]
    [InlineData(-3, 0)]
    [InlineData(1.9, 1)]
    public void A_level_is_brought_to_the_nearest_of_the_three(double requested, int expected) => Assert.Equal(expected, SafetyLevel.Normalize(requested));

    [Fact]
    public void A_value_that_is_no_number_is_full()
    {
        Assert.Equal(SafetyLevel.Full, SafetyLevel.Normalize(null));
        Assert.Equal(SafetyLevel.Full, SafetyLevel.Normalize(double.NaN));
        Assert.Equal(SafetyLevel.Full, SafetyLevel.Normalize(double.PositiveInfinity));
    }

    [PgFact]
    public async Task A_character_is_at_full_until_it_sets_a_level_and_the_level_is_kept_per_character()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "cw_" + Guid.NewGuid().ToString("N")[..12];
        await PgCrimewatchStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgCrimewatchStore(source, schema);
            Assert.Equal(SafetyLevel.Full, await store.SafetyOfAsync(1));
            Assert.Equal(SafetyLevel.Partial, await store.SetSafetyAsync(1, 1, 1000));
            Assert.Equal(SafetyLevel.Partial, await store.SafetyOfAsync(1));
            Assert.Equal(SafetyLevel.Full, await store.SafetyOfAsync(2));
            Assert.Equal(SafetyLevel.None, await store.SetSafetyAsync(1, 0, 2000));
            Assert.Equal(SafetyLevel.Full, await store.SetSafetyAsync(1, 7, 3000));   // out of range: the nearest
            Assert.Equal(SafetyLevel.Full, await store.SafetyOfAsync(1));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }
}
