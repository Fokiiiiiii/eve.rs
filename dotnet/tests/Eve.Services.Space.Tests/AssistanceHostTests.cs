using Eve.Space;

namespace Eve.Services.Space.Tests;

public class AssistanceHostTests
{
    [PgFact]
    public async Task Remote_sensor_boost_is_refused_before_simulation_when_the_target_disallows_assistance()
    {
        const int RemoteSensorBoosterI = 1963;
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        var (session, _, module) = await RepairHostTests.InSpaceWithAsync(rig, RemoteSensorBoosterI, 19);

        var report = await rig.Space.SpawnNpcsAsync(session, "Blood Visionary", 1, CancellationToken.None);
        Assert.Null(report.Error);
        Assert.Equal(1, report.Spawned);
        var npc = Assert.Single(rig.Sim.Heard.OfType<SimSpawn>());
        Assert.Equal(23245, npc.Ship.TypeId);
        Assert.Equal(1d, rig.Statics.Type(23245)!.Attributes[854]);

        var simActivationsBefore = rig.Sim.Heard.OfType<SimActivate>().Count();
        var result = await rig.Space.ActivateAsync(
            session, module, "remoteSensorBoostFalloff", npc.Ship.ShipId, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Refusal("REFUSED", RemoteSensorBoosterI, "Remote Sensor Booster I"), result);
        Assert.Equal(simActivationsBefore, rig.Sim.Heard.OfType<SimActivate>().Count());
    }
}
