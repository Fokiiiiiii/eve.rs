using Eve.Inventory;
using Eve.Marshal;

namespace Eve.Services.Dogma.Tests;

/// <summary><c>/createitem</c> of the game master: a pilot in space gets the items in the cargo hold of the ship, a docked one in the hangar.</summary>
public class CreateItemTests
{
    private const int Gunnery = 3300;
    private const int Turret = 3301;

    [PgFact]
    public async Task In_space_the_items_go_to_the_ship_cargo_and_docked_they_go_to_the_hangar()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Create Pilot");
        var inSpace = Rig.Session(pilot.CharacterId, station: null);
        inSpace.Set("shipid", new PyLong(pilot.ShipId));

        Assert.Equal("2x Small Hybrid Turret was added to your ship's cargo hold.", await rig.SlashAsync(inSpace, "/createitem Small Hybrid Turret 2"));
        var cargo = (await rig.Items.InLocationAsync(pilot.ShipId, pilot.CharacterId)).Where(i => i.Flag == ItemFlags.Cargo && i.TypeId == Turret).ToList();
        Assert.Equal(2, Assert.Single(cargo).Quantity);

        var docked = Rig.Session(pilot.CharacterId);
        Assert.Equal("2x Small Hybrid Turret was added to your station hangar.", await rig.SlashAsync(docked, "/createitem Small Hybrid Turret 2"));
        var hangar = (await rig.Items.InLocationAsync(Rig.Station, pilot.CharacterId)).Where(i => i.Flag == ItemFlags.Hangar && i.TypeId == Turret).ToList();
        Assert.Equal(2, Assert.Single(hangar).Quantity);
    }

    [PgFact]
    public async Task What_cannot_be_done_is_answered_in_the_words_of_the_reference()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Create Refused");
        var nowhere = Rig.Session(pilot.CharacterId, station: null);

        Assert.Equal("Usage: /createitem <item name|typeID> [amount]", await rig.SlashAsync(nowhere, "/createitem"));
        Assert.Equal("You must be docked or in space before using /createitem.", await rig.SlashAsync(nowhere, "/createitem Small Hybrid Turret"));
        Assert.Equal("Item not found: Nonsense Item.", await rig.SlashAsync(Rig.Session(pilot.CharacterId), "/createitem Nonsense Item"));
    }
}
