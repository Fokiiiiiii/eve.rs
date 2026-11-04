using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Space;
using Eve.Services;
using Eve.Ships;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public async Task Tournament_cutoff_waits_for_actual_host_remote_repair_result()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true };
        start.Environment["EVE_SIM_LISTEN"]="127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"]=Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process=System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulator failed to start.");
        try
        {
            string? endpoint=null; using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ",StringComparison.Ordinal)) { endpoint=line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint); _=process.StandardError.ReadToEndAsync(); _=process.StandardOutput.ReadToEndAsync();
            await using var sim=SimClient.ToTcpSocket(endpoint); sim.Start();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (!sim.IsConnected) await Task.Delay(20,timeout.Token);
            await using var rig=await Rig.Create(sim); rig.Now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var healerRun=await rig.CreateRun(enter:false); var targetRun=await rig.CreateRun(enter:false);
            var statics=await DogmaStatic.LoadAsync(rig.Sde);
            var effect=Assert.Single(statics.Effects, candidate => candidate.Name.Equals("ShipModuleRemoteArmorMutadaptiveRepairer",StringComparison.OrdinalIgnoreCase));
            var groups=(await rig.Sde.ReadAsync("groups")).Where(row => row.Doc.GetProperty("categoryID").GetInt32()==7).Select(row=>row.Key).ToArray();
            var types=(await rig.Sde.ReadByFieldAsync("types","groupID",groups)).Where(row=>row.Doc.TryGetProperty("published",out var published) && published.ValueKind==System.Text.Json.JsonValueKind.True).Select(row=>row.Key).ToArray();
            var dogma=await rig.Sde.ReadAsync("typeDogma",types);
            var moduleType=checked((int)dogma.First(row=>row.Doc.TryGetProperty("dogmaEffects",out var effects) && effects.ValueKind==System.Text.Json.JsonValueKind.Array && effects.EnumerateArray().Any(e=>e.GetProperty("effectID").GetInt32()==effect.Id)).Key);
            long module=0;
            await rig.Transaction(async (c,tx) => { module=await rig.Items.CreateAsync(c,tx,new NewItem(moduleType,healerRun.Data.CreatorCharacterId,healerRun.Data.CreatorShipId,ItemFlags.HiSlot0,1,Singleton:true)); });
            await rig.Transaction(async (c,tx) => await new Eve.Skills.PgSkillStore(rig.Source,rig.Schemas[2]).GrantAsync(c,tx,healerRun.Data.CreatorCharacterId,
                new[] { 3392,3393,3416,3419,3413,3426,3318,3422,3423,16069,27902,3435,3449,3433,19921,3427,3434,3421,21603,62450 }
                    .Select(type => new Eve.Skills.CharacterSkill(type,5,0))));
            var online=await rig.Ships.SetOnlineAsync(healerRun.Data.CreatorCharacterId,healerRun.Data.CreatorShipId,module,true);
            Assert.True(online is ShipChanged, online.ToString());
            await new PgModuleStateStore(rig.Source,rig.Schemas[3]).SetShipConditionAsync(targetRun.Data.CreatorShipId,ShipCondition.Full with { ArmorDamage=0.5 });
            var healer=await rig.ConnectPublic(healerRun); var target=await rig.ConnectPublic(targetRun);
            var system=rig.Space.SolarSystemOf(healer)!.Value; Assert.Equal(system,rig.Space.SolarSystemOf(target));
            var targetShip=targetRun.Data.CreatorShipId; var scene=SimSceneKey.SolarSystem(system);
            var initial=(await sim.CommandAsync((ulong)target.SessionId,SimCommands.GetCondition,[],timeout.Token)).Condition!;
            Assert.True(initial.ArmorDamage>0);
            var lockResult=await sim.CommandAsync((ulong)healer.SessionId,SimCommands.AddTarget,[targetShip],timeout.Token);
            Assert.True(lockResult.Accepted,lockResult.Reason);
            while (!(await sim.CommandAsync((ulong)healer.SessionId,SimCommands.GetTargets,[],timeout.Token)).Targets.Contains(targetShip)) await Task.Delay(100,timeout.Token);
            var startTime=Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+3000);
            var deadline=startTime+200_000_000;
            var register=new SimRegisterTournamentCutoff(SimClient.NextRequestId(),scene,9001,1,startTime,deadline,[new(targetRun.Data.CreatorCharacterId,targetShip)]);
            Assert.Equal(SimTournamentCutoffStatus.Pending,(await sim.RegisterTournamentCutoffAsync(register,timeout.Token)).Status);
            rig.Space.RemoteAssistancePolicyOf = (request, safetyLevel, _) =>
            {
                var facts = new Eve.Crimewatch.PolicyFacts(AttackerIsPlayer:request.Attacker>0,
                    TargetIsPlayerProperty:request.Target>0,SamePilot:request.Attacker==request.Target,TargetIsCapsule:request.TargetIsCapsule);
                return Task.FromResult(Eve.Crimewatch.PolicyEngine.Evaluate(new Eve.Crimewatch.PolicyRequest(
                    Eve.Crimewatch.PolicyAction.RemoteAssistance,request.Where,facts,safetyLevel)));
            };
            rig.Space.RecordRemoteAssistance = (_,_) => Task.CompletedTask;
            rig.Simulator.HoldNextRemoteRepairResult=true;
            var activation=await rig.Space.ActivateAsync(healer,module,"ShipModuleRemoteArmorMutadaptiveRepairer",targetShip,1,timeout.Token);
            Assert.NotNull(activation); Assert.True(activation.Ok,activation.Reason);
            var ack=await rig.Simulator.HeldRemoteRepairResult.Task.WaitAsync(timeout.Token);
            Assert.True(ack.Accepted,ack.Reason); Assert.True(ack.Amount>0); Assert.Equal(targetShip,ack.TargetId);
            var remaining=(deadline-Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))/10000;
            if (remaining>0) await Task.Delay(TimeSpan.FromMilliseconds(remaining+200),timeout.Token);
            var read=new SimReadTournamentCutoff(SimClient.NextRequestId(),scene,9001,1,startTime,deadline);
            Assert.Equal(SimTournamentCutoffStatus.Pending,(await sim.ReadTournamentCutoffAsync(read,timeout.Token)).Status);
            rig.Simulator.ReleaseHeldRemoteRepairResult();
            SimReadTournamentCutoffReply result;
            do { result=await sim.ReadTournamentCutoffAsync(read with { RequestId=SimClient.NextRequestId() },timeout.Token); if (result.Status==SimTournamentCutoffStatus.Pending) await Task.Delay(50,timeout.Token); } while (result.Status==SimTournamentCutoffStatus.Pending);
            Assert.Equal(SimTournamentCutoffStatus.Ready,result.Status);
            Assert.Equal(scene,result.Scene); Assert.Equal(9001,result.MatchId); Assert.Equal(1,result.Generation);
            Assert.Equal(startTime,result.Start); Assert.Equal(deadline,result.Deadline);
            var evidence=Assert.Single(result.Evidence);
            Assert.Equal(targetRun.Data.CreatorCharacterId,evidence.CharacterId); Assert.Equal(targetShip,evidence.ShipId);
            Assert.Equal(SimTournamentCutoffEvidence.Alive,evidence.Evidence);
            var repaired=(await sim.CommandAsync((ulong)target.SessionId,SimCommands.GetCondition,[],timeout.Token)).Condition!;
            Assert.True(repaired.ArmorDamage<initial.ArmorDamage);
            // Replay the same real Host-computed result; the correlated cycle can apply once only.
            rig.Simulator.ReleaseHeldRemoteRepairResult();
            var replayed=(await sim.CommandAsync((ulong)target.SessionId,SimCommands.GetCondition,[],timeout.Token)).Condition!;
            Assert.Equal(repaired.ArmorDamage,replayed.ArmorDamage);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree:true); await process.WaitForExitAsync(); }
    }
}
