using Eve.Data;
using Eve.Sde;
using Eve.Services.Slash;
using Npgsql;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// <c>/bp</c> (EVE.js <c>executeBlueprintCommand</c>): the words of its branches and their rows. The lists come from a fake host; the lookups read the pinned SDE.
/// </summary>
public sealed class IndustryCommandTests
{
    private sealed class FakeIndustry : IGmIndustry
    {
        public List<GmBlueprintItem> Blueprints { get; } = [];

        public List<GmIndustryJob> Jobs { get; } = [];

        public Task<IReadOnlyList<GmBlueprintItem>> BlueprintsOfAsync(long ownerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GmBlueprintItem>>(Blueprints.Where(blueprint => blueprint.OwnerId == ownerId).ToArray());

        public Task<IReadOnlyList<GmIndustryJob>> JobsOfAsync(long ownerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GmIndustryJob>>(Jobs.Where(job => job.OwnerId == ownerId).ToArray());
    }

    private static IndustryCommands Commands(FakeIndustry industry, NpgsqlDataSource? source = null) =>
        new(new ItemTypeNames(source is null ? null! : new SdeReader(source)), source is null ? null! : new SdeReader(source), industry);

    [Theory]
    [InlineData("")]
    [InlineData("help")]
    [InlineData("nothing")]
    public async Task Help_and_unknown_subcommands_give_the_help(string argument)
    {
        Assert.Equal(IndustryCommands.Help, await Commands(new FakeIndustry()).RunAsync(Rig.Session(1001), argument, default));
    }

    [Fact]
    public async Task List_and_jobs_need_a_selected_character()
    {
        var commands = Commands(new FakeIndustry());

        Assert.Equal("Select a character before using /bp.", await commands.RunAsync(Rig.Session(null), "list", default));
        Assert.Equal("Select a character before using /bp.", await commands.RunAsync(Rig.Session(null), "jobs", default));
    }

    [Fact]
    public async Task List_says_when_the_character_holds_no_blueprint_and_shows_its_own_items()
    {
        var industry = new FakeIndustry();
        var commands = Commands(industry);
        Assert.Equal(IndustryCommands.NoBlueprints, await commands.RunAsync(Rig.Session(1001), "list", default));

        industry.Blueprints.Add(new GmBlueprintItem(1_000_000_001, 824, 1001, 60015249, 4, -1));
        industry.Blueprints.Add(new GmBlueprintItem(1_000_000_002, 1234, 1001, 60015249, 4, null));
        industry.Blueprints.Add(new GmBlueprintItem(1_000_000_003, 999, 2002, 60015249, 4, 5));

        Assert.Equal("itemID=1000000001 typeID=824 runs=-1 owner=1001 flag=4 facility=60015249\nitemID=1000000002 typeID=1234 runs=-1 owner=1001 flag=4 facility=60015249",
            await commands.RunAsync(Rig.Session(1001), "list", default));
    }

    [Fact]
    public async Task List_shows_at_most_twenty_rows()
    {
        var industry = new FakeIndustry();
        for (var i = 1; i <= 25; i++)
            industry.Blueprints.Add(new GmBlueprintItem(i, 824, 1001, 60015249, 4, 1));

        var answer = await Commands(industry).RunAsync(Rig.Session(1001), "list", default);

        Assert.Equal(20, answer.Split('\n').Length);
        Assert.StartsWith("itemID=1 ", answer);
    }

    [Fact]
    public async Task Jobs_says_when_there_are_none_and_shows_the_owners_jobs()
    {
        var industry = new FakeIndustry();
        var commands = Commands(industry);
        Assert.Equal(IndustryCommands.NoJobs, await commands.RunAsync(Rig.Session(1001), "jobs", default));

        industry.Jobs.Add(new GmIndustryJob(42, 1, 824, 587, 3, 1001));
        industry.Jobs.Add(new GmIndustryJob(43, 100, 999, 1000, 1, 2002));

        Assert.Equal("job=42 status=1 blueprint=824 product=587 runs=3 owner=1001", await commands.RunAsync(Rig.Session(1001), "jobs", default));
    }

    [Fact]
    public async Task Lookup_without_a_name_gives_its_usage()
    {
        Assert.Equal(IndustryCommands.LookupUsage, await Commands(new FakeIndustry()).RunAsync(Rig.Session(1001), "lookup", default));
    }

    [PgFact]
    public async Task Lookup_names_the_blueprint_and_the_product_of_a_product_a_blueprint_or_a_preset()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var commands = Commands(new FakeIndustry(), source);

        var byProduct = await commands.RunAsync(Rig.Session(1001), "lookup Rifter", default);
        var byPreset = await commands.RunAsync(Rig.Session(1001), "lookup frigate", default);
        var byBlueprint = await commands.RunAsync(Rig.Session(1001), "lookup Rifter Blueprint", default);

        Assert.Equal(byProduct, byPreset);
        Assert.Equal(byProduct, byBlueprint);
        var lines = byProduct.Split('\n');
        Assert.Equal("Blueprint: Rifter Blueprint (691)", lines[0]);
        Assert.Equal("Product: Rifter (587)", lines[1]);
        Assert.StartsWith("Max production limit: ", lines[2]);
    }

    [PgFact]
    public async Task Lookup_of_a_name_that_is_no_type_gives_the_usage()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));

        Assert.Equal(IndustryCommands.LookupUsage, await Commands(new FakeIndustry(), source).RunAsync(Rig.Session(1001), "lookup zzzzqq", default));
    }
}
