using Eve.Data;
using Npgsql;

namespace Eve.Agents.Tests;

/// <summary>The storyline offer rule (one offer for every 16 completed missions of a level and faction) and its PostgreSQL counter.</summary>
public class StorylineCounterTests
{
    [Fact]
    public void The_storyline_offers_due_are_the_whole_intervals_less_the_offers_made()
    {
        Assert.Equal(0, StorylineCounters.PendingOffers(15, 0));
        Assert.Equal(1, StorylineCounters.PendingOffers(16, 0));
        Assert.Equal(1, StorylineCounters.PendingOffers(31, 0));
        Assert.Equal(2, StorylineCounters.PendingOffers(32, 0));
        Assert.Equal(1, StorylineCounters.PendingOffers(32, 1));
        Assert.Equal(0, StorylineCounters.PendingOffers(16, 1));
        Assert.Equal(0, StorylineCounters.PendingOffers(0, 3));
    }

    [PgFact]
    public async Task Sixteen_completions_of_one_faction_and_level_are_one_pending_storyline_offer_until_it_is_made()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "stc_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await AgentMissionStore.MigrateAsync(source, schema);
        var counters = new StorylineCounters(source, schema);
        const long character = 90000201, faction = 500003, otherFaction = 500004;

        for (var i = 0; i < 16; i++)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await counters.IncrementAsync(connection, transaction, character, faction, 4);
            await transaction.CommitAsync();
        }

        var (completed, offered) = await counters.ReadAsync(character, faction, 4);
        Assert.Equal((16L, 0L), (completed, offered));
        Assert.Equal(1, StorylineCounters.PendingOffers(completed, offered));
        Assert.Equal((0L, 0L), await counters.ReadAsync(character, otherFaction, 4));

        await counters.MarkOfferedAsync(character, faction, 4);
        var after = await counters.ReadAsync(character, faction, 4);
        Assert.Equal(0, StorylineCounters.PendingOffers(after.Completed, after.Offered));
    }
}
