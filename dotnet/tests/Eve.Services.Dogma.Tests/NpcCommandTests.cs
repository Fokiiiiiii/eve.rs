using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary><c>/npc</c>: what its arguments mean and what the pilot is told; the NPCs are made by space (<see cref="ISpaceNpcs"/>).</summary>
public class NpcCommandTests
{
    private sealed class Npcs(NpcSpawnReport answer) : ISpaceNpcs
    {
        public List<(string Query, int Amount)> Asked { get; } = [];

        public List<bool> Stationary { get; } = [];

        public Task<NpcSpawnReport> SpawnNpcsAsync(ClientSession session, string query, int amount, CancellationToken cancellationToken, bool stationary = false)
        {
            Asked.Add((query, amount));
            Stationary.Add(stationary);
            return Task.FromResult(answer);
        }
    }

    private static ClientSession InSpace()
    {
        var session = new ClientSession(new Sink()) { SessionId = 1, ClientId = 431065451, UserId = 34, NodeId = 65450 };
        session.Set("solarsystemid", new PyInt(30100032));
        return session;
    }

    private static NpcSpawnReport Made(params (string, int)[] composition) => new(null, [], "Standard Pirate Hostiles", composition, composition.Sum(c => c.Item2));

    [Theory]
    [InlineData("", 5, "")]
    [InlineData("   ", 5, "")]
    [InlineData("3", 3, "")]
    [InlineData("guristas", 5, "guristas")]
    [InlineData("3 guristas", 3, "guristas")]
    [InlineData("guristas 3", 3, "guristas")]
    [InlineData("blood  raider 12", 12, "blood raider")]
    [InlineData("blood 7 raider", 7, "blood raider")]
    [InlineData("2k", 2000, "")]
    [InlineData("1_0 rats", 10, "rats")]
    [InlineData("4 5", 4, "5")]
    public void The_first_number_is_the_amount_and_the_rest_is_what_is_named(string argument, int amount, string query) =>
        Assert.Equal((amount, query), NpcCommands.Parse(argument));

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("2.5")]
    [InlineData("guristas 0")]
    [InlineData("99999999999")]
    public void An_amount_that_is_not_a_whole_number_above_nothing_is_refused(string argument) =>
        Assert.Null(NpcCommands.Parse(argument));

    [Fact]
    public async Task An_amount_that_cannot_be_parsed_is_the_usage_and_one_over_fifty_is_told()
    {
        var npcs = new Npcs(Made(("Rat", 1)));
        var command = new NpcCommands(npcs);

        Assert.Equal("Usage: /npc [amount] [faction|profile|pool]", await command.RunAsync(InSpace(), "0", CancellationToken.None));
        Assert.Equal("NPC spawn count must be between 1 and 50.", await command.RunAsync(InSpace(), "51", CancellationToken.None));
        Assert.Empty(npcs.Asked);

        await command.RunAsync(InSpace(), "50 pirates", CancellationToken.None);
        Assert.Equal(("pirates", 50), npcs.Asked.Single());
    }

    [Fact]
    public async Task What_was_made_is_told_by_name_and_count()
    {
        var npcs = new Npcs(Made(("Blood Visionary", 2), ("Sansha Devoted", 1)));
        var command = new NpcCommands(npcs);

        var message = await command.RunAsync(InSpace(), "3", CancellationToken.None);

        Assert.Equal(
            "Spawned 3 hulls from Standard Pirate Hostiles: 2x Blood Visionary, 1x Sansha Devoted. These command-spawned NPCs are transient and are not written to disk.",
            message);
        Assert.Equal(("", 3), npcs.Asked.Single());
    }

    [Fact]
    public async Task One_hull_is_not_plural_and_an_unnamed_selection_is_not_told()
    {
        var command = new NpcCommands(new Npcs(new NpcSpawnReport(null, [], "", [("Rat", 1)], 1)));

        Assert.Equal(
            "Spawned 1 hull: 1x Rat. These command-spawned NPCs are transient and are not written to disk.", await command.RunAsync(InSpace(), "1", CancellationToken.None));
    }

    [Fact]
    public async Task Still_holds_the_NPCs_in_place_and_is_told()
    {
        var npcs = new Npcs(Made(("Rat", 1)));

        var message = await new NpcCommands(npcs).RunAsync(InSpace(), "1 still", CancellationToken.None);

        Assert.Equal(("", 1), npcs.Asked.Single());
        Assert.True(npcs.Stationary.Single());
        Assert.Contains(" They hold still (a QA spawn: no top speed).", message);
    }

    [Fact]
    public async Task What_could_not_be_armed_and_what_stopped_the_batch_are_told()
    {
        var report = new NpcSpawnReport(null, [], "Guristas", [("Dire Guristas Invader", 2)], 5, Unarmed: 2);

        var message = await new NpcCommands(new Npcs(report)).RunAsync(InSpace(), "5", CancellationToken.None);

        Assert.Equal(
            "Spawned 2 hulls from Guristas: 2x Dire Guristas Invader. These command-spawned NPCs are transient and are not written to disk. "
            + "2 of them carry no weapon this server can fire yet (missile launchers). Spawn stopped at 3/5: NPC_NATIVE_SPAWN_FAILED.",
            message);
    }

    [Fact]
    public async Task One_hull_that_could_not_be_armed_is_told_too()
    {
        var report = new NpcSpawnReport(null, [], "", [("Dire Guristas Invader", 1)], 1, Unarmed: 1);

        var message = await new NpcCommands(new Npcs(report)).RunAsync(InSpace(), "1", CancellationToken.None);

        Assert.EndsWith(" 1 of them carry no weapon this server can fire yet (missile launchers).", message);
    }

    [Theory]
    [InlineData(NpcSpawnReport.NotInSpace, "You must be in space before using /npc.")]
    [InlineData(NpcSpawnReport.ShipNotFound, "Active ship was not found in space.")]
    [InlineData("PROFILE_NOT_FOUND", "NPC profile or pool not found: zzz.")]
    [InlineData("PROFILE_AMBIGUOUS", "NPC profile or pool is ambiguous: zzz.")]
    [InlineData("PROFILE_HULL_NOT_DAMAGEABLE", "The selected NPC profile's entity type declares no shield, armor or structure hit points, so the spawned NPC could not be shot or killed.")]
    [InlineData("NPC_DEFINITION_INCOMPLETE", "The selected NPC profile is missing authored loadout or behavior data.")]
    [InlineData("POOL_EMPTY", "The selected NPC pool has no spawnable authored entries.")]
    [InlineData("NPC_NATIVE_SPAWN_FAILED", "NPC spawn failed: NPC_NATIVE_SPAWN_FAILED.")]
    [InlineData("SOMETHING_ELSE", "NPC spawn failed: SOMETHING_ELSE.")]
    public async Task Why_nothing_was_made_is_told_in_the_words_of_the_reference(string error, string expected)
    {
        var command = new NpcCommands(new Npcs(NpcSpawnReport.Failed(error)));

        Assert.Equal(expected, await command.RunAsync(InSpace(), "zzz", CancellationToken.None));
    }

    [Fact]
    public async Task What_was_close_is_suggested_and_a_query_that_is_empty_is_called_default()
    {
        var suggestions = new[] { "Rat One (rat_1)", "Rat Two (rat_2)" };
        var command = new NpcCommands(new Npcs(NpcSpawnReport.Failed("PROFILE_NOT_FOUND", suggestions)));

        Assert.Equal(
            "NPC profile or pool not found: default. Suggestions: Rat One (rat_1), Rat Two (rat_2)", await command.RunAsync(InSpace(), "", CancellationToken.None));
        Assert.Equal(
            "NPC profile or pool not found: rat. Suggestions: Rat One (rat_1), Rat Two (rat_2)", await command.RunAsync(InSpace(), "rat", CancellationToken.None));
    }

    [Fact]
    public async Task A_pool_with_nothing_to_roll_says_what_was_close_without_a_trailing_space()
    {
        var command = new NpcCommands(new Npcs(NpcSpawnReport.Failed("POOL_EMPTY", ["Pool One (p1)"])));

        Assert.Equal("The selected NPC pool has no spawnable authored entries. Suggestions: Pool One (p1)", await command.RunAsync(InSpace(), "p", CancellationToken.None));
    }
}
