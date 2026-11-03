using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services.Core;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public Task Postgame_scheduler_cleans_real_wreck_then_returns_snapshotted_referee() => PostgameRoundTrip(false);

    [RealAbyssalSimPgFact]
    public Task Postgame_scheduler_reinstantiation_retains_offline_player_until_reconnect() => PostgameRoundTrip(true);

    private async Task PostgameRoundTrip(bool reconnect)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true };
        start.Environment["EVE_SIM_LISTEN"]="127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"]=Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        var schema="tpg_"+Guid.NewGuid().ToString("N")[..10];
        try
        {
            string? endpoint=null; using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ",StringComparison.Ordinal)) { endpoint=line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint); var stderr=process.StandardError.ReadToEndAsync(); _=process.StandardOutput.ReadToEndAsync();
            var simLog=new List<string>(); await using var simulation=SimClient.ToTcpSocket(endpoint,simLog.Add); simulation.Start();
            using var connected=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!simulation.IsConnected) await Task.Delay(20,connected.Token);
            await using var rig=await Rig.Create(simulation); rig.Now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var run=await rig.CreateRun(enter:false); var session=await rig.ConnectPublic(run);
            var original=await rig.Space.PilotPoseAsync(session,CancellationToken.None); Assert.NotNull(original);
            var owner=run.Data.CreatorCharacterId; const int system=30000144;
            var playerRun=await rig.CreateRun(enter:false); var playerId=playerRun.Data.CreatorCharacterId; var playerShip=playerRun.Data.CreatorShipId;
            const long playerStation=60003760; var stationData=rig.Stations.Get(playerStation); Assert.NotNull(stationData);
            var dock=await rig.Ships.DockAsync(playerId,playerStation,null); Assert.NotNull(dock.Moved);
            var player=new ClientSession(new RecordingSink(rig.Order)) { SessionId=playerId+500,ClientId=playerId+600 };
            player.Set("charid",new PyLong(playerId)); player.Set("shipid",new PyLong(playerShip));
            player.Set("stationid",new PyLong(playerStation)); player.Set("stationid2",new PyLong(playerStation));
            player.Set("solarsystemid",PyNone.Instance); player.Set("solarsystemid2",new PyInt(stationData.SolarSystemId)); player.Set("locationid",new PyLong(playerStation));
            rig.Sessions.Enter(playerId,player); await rig.Space.OnCharacterEnteredAsync(player,playerId,CancellationToken.None);
            Assert.False(rig.Space.IsInSpace(player));
            await using (var seed=rig.Source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments(id bigint PRIMARY KEY, ship_types integer[] NOT NULL);
                CREATE TABLE {schema}.series(tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id),id bigint NOT NULL,PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES (1,ARRAY[601]); INSERT INTO {schema}.series VALUES (1,1);
                """)) await seed.ExecuteNonQueryAsync();
            var deployed=new PgDeployedStore(rig.Source,rig.Schemas[4]);
            await PersistenceTournamentArenas.MigrateAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateFleetAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateAutobansAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRecoveryAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRefereeTravelAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRulesAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRefereeDirectionAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRefereeMoveIdsAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateParticipantMoveIdsAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateParticipantIntentAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateCleanupStagesAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateCleanupScanAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(rig.Source,schema); await PersistenceTournamentMatches.MigrateCutoffIntentAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRefereeLeaseGenerationAsync(rig.Source,schema);
            var arenas=new PersistenceTournamentArenas(rig.Source,rig.Sde,rig.Characters,rig.Items,deployed,rig.Foundations.Events,schema);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(system,owner,[new TournamentBeaconImport(10645,"Postgame Arena",[100000000000d,200000000000d,300000000000d],5)]));
            var beacon=(await arenas.ReadSystemsAsync()).Single().BeaconIds[0];
            var statics=await DogmaStatic.LoadAsync(rig.Sde);
            using var containers=new SpaceContainers(simulation,rig.Ships,rig.Items,rig.SpaceItems,
                new PgWorkCycleLedger(rig.Source,rig.Schemas[4]),new EventBusFacts(rig.Foundations.Events),statics,
                rig.Space,()=>rig.Now,deployed:deployed,scheduler:rig.Foundations.Scheduler,source:rig.Source);
            // Match Host's real tournament deployable registration before entering its system.
            containers.RegisterDeployedKind(PersistenceTournamentArenas.BeaconKind,
                _ => Task.FromException(new InvalidOperationException("Tournament beacons do not expire.")),
                async (record, _) =>
                {
                    var item = await rig.Items.GetAsync(record.DeployableId);
                    var type = await new ItemTypeCatalog(rig.Sde).GetAsync(record.TypeId);
                    var sdeRows = await rig.Sde.ReadAsync("types",[record.TypeId]);
                    if (item is null || item.TypeId != record.TypeId || item.LocationId != record.SolarSystemId ||
                        item.OwnerId != record.OwnerId || record.ExpiresAtMs != long.MaxValue || type is not { CategoryId:2, GroupId:310 } ||
                        sdeRows.Count != 1 || !sdeRows[0].Doc.TryGetProperty("graphicID",out var graphic) || !graphic.TryGetInt32(out var graphicId))
                        throw new InvalidOperationException("Invalid tournament beacon.");
                    var error = await simulation.EnsureTournamentBeaconAsync(new SimEnsureTournamentBeacon(SimClient.NextRequestId(),
                        SimSceneKey.SolarSystem(record.SolarSystemId),record.DeployableId,record.TypeId,record.GroupId,type.CategoryId,
                        record.OwnerId,graphicId,record.Name,record.Position,record.Radius));
                    if (error is not null) throw new InvalidOperationException(error);
                });
            var store=new PersistenceTournamentMatches(rig.Source,arenas,rig.Items,deployed,rig.Foundations.Events,simulation,rig.Sde,
                schema,rig.Foundations.Scheduler,rig.Sessions,space:rig.Space,tournamentCharacters:rig.Characters,tournamentContainers:containers);
            store.RegisterActionHandlers(); store.RegisterSpaceHandlers(); await store.InitializeTournamentRestrictionsAsync();
            await using var restartedScheduler=new DurableScheduler(rig.Source,()=>rig.Now,schema:rig.Schemas[5]);
            var activeScheduler=rig.Foundations.Scheduler;
            rig.Space.TournamentRestricted=store.IsTournamentRestricted;
            await store.ConfigurePhysicalRegionAsync(system,beacon,500);
            await store.RefTravelAsync(new TourneyRefTravelRequest(TourneyRefTravelOperation.TeleportToArena,system,"N=1:travel",session),CancellationToken.None);
            if (!simulation.IsConnected)
            {
                if (!process.HasExited) process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync();
                Assert.Fail(string.Join(" | ",simLog) + " STDERR: " + await stderr);
            }
            Assert.Null(await simulation.EnsureTournamentBeaconAsync(new SimEnsureTournamentBeacon(SimClient.NextRequestId(),
                SimSceneKey.SolarSystem(system),beacon,10645,310,2,owner,1211,"Postgame Arena",[100000000000d,200000000000d,300000000000d],5)));
            long match;
            await using (var seed=rig.Source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state)
                VALUES(1,1,$1,100,600,0,1) RETURNING id
                """)) { seed.Parameters.AddWithValue((long)system); match=(long)(await seed.ExecuteScalarAsync())!; }
            await using (var seed=rig.Source.CreateCommand($"INSERT INTO {schema}.match_referees(oid,match_id,session_id,character_id) VALUES('N=1:ref',$1,$2,$3)"))
            { seed.Parameters.AddWithValue(match); seed.Parameters.AddWithValue(session.SessionId); seed.Parameters.AddWithValue(owner); await seed.ExecuteNonQueryAsync(); }
            await using (var seed=rig.Source.CreateCommand($"INSERT INTO {schema}.match_teams(match_id,team,is_locked) VALUES($1,0,true)"))
            { seed.Parameters.AddWithValue(match); await seed.ExecuteNonQueryAsync(); }
            await using (var seed=rig.Source.CreateCommand($"INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES($1,0,$2,false),($1,0,$3,true)"))
            { seed.Parameters.AddWithValue(match); seed.Parameters.AddWithValue(owner); seed.Parameters.AddWithValue(playerId); await seed.ExecuteNonQueryAsync(); }
            try { await store.SpaceActionAsync(new TourneyMatchActionRequest(TourneyMatchAction.TeleportPlayers,match,[new PyLong(match),new PyInt(0),new PyInt(0)],PyNone.Instance,system,"N=1:ref",session),CancellationToken.None); }
            catch (Exception error)
            {
                var actual=await rig.Space.PilotPlaceAsync(player,CancellationToken.None);
                var item=await rig.Items.GetAsync(playerShip);
                throw new InvalidOperationException($"Player arrival system={actual?.SolarSystemId}, pose={string.Join(",",actual?.Position ?? [])}, shipLocation={item?.LocationId}, sessionSystem={player.GetInt("solarsystemid")}, logs={string.Join(" | ",rig.Problems)}",error);
            }
            var playerPose=await rig.Space.PilotPlaceAsync(player,CancellationToken.None);
            Assert.NotNull(playerPose); Assert.Equal(system,playerPose.Value.SolarSystemId);
            var refereePose=await rig.Space.PilotPlaceAsync(session,CancellationToken.None); Assert.NotNull(refereePose);
            Assert.Equal(system,refereePose.Value.SolarSystemId); Assert.NotNull(playerPose.Value.Position); Assert.NotNull(refereePose.Value.Position);
            var center=new double[] {100000000000d,200000000000d,300000000000d};
            var distance=Math.Sqrt(playerPose.Value.Position.Zip(center,(x,y)=>(x-y)*(x-y)).Sum());
            Assert.InRange(distance,0,500); Assert.NotEqual(refereePose.Value.Position,playerPose.Value.Position);
            await using (var prepare=rig.Source.CreateCommand($"UPDATE {schema}.matches SET state=5 WHERE id=$1"))
            { prepare.Parameters.AddWithValue(match); await prepare.ExecuteNonQueryAsync(); }
            long wreck;
            await using (var c=await rig.Source.OpenConnectionAsync()) await using (var tx=await c.BeginTransactionAsync())
            {
                wreck=await rig.Items.CreateAsync(c,tx,new NewItem(26506,playerId,system,0,1,true));
                await rig.SpaceItems.SaveAsync(c,tx,new SpaceItem(wreck,system,[100000000001d,200000000000d,300000000000d],[1,0,0],47,186,playerShip,playerId,0,0,0,rig.Now+7200000));
                await tx.CommitAsync();
            }
            var scene=SimSceneKey.SolarSystem(system);
            simulation.PlaceWreck(new SimPlaceWreck(scene,wreck,186,new SimWreckKind(26506,"Wreck",3117),[100000000001d,200000000000d,300000000000d],[1,0,0],47,playerShip,new SimCredit(playerId,0,0,0),false));
            var before=await simulation.QueryTournamentWreckAsync(new SimQueryTournamentWreck(SimClient.NextRequestId(),scene,wreck)); Assert.True(before.Present);
            var completion=rig.Now;
            await store.ActionAsync(new TourneyMatchActionRequest(TourneyMatchAction.MatchCompleted,match,[new PyLong(match)],PyNone.Instance,
                system,"N=1:ref",session),CancellationToken.None);
            if (reconnect)
            {
                player.Close(); await rig.Space.QuiesceAsync(); Assert.Null(rig.Sessions.Of(playerId));
            }
            else
            {
                await using var detach=rig.Source.CreateCommand($"DELETE FROM {schema}.match_referees WHERE match_id=$1");
                detach.Parameters.AddWithValue(match); await detach.ExecuteNonQueryAsync();
            }
            rig.Now=completion+59999; await activeScheduler.RunDueAsync();
            if (!reconnect) Assert.Equal(system,rig.Space.SolarSystemOf(player));
            Assert.True((await simulation.QueryTournamentWreckAsync(new SimQueryTournamentWreck(SimClient.NextRequestId(),scene,wreck))).Present);
            rig.Now=completion+60000; await activeScheduler.RunDueAsync();
            Assert.True((await simulation.QueryTournamentWreckAsync(new SimQueryTournamentWreck(SimClient.NextRequestId(),scene,wreck))).Present);
            if (reconnect)
            {
                await activeScheduler.RunDueAsync();
                await using (var pending=rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.match_participants WHERE match_id=$1"))
                { pending.Parameters.AddWithValue(match); Assert.Equal(1L,(long)(await pending.ExecuteScalarAsync())!); }
                await using (var refs=rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.postgame_referees WHERE match_id=$1"))
                { refs.Parameters.AddWithValue(match); Assert.Equal(1L,(long)(await refs.ExecuteScalarAsync())!); }
                var restartedEvents=new EventBus(rig.Source,restartedScheduler,schema:rig.Schemas[6]);
                var restarted=new PersistenceTournamentMatches(rig.Source,arenas,rig.Items,deployed,restartedEvents,simulation,rig.Sde,
                    schema,restartedScheduler,rig.Sessions,space:rig.Space,tournamentCharacters:rig.Characters,tournamentContainers:containers);
                restarted.RegisterActionHandlers(); restarted.RegisterSpaceHandlers(); await restarted.InitializeTournamentRestrictionsAsync();
                rig.Space.TournamentRestricted=restarted.IsTournamentRestricted;
                await restarted.ResetAfterRestartAsync();
                activeScheduler=restartedScheduler;
                player=new ClientSession(new RecordingSink(rig.Order)) { SessionId=playerId+700,ClientId=playerId+800 };
                player.Set("charid",new PyLong(playerId)); player.Set("shipid",new PyLong(playerShip));
                player.Set("solarsystemid",new PyInt(system)); player.Set("solarsystemid2",new PyInt(system)); player.Set("locationid",new PyInt(system));
                rig.Sessions.Enter(playerId,player); await rig.Space.OnCharacterEnteredAsync(player,playerId,CancellationToken.None);
                Assert.True(rig.Space.IsInSpace(player));
            }
            for(var attempt=0;attempt<12 && player.GetInt("stationid")!=playerStation;attempt++)
            { await activeScheduler.RunDueAsync(); rig.Now+=5000; }
            Assert.Equal(playerStation,player.GetInt("stationid")); Assert.Equal(playerStation,(await rig.Items.GetAsync(playerShip))!.LocationId);
            rig.Now=Math.Max(rig.Now,completion+65000);
            for(var attempt=0;attempt<12 && await rig.Items.GetAsync(wreck) is not null;attempt++)
            { await activeScheduler.RunDueAsync(); rig.Now+=5000; }
            Assert.Null(await rig.Items.GetAsync(wreck));
            Assert.Equal(system,rig.Space.SolarSystemOf(session));
            rig.Now=Math.Max(rig.Now,completion+70000);
            for(var attempt=0;attempt<12 && rig.Space.SolarSystemOf(session)==system;attempt++)
            { await activeScheduler.RunDueAsync(); rig.Now+=5000; }
            var home=await rig.Space.PilotPoseAsync(session,CancellationToken.None); Assert.NotNull(home);
            Assert.Equal(original.Value.SolarSystemId,home.Value.SolarSystemId); Assert.Equal(original.Value.Position,home.Value.Position);
            await using var state=rig.Source.CreateCommand($"SELECT postgame_pending,cleanup_required FROM {schema}.matches WHERE id=$1");
            state.Parameters.AddWithValue(match); await using var row=await state.ExecuteReaderAsync();
            if (reconnect) Assert.False(await row.ReadAsync());
            else { Assert.True(await row.ReadAsync()); Assert.False(row.GetBoolean(0)); Assert.False(row.GetBoolean(1)); }
        }
        finally
        {
            await using var source=NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup=source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync();
            if(!process.HasExited) process.Kill(entireProcessTree:true); await process.WaitForExitAsync();
        }
    }
}
