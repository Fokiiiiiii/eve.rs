using System.Text.Json.Nodes;
using Eve.Data;
using Eve.Sde;
using Npgsql;

namespace Eve.Skills.Tests;

public class SkillPointsTests
{
    [Fact]
    public void A_rank_one_skill_costs_the_levels_of_the_game()
    {
        Assert.Equal([0L, 250, 1414, 8000, 45255, 256000], Enumerable.Range(0, 6).Select(level => SkillPoints.At(1, level)));
    }

    [Theory]
    [InlineData(2, 2, 2828)]   // recorded from the reference: rank 2, level 2
    [InlineData(4, 0, 0)]
    [InlineData(3, 5, 768000)]
    [InlineData(8, 1, 2000)]
    public void The_rank_multiplies_the_points(int rank, int level, long expected) => Assert.Equal(expected, SkillPoints.At(rank, level));

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void A_level_outside_zero_to_five_is_refused(int level) => Assert.Throws<ArgumentOutOfRangeException>(() => SkillPoints.At(1, level));
}

public class SkillStoreTests
{
    [PgFact]
    public async Task Skills_are_granted_read_back_and_replaced()
    {
        await using var s = await PgSchema.CreateAsync();
        await s.GrantAsync(7, [new(3300, 4, 45255), new(3301, 1, 250), new(3315, 0, 0)]);
        Assert.Equal(new CharacterSkill[] { new(3300, 4, 45255), new(3301, 1, 250), new(3315, 0, 0) }, await s.Store.ForCharacterAsync(7));

        await s.GrantAsync(7, [new(3301, 2, 1414)]);
        Assert.Equal(new CharacterSkill(3301, 2, 1414), (await s.Store.ForCharacterAsync(7)).Single(k => k.TypeId == 3301));
        Assert.Equal(3, (await s.Store.ForCharacterAsync(7)).Count);
    }

    [PgFact]
    public async Task Characters_keep_their_own_skills_and_totals()
    {
        await using var s = await PgSchema.CreateAsync();
        await s.GrantAsync(1, [new(3300, 4, 45255), new(3301, 1, 250)]);
        await s.GrantAsync(2, [new(3300, 1, 250)]);
        Assert.Empty(await s.Store.ForCharacterAsync(3));

        var totals = await s.Store.TotalPointsAsync([1, 2, 3]);
        Assert.Equal(new Dictionary<long, long> { [1] = 45505, [2] = 250, [3] = 0 }, totals.ToDictionary(e => e.Key, e => e.Value));
        Assert.Empty(await s.Store.TotalPointsAsync([]));
    }

    [PgFact]
    public async Task A_grant_that_is_rolled_back_leaves_nothing()
    {
        await using var s = await PgSchema.CreateAsync();
        await using (var connection = await s.Source.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await s.Store.GrantAsync(connection, transaction, 9, [new CharacterSkill(3300, 1, 250)]);
            await transaction.RollbackAsync();
        }
        Assert.Empty(await s.Store.ForCharacterAsync(9));
    }

    [PgFact]
    public async Task The_table_refuses_a_level_beyond_five_or_negative_points()
    {
        await using var s = await PgSchema.CreateAsync();
        await Assert.ThrowsAsync<PostgresException>(() => s.GrantAsync(1, [new CharacterSkill(3300, 6, 1)]));
        await Assert.ThrowsAsync<PostgresException>(() => s.GrantAsync(1, [new CharacterSkill(3300, 1, -1)]));
    }

    [PgFact]
    public async Task The_migration_can_run_again()
    {
        await using var s = await PgSchema.CreateAsync();
        Assert.Empty(await PgSkillStore.MigrateAsync(s.Source, s.Name));
    }
}

public class SkillCatalogTests
{
    private static readonly JsonObject Starters = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", "starters.json")))!;

    private static async Task<SkillCatalog> LoadAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgSchema.Url!));
        var reader = new SdeReader(source);
        Assert.True(await reader.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        return await SkillCatalog.LoadAsync(reader);
    }

    [PgFact]
    public async Task The_skills_of_the_game_are_read_with_their_rank_and_group()
    {
        var catalog = await LoadAsync();
        Assert.InRange(catalog.Skills.Count, 300, 1000);
        Assert.Equal(new SkillType(3300, "Gunnery", 255, "Gunnery", true, 1), catalog.Get(3300));
        Assert.Equal((4, 1210, "Armor"), (catalog.Get(3315)!.Rank, catalog.Get(3393)!.GroupId, catalog.Get(3393)!.GroupName));
        Assert.All(catalog.Skills.Where(skill => skill.Published), skill => Assert.InRange(skill.Rank, 1, 16)); // the unpublished test skills have odd ranks
        Assert.Null(catalog.Get(587)); // a ship is not a skill
    }

    [PgFact]
    public async Task Each_race_starts_with_fifty_two_skills_three_of_them_untrained()
    {
        var catalog = await LoadAsync();
        foreach (var race in new[] { 1, 2, 4, 8 })
        {
            var start = catalog.StartingSkills(race);
            Assert.Equal(52, start.Count);
            Assert.Equal(3, start.Count(s => s.Level == 0));
            Assert.NotNull(catalog.StartingSet(race)); // every one of them is a skill of the catalog
        }
        Assert.Empty(catalog.StartingSkills(16)); // the Jove cannot be played
    }

    [PgFact]
    public async Task The_starting_points_are_the_ones_the_reference_gave_every_bloodline()
    {
        var catalog = await LoadAsync();
        var checkedBloodlines = 0;
        foreach (var node in (JsonArray)Starters["starters"]!)
        {
            var recorded = (JsonObject)node!;
            var row = ((JsonObject)((JsonArray)((JsonObject)recorded["selection"]!["ok"]!)["$tuple"]!)[2]![0]!["args"]!)["$dict"]!.AsArray()
                .ToDictionary(pair => (string)pair![0]!, pair => pair![1]);
            var total = catalog.StartingSet((int)recorded["raceID"]!)!.Sum(s => s.SkillPoints);
            Assert.Equal((long)row["skillPoints"]!, total);
            checkedBloodlines++;
        }
        Assert.Equal(12, checkedBloodlines);
    }
}
