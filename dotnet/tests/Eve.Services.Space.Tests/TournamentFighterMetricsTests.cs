using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

public class TournamentFighterMetricsTests
{
    [PgFact]
    public async Task Real_templar_tube_and_native_squadron_size_use_primary_and_secondary()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source); Assert.Equal(3396210, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde); await statics.EnsureTypesAsync([587, 23055, 23069, 37599]);
        const long characterId = 83101, shipId = 83102, fighterId = 83103; const int system = 30000142;
        var character = new Character(characterId, 1, "Pilot", 0, 1, 1, 1, 1, 1373, 1, null, null, 1, 1, system, 1, 1, shipId, DateTimeOffset.UtcNow, null, null);
        var ship = new Item(shipId, 587, characterId, system, 0, 1, true, null);
        var fighter = new Item(fighterId, 23055, characterId, shipId, 159, 9, false, null);
        ShipSnapshot Snapshot(int fighterSkill = 0)
        {
            var world = new DogmaWorld(statics, characterId, shipId);
            world.Add(new DogmaItem(characterId, character.TypeId, characterId, shipId, ShipSnapshot.PilotFlag, ItemState.Online));
            world.Add(new DogmaItem(shipId, ship.TypeId, characterId, system, 0, ItemState.Online));
            if (fighterSkill > 0)
            {
                var skillId = ShipSnapshot.SkillItemId(characterId, 23069);
                world.Add(new DogmaItem(skillId, 23069, characterId, characterId, ShipSnapshot.SkillFlag));
                world.SetValue(skillId, DogmaWorld.SkillLevel, fighterSkill);
            }
            return new ShipSnapshot { Character = character, Ship = ship, Fitted = [], Skills = [], World = world };
        }
        var inTube = TournamentFighterMetrics.Calculate(Snapshot(), statics, [new(fighter, null)], [], []);
        Assert.Null(inTube.Reason);
        Assert.Equal(9 * (97.5 / 5000d + 178.5 / 14000d) * 1000d, inTube.Dps!.Value, 5);
        // The same ItemDogma projection used by launch must retain a rolled source value.
        var mutation = new Eve.Inventory.Mutation.ItemMutationRecord(fighterId, 1, 2, fighter.TypeId, 3, characterId, 1,
            new Dictionary<int, double>(), new Dictionary<int, double>(), new Dictionary<int, double> { [2227] = 195 });
        var modified = TournamentFighterMetrics.Calculate(Snapshot(), statics, [new(fighter, mutation)], [], []);
        Assert.Null(modified.Reason);
        Assert.Equal(9 * (195d / 5000d + 178.5 / 14000d) * 1000d, modified.Dps!.Value, 5);
        var skilled = TournamentFighterMetrics.Calculate(Snapshot(5), statics, [new(fighter, null)], [], []);
        Assert.Null(skilled.Reason);
        Assert.True(skilled.Dps > inTube.Dps);
        var neutralizer = fighter with { TypeId = 37599 };
        var nonDamage = TournamentFighterMetrics.Calculate(Snapshot(), statics, [new(neutralizer, null)], [], []);
        Assert.Null(nonDamage.Reason); Assert.Equal(0, nonDamage.Dps);
        var ewar = new SimTournamentMetricsCompanion(83104, 37599, shipId, 0, SimTournamentCompanionKind.Fighter, 3);
        Assert.True(TournamentFighterMetrics.HasUnobservedControl([ewar]));
        Assert.False(TournamentFighterMetrics.HasUnobservedControl([ewar with { Members = 0 }]));
        Assert.False(TournamentFighterMetrics.HasUnobservedControl([]));
        var airborne = fighter with { LocationId = system, Flag = CharacterShips.InSpaceFlag };
        var flight = new TournamentFighterFlight(fighterId, fighter.TypeId, 159);
        var native = new SimTournamentMetricsCompanion(fighterId, fighter.TypeId, shipId, 1, SimTournamentCompanionKind.Fighter, 4);
        var active = TournamentFighterMetrics.Calculate(Snapshot(), statics, [new(airborne, null)], [flight], [native]);
        Assert.Null(active.Reason);
        Assert.Equal(inTube.Dps.Value * 4 / 9, active.Dps!.Value, 5);
        Assert.Equal("FIGHTER_STATE_UNAVAILABLE", TournamentFighterMetrics.Calculate(Snapshot(), statics, [new(airborne, null)], [flight], [native with { Members = null }]).Reason);
        Assert.Equal(0, TournamentFighterMetrics.Calculate(Snapshot(), statics, [], [flight], [native with { Members = 0 }]).Dps);
        Assert.Equal("FIGHTER_IDENTITY_MISMATCH", TournamentFighterMetrics.Calculate(Snapshot(), statics, [new(airborne, null)], [flight], [native with { TypeId = 999 }]).Reason);
    }
}
