using Eve.Characters;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>The drone bay of a ship in a station (the recording <c>drones.json</c>): it holds drones and nothing else, up to its drone capacity.</summary>
public class DroneBayTests
{
    private const int Hobgoblin = 2454, Tritanium = 34;

    private static async Task<(Created Character, long Drones)> WithDronesAsync(Rig rig, long quantity = 2)
    {
        var c = await rig.NewCharacterAsync();
        var granted = (await rig.Ships.GrantAsync(c.CharacterId, Hobgoblin, quantity, singleton: false))!.Single().After;
        return (c, granted.Id);
    }

    [PgFact]
    public async Task The_drone_bay_of_a_rookie_ship_holds_one_drone_and_what_is_put_in_it_is_split_off_a_stack_as_for_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, drones) = await WithDronesAsync(rig);
        var empty = Assert.IsType<CharacterShips.Room>(await rig.Ships.RoomAsync(c.CharacterId, c.ShipId, CharacterShips.DroneBayFlag));
        Assert.Equal((5d, 0d), (empty.Capacity, empty.Used));

        var changed = Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, drones, Rig.Station, c.ShipId, CharacterShips.DroneBayFlag, 1));

        // the stack that gave (one left in the hangar, two before) and the stack that was made (in the bay), told as stacks that changed
        var stacks = Assert.IsType<List<ItemChanged>>(changed.Stacks);
        Assert.Equal((drones, 1L, 2L, ItemFlags.Hangar), (stacks[0].After.Id, stacks[0].After.Quantity, stacks[0].PreviousQuantity, stacks[0].After.Flag));
        Assert.Equal((c.ShipId, CharacterShips.DroneBayFlag, 1L, 0L), (stacks[1].After.LocationId, stacks[1].After.Flag, stacks[1].After.Quantity, stacks[1].PreviousQuantity));
        Assert.Equal(stacks[1].After.Id, changed.ItemId);
        var full = Assert.IsType<CharacterShips.Room>(await rig.Ships.RoomAsync(c.CharacterId, c.ShipId, CharacterShips.DroneBayFlag));
        Assert.Equal((5d, 5d), (full.Capacity, full.Used));
    }

    [PgFact]
    public async Task A_second_drone_does_not_fit_and_is_refused_with_the_room_that_is_left_and_the_room_it_needs()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, drones) = await WithDronesAsync(rig);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, drones, Rig.Station, c.ShipId, CharacterShips.DroneBayFlag, 1));

        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, drones, Rig.Station, c.ShipId, CharacterShips.DroneBayFlag, 1));

        Assert.Equal((ShipRefusal.NotEnoughDroneBaySpace, Hobgoblin, 0d, 5d), (refused.Reason, refused.ModuleTypeId, refused.Available, refused.Volume));
        Assert.Equal(1, (await rig.Items.GetAsync(drones))!.Quantity); // nothing moved
    }

    [PgFact]
    public async Task What_is_not_a_drone_is_refused_before_the_room_is_counted_and_a_drone_goes_back_to_the_hangar()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, drones) = await WithDronesAsync(rig, quantity: 1);
        var tritanium = (await rig.Ships.GrantAsync(c.CharacterId, Tritanium, 10, singleton: false))!.Single().After.Id;
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, drones, Rig.Station, c.ShipId, CharacterShips.DroneBayFlag, null)); // the bay is full

        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, tritanium, Rig.Station, c.ShipId, CharacterShips.DroneBayFlag, 5));
        Assert.Equal(ShipRefusal.NotADrone, refused.Reason);

        var back = Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, drones, c.ShipId, Rig.Station, ItemFlags.Hangar, 1));
        Assert.Equal((Rig.Station, ItemFlags.Hangar), ((await rig.Items.GetAsync(drones))!.LocationId, (await rig.Items.GetAsync(drones))!.Flag));
        Assert.Equal(drones, back.ItemId);
        Assert.Equal(0d, (await rig.Ships.RoomAsync(c.CharacterId, c.ShipId, CharacterShips.DroneBayFlag))!.Value.Used);
    }
}
