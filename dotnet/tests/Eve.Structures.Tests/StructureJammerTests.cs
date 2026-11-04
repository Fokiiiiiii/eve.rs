namespace Eve.Structures.Tests;

/// <summary>
/// The cyno jammer's state on PostgreSQL: a service's online time is kept with it, the jammer runs five minutes after it came online, it is per system, and a
/// change is announced in the change's transaction.
/// </summary>
public class StructureJammerTests
{
    private const long Structure = 1001;
    private const int JammerService = StructureServiceIds.CynoJammer;
    private const int System = 30_000_142;

    [PgFact]
    public async Task The_jammer_runs_five_minutes_after_its_service_came_online_and_only_in_its_system()
    {
        await using var rig = await Rig.Create();
        await rig.Working(Structure);
        var onlineAt = rig.Now;
        await rig.Commit((c, t) => rig.Timers.SetServiceAsync(c, t, Structure, JammerService, true));

        var since = await rig.Commit((c, t) => rig.Store.OnlineSinceAsync(c, t, System, JammerService));
        Assert.Equal(onlineAt, since);
        Assert.Equal(StructureJammer.FileTimeOf(onlineAt + StructureJammer.WarmupMs), StructureJammer.StateAt(since, rig.Now));

        rig.Now += StructureJammer.WarmupMs;
        Assert.Equal(0L, StructureJammer.StateAt(since, rig.Now));
        Assert.Null(await rig.Commit((c, t) => rig.Store.OnlineSinceAsync(c, t, System + 1, JammerService)));

        await rig.Commit((c, t) => rig.Timers.SetServiceAsync(c, t, Structure, JammerService, false));
        Assert.Null(await rig.Commit((c, t) => rig.Store.OnlineSinceAsync(c, t, System, JammerService)));
    }

    [PgFact]
    public async Task A_service_change_is_announced_with_its_system_and_online_time()
    {
        await using var rig = await Rig.Create();
        await rig.Working(Structure);
        var received = new List<StructureServiceChanged>();
        rig.Bus.Subscribe("test.services", [StructureTimers.ServiceChangedTopic], (context, _) =>
        {
            received.Add(context.Event.Payload<StructureServiceChanged>()!);
            return Task.CompletedTask;
        });
        await rig.Bus.PumpAsync();

        await rig.Commit((c, t) => rig.Timers.SetServiceAsync(c, t, Structure, JammerService, true));
        await rig.Bus.PumpAsync();

        var change = Assert.Single(received);
        Assert.Equal((Structure, System, JammerService, ServiceState.Online, rig.Now), (change.StructureId, change.SolarSystemId, change.ServiceId, change.State, change.OnlineSinceMs));
    }
}
