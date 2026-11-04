using Eve.Inventory;

namespace Eve.Services.Dogma.Tests;

/// <summary><c>/ship</c>: a ship hull made in the docked pilot's station hangar; anything that is not a ship is refused.</summary>
public class ShipCommandTests
{
    [PgFact]
    public async Task A_docked_game_master_gets_the_ship_hull_in_the_station_hangar_and_nothing_else()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Ship Pilot");
        var session = Rig.Session(pilot.CharacterId);
        var before = (await rig.Items.OwnedByAsync(pilot.CharacterId)).Count(item => item.TypeId == Rig.Ibis);

        Assert.Equal("Ibis was added to your ship hangar. /ship only spawns the hull for now; board it manually from the hangar.", await rig.SlashAsync(session, "/ship Ibis"));
        var after = (await rig.Items.OwnedByAsync(pilot.CharacterId)).Where(item => item.TypeId == Rig.Ibis).ToList();
        Assert.Equal(before + 1, after.Count);
        Assert.Contains(after, item => item.LocationId == Station);
        Assert.Equal("Ship not found: Tritanium.", await rig.SlashAsync(session, "/ship Tritanium"));
        Assert.Equal("Usage: /ship <ship name|typeID>", await rig.SlashAsync(session, "/ship"));
    }

    [PgFact]
    public async Task A_pilot_outside_a_station_is_told_to_dock_and_nothing_is_made()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Undocked Pilot");
        var session = Rig.Session(pilot.CharacterId, station: null);
        Assert.Equal("You must be docked before spawning ships into your hangar.", await rig.SlashAsync(session, "/ship Ibis"));
        Assert.DoesNotContain((await rig.Items.OwnedByAsync(pilot.CharacterId)), item => item.TypeId == Rig.Ibis && item.LocationId == Station);
    }

    [PgFact]
    public async Task Repair_answers_what_heal_answers_for_the_docked_pilot()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Repair Pilot");
        var session = Rig.Session(pilot.CharacterId);
        var repaired = await rig.SlashAsync(session, "/repair");
        Assert.Equal(await rig.SlashAsync(session, "/heal"), repaired);
        Assert.DoesNotContain("Unknown command", repaired);
    }

    private const long Station = Rig.Station;
}
