using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Scheduler;
using Eve.Dogma;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public async Task Tournament_cleanup_registered_wire_waits_for_confirmed_removal()
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
            var run = await rig.CreateRun(enter: false); var session = await rig.ConnectPublic(run);
            var system = rig.Space.SolarSystemOf(session)!.Value; var owner = run.Data.CreatorCharacterId;
            await using (var seed = rig.Source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments (id bigint PRIMARY KEY, ship_types integer[] NOT NULL);
                CREATE TABLE {schema}.series (tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id), id bigint NOT NULL, PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES (1, ARRAY[601]);
                INSERT INTO {schema}.series VALUES (1,1);
                """)) await seed.ExecuteNonQueryAsync();
            var deployed = new PgDeployedStore(rig.Source, rig.Schemas[4]);
            await PersistenceTournamentArenas.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCleanupStagesAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCleanupScanAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRefereeLeaseGenerationAsync(rig.Source, schema);
            var arenas = new PersistenceTournamentArenas(rig.Source, rig.Sde, rig.Characters, rig.Items, deployed, rig.Foundations.Events, schema);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(system, owner,
                [new TournamentBeaconImport(10645, "Test Arena", [1000,2000,3000], 5)]));
            var beaconId = (await arenas.ReadSystemsAsync()).Single().BeaconIds[0];
            var statics = await DogmaStatic.LoadAsync(rig.Sde);
            using var containers = new SpaceContainers(simulation, rig.Ships, rig.Items, rig.SpaceItems,
                new PgWorkCycleLedger(rig.Source, rig.Schemas[4]), new EventBusFacts(rig.Foundations.Events), statics,
                rig.Space, () => rig.Now, deployed: deployed, scheduler: rig.Foundations.Scheduler, source: rig.Source);
            var store = new PersistenceTournamentMatches(rig.Source, arenas, rig.Items, deployed, rig.Foundations.Events,
                simulation, rig.Sde, schema, rig.Foundations.Scheduler, rig.Sessions, space: rig.Space, tournamentContainers: containers);
            store.RegisterActionHandlers(); store.RegisterSpaceHandlers();
            await store.ConfigurePhysicalRegionAsync(system, beaconId, 500);
            session.Set("accountRole",new PyLong(GameMasterRoles.Qa));
            var sink = new TournamentWireSink(); session.Sink = sink;
            var service = new TourneyMgrService(matches:new TourneyMatchCallbacks(store.CreateAsync,store.ReleaseAsync,store.ActionAsync,
                SpaceActionAsync:store.SpaceActionAsync,ReleaseStampedAsync:store.ReleaseStampedAsync));
            var registry = new ServiceRegistry(); registry.Register(service);
            var dispatcher = new PacketDispatcher(registry,()=>rig.Now); long call = 1;
            async Task<PyValue> Dispatch(string? svc,PyValue remote,string method,params PyValue[] args)
            {
                var request = TournamentWireRequest(call++,svc,remote,method,args);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)),session));
                return sink.Packets[^1];
            }
            var bound = Assert.IsType<PyTuple>(TournamentWireResult(await Dispatch("tourneyMgr",new PyInt(1),"MachoBindObject",new PyInt(system))));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(bound.Items[0])); Assert.NotNull(oid);
            Assert.Null(await simulation.EnsureTournamentBeaconAsync(new SimEnsureTournamentBeacon(SimClient.NextRequestId(),
                SimSceneKey.SolarSystem(system), beaconId, 10645, 310, 2, owner, 1211, "Test Arena", [1000,2000,3000], 5)));
            long matchId;
            await using (var seed = rig.Source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state,cleanup_required)
                VALUES (1,1,$1,100,600,0,6,true) RETURNING id
                """)) { seed.Parameters.AddWithValue((long)system); matchId = (long)(await seed.ExecuteScalarAsync())!; }
            // Seed a completed match and its durable postgame job; registered bind/auth and cleanup are exercised below.
            await using (var seed = rig.Source.CreateCommand($"UPDATE {schema}.matches SET postgame_pending=true WHERE id=$1"))
            { seed.Parameters.AddWithValue(matchId); await seed.ExecuteNonQueryAsync(); }
            await using (var seed = rig.Source.CreateCommand($"INSERT INTO {schema}.match_referees(oid,match_id,session_id,character_id) VALUES($1,$2,$3,$4)"))
            { seed.Parameters.AddWithValue(oid!); seed.Parameters.AddWithValue(matchId); seed.Parameters.AddWithValue(session.SessionId); seed.Parameters.AddWithValue(owner); await seed.ExecuteNonQueryAsync(); }
            await using (var c = await rig.Source.OpenConnectionAsync())
            await using (var tx = await c.BeginTransactionAsync())
            {
                await rig.Foundations.Scheduler.ScheduleAsync(c,tx,new JobSpec("tournament.match.postgame",rig.Now+65000)
                { Key=$"match:{matchId}",Payload=new { MatchId=matchId,CompletedMs=rig.Now,Stage=1 } });
                await tx.CommitAsync();
            }
            await using (var roster = rig.Source.CreateCommand($"INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES ($1,0,$2,true)"))
            { roster.Parameters.AddWithValue(matchId); roster.Parameters.AddWithValue(owner); await roster.ExecuteNonQueryAsync(); }
            var launcher = session.GetInt("shipid")!.Value; const int wreckType = 26506;
            var wrecks = new List<long>();
            await using (var c = await rig.Source.OpenConnectionAsync())
            await using (var tx = await c.BeginTransactionAsync())
            {
                foreach (var (credit, position) in new[] { (owner, new double[] { 1001,2000,3000 }),
                    (owner, new double[] { 10000,2000,3000 }), (owner + 1, new double[] { 1002,2000,3000 }) })
                {
                    var id = await rig.Items.CreateAsync(c, tx, new NewItem(wreckType, credit, system, 0, 1, true));
                    await rig.SpaceItems.SaveAsync(c, tx, new SpaceItem(id, system, position, [1,0,0], 47, 186, launcher, credit, 0, 0, 0, rig.Now + 7200000));
                    wrecks.Add(id);
                }
                await tx.CommitAsync();
            }
            var scene = SimSceneKey.SolarSystem(system);
            for (var i = 0; i < wrecks.Count; i++)
            {
                var credit = i == 2 ? owner + 1 : owner;
                var position = i == 1 ? new double[] { 10000,2000,3000 } : new double[] { 1001 + i,2000,3000 };
                simulation.PlaceWreck(new SimPlaceWreck(scene, wrecks[i], 186, new SimWreckKind(wreckType, "Wreck", 3117),
                    position, [1,0,0], 47, launcher, new SimCredit(credit,0,0,0), false));
            }
            foreach (var id in wrecks)
            {
                var before = await simulation.QueryTournamentWreckAsync(new SimQueryTournamentWreck(SimClient.NextRequestId(), scene, id));
                Assert.Null(before.Error); Assert.True(before.Present);
            }
            // Simulate a lost removal acknowledgment: the real scene has removed
            // the target, but only "seen" was persisted and the inventory item remains.
            await using (var target = rig.Source.CreateCommand($"""
                INSERT INTO {schema}.match_cleanup_targets(match_id,item_id,owner_id,launcher_id,type_id,center_x,center_y,center_z,radius_meters,sim_seen)
                VALUES ($1,$2,$3,$4,$5,1000,2000,3000,500,true)
                """))
            {
                target.Parameters.AddWithValue(matchId); target.Parameters.AddWithValue(wrecks[0]); target.Parameters.AddWithValue(owner);
                target.Parameters.AddWithValue(launcher); target.Parameters.AddWithValue(wreckType); await target.ExecuteNonQueryAsync();
            }
            var detached = await simulation.RemoveTournamentWreckAsync(new SimRemoveTournamentWreck(SimClient.NextRequestId(), scene,
                wrecks[0], owner, launcher, wreckType, [1000,2000,3000], 500));
            Assert.Null(detached.Error); Assert.True(detached.Removed);
            var blocked = await Dispatch(null,new PyStr(oid!),"CleanupGrid",new PyLong(matchId));
            Assert.Equal(MachoMessage.ErrorResponse,Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(blocked).Args).Items[0]).Value);
            Assert.NotNull(await rig.Items.GetAsync(wrecks[0]));
            await using (var pending = rig.Source.CreateCommand($"""
                SELECT m.cleanup_required,t.sim_seen,t.sim_removed FROM {schema}.matches m JOIN {schema}.match_cleanup_targets t ON t.match_id=m.id
                WHERE m.id=$1 AND t.item_id=$2
                """))
            {
                pending.Parameters.AddWithValue(matchId); pending.Parameters.AddWithValue(wrecks[0]);
                await using var row = await pending.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
                Assert.True(row.GetBoolean(0)); Assert.True(row.GetBoolean(1)); Assert.False(row.GetBoolean(2));
            }
            // Restore the real sim object so the retry can obtain a guarded ACK.
            simulation.PlaceWreck(new SimPlaceWreck(scene, wrecks[0], 186, new SimWreckKind(wreckType, "Wreck", 3117),
                [1001,2000,3000], [1,0,0], 47, launcher, new SimCredit(owner,0,0,0), false));
            var restored = await simulation.QueryTournamentWreckAsync(new SimQueryTournamentWreck(SimClient.NextRequestId(), scene, wrecks[0]));
            Assert.Null(restored.Error); Assert.True(restored.Present);
            rig.Now += 30001;
            Assert.IsType<PyNone>(TournamentWireResult(await Dispatch(null,new PyStr(oid!),"CleanupGrid",new PyLong(matchId))));
            Assert.IsType<PyNone>(TournamentWireResult(await Dispatch(null,new PyStr(oid!),"CleanupGrid",new PyLong(matchId))));
            var removed = await simulation.QueryTournamentWreckAsync(new SimQueryTournamentWreck(SimClient.NextRequestId(), scene, wrecks[0]));
            Assert.Null(removed.Error); Assert.False(removed.Present); Assert.Null(await rig.Items.GetAsync(wrecks[0]));
            foreach (var id in wrecks.Skip(1))
            {
                var preserved = await simulation.QueryTournamentWreckAsync(new SimQueryTournamentWreck(SimClient.NextRequestId(), scene, id));
                Assert.Null(preserved.Error); Assert.True(preserved.Present); Assert.NotNull(await rig.Items.GetAsync(id));
            }
            var beacon = await simulation.GetSceneBodyAsync(new SimGetSceneBody(SimClient.NextRequestId(), scene, beaconId));
            Assert.Null(beacon.Error); Assert.Equal(10645, beacon.TypeId);
            await using var state = rig.Source.CreateCommand($"SELECT cleanup_required FROM {schema}.matches WHERE id=$1");
            state.Parameters.AddWithValue(matchId); Assert.False((bool)(await state.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
        }
    }
}
