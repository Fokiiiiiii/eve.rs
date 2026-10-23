using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// <c>/asteroid rewards</c> with the pilot's rocks given (the rock query itself is in the space tests): the reply lists each rock and the units of each ore.
/// </summary>
public class AsteroidCommandTests
{
    private static AsteroidCommands Rocks(IReadOnlyList<(long ItemId, int TypeId, string Name, long Units)>? found) =>
        new((session, cancellationToken) => Task.FromResult(found));

    [Fact]
    public async Task Rewards_list_each_rock_and_the_total_of_each_ore()
    {
        var command = Rocks([(11, 1230, "Veldspar", 500), (12, 1230, "Veldspar", 300), (13, 1228, "Scordite", 200)]);

        var reply = await command.RunAsync(Rig.Session(7), "rewards", CancellationToken.None);

        Assert.Equal(
            "Asteroid rewards in your solar system (3 rocks):\n11: Veldspar (1230) reward 500 units\n12: Veldspar (1230) reward 300 units\n13: Scordite (1228) reward 200 units\n" +
            "Totals: Scordite: 200 units, Veldspar: 800 units",
            reply);
    }

    [Fact]
    public async Task Rewards_say_when_the_pilot_is_not_in_space_or_no_rock_has_units()
    {
        Assert.Equal("You must be in space before using /asteroid.", await Rocks(null).RunAsync(Rig.Session(7), "rewards", CancellationToken.None));
        Assert.Equal("No asteroid with units left is in your solar system.", await Rocks([]).RunAsync(Rig.Session(7), "rewards", CancellationToken.None));
    }

    [Fact]
    public async Task Other_words_answer_the_usage()
    {
        Assert.Equal(AsteroidCommands.Usage, await Rocks([]).RunAsync(Rig.Session(7), "highlight 5", CancellationToken.None));
    }
}
