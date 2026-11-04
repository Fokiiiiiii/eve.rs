using Eve.Npc;

namespace Eve.Npc.Tests;

/// <summary>The rats of asteroid belts: which faction, how many and how big for a system's security, and when a belt is rolled again.</summary>
public sealed class BeltRatsTests
{
    private static NpcProfile Profile(string id, int ship = 100, string name = "") => new(id, name.Length > 0 ? name : id, [], "npc", ship, 1000134, 0, 500012, NpcAuthority.RetailEmptyLoadoutId, "", id, -5, 1000, 20_000);

    /// <summary>Two factions' pools, each with a frigate, a destroyer and a cruiser (the cruiser twice, at different weights).</summary>
    private static NpcAuthority Authority(IEnumerable<KeyValuePair<int, string>>? regions = null)
    {
        NpcProfile[] profiles =
        [
            Profile("parity_blood_raider_pulse_frigate", 1), Profile("parity_blood_raider_pulse_destroyer", 2), Profile("parity_blood_raider_pulse_cruiser", 3),
            Profile("parity_blood_raider_beam_cruiser", 4), Profile("parity_blood_raider_beam_battle_cruiser", 5), Profile("parity_blood_raider_pulse_battleship", 9), Profile("parity_guristas_rail_frigate", 6),
            Profile("parity_guristas_missile_frigate", 7), Profile("parity_blood_raider_hollow_frigate", 8),
        ];
        NpcPool[] pools =
        [
            new("blood", "Blood", [], "npc", [new("parity_blood_raider_pulse_frigate", 3), new("parity_blood_raider_pulse_destroyer", 2), new("parity_blood_raider_pulse_cruiser", 1),
                new("parity_blood_raider_beam_cruiser", 3), new("parity_blood_raider_beam_battle_cruiser", 2), new("parity_blood_raider_pulse_battleship", 1), new("parity_blood_raider_hollow_frigate", 4)]),
            new("guristas", "Guristas", [], "npc", [new("parity_guristas_rail_frigate", 1), new("parity_guristas_missile_frigate", 1)]),
        ];
        return new NpcAuthority(profiles, [], pools).WithPirateRegions(regions ?? [new(10000012, "blood"), new(10000002, "guristas")]);
    }

    private sealed class Dice(params double[] values)
    {
        private readonly Queue<double> _values = new(values);

        public double Next() => _values.Dequeue();

        public int Left => _values.Count;
    }

    private static readonly BeltRatConfig Always = BeltRatConfig.Reference with { ChanceHighSec = 1, ChanceLowSec = 1, ChanceNullSec = 1 };

    private static BeltSystem System(double security, int region = 10000012, int faction = 500007, int id = 30000142) => new(id, security, region, faction);

    private static string[] Ids(BeltDecision decision) => [.. decision.Plan!.Profiles.Select(p => p.Id)];

    [Fact]
    public void A_safe_system_takes_one_frigate_and_a_less_safe_one_adds_destroyers_and_more_rats_as_the_security_falls()
    {
        var rats = new BeltRats(Authority(), Always, null);
        // 0.8 and above: one frigate (no roll for the count when the band has one number; the hull, the profile, the chance)
        var high = rats.Decide(System(0.9), 1, 0, 0, new Dice(0.0, 0.0, 0.0).Next);
        Assert.Equal((BeltOutcome.Spawn, "highsec"), (high.Outcome, high.Band));
        Assert.Single(high.Plan!.Profiles);
        // 0.5: one or two, frigates and destroyers
        var mid = new BeltRats(Authority(), Always).Decide(System(0.5), 1, 0, 0, new Dice(0.0, 0.99, 0.0, 0.0).Next);
        Assert.Equal("parity_blood_raider_pulse_destroyer", Ids(mid)[0]);
        Assert.Equal("highsec", mid.Band);
        // 0.1 to 0.45 is lowsec (two or three), 0 to 0.1 lowsec (two to four), -0.45 and below nullsec (three to four), further down three to five
        var bands = new[] { (0.44, "lowsec"), (0.1, "lowsec"), (0.09, "lowsec"), (0.0, "lowsec"), (-0.01, "nullsec"), (-0.45, "nullsec"), (-0.46, "nullsec"), (-1.0, "nullsec") };
        foreach (var (security, band) in bands)
        {
            var decision = new BeltRats(Authority(), Always).Decide(System(security), 1, 0, 0, () => 0.0);
            Assert.Equal(band, decision.Band);
        }
    }

