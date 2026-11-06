using Eve.Space;

namespace Eve.Services.Space.Tests;

public class TournamentRunningRaceTests
{
    [PgFact]
    public async Task Old_failed_activation_cannot_remove_newer_running_generation()
    {
        await using var rig=await Rig.CreateAsync();
        var (session, _, module)=await RepairHostTests.InSpaceWithAsync(rig,523,11);
        var sessionId=(ulong)session.SessionId;
        var firstGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Sim.ActivateGate=firstGate;
        var first=rig.Space.ActivateAsync(session,module,"armorRepair",0,1,CancellationToken.None);
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimActivate>().Any(x=>x.Effect.ModuleId==module));
        rig.Sim.Say(new SimModuleEnded(sessionId,module,SimModuleEndReasons.Done));
        await Rig.UntilAsync(() => !rig.Space.IsModuleActive(sessionId,module));
        var secondGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Sim.ActivateGate=secondGate;
        var second=rig.Space.ActivateAsync(session,module,"armorRepair",0,1,CancellationToken.None);
        await Rig.UntilAsync(() => rig.Space.TournamentRunningForTest(sessionId).ContainsKey(module));
        rig.Sim.ActivateAnswer=new CommandReply(false,0,"REFUSED");
        firstGate.SetResult();
        var failed=await first;
        Assert.NotNull(failed); Assert.False(failed.Ok);
        Assert.True(rig.Space.IsModuleActive(sessionId,module));
        Assert.Contains(module,rig.Space.TournamentRunningForTest(sessionId).Keys);
        rig.Sim.ActivateAnswer=new CommandReply(true,0,null);
        secondGate.SetResult();
        var accepted=await second;
        Assert.NotNull(accepted); Assert.True(accepted.Ok,accepted.Reason);
        Assert.True(rig.Space.IsModuleActive(sessionId,module));
        Assert.Contains(module,rig.Space.TournamentRunningForTest(sessionId).Keys);
    }

    [PgFact]
    public async Task Ended_before_activation_reply_does_not_resurrect_running_module()
    {
        await using var rig=await Rig.CreateAsync();
        var (session, _, module)=await RepairHostTests.InSpaceWithAsync(rig,523,11);
        var sessionId=(ulong)session.SessionId;
        rig.Sim.ActivateGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation=rig.Space.ActivateAsync(session,module,"armorRepair",0,1,CancellationToken.None);
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimActivate>().Any(x=>x.Effect.ModuleId==module));
        Assert.True(rig.Space.IsModuleActive(sessionId,module));
        rig.Sim.Say(new SimModuleEnded(sessionId,module,SimModuleEndReasons.Done));
        await Rig.UntilAsync(() => !rig.Space.IsModuleActive(sessionId,module));
        rig.Sim.ActivateGate.SetResult();
        Assert.NotNull(await activation);
        Assert.False(rig.Space.IsModuleActive(sessionId,module));
        Assert.DoesNotContain(module,rig.Space.TournamentRunningForTest(sessionId).Keys);
    }
}
