using System.Text.Json;
using Eve.Data;
using Eve.Inventory.Mutation;
using Eve.Sde;
using Npgsql;

namespace Eve.Inventory.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL with the SDE (skipped is not passed).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }

    public static string? Url => Environment.GetEnvironmentVariable("EVE_PG_URL");
}

public class MutatorCatalogTests
{
    private static async Task<(SdeReader Reader, MutatorCatalog Catalog)> LoadAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(PgFactAttribute.Url!));
        var reader = new SdeReader(source);
        Assert.True(await reader.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        return (reader, await MutatorCatalog.LoadAsync(reader));
    }

    private static Dictionary<int, double> Attributes(SdeRow typeDogma) =>
        typeDogma.Doc.GetProperty("dogmaAttributes").EnumerateArray()
            .ToDictionary(a => a.GetProperty("attributeID").GetInt32(), a => a.GetProperty("value").GetDouble());

    [PgFact]
    public async Task The_published_mapping_has_the_counts_of_the_static_data()
    {
        var (reader, catalog) = await LoadAsync();
        var published = (await reader.ReadTypeSummariesAsync()).Where(t => t.Published).Select(t => (long)t.TypeId).ToHashSet();

        var rows = await reader.ReadAsync("dynamicItemAttributes");
        var allPairs = rows.Sum(row => row.Doc.GetProperty("inputOutputMapping").EnumerateArray()
            .Sum(mapping => mapping.GetProperty("applicableTypes").GetArrayLength()));
        var publishedPairs = rows.Sum(row => row.Doc.GetProperty("inputOutputMapping").EnumerateArray()
            .Sum(mapping => mapping.GetProperty("applicableTypes").EnumerateArray().Count(t => published.Contains(t.GetInt64()))));

        Assert.Equal(413, rows.Count);
        Assert.Equal(413, catalog.MutatorCount);
        Assert.Equal(89, catalog.OutputTypeCount);
        Assert.Equal(5665, allPairs);
        Assert.Equal(5227, publishedPairs);
        Assert.Equal(438, allPairs - publishedPairs);
        Assert.Equal(publishedPairs, catalog.PairCount);
        Assert.Equal(945, catalog.InputTypeCount);
    }

    [PgFact]
    public async Task The_worked_example_plan_is_the_spec_plan()
    {
        var (_, catalog) = await LoadAsync();

        var plan = Assert.IsType<Accepted<MutationPlan>>(catalog.Plan(47297, 12052)).Value;

        Assert.Equal(47408, plan.OutputTypeId);
        Assert.Equal(new[] { 6, 20, 30, 50, 554 }, plan.Ranges.Select(r => r.AttributeId).ToArray());
        Assert.Equal((0.6, 1.4), (plan.Ranges[0].Min, plan.Ranges[0].Max));
        Assert.Equal((0.9, 1.1), (plan.Ranges[1].Min, plan.Ranges[1].Max));
        Assert.Equal((0.8, 1.5), (plan.Ranges[2].Min, plan.Ranges[2].Max));
        Assert.Equal((0.8, 1.5), (plan.Ranges[3].Min, plan.Ranges[3].Max));
        Assert.Equal((0.7, 1.3), (plan.Ranges[4].Min, plan.Ranges[4].Max));
    }

    [PgFact]
    public async Task Refusals_are_typed_for_an_unknown_mutator_and_an_input_that_is_not_mappable()
    {
        var (_, catalog) = await LoadAsync();

        Assert.Equal(MutationRefusal.UnknownMutator, Assert.IsType<Refused<MutationPlan>>(catalog.Plan(12052, 12052)).Reason);
        Assert.Equal(MutationRefusal.UnknownMutator, Assert.IsType<Refused<MutationPlan>>(catalog.Plan(999_999_999, 12052)).Reason);
        Assert.Equal(MutationRefusal.InputNotMappable, Assert.IsType<Refused<MutationPlan>>(catalog.Plan(47297, 1183)).Reason);
        Assert.Equal(MutationRefusal.InputNotMappable, Assert.IsType<Refused<MutationPlan>>(catalog.Plan(47297, 47408)).Reason);
        Assert.Equal(MutationRefusal.UnknownMutator, Assert.IsType<Refused<IReadOnlyList<AttributeRange>>>(catalog.Intervals(12052)).Reason);
    }

    [PgFact]
    public async Task An_unpublished_input_is_not_mappable_and_the_data_has_such_a_pair()
    {
        var (reader, catalog) = await LoadAsync();
        var published = (await reader.ReadTypeSummariesAsync()).Where(t => t.Published).Select(t => (long)t.TypeId).ToHashSet();

        var unpublished = (await reader.ReadAsync("dynamicItemAttributes"))
            .SelectMany(row => row.Doc.GetProperty("inputOutputMapping").EnumerateArray()
                .SelectMany(mapping => mapping.GetProperty("applicableTypes").EnumerateArray())
                .Select(input => (Mutator: row.Key, Input: input.GetInt64())))
            .Where(pair => !published.Contains(pair.Input))
            .First();

        Assert.Equal(MutationRefusal.InputNotMappable, Assert.IsType<Refused<MutationPlan>>(catalog.Plan((int)unpublished.Mutator, (int)unpublished.Input)).Reason);
    }

    [PgFact]
    public async Task An_inverted_row_is_kept_as_an_interval_with_its_printed_ends()
    {
        var (_, catalog) = await LoadAsync();

        var range = Assert.Single(Assert.IsType<Accepted<IReadOnlyList<AttributeRange>>>(catalog.Intervals(56299)).Value, r => r.AttributeId == 2346);

        Assert.Equal(1.025, range.Min);
        Assert.Equal(0.95, range.Max);
        Assert.Equal(0.95, range.Low);
        Assert.Equal(1.025, range.High);
    }

    [PgFact]
    public async Task The_direction_of_an_attribute_is_the_row_override_else_the_dogma_attribute()
    {
        var (_, catalog) = await LoadAsync();

        var overrideTrue = Assert.Single(Assert.IsType<Accepted<IReadOnlyList<AttributeRange>>>(catalog.Intervals(52228)).Value, r => r.AttributeId == 73);
        var overrideFalse = Assert.Single(Assert.IsType<Accepted<IReadOnlyList<AttributeRange>>>(catalog.Intervals(47699)).Value, r => r.AttributeId == 20);
        var dogmaTrue = Assert.Single(Assert.IsType<Accepted<IReadOnlyList<AttributeRange>>>(catalog.Intervals(47297)).Value, r => r.AttributeId == 20);
        var dogmaFalse = Assert.Single(Assert.IsType<Accepted<IReadOnlyList<AttributeRange>>>(catalog.Intervals(47297)).Value, r => r.AttributeId == 6);

        Assert.True(overrideTrue.HighIsGood);
        Assert.False(overrideFalse.HighIsGood);
        Assert.True(dogmaTrue.HighIsGood);
        Assert.False(dogmaFalse.HighIsGood);
    }

    [PgFact]
    public async Task The_skill_attributes_are_the_requiredSkill_attributes_of_the_data()
    {
        var (_, catalog) = await LoadAsync();

        Assert.Equal(new[] { 182, 183, 184, 277, 278, 279, 1285, 1286, 1287, 1288, 1289, 1290 }.OrderBy(x => x), catalog.SkillAttributeIds.OrderBy(x => x));
    }

    [PgFact]
    public async Task The_worked_example_from_the_real_static_data_gives_the_spec_numbers()
    {
        var (reader, catalog) = await LoadAsync();
        var typeDogma = (await reader.ReadAsync("typeDogma", [12052L, 47408L])).ToDictionary(row => row.Key);
        var source = Attributes(typeDogma[12052]);
        var output = Attributes(typeDogma[47408]);

        var plan = Assert.IsType<Accepted<MutationPlan>>(catalog.Plan(47297, 12052)).Value;
        var rolls = new Dictionary<int, double>
        {
            [6] = MutationMath.MultiplierAt(plan.Ranges[0], 0.5),
            [20] = MutationMath.MultiplierAt(plan.Ranges[1], 0.25),
            [30] = MutationMath.MultiplierAt(plan.Ranges[2], 0.75),
            [50] = MutationMath.MultiplierAt(plan.Ranges[3], 0),
            [554] = MutationMath.MultiplierAt(plan.Ranges[4], 1),
        };

        var effective = Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(
            MutationMath.EffectiveAttributes(source, output, catalog.SkillAttributeIds, plan.Ranges, rolls)).Value;

        Assert.Equal(180.0, effective[6], 9);
        Assert.Equal(475.0, effective[20], 9);
        Assert.Equal(198.75, effective[30], 9);
        Assert.Equal(40.0, effective[50], 9);
        Assert.Equal(650.0, effective[554], 9);
        Assert.Equal(40.0, effective[9]);
        Assert.Equal(10000.0, effective[73]);
        Assert.Equal(3454.0, effective[182]);
        Assert.Equal(4.0, effective[277]);
        Assert.Equal(source[796], effective[796]);
    }

    [PgFact]
    public async Task A_roll_outside_the_real_interval_is_refused_end_to_end()
    {
        var (_, catalog) = await LoadAsync();
        var plan = Assert.IsType<Accepted<MutationPlan>>(catalog.Plan(47297, 12052)).Value;
        var rolls = plan.Ranges.ToDictionary(r => r.AttributeId, r => r.High);
        rolls[30] = plan.Ranges[2].High + 0.01;

        var refused = Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(
            MutationMath.ValidateRolls(plan.Ranges, rolls));
        Assert.Equal(MutationRefusal.RollOutOfRange, refused.Reason);
    }
}
