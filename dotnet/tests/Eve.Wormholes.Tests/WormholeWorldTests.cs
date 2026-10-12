using Eve.Universe;

namespace Eve.Wormholes.Tests;

/// <summary>The host's view of the world: the ends it places (body IDs, far ends, positions), the generation once, and the transit's capture.</summary>
public sealed class WormholeWorldTests
{
    private static WormholeWorld WorldOf(Rig rig) =>
        new(rig.Source, rig.Scheduler, rig.WormholeSchema, rig.Types, rig.Classes, WormholePlannerTests.World());

    [PgFact]
    public async Task Each_connection_has_two_ends_that_lead_to_each_other_in_their_own_bodies_and_systems()
    {
        await using var rig = await Rig.CreateAsync(WormholePlannerTests.Types(), WormholePlannerTests.Classes());
        var world = WorldOf(rig);
        var id = await rig.OpenAsync(new PlannedConnection(31000001, 30000001, "AAA", 1001, 5e8, 42), isStatic: true);
        var stored = (await rig.ActiveFromAsync(31000001)).Single();

        var near = world.EndOf(stored, far: false);
        var far = world.EndOf(stored, far: true);

        Assert.Equal((id * 2, 31000001, id * 2 + 1, 30000001), (near.BodyId, near.SolarSystemId, near.DestinationEnd, near.DestinationSystemId));
        Assert.Equal((id * 2 + 1, 30000001, id * 2, 31000001), (far.BodyId, far.SolarSystemId, far.DestinationEnd, far.DestinationSystemId));
        Assert.Equal((7, 1), (near.DestinationClass, far.DestinationClass));
        Assert.Equal("Wormhole AAA", near.Name);
        Assert.Equal((988, 1001, WormholeWorld.EndRadiusMeters, 100_000_000d), (near.GroupId, near.TypeId, near.Radius, near.MaxJumpMass));
        Assert.Equal(WormholeWorld.EndPosition(42, far: true), near.DestinationPosition);
        Assert.Equal(WormholeWorld.EndPosition(42, far: false), far.DestinationPosition);
    }

    [Fact]
    public void The_positions_are_fifty_kilometres_from_the_centre_on_the_seeded_bearing_and_the_far_end_uses_another_bearing()
    {
        var near = WormholeWorld.EndPosition(42, far: false);
        var far = WormholeWorld.EndPosition(42, far: true);

        Assert.Equal(WormholeWorld.EndOffsetMeters, Math.Sqrt(near[0] * near[0] + near[2] * near[2]), 6);
        Assert.Equal(0, near[1]);
        Assert.NotEqual(near, far);
        Assert.Equal(near, WormholeWorld.EndPosition(42, far: false));
    }

    [PgFact]
    public async Task The_world_is_generated_once_and_a_captured_collapse_is_not_published_until_the_caller_takes_it()
    {
        await using var rig = await Rig.CreateAsync(WormholePlannerTests.Types(), WormholePlannerTests.Classes());
        var world = WorldOf(rig);
        var published = new List<WormholeChange>();
        world.Changed += published.Add;

        await world.EnsureWorldAsync(7, rig.Now);
        await world.EnsureWorldAsync(7, rig.Now);
        var stored = await rig.ActiveFromAsync(31000001);
        Assert.Equal(2, stored.Count);
        Assert.Empty(published);

        var connection = stored.Single(c => c.TypeId == 1001);
        var captured = new List<WormholeChange>();
        var first = await world.TransitAsync(connection.Id, 5e7, rig.Now, captured.Add);
        Assert.Equal(TransitOutcome.Passed, first.Outcome);
        var refused = await world.TransitAsync(connection.Id, 1e12, rig.Now, captured.Add);
        Assert.Equal(TransitOutcome.Refused, refused.Outcome);
        Assert.Empty(captured);
        Assert.Empty(published);
    }
}
