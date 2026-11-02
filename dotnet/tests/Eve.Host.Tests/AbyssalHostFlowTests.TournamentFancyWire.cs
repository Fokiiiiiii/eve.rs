using Eve.Data;
using Eve.Inventory;
using Eve.Host.Persistence;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public async Task Tournament_fancy_registered_wire_reads_live_two_team_metrics()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        var schema = "tc_" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            string? endpoint = null; using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal)) { endpoint = line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint); _ = process.StandardError.ReadToEndAsync(); _ = process.StandardOutput.ReadToEndAsync();
            await using var simulation = SimClient.ToTcpSocket(endpoint); simulation.Start();
            using var connected = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!simulation.IsConnected) await Task.Delay(20, connected.Token);
            await using var rig = await Rig.Create(simulation);
            rig.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var run = await rig.CreateRun(enter: false); var referee = await rig.ConnectPublic(run);
            var player = await rig.CreateRun(enter: false); var other = await rig.CreateRun(enter: false, shipTypeId: 23757);
            var skillGroups=(await rig.Sde.ReadByFieldAsync("groups","categoryID",[16])).Select(row=>row.Key).ToArray();
            var skillTypes=(await rig.Sde.ReadByFieldAsync("types","groupID",skillGroups))
                .Where(row=>row.Doc.TryGetProperty("published",out var published) && published.ValueKind==System.Text.Json.JsonValueKind.True)
                .Select(row=>checked((int)row.Key)).ToArray();
            Assert.NotEmpty(skillTypes);
            long laser = 0, droneId = 0, fighterId = 0;
            await rig.Transaction(async (c,tx) =>
            {
                laser=await rig.Items.CreateAsync(c,tx,new NewItem(458,player.Data.CreatorCharacterId,player.Data.CreatorShipId,ItemFlags.HiSlot0,1,Singleton:true));
                droneId=await rig.Items.CreateAsync(c,tx,new NewItem(2454,player.Data.CreatorCharacterId,player.Data.CreatorShipId,CharacterShips.DroneBayFlag,1,Singleton:true));
                fighterId=await rig.Items.CreateAsync(c,tx,new NewItem(23055,other.Data.CreatorCharacterId,other.Data.CreatorShipId,159,9));
                await rig.Items.CreateAsync(c,tx,new NewItem(246,player.Data.CreatorCharacterId,player.Data.CreatorShipId,ItemFlags.HiSlot0,1,Singleton:true));
                await new Eve.Skills.PgSkillStore(rig.Source,rig.Schemas[2]).GrantAsync(c,tx,player.Data.CreatorCharacterId,
                    skillTypes.Select(type=>new Eve.Skills.CharacterSkill(type,5,0)));
            });
            var online=await rig.Ships.SetOnlineAsync(player.Data.CreatorCharacterId,player.Data.CreatorShipId,laser,true);
            Assert.True(online is ShipChanged,online.ToString());
            var redSession=await rig.ConnectPublic(player); var blueSession=await rig.ConnectPublic(other);
            var fighterStatics = await Eve.Dogma.DogmaStatic.LoadAsync(rig.Sde);
            await fighterStatics.EnsureTypesAsync([23055, 23757]);
            using var fighters = new Eve.Services.Space.SpaceFighters(simulation, rig.Ships, fighterStatics, s => rig.Space.SolarSystemOf(s));
            rig.Space.TournamentFighterFlightsOf = fighters.TournamentFlights;
            var system = rig.Space.SolarSystemOf(referee)!.Value;
            Assert.Equal(system,rig.Space.SolarSystemOf(redSession)); Assert.Equal(system,rig.Space.SolarSystemOf(blueSession));
            Assert.Null(await rig.Space.RelocateAsync(redSession,system,[100000000000d,200000000000d,300000000000d],[1d,0d,0d],CancellationToken.None));
            Assert.Null(await rig.Space.RelocateAsync(blueSession,system,[100000001000d,200000000000d,300000000000d],[1d,0d,0d],CancellationToken.None));
            var redPose=await rig.Space.PilotPlaceAsync(redSession,CancellationToken.None);
            var bluePose=await rig.Space.PilotPlaceAsync(blueSession,CancellationToken.None);
            Assert.NotNull(redPose); Assert.NotNull(bluePose);
            Assert.Equal(system,redPose.Value.SolarSystemId); Assert.Equal(system,bluePose.Value.SolarSystemId);
            Assert.NotNull(redPose.Value.Position); Assert.NotNull(bluePose.Value.Position);
            Assert.True(Math.Abs(redPose.Value.Position[0]-bluePose.Value.Position[0])<5000);

            await using (var seed = rig.Source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments (id bigint PRIMARY KEY, ship_types integer[] NOT NULL, maximum_points integer NOT NULL, external_ruleset_id bigint, name text NOT NULL DEFAULT 'Test', match_length integer NOT NULL DEFAULT 600, overtime_length integer NOT NULL DEFAULT 60);
                CREATE TABLE {schema}.series (tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id), id bigint NOT NULL, red_team text NOT NULL DEFAULT 'Red', blue_team text NOT NULL DEFAULT 'Blue', system_id bigint NOT NULL DEFAULT 0, current_match integer NOT NULL DEFAULT 0, match_count integer NOT NULL DEFAULT 1, PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments(id,ship_types,maximum_points,external_ruleset_id) VALUES (1, ARRAY[601],100,NULL); INSERT INTO {schema}.series(tournament_id,id) VALUES (1,1);
                """)) await seed.ExecuteNonQueryAsync();
            var deployed = new PgDeployedStore(rig.Source, rig.Schemas[4]);
            await PersistenceTournamentArenas.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRecoveryAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(rig.Source, schema); await PersistenceTournamentMatches.MigrateCutoffIntentAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRefereeLeaseGenerationAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateMetricsIntentAsync(rig.Source, schema);
            var arenas = new PersistenceTournamentArenas(rig.Source, rig.Sde, rig.Characters, rig.Items, deployed, rig.Foundations.Events, schema);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(system, run.Data.CreatorCharacterId,
                [new TournamentBeaconImport(10645, "Camera Arena", [100000000000d,200000000000d,300000000000d], 5)]));
            var store = new PersistenceTournamentMatches(rig.Source, arenas, rig.Items, deployed, rig.Foundations.Events,
                simulation, rig.Sde, schema, rig.Foundations.Scheduler, rig.Sessions, space: rig.Space);
            store.RegisterActionHandlers(); await store.InitializeMetricsRecoveryAsync(); await rig.Foundations.Events.PumpAsync();
            var sink = new TournamentWireSink(); referee.Sink = sink; referee.Set("accountRole", new PyLong(GameMasterRoles.Qa));
            var queries = new PersistenceTournamentStore(rig.Source,rig.Sde,arenas.ReadSystemsAsync,includeActiveMatches:true,schema:schema);
            var service = new TourneyMgrService(query: queries.QueryAsync,matches: new TourneyMatchCallbacks(store.CreateAsync, store.ReleaseAsync, store.ActionAsync,
                CameraDetailsAsync: store.CameraDetailsAsync, OtherMatchAsync: store.OtherMatchAsync, ActiveCameraAsync: store.ActiveCameraAsync,
                ReleaseStampedAsync: store.ReleaseStampedAsync, RemoveRefereeAsync: store.RemoveRefereeAsync));
            var registry = new ServiceRegistry(); registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => rig.Now);
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                TournamentWireRequest(1, "tourneyMgr", new PyInt(1), "MachoBindObject", new PyInt(system)))), referee));
            var binding = Assert.IsType<PyTuple>(TournamentWireResult(sink.Packets[^1]));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0])); Assert.NotNull(oid);
            long call = 2;
            async Task<PyValue> Query(string method, params PyValue[] args)
            {
                var request=TournamentWireRequest(call++,"tourneyMgr",new PyInt(1),method,args);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)),referee));
                return TournamentWireResult(sink.Packets[^1]);
            }
            static PyValue Field(PyDict dict,string key) => dict.Entries.Single(x => x.Key is PyStr s && s.Value == key || x.Key is PyBytes b && System.Text.Encoding.UTF8.GetString(b.Value.Span) == key).Value;
            async Task AssertSetup(bool occupied)
            {
                var setup=Assert.IsType<PyTuple>(await Query("GetTourneySetup"));
                var active=Assert.IsType<PyList>(setup.Items[2]);
                if (occupied) Assert.Contains(new PyInt(system),active.Items); else Assert.Empty(active.Items);
                var possible=Assert.IsType<PyDict>(await Query("GetPotentialMatches",new PyInt(1)));
                var details=Assert.IsType<PyDict>(Assert.Single(possible.Entries).Value);
                Assert.Equal(new PyInt(occupied ? system : 0),Field(details,"system"));
            }
            await AssertSetup(false);
            var shipList=Assert.IsType<PyDict>(await Query("QueryShipList",new PyInt(1)));
            var option=Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Field(shipList,"allowedShipList")).Items));
            Assert.Equal(new PyInt(601),option.Items[1]);
            Assert.NotEmpty(Eve.Macho.JsSemantics.StringValue(option.Items[0])!);
            async Task<PyValue> Invoke(string method, params PyValue[] args)
            {
                var request=TournamentWireRequest(call++,null,new PyStr(oid!),method,args);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)),referee));
                return TournamentWireResult(sink.Packets[^1]);
            }
            var created=Assert.IsType<PyTuple>(await Invoke("CreateMatch",new PyInt(1),new PyInt(1),new PyInt(100),new PyInt(600),new PyInt(60),PyNone.Instance));
            var matchId=created.Items[0] switch { PyLong n=>n.Value,PyInt n=>n.Value,_=>throw new InvalidOperationException("Invalid match") };
            // Locked roster is seeded; both ships and their fitted item state are actual Host+sim objects.
            var matchStart=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); rig.Now=matchStart;
            await using (var seed=rig.Source.CreateCommand($"""
                WITH members AS (INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES ($1,0,$2,true),($1,1,$3,true)),
                details AS (INSERT INTO {schema}.match_roster_details VALUES ($1,0,$2,601,$4,30000142,7,ARRAY[]::text[],decode('00','hex')),
                    ($1,1,$3,23757,$5,30000142,7,ARRAY[]::text[],decode('00','hex'))),
                ban AS (INSERT INTO {schema}.match_bans VALUES ($1,1,601))
                UPDATE {schema}.matches SET state=3,start_ms=$6 WHERE id=$1
                """))
            {
                seed.Parameters.AddWithValue(matchId); seed.Parameters.AddWithValue(player.Data.CreatorCharacterId); seed.Parameters.AddWithValue(other.Data.CreatorCharacterId);
                seed.Parameters.AddWithValue(player.Data.CreatorShipId); seed.Parameters.AddWithValue(other.Data.CreatorShipId); seed.Parameters.AddWithValue(matchStart); await seed.ExecuteNonQueryAsync();
            }
            var early=Assert.IsType<PyTuple>(await Invoke("GetFancyDetails",new PyLong(matchId)));
            Assert.IsType<PyNone>(early.Items[0]); Assert.IsType<PyNone>(early.Items[1]);
            var timer=Assert.IsType<PyTuple>(early.Items[2]); Assert.Equal(6,timer.Items.Count);
            Assert.Equal(3,Assert.IsType<PyInt>(timer.Items[0]).Value); Assert.Equal(Py.FileTimeFromUnixMs(matchStart),Assert.IsType<PyLong>(timer.Items[1]).Value);
            var bans=Assert.IsType<PyTuple>(early.Items[3]); Assert.Empty(Assert.IsType<PyList>(bans.Items[0]).Items);
            Assert.Single(Assert.IsType<PyList>(bans.Items[1]).Items);
            await Task.Delay(1500);
            var details=Assert.IsType<PyTuple>(await Invoke("GetFancyDetails",new PyLong(matchId)));
            var red=Assert.IsType<PyTuple>(details.Items[0]); var blue=Assert.IsType<PyTuple>(details.Items[1]);
            Assert.Equal(6,red.Items.Count); Assert.Equal(6,blue.Items.Count);
            var redValues=red.Items.Select(v=>Assert.IsType<PyReal>(v).Value).ToArray();
            var blueValues=blue.Items.Select(v=>Assert.IsType<PyReal>(v).Value).ToArray();
            Assert.True(redValues[0]>0); Assert.True(blueValues[0]>0); Assert.True(redValues[2]>0);
            Assert.Equal(0,redValues[3]); Assert.True(blueValues[2] > 0); Assert.Equal(0,blueValues[3]);
            // Move a real squadron from its tube into the native scene; V2 reports its member count.
            var launchFighter = new ServiceCall { Method = "LaunchFightersFromTubes", Session = blueSession, Args = [new PyList([new PyInt(159)])] };
            Assert.Empty(Assert.IsType<PyDict>(await fighters.LaunchFightersFromTubesAsync(launchFighter)).Entries);
            var launchedFighter = await rig.Items.GetAsync(fighterId);
            Assert.NotNull(launchedFighter); Assert.Equal(CharacterShips.InSpaceFlag, launchedFighter.Flag);
            long metricsGeneration;
            await using (var findGeneration = rig.Source.CreateCommand($"SELECT generation FROM {schema}.tournament_metrics_intents WHERE match_id=$1 AND state='active'"))
            { findGeneration.Parameters.AddWithValue(matchId); metricsGeneration = (long)(await findGeneration.ExecuteScalarAsync())!; }
            var nativeRoster = new[] { new SimTournamentMetricsPilot(player.Data.CreatorCharacterId, player.Data.CreatorShipId, 0),
                new SimTournamentMetricsPilot(other.Data.CreatorCharacterId, other.Data.CreatorShipId, 1) }.OrderBy(x => x.CharacterId).ToArray();
            var nativeFighter = await simulation.ReadTournamentMetricsV2Async(new(SimClient.NextRequestId(), SimSceneKey.SolarSystem(system),
                matchId, metricsGeneration, Py.FileTimeFromUnixMs(matchStart), nativeRoster));
            Assert.Equal(SimTournamentMetricsStatus.Success, nativeFighter.Status);
            Assert.Equal(9, Assert.Single(nativeFighter.Sample!.Pilots.SelectMany(x => x.Companions), x => x.ShipId == fighterId).Members);
            var afterFighter = Assert.IsType<PyTuple>(await Invoke("GetFancyDetails", new PyLong(matchId)));
            Assert.Equal(blueValues[2], Assert.IsType<PyReal>(Assert.IsType<PyTuple>(afterFighter.Items[1]).Items[2]).Value, 5);
            // The loaded bay drone contributes to fitting DPS and continues to count once launched.
            var droneStatics = await Eve.Dogma.DogmaStatic.LoadAsync(rig.Sde);
            await droneStatics.EnsureTypesAsync([458, 246, 2454, 601]);
            var fitted = await rig.Ships.LoadAsync(player.Data.CreatorCharacterId);
            Assert.NotNull(fitted);
            var turret = Eve.Services.Space.TournamentPotentialFit.Calculate(fitted, droneStatics, new Dictionary<long, string>(), true);
            Assert.Null(turret.UnavailableReason);
            Assert.True(redValues[2] > turret.Totals!.Value.FittedDamagePerSecond);
            using var drones = new Eve.Services.Space.SpaceDrones(simulation, rig.Ships, droneStatics, rig.Space, () => rig.Now);
            _ = await drones.LaunchAsync(redSession, [new Eve.Services.Space.DroneRequest(droneId, 1)], CancellationToken.None);
            var launched = await rig.Items.GetAsync(droneId);
            Assert.NotNull(launched); Assert.Equal(CharacterShips.InSpaceFlag, launched.Flag);
            var deployedDetails = Assert.IsType<PyTuple>(await Invoke("GetFancyDetails", new PyLong(matchId)));
            var deployedRed = Assert.IsType<PyTuple>(deployedDetails.Items[0]);
            Assert.Equal(redValues[2], Assert.IsType<PyReal>(deployedRed.Items[2]).Value, 5);
            // Fire the real fitted laser through SpaceSessions. Safety follows the
            // normal weapon policy at the pilot's explicitly selected red setting.
            rig.Space.SafetyLevelOf = (_,_) => Task.FromResult(Eve.Crimewatch.SafetyLevel.None);
            rig.Space.Aggression = (_,_) => Task.CompletedTask;
            using var combatTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var target=other.Data.CreatorShipId;
            var initial=(await simulation.CommandAsync((ulong)blueSession.SessionId,SimCommands.GetCondition,[],combatTimeout.Token)).Condition!;
            var lockResult=await simulation.CommandAsync((ulong)redSession.SessionId,SimCommands.AddTarget,[target],combatTimeout.Token);
            Assert.True(lockResult.Accepted,lockResult.Reason);
            while (!(await simulation.CommandAsync((ulong)redSession.SessionId,SimCommands.GetTargets,[],combatTimeout.Token)).Targets.Contains(target))
                await Task.Delay(100,combatTimeout.Token);
            var activation=await rig.Space.ActivateAsync(redSession,laser,"targetAttack",target,1000,combatTimeout.Token);
            Assert.NotNull(activation); Assert.True(activation.Ok,activation.Reason);
            var observed=false; int unavailable=0, zero=0;
            for (var attempt=0;attempt<120;attempt++)
            {
                var live=Assert.IsType<PyTuple>(await Invoke("GetFancyDetails",new PyLong(matchId)));
                if (live.Items[0] is PyTuple liveRed && live.Items[1] is PyTuple liveBlue && liveRed.Items[3] is PyReal applied)
                {
                    if (applied.Value>0)
                    {
                        Assert.Equal(0,Assert.IsType<PyReal>(liveBlue.Items[3]).Value);
                        observed=true; break;
                    }
                    zero++;
                }
                else unavailable++;
                await Task.Delay(200,combatTimeout.Token);
            }
            var after=(await simulation.CommandAsync((ulong)blueSession.SessionId,SimCommands.GetCondition,[],combatTimeout.Token)).Condition!;
            if (!observed)
            {
                long generation;
                await using (var find=rig.Source.CreateCommand($"SELECT generation FROM {schema}.tournament_metrics_intents WHERE match_id=$1 AND state='active'"))
                { find.Parameters.AddWithValue(matchId); generation=(long)(await find.ExecuteScalarAsync())!; }
                var roster=new[] {new SimTournamentMetricsPilot(player.Data.CreatorCharacterId,player.Data.CreatorShipId,0),new SimTournamentMetricsPilot(other.Data.CreatorCharacterId,other.Data.CreatorShipId,1)}.OrderBy(x=>x.CharacterId).ToArray();
                var scene=SimSceneKey.SolarSystem(system);
                var captures=new[] {await rig.Space.CaptureTournamentPotentialAsync(roster[0].CharacterId,roster[0].ShipId,scene,combatTimeout.Token),await rig.Space.CaptureTournamentPotentialAsync(roster[1].CharacterId,roster[1].ShipId,scene,combatTimeout.Token)};
                var native=await simulation.ReadTournamentMetricsAsync(new(SimClient.NextRequestId(),scene,matchId,generation,Py.FileTimeFromUnixMs(matchStart),roster),combatTimeout.Token);
                var reasons=new List<string>();
                if (native.Sample is { } sample)
                    for(var i=0;i<roster.Length;i++)
                        if(captures[i] is { } capture) reasons.Add((await rig.Space.CompleteTournamentPotentialAsync(capture,scene,sample.Pilots[i],roster.Where(x=>x.Team==roster[i].Team).Select(x=>x.ShipId).ToHashSet(),combatTimeout.Token)).UnavailableReason ?? "ok");
                Assert.Fail($"No applied DPS: None={unavailable}, zero={zero}, initial={initial}, after={after}, native={native.Status}/{native.Error}, damage={string.Join(',',native.Sample?.AppliedDamage ?? [])}, reasons={string.Join(',',reasons)}, problems={string.Join('|',rig.Problems)}");
            }
            Assert.True(after.ShieldCharge<initial.ShieldCharge || after.ArmorDamage>initial.ArmorDamage || after.Damage>initial.Damage);

            await using var intent=rig.Source.CreateCommand($"SELECT system_id,start_ms,characters,ships,teams FROM {schema}.tournament_metrics_intents WHERE match_id=$1 AND state='active'");
            intent.Parameters.AddWithValue(matchId); await using var row=await intent.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal((long)system,row.GetInt64(0)); Assert.Equal(matchStart,row.GetInt64(1));
            Assert.Equal(new[] {player.Data.CreatorCharacterId,other.Data.CreatorCharacterId}.Order(),row.GetFieldValue<long[]>(2));
            Assert.Equal(new[] {player.Data.CreatorShipId,other.Data.CreatorShipId}.Order(),row.GetFieldValue<long[]>(3));
            Assert.Equal(new[] {0,1},row.GetFieldValue<int[]>(4));
            _ = redSession; _ = blueSession;
        }
        finally
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
        }
    }
}