    [Fact]
    public void A_band_is_reached_at_its_limit_and_not_before()
    {
        int Count(double security, double roll) => new BeltRats(Authority(), Always).Decide(System(security), 1, 0, 0, () => roll).Plan!.Profiles.Count;
        // 0.8: one frigate however the count rolls; just below it one or two
        Assert.Equal((1, 2), (Count(0.8, 0.99), Count(0.7999, 0.99)));
        // 0.45: one or two; just below, lowsec's two or three
        Assert.Equal((1, 2), (Count(0.45, 0.0), Count(0.4499, 0.0)));
        // 0.1: two or three; just below, two to four
        Assert.Equal((3, 4), (Count(0.1, 0.99), Count(0.0999, 0.99)));
        // 0: two to four; just below, nullsec's three to four
        Assert.Equal((2, 3), (Count(0.0, 0.0), Count(-0.0001, 0.0)));
        // -0.45: three or four; just below, three to five
        Assert.Equal((4, 5), (Count(-0.45, 0.99), Count(-0.4501, 0.99)));
    }

    [Fact]
    public void The_count_is_the_least_the_band_gives_plus_what_the_roll_adds_and_at_most_the_most()
    {
        // lowsec 0.3: two or three rats
        var two = new BeltRats(Authority(), Always).Decide(System(0.3), 1, 0, 0, () => 0.0);
        Assert.Equal(2, two.Plan!.Profiles.Count);
        var three = new BeltRats(Authority(), Always).Decide(System(0.3), 1, 0, 0, () => 0.99);
        Assert.Equal(3, three.Plan!.Profiles.Count);
        // nullsec -1: three to five
        Assert.Equal(5, new BeltRats(Authority(), Always).Decide(System(-1), 1, 0, 0, () => 0.99).Plan!.Profiles.Count);
    }

    [Fact]
    public void The_faction_is_the_one_that_owns_the_system_else_the_one_that_holds_the_region_else_the_one_beside_the_empire_and_else_any()
    {
        var rats = new BeltRats(Authority(), Always);
        // a system a pirate faction owns beats the region (500010 Guristas in a region that Blood holds)
        Assert.Equal("guristas", rats.FactionOf(System(0.5, region: 10000012, faction: 500010), () => 0));
        Assert.Equal("blood", rats.FactionOf(System(0.5, region: 10000012, faction: 500007), () => 0));
        // no region: the pirate beside the empire the system belongs to
        Assert.Equal("serpentis", rats.FactionOf(System(0.5, region: 1, faction: 500004), () => 0));
        Assert.Equal("angels", rats.FactionOf(System(0.5, region: 1, faction: 500002), () => 0));
        Assert.Equal("sanshas", rats.FactionOf(System(0.5, region: 1, faction: 500008), () => 0));
        // none: one of the six, by the roll
        string[] any = ["guristas", "serpentis", "angels", "blood", "sanshas", "rogue_drones"];
        Assert.Equal(any, any.Select((_, i) => rats.FactionOf(System(0.5, region: 1, faction: 0), () => (i + 0.5) / any.Length)));
    }

