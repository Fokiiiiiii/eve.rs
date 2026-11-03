using Eve.Services;
using Eve.Host.Persistence;
using Eve.Scheduler;
using Eve.Events;
using Eve.Space;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public async Task Tournament_cutoff_real_sim_snapshots_alive_pilot_and_releases()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true };
        start.Environment["EVE_SIM_LISTEN"]="127.0.0.1:0"; start.Environment["EVE_SDE_DIR"]=Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process=System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        try
        {
            string? endpoint=null; using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ",StringComparison.Ordinal)) { endpoint=line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint); _=process.StandardError.ReadToEndAsync(); _=process.StandardOutput.ReadToEndAsync();
            await using var sim=SimClient.ToTcpSocket(endpoint); sim.Start();
            using var connected=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!sim.IsConnected) await Task.Delay(20,connected.Token);
            await using var rig=await Rig.Create(sim); rig.Now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var run=await rig.CreateRun(enter:false); var pilot=await rig.ConnectPublic(run);
            var system=rig.Space.SolarSystemOf(pilot)!.Value; var scene=SimSceneKey.SolarSystem(system);
            var startTime=Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+3000); var deadline=startTime+10_000_000;
            var register=new SimRegisterTournamentCutoff(SimClient.NextRequestId(),scene,11,1,startTime,deadline,[new(run.Data.CreatorCharacterId,run.Data.CreatorShipId)]);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            SimTournamentMetricsPilot[] metricRoster=[new(run.Data.CreatorCharacterId,run.Data.CreatorShipId,0)];
            var metricRegister=new SimRegisterTournamentMetrics(SimClient.NextRequestId(),scene,11,1,startTime,metricRoster);
            Assert.Equal(SimTournamentMetricsStatus.Success,(await sim.RegisterTournamentMetricsAsync(metricRegister,timeout.Token)).Status);
            var accepted=await sim.RegisterTournamentCutoffAsync(register,timeout.Token);
            Assert.Equal(SimTournamentCutoffStatus.Pending,accepted.Status);
            var read=new SimReadTournamentCutoff(SimClient.NextRequestId(),scene,11,1,startTime,deadline);
            Assert.Equal(SimTournamentCutoffStatus.Pending,(await sim.ReadTournamentCutoffAsync(read,timeout.Token)).Status);
            await Task.Delay(4500,timeout.Token);
            var result=await sim.ReadTournamentCutoffAsync(read with { RequestId=SimClient.NextRequestId() },timeout.Token);
            Assert.Equal(SimTournamentCutoffStatus.Ready,result.Status);
            var evidence=Assert.Single(result.Evidence);
            Assert.Equal(run.Data.CreatorCharacterId,evidence.CharacterId); Assert.Equal(run.Data.CreatorShipId,evidence.ShipId);
            Assert.Equal(SimTournamentCutoffEvidence.Alive,evidence.Evidence);
            var metrics=await sim.ReadTournamentMetricsAsync(new SimReadTournamentMetrics(SimClient.NextRequestId(),scene,11,1,startTime,metricRoster),timeout.Token);
            Assert.Equal(SimTournamentMetricsStatus.Success,metrics.Status); Assert.NotNull(metrics.Sample);
            Assert.Equal(metricRoster,metrics.Roster); Assert.Equal(metricRoster[0],Assert.Single(metrics.Sample.Pilots).Pilot);
            Assert.Equal(SimTournamentMetricsStatus.Success,(await sim.ReleaseTournamentMetricsAsync(new SimReleaseTournamentMetrics(SimClient.NextRequestId(),scene,11,1,startTime,metricRoster),timeout.Token)).Status);
            var release=new SimReleaseTournamentCutoff(SimClient.NextRequestId(),scene,11,1,startTime,deadline);
            Assert.Equal(SimTournamentCutoffStatus.Pending,(await sim.ReleaseTournamentCutoffAsync(release,timeout.Token)).Status);
            Assert.Equal(SimTournamentCutoffStatus.Pending,(await sim.ReleaseTournamentCutoffAsync(release with { RequestId=SimClient.NextRequestId() },timeout.Token)).Status);
            var late=await sim.RegisterTournamentCutoffAsync(register with { RequestId=SimClient.NextRequestId() },timeout.Token);
            Assert.NotEqual(SimTournamentCutoffStatus.Pending,late.Status);
            var releasedRead=await sim.ReadTournamentCutoffAsync(read with { RequestId=SimClient.NextRequestId() },timeout.Token);
            Assert.Equal(SimTournamentCutoffStatus.Unavailable,releasedRead.Status); Assert.Empty(releasedRead.Evidence);

            var suffix=Guid.NewGuid().ToString("N")[..10]; var schema="cr_"+suffix; var jobs="cj_"+suffix; var outbox="ce_"+suffix;
            try
            {
                await using (var init=rig.Source.CreateCommand($"""
                    CREATE SCHEMA {schema};
                    CREATE TABLE {schema}.tournaments(id bigint PRIMARY KEY,ship_types integer[] NOT NULL);
                    CREATE TABLE {schema}.series(tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id),id bigint NOT NULL,PRIMARY KEY(tournament_id,id));
                    INSERT INTO {schema}.tournaments VALUES(1,ARRAY[601]); INSERT INTO {schema}.series VALUES(1,0);
                    """)) await init.ExecuteNonQueryAsync();
                await DurableScheduler.MigrateAsync(rig.Source,jobs); await EventBus.MigrateAsync(rig.Source,outbox);
                await PersistenceTournamentMatches.MigrateAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateFleetAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source,schema);
                await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(rig.Source,schema);
                await PersistenceTournamentMatches.MigratePostgameAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(rig.Source,schema);
                await PersistenceTournamentMatches.MigrateCutoffIntentAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateMetricsIntentAsync(rig.Source,schema);
                await using (var seed=rig.Source.CreateCommand($"""
                    INSERT INTO {schema}.matches(id,tournament_id,series_id,system_id,maximum_points,match_length,overtime_length) OVERRIDING SYSTEM VALUE
                      VALUES(22,1,0,{system},100,120,0);
                    INSERT INTO {schema}.match_referees(oid,match_id,session_id,character_id) VALUES('N=1:1',22,123,456);
                    """)) await seed.ExecuteNonQueryAsync();
                long clock=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await using var scheduler=new DurableScheduler(rig.Source,()=>clock,schema:jobs);
                var owner=new PersistenceTournamentMatches(rig.Source,null!,null!,null!,new EventBus(rig.Source,scheduler,schema:outbox),sim,null!,schema,scheduler,new CharacterSessions());
                owner.RegisterActionHandlers(); await owner.InitializeMetricsRecoveryAsync();
                // Independent observer generations retire through the real simulator before replacement.
                var metricsStart=clock+30000;
                await using (var state=rig.Source.CreateCommand($"UPDATE {schema}.matches SET state=3,start_ms=$1 WHERE id=22"))
                { state.Parameters.AddWithValue(metricsStart); await state.ExecuteNonQueryAsync(); }
                var roster=new[] { new SimTournamentMetricsPilot(run.Data.CreatorCharacterId,run.Data.CreatorShipId,0) };
                var oldMetrics=await owner.GetMetricsIntentAsync(22,system,metricsStart,roster,timeout.Token); Assert.NotNull(oldMetrics);
                Assert.Equal(SimTournamentMetricsStatus.Success,(await sim.RegisterTournamentMetricsAsync(new(SimClient.NextRequestId(),scene,22,oldMetrics.Generation,Py.FileTimeFromUnixMs(metricsStart),roster),timeout.Token)).Status);
                var nextStart=metricsStart+1000;
                await using (var state=rig.Source.CreateCommand($"UPDATE {schema}.matches SET start_ms=$1 WHERE id=22"))
                { state.Parameters.AddWithValue(nextStart); await state.ExecuteNonQueryAsync(); }
                Assert.Null(await owner.GetMetricsIntentAsync(22,system,nextStart,roster,timeout.Token));
                await scheduler.RunDueAsync(timeout.Token);
                await using (var state=rig.Source.CreateCommand($"SELECT state FROM {schema}.tournament_metrics_intents WHERE generation=$1"))
                { state.Parameters.AddWithValue(oldMetrics.Generation); Assert.Equal("released",await state.ExecuteScalarAsync()); }
                var newMetrics=await owner.GetMetricsIntentAsync(22,system,nextStart,roster,timeout.Token); Assert.NotNull(newMetrics); Assert.NotEqual(oldMetrics.Generation,newMetrics.Generation);
                Assert.Equal(SimTournamentMetricsStatus.Success,(await sim.RegisterTournamentMetricsAsync(new(SimClient.NextRequestId(),scene,22,newMetrics.Generation,Py.FileTimeFromUnixMs(nextStart),roster),timeout.Token)).Status);
                PersistenceTournamentMatches.CutoffIntent intent;
                await using (var c=await rig.Source.OpenConnectionAsync())
                await using (var tx=await c.BeginTransactionAsync())
                {
                    intent=await owner.PrepareCutoffIntentAsync(c,tx,22,system,clock+30000,clock+150000,
                        [new(run.Data.CreatorCharacterId,run.Data.CreatorShipId)],timeout.Token);
                    await tx.CommitAsync();
                }
                var other=new SimRegisterTournamentCutoff(SimClient.NextRequestId(),scene,intent.MatchId,intent.Generation,
                    Py.FileTimeFromUnixMs(intent.StartMs),Py.FileTimeFromUnixMs(intent.DeadlineMs),[new(run.Data.CreatorCharacterId,run.Data.CreatorShipId)]);
                Assert.Equal(SimTournamentCutoffStatus.Pending,(await sim.RegisterTournamentCutoffAsync(other,timeout.Token)).Status);
                await using (var c=await rig.Source.OpenConnectionAsync())
                await using (var tx=await c.BeginTransactionAsync())
                {
                    Assert.True(await owner.TransitionCutoffIntentAsync(c,tx,intent,"prepared","registered",timeout.Token));
                    Assert.True(await owner.TransitionCutoffIntentAsync(c,tx,intent,"registered","committed",timeout.Token)); await tx.CommitAsync();
                }
                await owner.ReleaseSessionAsync(123,timeout.Token);
                await using (var pending=rig.Source.CreateCommand($"SELECT state FROM {schema}.tournament_cutoff_intents WHERE generation=$1"))
                { pending.Parameters.AddWithValue(intent.Generation); Assert.Equal("release_pending",await pending.ExecuteScalarAsync()); }
                await using (var retained=rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.matches WHERE id=22 AND closing")) Assert.Equal(1L,await retained.ExecuteScalarAsync());
                await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
                {
                    await using var duplicate=rig.Source.CreateCommand($"INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length) VALUES(1,0,{system},100,120,0)");
                    await duplicate.ExecuteNonQueryAsync();
                });
                await owner.ResetAfterRestartAsync(timeout.Token);
                await using (var retained=rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.matches WHERE id=22 AND closing")) Assert.Equal(1L,await retained.ExecuteScalarAsync());
                clock += 30001; await scheduler.RunDueAsync(timeout.Token);
                await using var check=rig.Source.CreateCommand($"SELECT state FROM {schema}.tournament_cutoff_intents WHERE generation=$1");
                check.Parameters.AddWithValue(intent.Generation); Assert.Equal("released",await check.ExecuteScalarAsync());
                await using var removed=rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.matches WHERE id=22"); Assert.Equal(0L,await removed.ExecuteScalarAsync());
                clock += 5001; await scheduler.RunDueAsync(timeout.Token); await scheduler.RunDueAsync(timeout.Token);
                await using var observer=rig.Source.CreateCommand($"SELECT state FROM {schema}.tournament_metrics_intents WHERE generation=$1");
                observer.Parameters.AddWithValue(newMetrics.Generation); Assert.Equal("released",await observer.ExecuteScalarAsync());
            }
            finally { await using var cleanup=rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{jobs},{outbox} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree:true); await process.WaitForExitAsync(); }
    }
}
