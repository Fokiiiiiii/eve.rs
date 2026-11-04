using Eve.Characters;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>
/// The fighter bay and the launch tubes of a carrier, in the database: a squadron goes from the bay into a tube and back, under the tube rules
/// (<see cref="FighterTubes"/>), and the bay keeps the volume the squadrons in it take. The carrier is <see cref="Rig.FixtureFighterCarrier"/>.
/// </summary>
public class FighterBayTests
{
    /// <summary>A light squadron of the static data (its max size is six).</summary>
    private const int Templar = 23055;

    /// <summary>A heavy squadron of the static data.</summary>
    private const int Shadow = 2948;

    private const int Tritanium = 34;

    private static async Task<Created> CarrierAsync(Rig rig) =>
        await rig.NewCharacterAsync("Carrier Pilot", shipTypeId: Rig.FixtureFighterCarrier);

    private static Task<long> SquadronInBayAsync(Rig rig, Created carrier, int type, long quantity = 2) =>
        rig.GiveAsync(carrier.CharacterId, type, singleton: false, quantity: quantity, location: carrier.ShipId, flag: CharacterShips.FighterBayFlag);

    [PgFact]
    public async Task A_squadron_goes_into_a_tube_and_a_second_one_is_refused_for_the_same_tube()
    {
        await using var rig = await Rig.CreateAsync(staticFixtures: Rig.WithShipFixtures);
        var carrier = await CarrierAsync(rig);
        var first = await SquadronInBayAsync(rig, carrier, Templar);
        var second = await SquadronInBayAsync(rig, carrier, Templar);

        var loaded = await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, first, 159);

        Assert.True(loaded.Accepted);
        Assert.Equal((carrier.ShipId, 159, CharacterShips.FighterBayFlag, 2L),
            (loaded.Change!.After.LocationId, loaded.Change.After.Flag, loaded.Change.PreviousFlag, loaded.Change.After.Quantity));
        var stored = await rig.Items.GetAsync(first);
        Assert.Equal((carrier.ShipId, 159), (stored!.LocationId, stored.Flag));
        Assert.Empty(await rig.Ships.FightersInSpaceAsync(carrier.CharacterId, 30000142));

