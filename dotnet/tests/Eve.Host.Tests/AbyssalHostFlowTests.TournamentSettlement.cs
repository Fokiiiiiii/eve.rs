using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Scheduler;
using Eve.Services;
using Eve.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public async Task Tournament_deadline_consumes_real_scene_cutoff_and_finalizes_unequal_scores()
    {
        var launch=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
        launch.Environment["EVE_SIM_LISTEN"]="127.0.0.1:0"; launch.Environment["EVE_SDE_DIR"]=Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process=System.Diagnostics.Process.Start(launch) ?? throw new InvalidOperationException("Simulation failed to start.");
        var suffix=Guid.NewGuid().ToString("N")[..10]; var schema="tx_"+suffix; var jobs="tj_"+suffix; var outbox="te_"+suffix;
        try
        {
            string? endpoint=null; using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while(await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if(line.StartsWith("eve-sim: listening on ",StringComparison.Ordinal)) { endpoint=line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint); _=process.StandardError.ReadToEndAsync(); _=process.StandardOutput.ReadToEndAsync();
            await using var sim=SimClient.ToTcpSocket(endpoint); sim.Start();
            using var connected=new CancellationTokenSource(TimeSpan.FromSeconds(15)); while(!sim.IsConnected) await Task.Delay(20,connected.Token);
            await using var rig=await Rig.Create(sim); rig.Now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var red=await rig.CreateRun(enter:false); var blue=await rig.CreateRun(enter:false);
            var redSession=await rig.ConnectPublic(red); var blueSession=await rig.ConnectPublic(blue);
            var system=rig.Space.SolarSystemOf(redSession)!.Value; Assert.Equal(system,rig.Space.SolarSystemOf(blueSession));
            await DurableScheduler.MigrateAsync(rig.Source,jobs); await EventBus.MigrateAsync(rig.Source,outbox);
            await using (var init=rig.Source.CreateCommand($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.tournaments(id bigint PRIMARY KEY,ship_types integer[] NOT NULL); CREATE TABLE {schema}.series(tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id),id bigint NOT NULL,PRIMARY KEY(tournament_id,id)); INSERT INTO {schema}.tournaments VALUES(1,ARRAY[601]); INSERT INTO {schema}.series VALUES(1,0)")) await init.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(rig.Source,schema); await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateRefereeTravelAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateCutoffIntentAsync(rig.Source,schema);
            long clock=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); long start=clock+3000; long deadline=start+1000;
            await using var scheduler=new DurableScheduler(rig.Source,()=>clock,schema:jobs);
            var events=new EventBus(rig.Source,scheduler,schema:outbox);
            var store=new PersistenceTournamentMatches(rig.Source,null!,null!,null!,events,sim,null!,schema,scheduler,rig.Sessions);
            store.RegisterActionHandlers();
            var pilots=new[] { new SimTournamentCutoffPilot(red.Data.CreatorCharacterId,red.Data.CreatorShipId),new SimTournamentCutoffPilot(blue.Data.CreatorCharacterId,blue.Data.CreatorShipId) }.OrderBy(p=>p.CharacterId).ToArray();
            await using (var c=await rig.Source.OpenConnectionAsync())
            await using (var tx=await c.BeginTransactionAsync())
            {
                async Task Exec(string sql,params object[] args)
                { await using var command=new NpgsqlCommand(sql,c,tx); foreach(var arg in args) command.Parameters.AddWithValue(arg); await command.ExecuteNonQueryAsync(); }
                await Exec($"INSERT INTO {schema}.tournament_completion_policies VALUES(1,'alliance_tournament')");
                await Exec($"INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state,start_ms,settlement_pending) VALUES(1,0,$1,100,1,0,5,$2,true)",(long)system,start);
                await Exec($"INSERT INTO {schema}.match_teams VALUES(1,0,true),(1,1,true)");
                await Exec($"INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES(1,0,$1,true),(1,1,$2,true)",red.Data.CreatorCharacterId,blue.Data.CreatorCharacterId);
                await Exec($"INSERT INTO {schema}.match_roster_details VALUES(1,0,$1,601,$3,$5,10,ARRAY[]::text[],decode('00','hex')),(1,1,$2,601,$4,$5,20,ARRAY[]::text[],decode('00','hex'))",red.Data.CreatorCharacterId,blue.Data.CreatorCharacterId,red.Data.CreatorShipId,blue.Data.CreatorShipId,(long)system);
                await Exec($"INSERT INTO {schema}.tournament_cutoff_intents(match_id,system_id,start_ms,deadline_ms,characters,ships,state) VALUES(1,$1,$2,$3,$4,$5,'committed')",(long)system,start,deadline,pilots.Select(p=>p.CharacterId).ToArray(),pilots.Select(p=>p.ShipId).ToArray());
                await tx.CommitAsync();
            }
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var registration=new SimRegisterTournamentCutoff(SimClient.NextRequestId(),SimSceneKey.SolarSystem(system),1,1,Py.FileTimeFromUnixMs(start),Py.FileTimeFromUnixMs(deadline),pilots);
            Assert.Equal(SimTournamentCutoffStatus.Pending,(await sim.RegisterTournamentCutoffAsync(registration,timeout.Token)).Status);
            await Task.Delay(4500,timeout.Token); clock=deadline+1;
            var snapshot=await sim.ReadTournamentCutoffAsync(new SimReadTournamentCutoff(SimClient.NextRequestId(),registration.Scene,1,1,registration.Start,registration.Deadline),timeout.Token);
            Assert.Equal(SimTournamentCutoffStatus.Ready,snapshot.Status); Assert.All(snapshot.Evidence,item=>Assert.Equal(SimTournamentCutoffEvidence.Alive,item.Evidence));
            await scheduler.ScheduleAsync(new JobSpec("tournament.match.settle",clock) { Key="match:1",Payload=new { MatchId=1,StartMs=start } });
            await scheduler.RunDueAsync(timeout.Token);
            await using var state=rig.Source.CreateCommand($"SELECT state,winner_team,completion_reason FROM {schema}.matches WHERE id=1");
            await using var row=await state.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(6,row.GetInt32(0)); Assert.Equal(1,row.GetInt32(1)); Assert.Equal("score",row.GetString(2));
        }
        finally
        {
            await using var source=NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup=source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{jobs},{outbox} CASCADE"); await cleanup.ExecuteNonQueryAsync();
            if(!process.HasExited) process.Kill(entireProcessTree:true); await process.WaitForExitAsync();
        }
    }
}
