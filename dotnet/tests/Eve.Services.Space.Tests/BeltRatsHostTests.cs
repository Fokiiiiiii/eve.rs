using Eve.Npc;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>A pilot's warp ends at a belt: the director rolls and the rats of the belt come into space around it (<c>maybeSpawnAsteroidBeltRatsForArrival</c> of the reference).</summary>
public class BeltRatsHostTests
{
    private const long Belt = 40_009_256;
    private static readonly double[] BeltPosition = [3_000_000, -40_000, 12_000_000];

    private static readonly BeltRatConfig Always = BeltRatConfig.Reference with { ChanceHighSec = 1, ChanceLowSec = 1, ChanceNullSec = 1 };

    private static async Task<(Rig Rig, ClientSession Session)> InSpaceAsync(BeltRatConfig? rats)
    {
        var rig = await Rig.CreateAsync(beltRats: rats);
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new Eve.Marshal.PyLong(session.GetInt("shipid")!.Value), Eve.Marshal.PyBool.False);
        return (rig, session);
    }

    private static double Distance(IReadOnlyList<double> a, IReadOnlyList<double> b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());

    [PgFact]
    public async Task An_arrival_that_rolls_a_group_brings_it_into_space_around_the_belt_not_around_the_ship()
    {
        var (rig, session) = await InSpaceAsync(Always);
        await using var _ = rig;

        var decision = await rig.Space.BeltArrivalAsync((ulong)session.SessionId, Belt, BeltPosition);

        Assert.Equal(BeltOutcome.Spawn, decision!.Outcome);
        var plan = decision.Plan!;
        var spawns = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        Assert.Equal(plan.Profiles.Count, spawns.Count);
        Assert.All(spawns, spawn =>
        {
            Assert.Equal(0, spawn.AnchorShipId);
            Assert.Equal("belt rat", spawn.Behavior);
            Assert.Equal(0, spawn.Ship.CharacterId);
            // 30 km from the belt, plus or minus 8 km, plus 1.25 km for each one before it (at most 12 km)
            var away = Distance(spawn.Position, BeltPosition);
            Assert.InRange(away, 22_000 - 1, 30_000 + 8_000 + 12_000 + 1);
        });
        Assert.Equal(plan.Profiles.Select(p => p.ShipTypeId).Order(), spawns.Select(s => s.Ship.TypeId).Order());
        Assert.Equal(plan.Profiles.Count, spawns.Select(s => s.Ship.ShipId).Distinct().Count());
    }

    [PgFact]
    public async Task A_belt_that_has_its_group_is_not_given_another_and_one_whose_scene_was_made_anew_is()
    {
        // no cooldowns, so that only a group that is there can keep another from coming
        var (rig, session) = await InSpaceAsync(Always with { RollCooldownMs = 0, RespawnCooldownMs = 0 });
        await using var _ = rig;
        var id = (ulong)session.SessionId;

        Assert.Equal(BeltOutcome.Spawn, (await rig.Space.BeltArrivalAsync(id, Belt, BeltPosition))!.Outcome);
        var first = rig.Sim.Heard.OfType<SimSpawn>().Count();
        Assert.Equal(BeltOutcome.ActiveGroupPresent, (await rig.Space.BeltArrivalAsync(id, Belt, BeltPosition))!.Outcome);
        Assert.Equal(first, rig.Sim.Heard.OfType<SimSpawn>().Count());
        // another belt has none
        Assert.Equal(BeltOutcome.Spawn, (await rig.Space.BeltArrivalAsync(id, Belt + 1, BeltPosition))!.Outcome);

        // the pilot docks and undocks again: the scene of the system is made anew, and the rats of the old one are not in it
        rig.Sim.Say(new SimDocked(id, Rig.Station, SimCondition.Full));
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new Eve.Marshal.PyLong(session.GetInt("shipid")!.Value), Eve.Marshal.PyBool.False);
        Assert.Equal(BeltOutcome.Spawn, (await rig.Space.BeltArrivalAsync(id, Belt, BeltPosition))!.Outcome);
    }

    [PgFact]
    public async Task The_rats_of_a_belt_that_were_killed_leave_it_free_for_the_respawn_cooldown_to_decide()
    {
        var (rig, session) = await InSpaceAsync(Always with { RollCooldownMs = 0 });
        await using var _ = rig;
        var id = (ulong)session.SessionId;

        await rig.Space.BeltArrivalAsync(id, Belt, BeltPosition);
        var rats = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        foreach (var rat in rats)
        {
            rig.Sim.Say(new SimDestroyed(0, rat.Ship.ShipId, 0, [0, 0, 0], [1, 0, 0], 40, 0));
        }
        // a rat that is destroyed is off the host's books as the simulation says so: the belt has no group, and only the cooldown of the last one is left
        var again = await rig.Space.BeltArrivalAsync(id, Belt, BeltPosition);
        Assert.Equal(BeltOutcome.RespawnCooldown, again!.Outcome);
        rig.Now += 1_200_001;
        Assert.Equal(BeltOutcome.Spawn, (await rig.Space.BeltArrivalAsync(id, Belt, BeltPosition))!.Outcome);
    }

    [PgFact]
    public async Task A_roll_that_misses_a_switched_off_director_and_a_pilot_who_is_not_in_space_bring_nothing()
    {
        var (rig, session) = await InSpaceAsync(Always with { ChanceHighSec = 0, ChanceLowSec = 0, ChanceNullSec = 0 });
        await using var _ = rig;
        Assert.Equal(BeltOutcome.ChanceMiss, (await rig.Space.BeltArrivalAsync((ulong)session.SessionId, Belt, BeltPosition))!.Outcome);
        Assert.Null(await rig.Space.BeltArrivalAsync(42, Belt, BeltPosition));
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());

        var (off, offSession) = await InSpaceAsync(Always with { Enabled = false });
        await using var _off = off;
        Assert.Equal(BeltOutcome.Disabled, (await off.Space.BeltArrivalAsync((ulong)offSession.SessionId, Belt, BeltPosition))!.Outcome);

        var (none, noneSession) = await InSpaceAsync(null);
        await using var _none = none;
        Assert.Null(await none.Space.BeltArrivalAsync((ulong)noneSession.SessionId, Belt, BeltPosition));
        Assert.Empty(off.Sim.Heard.OfType<SimSpawn>().Concat(none.Sim.Heard.OfType<SimSpawn>()));
    }

    [PgFact]
    public async Task What_the_simulation_says_of_an_arrival_is_dealt_with()
    {
        var (rig, session) = await InSpaceAsync(Always);
        await using var _ = rig;

        rig.Sim.Say(new SimArrived((ulong)session.SessionId, Belt, BeltPosition));

        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimSpawn>().Any());
        Assert.All(rig.Sim.Heard.OfType<SimSpawn>(), spawn => Assert.Equal("belt rat", spawn.Behavior));
        // an arrival of a session that is not in space is no one's
        rig.Sim.Say(new SimArrived(99, Belt + 5, BeltPosition));
        await Task.Delay(50);
        Assert.DoesNotContain(rig.Log, line => line.Contains("could not be dealt with"));
    }

    [Fact]
    public void The_environment_changes_what_the_reference_has_and_a_value_that_is_not_one_is_not_taken()
    {
        static BeltRatConfig Config(params (string, string)[] values) => BeltRatConfig.FromEnvironment(name => values.Where(v => v.Item1 == name).Select(v => v.Item2).FirstOrDefault());
        Assert.Equal(BeltRatConfig.Reference, Config());
        Assert.False(Config(("EVE_BELT_RATS", "off")).Enabled);
        Assert.False(Config(("EVE_BELT_RATS", "0")).Enabled);
        Assert.False(Config(("EVE_BELT_RATS", "False")).Enabled);
        Assert.True(Config(("EVE_BELT_RATS", "on")).Enabled);
        var sure = Config(("EVE_BELT_RAT_CHANCE", "1"), ("EVE_BELT_RAT_ROLL_COOLDOWN_MS", "0"), ("EVE_BELT_RAT_RESPAWN_COOLDOWN_MS", "5000.5"));
        Assert.Equal((1d, 1d, 1d, 0L, 5000L), (sure.ChanceHighSec, sure.ChanceLowSec, sure.ChanceNullSec, sure.RollCooldownMs, sure.RespawnCooldownMs));
        Assert.Equal(BeltRatConfig.Reference, Config(("EVE_BELT_RAT_CHANCE", "2"), ("EVE_BELT_RAT_ROLL_COOLDOWN_MS", "-1"), ("EVE_BELT_RAT_RESPAWN_COOLDOWN_MS", "soon")));
        Assert.Equal(BeltRatConfig.Reference, Config(("EVE_BELT_RAT_CHANCE", "NaN")));
    }
}
