using Eve.Data;
using Eve.Sde;
using Eve.Universe;
using Npgsql;

namespace Eve.Wormholes.Tests;

/// <summary>The durable lifecycle on PostgreSQL: expiry, replacement of a static, a transit that collapses, and a restart.</summary>
public sealed class WormholeLifecycleTests
{
    private const long Hour = 60 * 60_000L;

    private static PlannedConnection Planned(int source, int destination, string code, int type, double mass, ulong seed) =>
        new(source, destination, code, type, mass, seed);

    [PgFact]
    public async Task A_static_that_expires_is_replaced_from_its_source_to_another_eligible_system()
    {
        await using var rig = await Rig.CreateAsync(WormholePlannerTests.Types(), WormholePlannerTests.Classes());
        var first = await rig.OpenAsync(Planned(31000001, 30000001, "AAA", 1001, 5e8, 7), isStatic: true);

        rig.Now += Hour + 1;
        await rig.Scheduler.RunDueAsync();

        var replacement = Assert.Single(await rig.ActiveFromAsync(31000001));
        Assert.NotEqual(first, replacement.Id);
        Assert.Equal(("AAA", 1001, true), (replacement.Code, replacement.TypeId, replacement.IsStatic));
        Assert.Contains(replacement.DestinationSystemId, new[] { 30000001, 30000002, 30000003 });
        Assert.Equal(rig.Now + Hour, replacement.ExpiresMs);
    }

    [PgFact]
    public async Task A_non_static_that_expires_collapses_for_good()
    {
        await using var rig = await Rig.CreateAsync(WormholePlannerTests.Types(), WormholePlannerTests.Classes());
        await rig.OpenAsync(Planned(31000001, 31000002, "BBB", 1002, 5e8, 3), isStatic: false);

        rig.Now += Hour + 1;
        await rig.Scheduler.RunDueAsync();

        Assert.Empty(await rig.ActiveFromAsync(31000001));
    }

    [PgFact]
    public async Task A_transit_that_empties_a_static_collapses_it_once_and_its_replacement_is_not_doubled()
    {
        await using var rig = await Rig.CreateAsync(WormholePlannerTests.Types(), WormholePlannerTests.Classes());
        var first = await rig.OpenAsync(Planned(31000001, 30000001, "AAA", 1001, 1.5e8, 11), isStatic: true);

        var passed = await rig.TransitAsync(first, 1e8);
        var refused = await rig.TransitAsync(first, 1e8 + 1);
        var collapsed = await rig.TransitAsync(first, 1e8);

        Assert.Equal(TransitOutcome.Passed, passed.Outcome);
        Assert.Equal(5e7, passed.MassKg, 3);
        Assert.Equal(TransitOutcome.Refused, refused.Outcome);
        Assert.Equal(TransitOutcome.Collapsed, collapsed.Outcome);
        var replacement = Assert.Single(await rig.ActiveFromAsync(31000001));
        Assert.NotEqual(first, replacement.Id);

        rig.Now += 1;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(replacement.Id, Assert.Single(await rig.ActiveFromAsync(31000001)).Id);
    }

    [PgFact]
    public async Task The_expiry_job_is_durable_and_runs_after_a_restart()
    {
        await using var rig = await Rig.CreateAsync(WormholePlannerTests.Types(), WormholePlannerTests.Classes());
        await rig.OpenAsync(Planned(31000001, 30000001, "AAA", 1001, 5e8, 21), isStatic: true);

        rig.Restart();
        rig.Now += Hour + 1;
        await rig.Scheduler.RunDueAsync();

        Assert.Single(await rig.ActiveFromAsync(31000001));
    }

    [PgFact]
    public async Task A_transit_before_the_expiry_does_not_run_the_job_early()
    {
        await using var rig = await Rig.CreateAsync(WormholePlannerTests.Types(), WormholePlannerTests.Classes());
        var id = await rig.OpenAsync(Planned(31000001, 30000001, "AAA", 1001, 5e8, 31), isStatic: true);

        rig.Now += Hour - 1;
        await rig.Scheduler.RunDueAsync();

        Assert.Equal(id, Assert.Single(await rig.ActiveFromAsync(31000001)).Id);
    }

    [PgFact]
    public async Task Every_static_of_the_real_world_gets_a_destination_of_its_target_class()
    {
        var jSystems = JSpaceSystems.Load();
        var classes = await WormholeSystemClasses.LoadAllAsync(Environment.GetEnvironmentVariable(ClientStaticData.DirectoryVariable));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var types = await WormholeTypeCatalog.LoadAsync(new SdeReader(source));

        var planned = WormholePlanner.PlanStatics(jSystems, classes, types, 20261012);

        Assert.Equal(jSystems.Sum(s => s.Statics.Count), planned.Count);
        foreach (var connection in planned)
        {
            Assert.Equal(types[connection.TypeId].TargetSystemClass, classes[connection.DestinationSystemId]);
        }
        Assert.Equal(planned.Count, planned.Select(c => (c.SourceSystemId, c.DestinationSystemId)).Distinct().Count());
    }
}
