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

        /// <summary>Where a connected pilot is exactly, by session ID (missing: in no system).</summary>
        public Dictionary<long, GmDestination> Places { get; } = [];

        /// <summary>What a move is answered with: null (done) or the words of a refusal.</summary>
        public string? Refusal { get; set; }

        public List<(long Session, GmDestination Destination)> Moves { get; } = [];

        public Task<GmSolarSystem?> FindSolarSystemAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(Systems.GetValueOrDefault(token));

        public Task<GmPilot?> FindPilotAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(Pilots.GetValueOrDefault(token));

        public Task<GmDestination?> DestinationNearAsync(ClientSession pilot, CancellationToken cancellationToken) => Task.FromResult(Near);

        public Task<GmDestination?> PlaceOfAsync(ClientSession pilot, CancellationToken cancellationToken) =>
            Task.FromResult(Places.GetValueOrDefault(pilot.SessionId));

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

    private static GmDestination InSpace(double x, double y, double z) => new(30000142, "Jita", [x, y, z]);

    [Fact]
    public async Task Tr_without_a_destination_gives_the_usage()
    {
        var relocation = WithJita();

        Assert.Equal(TeleportCommands.TrUsage, await new TeleportCommands(relocation).TrAsync(Rig.Session(1001), "me", default));
        Assert.Equal(TeleportCommands.TrUsage, await new TeleportCommands(relocation).TrAsync(Rig.Session(1001), "me noblock", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Tr_needs_a_selected_character_and_a_known_target()
    {
        var relocation = WithJita();

        Assert.Equal("Select a character before using /tr.", await new TeleportCommands(relocation).TrAsync(Rig.Session(null), "me Jita", default));
        Assert.Equal("Transport target not found: Nobody.", await new TeleportCommands(relocation).TrAsync(Rig.Session(1001), "Nobody Jita", default));
        relocation.Pilots["Alt"] = new GmPilot(7, null);
        Assert.Equal("Character 7 is not currently connected.", await new TeleportCommands(relocation).TrAsync(Rig.Session(1001), "Alt Jita", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Tr_to_a_system_moves_the_caller_to_its_anchor_and_names_it()
    {
        var relocation = WithJita();

        var answer = await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me Jita noblock", default);

        Assert.Equal("Transported me to Jita (30000142).", answer);
        var (session, destination) = Assert.Single(relocation.Moves);
        Assert.Equal(5, session);
        Assert.Equal(30000142, destination.SolarSystemId);
        Assert.Null(destination.Position);
    }

    [Fact]
    public async Task Tr_moves_another_character_not_the_caller()
    {
        var relocation = WithJita();
        relocation.Pilots["Alt"] = new GmPilot(7, Rig.Session(7, id: 2));

        var answer = await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "Alt Jita", default);

        Assert.Equal("Transported character 7 to Jita (30000142).", answer);
        Assert.Equal(2, Assert.Single(relocation.Moves).Session);
    }

    [Fact]
    public async Task Tr_pos_places_the_pilot_in_its_own_system_at_the_point()
    {
        var relocation = WithJita();
        relocation.Places[5] = InSpace(0, 0, 0);

        var answer = await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me pos=1000,2000.5,-3000", default);

        Assert.Equal("Transported me to (1000, 2000.5, -3000).", answer);
        var (session, destination) = Assert.Single(relocation.Moves);
        Assert.Equal(5, session);
        Assert.Equal(30000142, destination.SolarSystemId);
        Assert.Equal(new double[] { 1000, 2000.5, -3000 }, destination.Position!.ToArray());
    }

    [Fact]
    public async Task Tr_raw_coordinates_are_the_same_point()
    {
        var relocation = WithJita();
        relocation.Places[5] = InSpace(0, 0, 0);

        await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me 1 2 3", default);

        Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(relocation.Moves).Destination.Position!.ToArray());
    }

    [Fact]
    public async Task Tr_a_point_with_no_system_context_moves_nobody()
    {
        var relocation = WithJita();

        Assert.Equal("Could not resolve a solar-system context for /tr pos=...",
            await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me pos=1,2,3", default));
        Assert.Equal("Could not resolve a solar-system context for raw /tr coordinates.",
            await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me 1 2 3", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Tr_offset_is_from_the_pilots_own_place()
    {
        var relocation = WithJita();
        relocation.Places[5] = InSpace(100, 200, 300);

        var answer = await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me offset=32000,-1,0", default);

        Assert.Equal("Transported me to (32100, 199, 300).", answer);
        Assert.Equal(new double[] { 32100, 199, 300 }, Assert.Single(relocation.Moves).Destination.Position!.ToArray());
    }

    [Fact]
    public async Task Tr_offset_from_a_docked_pilot_has_no_anchor_and_moves_nobody()
    {
        var relocation = WithJita();
        relocation.Places[5] = new GmDestination(30000142, "Jita");

        Assert.Equal("Could not apply /tr offset=... because the target has no in-space anchor.",
            await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me offset=1,2,3", default));
        Assert.Empty(relocation.Moves);
    }

    [Fact]
    public async Task Tr_offset_from_a_named_location_uses_its_point_and_refuses_a_system()
    {
        var relocation = WithJita();
        relocation.Pilots["Alt"] = new GmPilot(7, Rig.Session(7, id: 2));
        relocation.Places[2] = InSpace(10, 20, 30);

        await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me Alt offset=1,1,1", default);
        Assert.Equal(new double[] { 11, 21, 31 }, Assert.Single(relocation.Moves).Destination.Position!.ToArray());

        Assert.Equal("Could not apply /tr offset=... to that destination.",
            await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me Jita offset=1,1,1", default));
        Assert.Single(relocation.Moves);
    }

    [Fact]
    public async Task Tr_names_a_destination_it_cannot_find()
    {
        var relocation = WithJita();

        Assert.Equal("Transport destination not found: Nowhere.",
            await new TeleportCommands(relocation).TrAsync(Rig.Session(1001, id: 5), "me Nowhere", default));
        Assert.Empty(relocation.Moves);
    }
}
