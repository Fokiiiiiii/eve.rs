using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Npc;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

public class TournamentDroneMetricsTests
{
    [PgFact]
    public async Task Real_drones_use_bandwidth_cap_and_do_not_double_count_deployed_items()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source); Assert.Equal(3396210, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([587, 2454, 2183, 2444, 10246, 23711]);
        const long characterId = 83001, shipId = 83002; const int system = 30000142;
        var character = new Character(characterId, 1, "Pilot", 0, 1, 1, 1, 1, 1373, 1, null, null, 1, 1, system, 1, 1, shipId, DateTimeOffset.UtcNow, null, null);
        var ship = new Item(shipId, 587, characterId, system, 0, 1, true, null);
        var small = new Item(83003, 2454, characterId, shipId, CharacterShips.DroneBayFlag, 5, false, null);
        var medium = new Item(83004, 2183, characterId, shipId, CharacterShips.DroneBayFlag, 2, false, null);
        var deployed = new Item(83005, 2444, characterId, system, CharacterShips.InSpaceFlag, 1, true, null);
        ShipSnapshot Snapshot()
        {
            var world = new DogmaWorld(statics, characterId, shipId);
            world.Add(new DogmaItem(characterId, character.TypeId, characterId, shipId, ShipSnapshot.PilotFlag, ItemState.Online));
            world.Add(new DogmaItem(shipId, ship.TypeId, characterId, system, 0, ItemState.Online));
            world.SetValue(shipId, 1271, 35); world.SetValue(characterId, 352, 3);
            return new ShipSnapshot { Character = character, Ship = ship, Fitted = [], Skills = [], World = world };
        }
        var sources = new TournamentDroneSource[] { new(small, null), new(medium, null), new(deployed, null) };
        var native = new[] { new SimTournamentMetricsCompanion(deployed.Id, deployed.TypeId, shipId, 0, SimTournamentCompanionKind.Drone) };
        var snapshot = Snapshot();
        var value = TournamentDroneMetrics.Calculate(snapshot, statics, sources, native);
        Assert.Null(value.Reason);
        // Pinned fitting greedily takes one 25-Mbit heavy, then one 10-Mbit medium.
        double Dps(Item item)
        {
            var type = statics.Type(item.TypeId)! with { Attributes = snapshot.World.Attributes(item.Id) };
            var weapon = NpcArmament.HullWeapon(type, item.Id, statics)!;
            return weapon.RawDamage.Sum() * 1000 / weapon.CycleMs;
        }
        Assert.Equal(Dps(deployed) + Dps(medium), value.Dps!.Value, 5);
        Assert.Equal("DRONE_IDENTITY_MISMATCH", TournamentDroneMetrics.Calculate(Snapshot(), statics, sources,
            [native[0] with { ControllerShipId = shipId + 1 }]).Reason);
        Assert.Equal("DUPLICATE_DRONE", TournamentDroneMetrics.Calculate(Snapshot(), statics, sources, [native[0], native[0]]).Reason);
        Assert.Equal(0, TournamentDroneMetrics.Calculate(Snapshot(), statics, [], []).Dps);
        var mining = new Item(83006, 10246, characterId, shipId, CharacterShips.DroneBayFlag, 1, false, null);
        Assert.Equal(0, TournamentDroneMetrics.Calculate(Snapshot(), statics, [new(mining, null)], []).Dps);
        var repair = new Item(83007, 23711, characterId, shipId, CharacterShips.DroneBayFlag, 1, false, null);
        Assert.Equal(0, TournamentDroneMetrics.Calculate(Snapshot(), statics, [new(repair, null)], []).Dps);
        var activeRepair = repair with { LocationId = system, Flag = CharacterShips.InSpaceFlag, Singleton = true };
        var companion = new SimTournamentMetricsCompanion(activeRepair.Id, activeRepair.TypeId, shipId, 1, SimTournamentCompanionKind.Drone);
        Assert.Equal("UNSUPPORTED_DRONE_EFFECT", TournamentDroneMetrics.Calculate(Snapshot(), statics, [new(activeRepair, null)], [companion]).Reason);
        Assert.Equal(0, TournamentDroneMetrics.Calculate(Snapshot(), statics, [new(activeRepair, null)], [companion with { Activity = 0 }]).Dps);
    }
}
