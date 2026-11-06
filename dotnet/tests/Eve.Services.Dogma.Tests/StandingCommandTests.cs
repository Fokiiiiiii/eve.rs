using Eve.Marshal;
using Eve.Relations;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

public sealed class FakeStandingDirectory : IStandingDirectory
{
    public const long CaldariState = 500001, CaldariCorp = 1000035, OtherAgency = 1000044;

    public Dictionary<string, long> Characters { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly StandingOwner[] Owners =
    [
        new(CaldariState, "Caldari State", true),
        new(CaldariCorp, "Caldari Test Corp", IsCorporation: true),
        new(OtherAgency, "Other Agency", IsCorporation: true),
    ];

    public Task<StandingOwner?> FindOwnerAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(Owners.Cast<StandingOwner?>().FirstOrDefault(owner =>
            owner!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == token || owner.Name.Equals(token, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<StandingOwner>> AgentOwnersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StandingOwner>>([Owners[0], Owners[1], Owners[2]]);

    public Task<long?> FindCharacterAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(Characters.TryGetValue(token, out var id) ? (long?)id
            : long.TryParse(token, out var known) && Characters.Values.Contains(known) ? known : null);

    public Task<string?> CharacterNameAsync(long characterId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>($"Pilot {characterId}");
}

public class StandingCommandTests
{
    [PgFact]
    public async Task A_game_master_sets_a_pilots_standing_toward_a_corporation_and_a_faction_and_the_pilot_is_told_each_change()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Standing Pilot");
        rig.StandingDirectory.Characters["Standing Pilot"] = pilot.CharacterId;
        var session = Rig.Session(pilot.CharacterId);

        Assert.Equal($"Set standing with Caldari Test Corp(1000035) to 5.00 for {"Pilot " + pilot.CharacterId}({pilot.CharacterId}).",
            await rig.SlashAsync(session, "/setstanding 5 1000035"));
        Assert.Equal(5.0, await rig.Relations.ValueAsync(RelationKinds.Standing.Name, FakeStandingDirectory.CaldariCorp, pilot.CharacterId));
        Assert.Equal((pilot.CharacterId, FakeStandingDirectory.CaldariCorp, 5.0), Assert.Single(rig.StandingNotices));

        var other = await rig.NewCharacterAsync("Second Pilot");
        rig.StandingDirectory.Characters["Second Pilot"] = other.CharacterId;
        Assert.Equal($"Set standing with Caldari State(500001) to -2.50 for {"Pilot " + other.CharacterId}({other.CharacterId}).",
            await rig.SlashAsync(session, "/setstanding -2.5 \"Caldari State\" \"Second Pilot\""));
        Assert.Equal(-2.5, await rig.Relations.ValueAsync(RelationKinds.Standing.Name, FakeStandingDirectory.CaldariState, other.CharacterId));
        Assert.Equal(0.0, await rig.Relations.ValueAsync(RelationKinds.Standing.Name, FakeStandingDirectory.CaldariState, pilot.CharacterId));
        Assert.Equal((other.CharacterId, FakeStandingDirectory.CaldariState, -2.5), rig.StandingNotices[^1]);
    }

    [PgFact]
    public async Task The_client_channel_tool_sends_the_owner_first_and_the_standing_is_set_toward_the_named_pilot()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Owner First Pilot");
        rig.StandingDirectory.Characters["Owner First Pilot"] = pilot.CharacterId;
        var session = Rig.Session(pilot.CharacterId);

        Assert.StartsWith("Set standing with Caldari Test Corp(1000035) to 3.00 for",
            await rig.SlashAsync(session, $"/setstanding \"{FakeStandingDirectory.CaldariCorp}\" {pilot.CharacterId} 3 \"Mass standing change\""));
        Assert.Equal(3.0, await rig.Relations.ValueAsync(RelationKinds.Standing.Name, FakeStandingDirectory.CaldariCorp, pilot.CharacterId));
        Assert.Equal((pilot.CharacterId, FakeStandingDirectory.CaldariCorp, 3.0), rig.StandingNotices[^1]);
    }

    [PgFact]
    public async Task Setting_the_same_standing_again_writes_and_tells_nothing_new()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Repeat Pilot");
        var session = Rig.Session(pilot.CharacterId);
        await rig.SlashAsync(session, "/setstanding 3 1000035");
        Assert.Single(rig.StandingNotices);

        Assert.Equal($"Set standing with Caldari Test Corp(1000035) to 3.00 for {"Pilot " + pilot.CharacterId}({pilot.CharacterId}).", await rig.SlashAsync(session, "/setstanding 3 1000035"));
        Assert.Single(rig.StandingNotices);
        Assert.Equal(3.0, await rig.Relations.ValueAsync(RelationKinds.Standing.Name, FakeStandingDirectory.CaldariCorp, pilot.CharacterId));
    }

    [PgFact]
    public async Task A_value_outside_minus_ten_to_ten_an_unknown_owner_and_a_missing_argument_change_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Refused Pilot");
        var session = Rig.Session(pilot.CharacterId);

        Assert.Equal("Standing value must be a number between -10.0 and 10.0.", await rig.SlashAsync(session, "/setstanding 11 1000035"));
        Assert.Equal("Standing value must be a number between -10.0 and 10.0.", await rig.SlashAsync(session, "/setstanding abc 1000035"));
        Assert.Equal("Usage: /setstanding <value> <owner name|id> [target]", await rig.SlashAsync(session, "/setstanding 1"));
        Assert.Equal("Could not resolve a standing owner from \"nobody\".", await rig.SlashAsync(session, "/setstanding 2 nobody"));
        Assert.Empty(rig.StandingNotices);
        Assert.Equal(0.0, await rig.Relations.ValueAsync(RelationKinds.Standing.Name, FakeStandingDirectory.CaldariCorp, pilot.CharacterId));

        Assert.Equal("Select a character before using /setstanding.", await rig.SlashAsync(Rig.Session(null), "/setstanding 2 1000035"));
        var plain = Rig.Session(pilot.CharacterId);
        plain.Set("accountRole", new PyLong(0));
        Assert.Equal(SlashService.NotGameMaster, await rig.SlashAsync(plain, "/setstanding 2 1000035"));
    }

    [PgFact]
    public async Task Fullstandings_sets_every_agent_owner_to_ten_once_and_the_maxagentstandings_alias_is_the_same()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Agent Pilot");
        var session = Rig.Session(pilot.CharacterId);

        Assert.Equal($"Set 2 agent corporation standings and 1 agent faction standings to 10.00 for {"Pilot " + pilot.CharacterId}({pilot.CharacterId}).",
            await rig.SlashAsync(session, "/fullstandings"));
        foreach (var owner in new long[] { FakeStandingDirectory.CaldariState, FakeStandingDirectory.CaldariCorp, FakeStandingDirectory.OtherAgency })
            Assert.Equal(10.0, await rig.Relations.ValueAsync(RelationKinds.Standing.Name, owner, pilot.CharacterId));
        Assert.Equal(3, rig.StandingNotices.Count);

        Assert.Equal($"Set 2 agent corporation standings and 1 agent faction standings to 10.00 for {"Pilot " + pilot.CharacterId}({pilot.CharacterId}).",
            await rig.SlashAsync(session, "/maxagentstandings"));
        Assert.Equal(3, rig.StandingNotices.Count);
        Assert.Equal("Character not found: nobody.", await rig.SlashAsync(session, "/maxagentstandings nobody"));
    }
}