    [Fact]
    public void A_profile_is_chosen_by_hull_class_from_the_pool_of_the_faction_by_its_weight_and_a_hull_nothing_can_hurt_is_never_chosen()
    {
        var rats = new BeltRats(Authority(), Always);
        // lowsec 0.5? no: highsec 0.9 takes frigates: the blood pool has two (pulse, hollow), weighted 3 and 4
        Assert.Equal("parity_blood_raider_pulse_frigate", Ids(rats.Decide(System(0.9), 1, 0, 0, () => 0.0))[0]);
        Assert.Equal("parity_blood_raider_hollow_frigate", Ids(new BeltRats(Authority(), Always).Decide(System(0.9), 1, 0, 0, () => 0.99))[0]);
        // the hollow frigate (ship type 8) has no hit points in the static data
        var hollow = new BeltRats(Authority(), Always, type => type != 8);
        Assert.All(Enumerable.Range(0, 10), i => Assert.Equal("parity_blood_raider_pulse_frigate", Ids(new BeltRats(Authority(), Always, type => type != 8).Decide(System(0.9, id: 30000142 + i), 1, 0, 0, () => 0.99))[0]));
        Assert.Equal(BeltOutcome.Spawn, hollow.Decide(System(0.9), 1, 0, 0, () => 0.0).Outcome);
    }

    [Fact]
    public void A_battle_cruiser_is_a_battlecruiser_and_a_hull_class_is_a_word()
    {
        Assert.True(BeltRats.MatchesHull(Profile("parity_blood_raider_beam_battle_cruiser"), "battlecruiser"));
        Assert.True(BeltRats.MatchesHull(Profile("parity_blood_raider_beam_battlecruiser"), "battlecruiser"));
        Assert.False(BeltRats.MatchesHull(Profile("parity_blood_raider_beam_cruiser"), "battlecruiser"));
        Assert.True(BeltRats.MatchesHull(Profile("parity_blood_raider_beam_cruiser"), "cruiser"));
        Assert.False(BeltRats.MatchesHull(Profile("parity_blood_raider_beam_battlecruiser"), "cruiser"));
        Assert.False(BeltRats.MatchesHull(Profile("a", name: "Frigates of nobody"), "frigate"));
        Assert.True(BeltRats.MatchesHull(Profile("a", name: "Blood Frigate"), "frigate"));
    }

    [Fact]
    public void A_roll_at_or_above_the_chance_finds_nothing_and_what_is_below_it_spawns()
    {
        var chance = BeltRatConfig.Reference with { ChanceHighSec = 0.25 };
        // the hull, the profile, then the chance (a band of one number rolls no count): 0.25 is not below 0.25
        var miss = new BeltRats(Authority(), chance).Decide(System(0.9), 1, 0, 0, new Dice(0.0, 0.0, 0.25).Next);
        Assert.Equal(BeltOutcome.ChanceMiss, miss.Outcome);
        var hit = new BeltRats(Authority(), chance).Decide(System(0.9), 1, 0, 0, new Dice(0.0, 0.0, 0.2499).Next);
        Assert.Equal(BeltOutcome.Spawn, hit.Outcome);
    }

    [Fact]
    public void A_belt_has_one_group_at_a_time_waits_a_cooldown_after_a_roll_and_longer_after_a_group()
    {
        var rats = new BeltRats(Authority(), BeltRatConfig.Reference with { ChanceHighSec = 0 });
        var system = System(0.9);

        // a roll that finds nothing: rolled again only after two minutes
        Assert.Equal(BeltOutcome.ChanceMiss, rats.Decide(system, 1, 1_000_000, 0, () => 0.5).Outcome);
        Assert.Equal(BeltOutcome.RollCooldown, rats.Decide(system, 1, 1_000_000 + 119_999, 0, () => 0.5).Outcome);
        Assert.Equal(BeltOutcome.ChanceMiss, rats.Decide(system, 1, 1_000_000 + 120_000, 0, () => 0.5).Outcome);
        // another belt of the same system is its own
        Assert.Equal(BeltOutcome.ChanceMiss, rats.Decide(system, 2, 1_000_000 + 1, 0, () => 0.5).Outcome);

        // a group was spawned: none before twenty minutes, none while one is there
        var always = new BeltRats(Authority(), Always);
        Assert.Equal(BeltOutcome.Spawn, always.Decide(system, 1, 5_000_000, 0, () => 0.0).Outcome);
        always.Spawned(system.SolarSystemId, 1, 5_000_000);
        Assert.Equal(BeltOutcome.ActiveGroupPresent, always.Decide(system, 1, 5_000_000 + 10 * 60_000, 1, () => 0.0).Outcome);
        Assert.Equal(BeltOutcome.RespawnCooldown, always.Decide(system, 1, 5_000_000 + 1_199_999, 0, () => 0.0).Outcome);
        Assert.Equal(BeltOutcome.Spawn, always.Decide(system, 1, 5_000_000 + 1_200_000, 0, () => 0.0).Outcome);
    }

