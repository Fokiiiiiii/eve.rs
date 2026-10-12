using Eve.Universe;

namespace Eve.Wormholes.Tests;

/// <summary>Thera's seeded connections: the documented types and count, the free ends in the classes the types name, and the respawn.</summary>
public sealed class TheraConnectionsTests
{
    /// <summary>Twenty systems of each class the types name (the real world has hundreds), and Thera.</summary>
    private static IReadOnlyDictionary<int, int> Classes()
    {
        var classes = new Dictionary<int, int> { [TheraConnections.SystemId] = 12 };
        foreach (var systemClass in new[] { 1, 2, 3, 7, 8, 9 })
        {
            for (var k = 0; k < 20; k++)
            {
                classes[(systemClass < 7 ? 31_000_100 : 30_000_100) + systemClass * 100 + k] = systemClass;
            }
        }
        return classes;
    }

    private static IReadOnlyDictionary<int, WormholeType> Types() =>
        TheraConnections.Types.ToDictionary(type => type.TypeId, type => new WormholeType(type.TypeId, 0, 960, 1e9, 0, 1e8, 0));

    [Fact]
    public void Thera_has_the_eight_documented_types_and_the_dozen_connections_the_page_gives()
    {
        Assert.Equal(12, TheraConnections.Count);
        Assert.Equal(new[] { "F135", "F353", "L031", "M164", "T458", "Q063", "V898", "E587" }, TheraConnections.Types.Select(t => t.Code));
        Assert.Equal(new[] { 2, 3 }, TheraConnections.TypeOf("F135")!.FreeClasses);
    }

    [Fact]
    public void Every_connection_has_thera_at_one_end_and_a_free_end_of_a_class_its_type_names()
    {
        foreach (var seed in Enumerable.Range(1, 40))
        {
            var planned = TheraConnections.Plan(Classes(), Types(), (ulong)seed);

            Assert.Equal(12, planned.Count);
            foreach (var connection in planned)
            {
                var type = TheraConnections.TypeOf(connection.Code)!;
                if (type.FromThera)
                {
                    Assert.Equal(TheraConnections.SystemId, connection.SourceSystemId);
                    Assert.Contains(Classes()[connection.DestinationSystemId], type.FreeClasses);
                }
                else
                {
                    Assert.Equal(TheraConnections.SystemId, connection.DestinationSystemId);
                    Assert.Contains(Classes()[connection.SourceSystemId], type.FreeClasses);
                }
            }
            var free = planned.Select(c => c.SourceSystemId == TheraConnections.SystemId ? c.DestinationSystemId : c.SourceSystemId).ToArray();
            Assert.Equal(free.Length, free.Distinct().Count());
        }
    }

    [Fact]
    public void The_same_seed_gives_the_same_connections()
    {
        Assert.Equal(TheraConnections.Plan(Classes(), Types(), 9), TheraConnections.Plan(Classes(), Types(), 9));
    }

    [PgFact]
    public async Task A_collapsed_thera_connection_is_opened_again_to_a_free_system_of_its_class()
    {
        var types = new Dictionary<int, WormholeType> { [34370] = new(34370, 9, 60, 1e9, 0, 1e8, 0) };
        await using var rig = await Rig.CreateAsync(types, Classes());
        await rig.OpenAsync(new PlannedConnection(TheraConnections.SystemId, 30001000, "E587", 34370, 5e8, 3), isStatic: true);

        rig.Now += 60 * 60_000L + 1;
        await rig.Scheduler.RunDueAsync();

        var touching = await rig.ActiveFromAsync(TheraConnections.SystemId);
        var replacement = Assert.Single(touching);
        Assert.Equal(("E587", TheraConnections.SystemId), (replacement.Code, replacement.SourceSystemId));
        Assert.Equal(9, Classes()[replacement.DestinationSystemId]);
    }
}
