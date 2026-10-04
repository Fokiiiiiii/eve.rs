using Eve.Inventory;

namespace Eve.Ships.Tests;

public class SpaceTests
{
    [PgFact]
    public async Task Undocking_puts_the_ship_in_the_solar_system_and_keeps_the_station_the_character_left()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var character = (await rig.Characters.GetAsync(c.CharacterId))!;

        var moved = Assert.IsType<SpaceMove>((await rig.Ships.UndockAsync(c.CharacterId)).Moved);

        Assert.Equal((character.SolarSystemId, CharacterShips.InSpaceFlag, Rig.Station, ItemFlags.Hangar), (moved.Ship.LocationId, moved.Ship.Flag, moved.PreviousLocationId, moved.PreviousFlag));
        var stored = (await rig.Items.GetAsync(c.ShipId))!;
        Assert.Equal((character.SolarSystemId, 0), (stored.LocationId, stored.Flag));
        Assert.Equal(Rig.Station, (await rig.Characters.GetAsync(c.CharacterId))!.StationId);
        // what is fitted goes with the ship
        Assert.Equal(3, (await rig.InShipAsync(c.CharacterId, c.ShipId)).Count);
    }

    [PgFact]
    public async Task A_ship_that_is_in_space_cannot_undock_and_a_character_without_a_ship_has_nothing_to_undock()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await rig.Ships.UndockAsync(c.CharacterId);
        Assert.Equal("ALREADY_IN_SPACE", (await rig.Ships.UndockAsync(c.CharacterId)).Refusal);
        Assert.Equal("NO_SHIP", (await rig.Ships.UndockAsync(999)).Refusal);
    }

    [PgFact]
    public async Task A_ship_that_is_not_in_the_hangar_of_the_characters_station_cannot_undock()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, c.ShipId, 60000001, ItemFlags.Hangar);
            await transaction.CommitAsync();
        }
        Assert.Equal("NOT_DOCKED", (await rig.Ships.UndockAsync(c.CharacterId)).Refusal);
    }

    [PgFact]
    public async Task Docking_puts_the_ship_in_the_hangar_of_the_station_and_the_character_there()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await rig.Ships.UndockAsync(c.CharacterId);
        const long other = 60015250;

        var moved = Assert.IsType<SpaceMove>((await rig.Ships.DockAsync(c.CharacterId, other)).Moved);

        Assert.Equal((other, ItemFlags.Hangar, CharacterShips.InSpaceFlag), (moved.Ship.LocationId, moved.Ship.Flag, moved.PreviousFlag));
        var stored = (await rig.Items.GetAsync(c.ShipId))!;
        Assert.Equal((other, ItemFlags.Hangar), (stored.LocationId, stored.Flag));
        Assert.Equal(other, (await rig.Characters.GetAsync(c.CharacterId))!.StationId);
        Assert.Equal(other, moved.Character.StationId);
    }

    [PgFact]
    public async Task A_ship_that_is_docked_cannot_dock()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        Assert.Equal("NOT_IN_SPACE", (await rig.Ships.DockAsync(c.CharacterId, Rig.Station)).Refusal);
        Assert.Equal("NO_SHIP", (await rig.Ships.DockAsync(999, Rig.Station)).Refusal);
    }

    [PgFact]
    public async Task A_ship_left_in_space_is_put_back_where_the_character_last_was_and_only_then()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        Assert.False(await rig.Ships.RecoverFromSpaceAsync(c.CharacterId), "nothing to recover while docked");

        await rig.Ships.UndockAsync(c.CharacterId);
        Assert.True(await rig.Ships.RecoverFromSpaceAsync(c.CharacterId));

        var stored = (await rig.Items.GetAsync(c.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (stored.LocationId, stored.Flag));
        Assert.False(await rig.Ships.RecoverFromSpaceAsync(c.CharacterId));
    }

    [PgFact]
    public async Task A_host_that_starts_puts_back_every_ship_a_host_that_stopped_left_in_space()
    {
        await using var rig = await Rig.CreateAsync();
        var inSpace = await rig.NewCharacterAsync();
        var docked = await rig.NewCharacterAsync("Ship Stayer", account: 2);
        await rig.Ships.UndockAsync(inSpace.CharacterId);
        Assert.Equal(ItemFlags.Hangar, (await rig.Items.GetAsync(docked.ShipId))!.Flag);

        Assert.Equal(1, await rig.Ships.RecoverAllFromSpaceAsync(null));

        var stored = (await rig.Items.GetAsync(inSpace.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (stored.LocationId, stored.Flag));
        // the one that was docked stays where it was, and a second sweep has nothing left to do
        Assert.Equal((Rig.Station, ItemFlags.Hangar), ((await rig.Items.GetAsync(docked.ShipId))!.LocationId, (await rig.Items.GetAsync(docked.ShipId))!.Flag));
        Assert.Equal(0, await rig.Ships.RecoverAllFromSpaceAsync(null));
    }

    [PgFact]
    public async Task Two_undocks_at_once_move_the_ship_once()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rig.Ships.UndockAsync(c.CharacterId)));
        Assert.Equal(1, results.Count(r => r.Moved is not null));
        Assert.All(results.Where(r => r.Moved is null), r => Assert.Equal("ALREADY_IN_SPACE", r.Refusal));
    }
}
