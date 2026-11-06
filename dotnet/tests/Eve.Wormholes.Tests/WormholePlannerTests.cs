using Eve.Universe;

namespace Eve.Wormholes.Tests;

/// <summary>The planner and the lifecycle rules on a small synthetic world (no database).</summary>
public sealed class WormholePlannerTests
{
    // Type 1001: a static of class 1 that leads to high-sec (SDE target 7). Type 1002: a J-space static leading to class 2.
    internal static WormholeType HighSecType => new(1001, 7, 60, 1_000_000_000, 0, 100_000_000, 302);
    internal static WormholeType JSpaceType => new(1002, 2, 60, 1_000_000_000, 0, 100_000_000, 297);

    internal static IReadOnlyDictionary<int, WormholeType> Types() => new Dictionary<int, WormholeType> { [1001] = HighSecType, [1002] = JSpaceType };

    internal static IReadOnlyDictionary<int, int> Classes() => new Dictionary<int, int>
    {
        [31000001] = 1, [31000002] = 2, [31000003] = 2, [30000001] = 7, [30000002] = 7, [30000003] = 7,
    };

    internal static IReadOnlyList<JSpaceSystem> World() =>
    [
        new JSpaceSystem(31000001, "J1", 1, null, null, null,
            [new JSpaceStatic("AAA", 1001), new JSpaceStatic("BBB", 1002)], "test"),
        new JSpaceSystem(31000002, "J2", 2, null, null, null, [], "test"),
        new JSpaceSystem(31000003, "J3", 2, null, null, null, [], "test"),
    ];

    [Fact]
    public void The_seeded_generator_gives_the_same_values_on_every_runtime()
    {
        Assert.Equal(10451216379200822465UL, new SeededRandom(1).NextUInt64());
        Assert.Equal(13679457532755275413UL, new SeededRandom(42).NextUInt64());
    }

    [Fact]
    public void The_same_seed_gives_the_same_connections_and_another_seed_gives_others()
    {
        var first = WormholePlanner.PlanStatics(World(), Classes(), Types(), 99);
        var again = WormholePlanner.PlanStatics(World(), Classes(), Types(), 99);
        Assert.Equal(first, again);

        var destinations = Enumerable.Range(1, 20)
            .Select(seed => WormholePlanner.PlanStatics(World(), Classes(), Types(), (ulong)seed).Single(c => c.Code == "AAA").DestinationSystemId)
            .Distinct()
            .ToArray();
        Assert.True(destinations.Length > 1, "over 20 seeds the high-sec exit should reach more than one system");
    }

    [Fact]
    public void Each_static_leads_to_a_system_of_its_target_class_and_never_to_its_source_or_twice()
    {
        foreach (var seed in Enumerable.Range(1, 50))
        {
            var planned = WormholePlanner.PlanStatics(World(), Classes(), Types(), (ulong)seed);

            Assert.Equal(2, planned.Count);
            var highSec = planned.Single(c => c.Code == "AAA");
            Assert.Contains(highSec.DestinationSystemId, new[] { 30000001, 30000002, 30000003 });
            var jSpace = planned.Single(c => c.Code == "BBB");
            Assert.Contains(jSpace.DestinationSystemId, new[] { 31000002, 31000003 });
            Assert.NotEqual(jSpace.SourceSystemId, jSpace.DestinationSystemId);
            Assert.Equal(planned.Count, planned.Select(c => (c.SourceSystemId, c.DestinationSystemId)).Distinct().Count());
        }
    }

    [Fact]
    public void A_static_with_no_eligible_system_is_not_planned()
    {
        var classes = new Dictionary<int, int> { [31000001] = 1 };

        Assert.Empty(WormholePlanner.PlanStatics(World(), classes, Types(), 5));
    }

    [Fact]
    public void The_initial_mass_is_within_ninety_and_one_hundred_and_ten_percent_of_the_stable_mass()
    {
        foreach (var seed in Enumerable.Range(1, 200))
        {
            foreach (var planned in WormholePlanner.PlanStatics(World(), Classes(), Types(), (ulong)seed))
            {
                var max = Types()[planned.TypeId].MaxStableMassKg;
                Assert.InRange(planned.MassKg, max * 0.9, max * 1.1);
            }
        }
    }

    [Fact]
    public void A_transit_is_refused_over_the_individual_limit_and_otherwise_takes_the_ship_mass()
    {
        var type = JSpaceType;

        Assert.Equal(TransitOutcome.Refused, WormholeLifecycle.Transit(type, 1_000_000_000, 100_000_001).Outcome);
        var passed = WormholeLifecycle.Transit(type, 1_000_000_000, 100_000_000);
        Assert.Equal((TransitOutcome.Passed, 900_000_000d), (passed.Outcome, passed.MassKg));
        Assert.Equal(WormholeMassState.Stable, passed.State);
    }

    [Fact]
    public void A_transit_that_empties_the_wormhole_collapses_it()
    {
        var result = WormholeLifecycle.Transit(JSpaceType, 50_000_000, 100_000_000);

        Assert.Equal((TransitOutcome.Collapsed, 0d, WormholeMassState.Collapsed), (result.Outcome, result.MassKg, result.State));
    }

    [Fact]
    public void A_wormhole_expires_its_stable_time_after_it_opens()
    {
        Assert.Equal(1_000_000 + 60 * 60_000L, WormholeLifecycle.ExpiresAt(1_000_000, JSpaceType));
    }
}
