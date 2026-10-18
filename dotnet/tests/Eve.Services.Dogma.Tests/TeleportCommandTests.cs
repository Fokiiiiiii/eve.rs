using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// The teleport commands (<c>/solar</c>, <c>/tele</c>) without a database: a fake host relocation names the systems and the pilots and records the moves, so the
/// words, the usage checks and the choice of destination are the commands' own.
/// </summary>
public sealed class TeleportCommandTests
{
    private static readonly GmDestination Beside = new(30000142, "Jita", [1_000.0, 2_000.0, 3_000.0], [-1.0, 0.0, 0.0]);

    private sealed class FakeRelocation : IGmRelocation
    {
        public Dictionary<string, GmSolarSystem> Systems { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, GmPilot> Pilots { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where a connected pilot is (null: in no system).</summary>
        public GmDestination? Near { get; set; }

        /// <summary>What a move is answered with: null (done) or the words of a refusal.</summary>
        public string? Refusal { get; set; }

        public List<(long Session, GmDestination Destination)> Moves { get; } = [];

        public Task<GmSolarSystem?> FindSolarSystemAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(Systems.GetValueOrDefault(token));

        public Task<GmPilot?> FindPilotAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(Pilots.GetValueOrDefault(token));

        public Task<GmDestination?> DestinationNearAsync(ClientSession pilot, CancellationToken cancellationToken) => Task.FromResult(Near);

        public Task<string?> MoveAsync(ClientSession session, GmDestination destination, CancellationToken cancellationToken)
        {
            Moves.Add((session.SessionId, destination));
            return Task.FromResult(Refusal);
        }
    }

    private static FakeRelocation WithJita()
    {
        var relocation = new FakeRelocation();
        relocation.Systems["Jita"] = new GmSolarSystem(30000142, "Jita");
        return relocation;
    }

    [Fact]
    public async Task Solar_without_a_name_gives_the_usage()
    {
        var relocation = WithJita();

        Assert.Equal(TeleportCommands.SolarUsage, await new TeleportCommands(relocation).SolarAsync(Rig.Session(1001), "  ", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Solar_needs_a_selected_character()
    {
        var relocation = WithJita();

        Assert.Equal("Select a character before using /solar.", await new TeleportCommands(relocation).SolarAsync(Rig.Session(null), "Jita", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Solar_names_a_system_it_cannot_find_and_moves_nobody()
    {
        var relocation = WithJita();

        Assert.Equal("Solar system not found: Nowhere.", await new TeleportCommands(relocation).SolarAsync(Rig.Session(1001), "Nowhere", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Solar_moves_the_pilot_to_the_anchor_of_the_system_and_names_it()
    {
        var relocation = WithJita();

        var answer = await new TeleportCommands(relocation).SolarAsync(Rig.Session(1001, id: 5), "Jita", default);

        Assert.Equal("Teleported to Jita (30000142).", answer);
        var (session, destination) = Assert.Single(relocation.Moves);
        Assert.Equal(5, session);
        Assert.Equal((30000142, "Jita", null, null), (destination.SolarSystemId, destination.SolarSystemName, destination.Position, destination.Direction));
    }

    [Fact]
    public async Task Solar_gives_the_refusal_of_the_move_as_it_is()
    {
        var relocation = WithJita();
        relocation.Refusal = "Space is not available right now. Try again in a moment.";

        Assert.Equal(relocation.Refusal, await new TeleportCommands(relocation).SolarAsync(Rig.Session(1001), "Jita", default));
    }

    [Fact]
    public async Task Tele_without_a_target_gives_the_usage()
    {
        var relocation = new FakeRelocation();

        Assert.Equal(TeleportCommands.TeleUsage, await new TeleportCommands(relocation).TeleAsync(Rig.Session(1001), "", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Tele_names_a_character_it_cannot_find()
    {
        var relocation = new FakeRelocation();

        Assert.Equal("Character not found: Nobody.", await new TeleportCommands(relocation).TeleAsync(Rig.Session(1001), "Nobody", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Tele_to_a_character_that_is_not_connected_moves_nobody()
    {
        var relocation = new FakeRelocation();
        relocation.Pilots["Alt"] = new GmPilot(7, null);

        Assert.Equal("Character 7 is not currently connected.", await new TeleportCommands(relocation).TeleAsync(Rig.Session(1001), "Alt", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Tele_to_a_connected_character_in_no_system_moves_nobody()
    {
        var relocation = new FakeRelocation();
        relocation.Pilots["Alt"] = new GmPilot(7, Rig.Session(7, id: 2));

        Assert.Equal("Could not resolve a live location for character 7.", await new TeleportCommands(relocation).TeleAsync(Rig.Session(1001), "Alt", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Tele_moves_the_pilot_to_the_place_beside_the_target_and_names_it()
    {
        var relocation = WithJita();
        relocation.Pilots["Alt"] = new GmPilot(7, Rig.Session(7, id: 2));
        relocation.Near = Beside;

        var answer = await new TeleportCommands(relocation).TeleAsync(Rig.Session(1001, id: 5), "Alt", default);

        Assert.Equal("Teleported to character 7 in Jita (30000142).", answer);
        var (session, destination) = Assert.Single(relocation.Moves);
        Assert.Equal(5, session);
        Assert.Same(Beside, destination);
    }

    [Fact]
    public async Task Tele_me_moves_the_pilot_to_its_own_place_as_the_destination_near_itself()
    {
        var relocation = new FakeRelocation { Near = Beside };

        var answer = await new TeleportCommands(relocation).TeleAsync(Rig.Session(1001, id: 5), "me", default);

        Assert.Equal("Teleported to character 1001 in Jita (30000142).", answer);
        Assert.Equal(5, Assert.Single(relocation.Moves).Session);
    }
}