    [Fact]
    public void Wormhole_space_and_a_switched_off_server_have_no_rats_and_a_faction_without_a_fitting_profile_makes_no_group()
    {
        Assert.Equal(BeltOutcome.NotEligible, new BeltRats(Authority(), Always).Decide(System(-0.99, id: 31_000_005), 1, 0, 0, () => 0.0).Outcome);
        Assert.Equal(BeltOutcome.Disabled, new BeltRats(Authority(), BeltRatConfig.Reference with { Enabled = false }).Decide(System(0.9), 1, 0, 0, () => 0.0).Outcome);
        // Guristas has frigates only: no cruiser (the last hull of lowsec at the highest roll) is there
        var none = new BeltRats(Authority(), Always).Decide(System(0.2, region: 10000002, faction: 500007), 1, 0, 0, () => 0.99);
        Assert.Equal((BeltOutcome.PlanFailed, "guristas"), (none.Outcome, none.Faction));
        // a roll that made no group is not a roll: the belt is not made to wait for the next one
        var rats = new BeltRats(Authority(), Always);
        Assert.Equal(BeltOutcome.PlanFailed, rats.Decide(System(0.2, region: 10000002, faction: 500007), 1, 1_000, 0, () => 0.99).Outcome);
        Assert.Equal(BeltOutcome.PlanFailed, rats.Decide(System(0.2, region: 10000002, faction: 500007), 1, 1_001, 0, () => 0.99).Outcome);
    }

    [Fact]
    public void The_authored_pools_have_rats_of_every_faction_for_every_hull_the_bands_ask_for()
    {
        var authority = NpcAuthority.Load();
        Assert.True(authority.PirateOfRegion(10000002) is "guristas");
        // every band, every faction the bands can name, with a roll that is the first and one that is the last
        string[] factions = ["guristas", "serpentis", "angels", "blood", "sanshas"];
        foreach (var (security, region) in new[] { (0.9, 0), (0.5, 0), (0.3, 0), (0.05, 0), (-0.2, 0), (-0.9, 0) })
        {
            foreach (var faction in factions)
            {
                var regionOf = authority.Pools.Count > 0 ? PirateRegionOf(authority, faction) : region;
                foreach (var roll in new[] { 0.0, 0.99 })
                {
                    var decision = new BeltRats(authority, Always).Decide(System(security, regionOf, 0), 1, 0, 0, () => roll);
                    Assert.True(decision.Outcome == BeltOutcome.Spawn, $"{faction} at {security}: {decision.Outcome}");
                    Assert.All(decision.Plan!.Profiles, p => Assert.True(p.FactionId == FactionIds[faction], $"{faction} at {security} roll {roll}: {p.Id} of faction {p.FactionId}"));
                }
            }
        }
    }

    private static readonly Dictionary<string, int> FactionIds = new() { ["guristas"] = 500010, ["angels"] = 500011, ["blood"] = 500012, ["sanshas"] = 500019, ["serpentis"] = 500020 };

    private static int PirateRegionOf(NpcAuthority authority, string faction) =>
        new[] { 10000002, 10000006, 10000060, 10000022, 10000058 }.First(region => authority.PirateOfRegion(region) == faction);
}