        var refused = await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, second, 159);
        Assert.Equal(FighterMoveRefusal.TubeOccupied, refused.Refusal);
        Assert.True((await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, second, 160)).Accepted);

        var placed = (await rig.Ships.FightersOfShipAsync(carrier.ShipId)).Select(i => (i.Id, i.Flag)).OrderBy(p => p.Flag).ToList();
        Assert.Equal(new[] { (first, 159), (second, 160) }, placed);
    }

    [PgFact]
    public async Task The_light_and_heavy_slots_of_the_host_fill_and_the_remaining_squadron_is_refused_for_its_class()
    {
        await using var rig = await Rig.CreateAsync(staticFixtures: Rig.WithShipFixtures);
        var carrier = await CarrierAsync(rig);
        var lights = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            lights.Add(await SquadronInBayAsync(rig, carrier, Templar));
        }
        var heavies = new List<long> { await SquadronInBayAsync(rig, carrier, Shadow), await SquadronInBayAsync(rig, carrier, Shadow) };

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, lights[i], 159 + i)).Accepted);
        }
        Assert.Equal(FighterMoveRefusal.ClassSlotsFull,
            (await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, lights[3], 162)).Refusal);
        Assert.True((await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, heavies[0], 162)).Accepted);
        Assert.True((await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, heavies[1], 163)).Accepted);
        // the fourth light squadron is still in the bay: the five tubes hold the other five
        var inTubes = (await rig.Ships.FightersOfShipAsync(carrier.ShipId)).Where(i => FighterTubes.IsTube(i.Flag)).ToList();
        Assert.Equal(5, inTubes.Count);
    }

    [PgFact]
    public async Task A_squadron_outside_the_bay_a_stack_that_is_not_a_squadron_and_a_tube_the_ship_lacks_are_refused()
    {
        await using var rig = await Rig.CreateAsync(staticFixtures: Rig.WithShipFixtures);
        var carrier = await CarrierAsync(rig);
        var hangar = await rig.GiveAsync(carrier.CharacterId, Templar, singleton: false, quantity: 2);
        var notSquadron = await rig.GiveAsync(carrier.CharacterId, Tritanium, singleton: false, quantity: 10, location: carrier.ShipId, flag: CharacterShips.FighterBayFlag);
        var squadron = await SquadronInBayAsync(rig, carrier, Templar);

        Assert.Equal(FighterMoveRefusal.NotInBay, (await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, hangar, 159)).Refusal);
        Assert.Equal(FighterMoveRefusal.NotASquadron, (await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, notSquadron, 159)).Refusal);
        Assert.Equal(FighterMoveRefusal.NoSuchTube, (await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, squadron, 164)).Refusal);
        var stored = await rig.Items.GetAsync(squadron);
        Assert.Equal((carrier.ShipId, CharacterShips.FighterBayFlag), (stored!.LocationId, stored.Flag));
    }

    [PgFact]
    public async Task A_squadron_comes_back_into_the_bay_when_the_bay_has_the_volume_and_an_empty_tube_has_nothing_to_return()
    {
        await using var rig = await Rig.CreateAsync(staticFixtures: Rig.WithShipFixtures);
        var carrier = await CarrierAsync(rig);
        var squadron = await SquadronInBayAsync(rig, carrier, Templar);
        var loaded = await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, squadron, 159);
        Assert.Null(loaded.Refusal);

        var back = await rig.Ships.UnloadTubeToBayAsync(carrier.CharacterId, carrier.ShipId, 159);

        Assert.Null(back.Refusal);
        Assert.Equal((carrier.ShipId, CharacterShips.FighterBayFlag, 159), (back.Change!.After.LocationId, back.Change.After.Flag, back.Change.PreviousFlag));
        Assert.Equal(FighterMoveRefusal.TubeEmpty, (await rig.Ships.UnloadTubeToBayAsync(carrier.CharacterId, carrier.ShipId, 159)).Refusal);
    }

    [PgFact]
    public async Task A_squadron_that_the_bay_cannot_hold_stays_in_its_tube_and_the_refusal_says_how_much_room_there_is()
    {
        await using var rig = await Rig.CreateAsync(staticFixtures: Rig.WithShipFixtures);
        var carrier = await CarrierAsync(rig);
        await rig.Statics.EnsureTypesAsync([Templar]);
        var unitVolume = rig.Statics.Type(Templar)!.Attributes[rig.Statics.AttributeId("volume")!.Value];
        var tooMany = (long)Math.Ceiling(Rig.FighterCarrierBayM3 / unitVolume) + 1;
        var squadron = await SquadronInBayAsync(rig, carrier, Templar, quantity: tooMany);
        Assert.True((await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, squadron, 159)).Accepted);

        var refused = await rig.Ships.UnloadTubeToBayAsync(carrier.CharacterId, carrier.ShipId, 159);

        Assert.Equal(FighterMoveRefusal.BayFull, refused.Refusal);
        Assert.Equal((Rig.FighterCarrierBayM3, true), (refused.Available, refused.Volume > Rig.FighterCarrierBayM3));
        Assert.Equal(159, (await rig.Items.GetAsync(squadron))!.Flag);
    }

    [PgFact]
    public async Task Two_loads_into_one_tube_at_once_let_exactly_one_squadron_in()
    {
        await using var rig = await Rig.CreateAsync(staticFixtures: Rig.WithShipFixtures);
        var carrier = await CarrierAsync(rig);
        var first = await SquadronInBayAsync(rig, carrier, Templar);
        var second = await SquadronInBayAsync(rig, carrier, Templar);

        var results = await Task.WhenAll(
            rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, first, 159),
            rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, second, 159));

        Assert.Single(results, r => r.Accepted);
        Assert.Equal(FighterMoveRefusal.TubeOccupied, results.Single(r => !r.Accepted).Refusal);
        // the squadron that was refused is still in the bay: one squadron in the tube, one in the bay
        var inTube = (await rig.Ships.FightersOfShipAsync(carrier.ShipId)).Where(i => FighterTubes.IsTube(i.Flag)).ToList();
        Assert.Single(inTube);
    }

    [PgFact]
    public async Task The_squadron_in_its_tube_is_still_there_for_a_host_that_starts_again()
    {
        await using var rig = await Rig.CreateAsync(staticFixtures: Rig.WithShipFixtures);
        var carrier = await CarrierAsync(rig);
        var squadron = await SquadronInBayAsync(rig, carrier, Templar);
        Assert.True((await rig.Ships.LoadFighterToTubeAsync(carrier.CharacterId, carrier.ShipId, squadron, 161)).Accepted);

        var restarted = new CharacterShips(rig.Source, rig.Statics, rig.Characters, rig.Items, rig.Skills, rig.Modules);

        var placed = (await restarted.FightersOfShipAsync(carrier.ShipId)).Select(i => (i.Id, i.Flag)).ToList();
        Assert.Equal(new[] { (squadron, 161) }, placed);
    }
}
