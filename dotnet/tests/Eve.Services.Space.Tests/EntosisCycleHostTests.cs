using Eve.Marshal;
using Eve.Space;
using Eve.Services.Space.Entosis;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-9 c through the host's containers (PostgreSQL, the real rig and the fake simulation): a link's cycle is settled by the capture service as the work cycle
/// of its module, the module's end report stops the link, and a pilot outside an alliance is refused by name (nothing is captured).
/// </summary>
public class EntosisCycleHostTests
{
    private const byte Entosis = SimModuleWorkCommands.Entosis;
    private const long Hub = 1_040_000_000_903;
    private const long Alliance = 5;
    private const long CycleMs = EntosisRules.CycleMs;
    private const long Corporation = 98_000_010;

    private static SimModuleWorkCycle LinkCycle(DroneHostTests.Flying flying, ulong cycleId) => new(
        (ulong)flying.Session.SessionId, flying.Character, flying.Session.GetInt("shipid")!.Value, flying.System, flying.Module, Hub, cycleId, Entosis,
        0, 0, 0, 0, 0, 0, 0, 0, 0);

    [PgFact]
    public async Task A_link_cycle_moves_the_capture_of_its_hub_and_the_module_end_stops_it()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: 34_593);
        flying.Session.Set("allianceid", new PyInt(Alliance));
        flying.Session.Set("corpid", new PyInt(Corporation));
        WorkCycleHostTests.StartModule(rig, flying);
        var schema = $"ent_{Guid.NewGuid():N}"[..20];
        await EntosisCaptureService.MigrateAsync(rig.Source, schema);
        try
        {
            var captures = new EntosisCaptureService(rig.Source, rig.Scheduler, () => rig.Now,
                (system, _) => Task.FromResult<EntosisHubReading?>(new(Hub, system, null, 1)), schema: schema);
            rig.Containers.EntosisCaptures = captures;

            var warmUp = await WorkCycleHostTests.SettleAsync(rig, LinkCycle(flying, 1));
            Assert.True(warmUp.Accepted, warmUp.Reason);
            Assert.Equal(0, (await captures.ReadAsync(Hub, CancellationToken.None))!.Progress);

            rig.Now += CycleMs;
            var second = await WorkCycleHostTests.SettleAsync(rig, LinkCycle(flying, 2));
            Assert.True(second.Accepted, second.Reason);
            var moved = await captures.ReadAsync(Hub, CancellationToken.None);
            Assert.Equal(CycleMs / EntosisRules.BaseCaptureMs, moved!.Progress, 9);
            Assert.Single(await captures.LinksAsync(Hub, CancellationToken.None));

            // the module ends (the simulation's end report): the link stops, and the capture is moved to the end
            rig.Now += 100_000;
            rig.Sim.Report(new SimModuleEnded((ulong)flying.Session.SessionId, flying.Module, 1));
            // the end report is handled in the background: poll for the link to go (bounded)
            for (var attempt = 0; attempt < 500 && (await captures.LinksAsync(Hub, CancellationToken.None)).Count > 0; attempt++)
            {
                await Task.Delay(10);
            }
            var ended = await captures.ReadAsync(Hub, CancellationToken.None);
            Assert.Equal((CycleMs + 100_000) / EntosisRules.BaseCaptureMs, ended!.Progress, 9);
        }
        finally
        {
            await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task A_link_cycle_of_a_pilot_outside_an_alliance_is_refused_by_name_and_captures_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: 34_593);
        flying.Session.Set("allianceid", new PyInt(0));
        flying.Session.Set("corpid", new PyInt(Corporation));
        WorkCycleHostTests.StartModule(rig, flying);
        var schema = $"ent_{Guid.NewGuid():N}"[..20];
        await EntosisCaptureService.MigrateAsync(rig.Source, schema);
        try
        {
            var captures = new EntosisCaptureService(rig.Source, rig.Scheduler, () => rig.Now,
                (system, _) => Task.FromResult<EntosisHubReading?>(new(Hub, system, null, 1)), schema: schema);
            rig.Containers.EntosisCaptures = captures;

            var refused = await WorkCycleHostTests.SettleAsync(rig, LinkCycle(flying, 1));

            Assert.False(refused.Accepted);
            Assert.Equal("ALLIANCE_MEMBERSHIP_REQUIRED", refused.Reason);
            Assert.Null(await captures.ReadAsync(Hub, CancellationToken.None));
        }
        finally
        {
            await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
