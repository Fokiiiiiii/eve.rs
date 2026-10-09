namespace Eve.Services.Space.Tests;

/// <summary>
/// <see cref="BurstRecipients"/>: the ships a command burst reaches on a cycle. The source always reaches itself; another ship needs a shared fleet or
/// explicit affinity group (or a burst that reaches all ships), and the range is the surface distance plus one metre.
/// </summary>
public class BurstRecipientsTests
{
    private const long Source = 10, Fleet = 20, Friend = 30, Stranger = 40, Carrier = 50;
    private const long SourcePilot = 1, FleetPilot = 2, StrangerPilot = 3;
    private const long Group = 7;

    private static readonly BurstShip Me = new(Source, SourcePilot, 0);

    private static bool SameFleet(long left, long right) => (left, right) is (1, 2) or (2, 1);

    private static IReadOnlyList<long> Reach(IEnumerable<BurstShip> ships, Dictionary<long, double> distance, double range = 15_000,
        bool all = false, Func<BurstShip, bool>? eligible = null, BurstShip? source = null) =>
        BurstRecipients.Resolve(source ?? Me, ships, range, ship => distance.GetValueOrDefault(ship.ShipId, double.PositiveInfinity), SameFleet, all, eligible);

    [Fact]
    public void The_source_reaches_itself_even_at_no_range()
    {
        Assert.Equal([Source], Reach([Me], new Dictionary<long, double>(), range: 0));
    }

    [Fact]
    public void A_fleet_member_in_range_is_reached_and_one_just_outside_is_not_but_a_metre_over_is_allowed()
    {
        var ships = new[] { Me, new BurstShip(Fleet, FleetPilot, 0), new BurstShip(Friend, FleetPilot, 0) };
        var distance = new Dictionary<long, double> { [Fleet] = 15_002, [Friend] = 15_002 };
        Assert.Equal([Source], Reach(ships, distance));

        distance[Fleet] = 15_001;
        Assert.Equal([Source, Fleet], Reach(ships, distance));
    }

    [Fact]
    public void A_ship_of_another_pilot_outside_the_fleet_is_not_reached_by_a_fleet_burst()
    {
        var ships = new[] { Me, new BurstShip(Stranger, StrangerPilot, 0) };
        Assert.Equal([Source], Reach(ships, new Dictionary<long, double> { [Stranger] = 1 }));
    }

    [Fact]
    public void A_shared_explicit_affinity_group_reaches_without_a_fleet()
    {
        var source = new BurstShip(Source, SourcePilot, Group);
        var ships = new[] { source, new BurstShip(Stranger, StrangerPilot, Group) };
        Assert.Equal([Source, Stranger], Reach(ships, new Dictionary<long, double> { [Stranger] = 100 }, source: source));
    }

    [Fact]
    public void A_burst_that_reaches_every_ship_in_range_reaches_a_stranger_in_range_and_not_one_outside()
    {
        var ships = new[] { Me, new BurstShip(Stranger, StrangerPilot, 0), new BurstShip(Carrier, 0, 0) };
        var distance = new Dictionary<long, double> { [Stranger] = 100, [Carrier] = 20_000 };
        Assert.Equal([Source, Stranger], Reach(ships, distance, all: true));
    }

    [Fact]
    public void The_eligibility_rule_removes_a_ship_even_when_it_is_in_the_fleet()
    {
        var ships = new[] { Me, new BurstShip(Fleet, FleetPilot, 0) };
        var distance = new Dictionary<long, double> { [Fleet] = 1 };
        Assert.Equal([Source], Reach(ships, distance, eligible: ship => ship.ShipId != Fleet));
    }

    [Fact]
    public void A_ship_with_no_finite_distance_is_not_reached_and_the_result_is_ordered_by_ship_id()
    {
        var ships = new[]
        {
            new BurstShip(Friend, FleetPilot, 0), Me, new BurstShip(Fleet, FleetPilot, 0),
        };
        var distance = new Dictionary<long, double> { [Friend] = double.NaN, [Fleet] = 5 };
        Assert.Equal([Source, Fleet], Reach(ships, distance));
    }
}
