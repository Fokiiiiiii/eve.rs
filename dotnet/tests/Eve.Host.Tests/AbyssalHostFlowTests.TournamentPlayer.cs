using Eve.Data;
using Eve.Host.Persistence;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public Task Tournament_player_teleport_retries_arrival_restricts_actual_commands_and_returns_to_dock() => RunTournamentPlayerFlow(false);

    [RealAbyssalSimPgFact]
    public Task Tournament_registered_automatic_start_reaches_real_deadline_and_keeps_tie_pending() => RunTournamentPlayerFlow(true);

    private async Task RunTournamentPlayerFlow(bool automaticDeadline)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        var schema = "tp_" + Guid.NewGuid().ToString("N")[..10];
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
            var refRun = await rig.CreateRun(enter: false); var referee = await rig.ConnectPublic(refRun);
            var playerRun = await rig.CreateRun(enter: false);
            var playerId = playerRun.Data.CreatorCharacterId; var shipId = playerRun.Data.CreatorShipId;
            var system = rig.Space.SolarSystemOf(referee)!.Value; const long station = 60003760;
            var dock = await rig.Ships.DockAsync(playerId, station, null);
            Assert.NotNull(dock.Moved);
            var pilot = new ClientSession(new RecordingSink(rig.Order)) { SessionId = playerId + 500, ClientId = playerId + 600 };
            pilot.Set("charid", new PyLong(playerId)); pilot.Set("shipid", new PyLong(shipId));
            pilot.Set("stationid", new PyLong(station)); pilot.Set("stationid2", new PyLong(station));
            pilot.Set("solarsystemid", PyNone.Instance); pilot.Set("solarsystemid2", new PyInt(system)); pilot.Set("locationid", new PyLong(station));
            rig.Sessions.Enter(playerId, pilot); await rig.Space.OnCharacterEnteredAsync(pilot, playerId, CancellationToken.None);
            await using (var seed = rig.Source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments (id bigint PRIMARY KEY, ship_types integer[] NOT NULL);
                CREATE TABLE {schema}.series (tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id), id bigint NOT NULL, red_team text NOT NULL DEFAULT 'Red', blue_team text NOT NULL DEFAULT 'Blue', PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES (1, ARRAY[601]); INSERT INTO {schema}.series VALUES (1,1);
                """)) await seed.ExecuteNonQueryAsync();
            var deployed = new PgDeployedStore(rig.Source, rig.Schemas[4]);
            await PersistenceTournamentArenas.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRecoveryAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateParticipantMoveIdsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateParticipantIntentAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRulesAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateAutobansAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRefereeTravelAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(rig.Source, schema); await PersistenceTournamentMatches.MigrateCutoffIntentAsync(rig.Source, schema);
            var arenas = new PersistenceTournamentArenas(rig.Source, rig.Sde, rig.Characters, rig.Items, deployed, rig.Foundations.Events, schema);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(system, refRun.Data.CreatorCharacterId,
                [new TournamentBeaconImport(10645, "Test Arena", [100000000000d,200000000000d,300000000000d], 5),
                 new TournamentBeaconImport(10645, "Outside Arena", [100000001000d,200000000000d,300000000000d], 5)]));
            var beacon = (await arenas.ReadSystemsAsync()).Single().BeaconIds[0];
            var store = new PersistenceTournamentMatches(rig.Source, arenas, rig.Items, deployed, rig.Foundations.Events,
                simulation, rig.Sde, schema, rig.Foundations.Scheduler, rig.Sessions, space: rig.Space, tournamentCharacters: rig.Characters, tournamentShips: rig.Ships);
            await store.ConfigurePhysicalRegionAsync(system, beacon, 500);
            store.RegisterSpaceHandlers(); store.RegisterActionHandlers(); await rig.Foundations.Events.PumpAsync();
            await store.InitializeTournamentRestrictionsAsync();
            rig.Space.TournamentRestricted = store.IsTournamentRestricted;
            var sink = new TournamentWireSink(); referee.Sink = sink; referee.Set("accountRole", new PyLong(GameMasterRoles.Qa));
            var service = new TourneyMgrService(matches: new TourneyMatchCallbacks(store.CreateAsync, store.ReleaseAsync,
                store.ActionAsync, SpaceActionAsync: store.SpaceActionAsync));
            var registry = new ServiceRegistry(); registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => rig.Now);
            var bind = TournamentWireRequest(1, "tourneyMgr", new PyInt(1), "MachoBindObject", new PyInt(system));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(bind)), referee));
            var binding = Assert.IsType<PyTuple>(TournamentWireResult(sink.Packets[^1]));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0])); Assert.NotNull(oid);
            long matchId;
            await using (var seed = rig.Source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state)
                VALUES (1,1,$1,100,600,0,1) RETURNING id
                """)) { seed.Parameters.AddWithValue((long)system); matchId = (long)(await seed.ExecuteScalarAsync())!; }
            await using (var seed = rig.Source.CreateCommand($"""
                WITH referee AS (INSERT INTO {schema}.match_referees(oid,match_id,session_id,character_id)
                    VALUES ($5,$1,$2,$3) RETURNING 1),
                team AS (INSERT INTO {schema}.match_teams(match_id,team,is_locked) VALUES ($1,0,true) RETURNING 1)
                INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES ($1,0,$4,true);
                """))
            {
                seed.Parameters.AddWithValue(matchId); seed.Parameters.AddWithValue(referee.SessionId);
                seed.Parameters.AddWithValue(refRun.Data.CreatorCharacterId); seed.Parameters.AddWithValue(playerId); seed.Parameters.AddWithValue(oid!); await seed.ExecuteNonQueryAsync();
            }
            long callId = 2;
            async Task<PyValue> Invoke(string method, params PyValue[] args)
            {
                var request = TournamentWireRequest(callId++, null, new PyStr(oid!), method, [new PyLong(matchId), .. args]);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), referee));
                return TournamentWireResult(sink.Packets[^1]);
            }
            var forbiddenSink = new TournamentWireSink();
            var forbidden = new ClientSession(forbiddenSink) { SessionId = referee.SessionId + 999, ClientId = referee.ClientId + 999 };
            forbidden.Set("charid", new PyLong(refRun.Data.CreatorCharacterId)); forbidden.Set("solarsystemid", new PyInt(system));
            var denied = TournamentWireRequest(callId++, null, new PyStr(oid!), "TeleportPlayers", new PyLong(matchId), new PyInt(0), new PyInt(0));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(denied)), forbidden));
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(forbiddenSink.Packets[^1]).Args).Items[0]).Value);
            var outside = TournamentWireRequest(callId++, null, new PyStr(oid!), "TeleportPlayers", new PyLong(matchId), new PyInt(0), new PyInt(1));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(outside)), referee));
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(sink.Packets[^1]).Args).Items[0]).Value);
            await using (var count = rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.match_participants"))
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            await Invoke("TeleportPlayers", new PyInt(0), new PyInt(0));
            var placed = await rig.Space.PilotPlaceAsync(pilot, CancellationToken.None);
            Assert.NotNull(placed); Assert.Equal(system, placed.Value.SolarSystemId); Assert.Equal(new double[] {100000000000d,200000000000d,300000000000d}, placed.Value.Position);
            Guid operation;
            await using (var query = rig.Source.CreateCommand($"SELECT outbound_operation_id FROM {schema}.match_participants WHERE character_id=$1"))
            { query.Parameters.AddWithValue(playerId); operation = (Guid)(await query.ExecuteScalarAsync())!; }
            await using (var reset = rig.Source.CreateCommand($"UPDATE {schema}.match_participants SET outbound_complete=false WHERE character_id=$1"))
            { reset.Parameters.AddWithValue(playerId); await reset.ExecuteNonQueryAsync(); }
            await Invoke("TeleportPlayers", new PyInt(0), new PyInt(0));
            await using (var query = rig.Source.CreateCommand($"SELECT outbound_operation_id,outbound_complete FROM {schema}.match_participants WHERE character_id=$1"))
            {
                query.Parameters.AddWithValue(playerId); await using var row = await query.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync()); Assert.Equal(operation, row.GetGuid(0)); Assert.True(row.GetBoolean(1));
            }
            // Force a real post-commit projection read failure without affecting any shared schema.
            await using (var fault = rig.Source.CreateCommand($"""
                CREATE FUNCTION {schema}.break_tournament_projection() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN ALTER TABLE {schema}.match_participants RENAME TO match_participants_projection_missing; RETURN NEW; END $body$;
                CREATE TRIGGER break_tournament_projection AFTER UPDATE OF restrict_move ON {schema}.matches
                FOR EACH ROW EXECUTE FUNCTION {schema}.break_tournament_projection();
                """)) await fault.ExecuteNonQueryAsync();
            try
            {
                var faulted = TournamentWireRequest(callId++, null, new PyStr(oid!), "OverrideMoveRestrict", new PyLong(matchId), PyBool.True);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(faulted)), referee));
                Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(sink.Packets[^1]).Args).Items[0]).Value);
                await using (var committed = rig.Source.CreateCommand($"SELECT restrict_move FROM {schema}.matches WHERE id=$1"))
                { committed.Parameters.AddWithValue(matchId); Assert.True((bool)(await committed.ExecuteScalarAsync())!); }
                Assert.True(store.IsTournamentRestricted(playerId, TournamentRestriction.Move));
                Assert.True(store.IsTournamentRestricted(playerId, TournamentRestriction.Warp));
                Assert.True(store.IsTournamentRestricted(playerId, TournamentRestriction.Lock));
                Assert.ThrowsAny<Exception>(() => rig.Space.Command(pilot, SimCommands.GotoDirection, 1,0,0));
                await Assert.ThrowsAnyAsync<Exception>(async () => await rig.Space.WarpAsync(pilot, beacon, 0, CancellationToken.None));
            }
            finally
            {
                await using var restore = rig.Source.CreateCommand($"""
                    DROP TRIGGER IF EXISTS break_tournament_projection ON {schema}.matches;
                    ALTER TABLE IF EXISTS {schema}.match_participants_projection_missing RENAME TO match_participants;
                    DROP FUNCTION IF EXISTS {schema}.break_tournament_projection();
                    """);
                await restore.ExecuteNonQueryAsync();
            }
            rig.Now += 6000;
            await rig.Foundations.Scheduler.RunDueAsync();
            Assert.True(store.IsTournamentRestricted(playerId, TournamentRestriction.Move));
            Assert.False(store.IsTournamentRestricted(playerId, TournamentRestriction.Warp));
            Assert.False(store.IsTournamentRestricted(playerId, TournamentRestriction.Lock));
            await Invoke("OverrideMoveRestrict", PyBool.True);
            await Invoke("OverrideWarpRestrict", PyBool.False);
            Assert.False(store.IsTournamentRestricted(playerId,TournamentRestriction.Warp));
            await Invoke("OverrideWarpRestrict", PyBool.True);
            Assert.True(store.IsTournamentRestricted(playerId,TournamentRestriction.Warp));
            await Invoke("OverrideLockRestrict", PyBool.True);
            Assert.ThrowsAny<Exception>(() => rig.Space.Command(pilot, SimCommands.GotoDirection, 1,0,0));
            await Assert.ThrowsAnyAsync<Exception>(async () => await rig.Space.WarpAsync(pilot, beacon, 0, CancellationToken.None));
            await Assert.ThrowsAnyAsync<Exception>(async () => await rig.Space.OrderAsync(pilot, TargetingOrder.Lock, [referee.GetInt("shipid")!.Value], CancellationToken.None));
            await Invoke("ReturnTeamPlayers", new PyInt(0));
            Assert.Equal(station, pilot.GetInt("stationid"));
            Assert.Equal(station, (await rig.Items.GetAsync(shipId))!.LocationId);
            await using (var count = rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.match_participants"))
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);

            // Seed a second locked member in this fixture. Disconnect that member
            // after the first actual entry but before its own turn, then reconnect.
            var secondRun = await rig.CreateRun(enter: false);
            var secondId = secondRun.Data.CreatorCharacterId; var secondShip = secondRun.Data.CreatorShipId;
            Assert.True(secondId > playerId); // the service processes locked members in character order
            Assert.NotNull((await rig.Ships.DockAsync(secondId, station, null)).Moved);
            ClientSession NewSecond(long offset)
            {
                var result = new ClientSession(new RecordingSink(rig.Order)) { SessionId = secondId + offset, ClientId = secondId + offset + 100 };
                result.Set("charid", new PyLong(secondId)); result.Set("shipid", new PyLong(secondShip));
                result.Set("stationid", new PyLong(station)); result.Set("stationid2", new PyLong(station));
                result.Set("solarsystemid", PyNone.Instance); result.Set("solarsystemid2", new PyInt(system)); result.Set("locationid", new PyLong(station));
                return result;
            }
            var second = NewSecond(700); rig.Sessions.Enter(secondId, second);
            await rig.Space.OnCharacterEnteredAsync(second, secondId, CancellationToken.None);
            await using (var add = rig.Source.CreateCommand($"INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES ($1,0,$2,true)"))
            { add.Parameters.AddWithValue(matchId); add.Parameters.AddWithValue(secondId); await add.ExecuteNonQueryAsync(); }
            var disconnected = false;
            rig.Space.DockedLocationChanged = (changed, _, _) =>
            {
                if (!disconnected && ReferenceEquals(changed, pilot)) { disconnected = true; second.Close(); }
            };
            var partial = TournamentWireRequest(callId++, null, new PyStr(oid!), "TeleportPlayers", new PyLong(matchId), new PyInt(0), new PyInt(0));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(partial)), referee));
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(sink.Packets[^1]).Args).Items[0]).Value);
            Assert.True(disconnected);
            var identities = new Dictionary<long, (Guid Operation, bool Complete)>();
            await using (var query = rig.Source.CreateCommand($"SELECT character_id,outbound_operation_id,outbound_complete FROM {schema}.match_participants ORDER BY character_id"))
            await using (var rows = await query.ExecuteReaderAsync())
                while (await rows.ReadAsync()) identities.Add(rows.GetInt64(0), (rows.GetGuid(1), rows.GetBoolean(2)));
            Assert.Equal(2, identities.Count); Assert.True(identities[playerId].Complete); Assert.False(identities[secondId].Complete);
            var firstEntryCount = rig.Simulator.PublicEnters.Count(entry => entry.Enter.SessionId == (ulong)pilot.SessionId);
            Assert.True(firstEntryCount >= 1);
            rig.Space.DockedLocationChanged = null;
            var reconnected = NewSecond(900); rig.Sessions.Enter(secondId, reconnected);
            await rig.Space.OnCharacterEnteredAsync(reconnected, secondId, CancellationToken.None);
            await Invoke("TeleportPlayers", new PyInt(0), new PyInt(0));
            Assert.Equal(firstEntryCount, rig.Simulator.PublicEnters.Count(entry => entry.Enter.SessionId == (ulong)pilot.SessionId));
            await using (var query = rig.Source.CreateCommand($"SELECT character_id,outbound_operation_id,outbound_complete FROM {schema}.match_participants ORDER BY character_id"))
            await using (var rows = await query.ExecuteReaderAsync())
                while (await rows.ReadAsync()) { var id = rows.GetInt64(0); Assert.Equal(identities[id].Operation, rows.GetGuid(1)); Assert.True(rows.GetBoolean(2)); }
            var firstPose = await rig.Space.PilotPlaceAsync(pilot, CancellationToken.None);
            var secondPose = await rig.Space.PilotPlaceAsync(reconnected, CancellationToken.None);
            Assert.NotNull(firstPose); Assert.NotNull(secondPose); Assert.Equal(system, firstPose.Value.SolarSystemId); Assert.Equal(system, secondPose.Value.SolarSystemId);
            Assert.NotEqual(shipId, secondShip);
            await Invoke("ReturnTeamPlayers", new PyInt(0));
            Assert.Equal(station, pilot.GetInt("stationid")); Assert.Equal(station, reconnected.GetInt("stationid"));

            // Seed locked roster details from each actual ship and fitting, then
            // establish each arena arrival through the registered RPC.
            await using (var setup = rig.Source.CreateCommand($"""
                WITH team AS (INSERT INTO {schema}.match_teams(match_id,team,is_locked) VALUES ($1,1,true)),
                     member AS (UPDATE {schema}.match_members SET team=1 WHERE match_id=$1 AND character_id=$2)
                UPDATE {schema}.matches SET state=2 WHERE id=$1
                """))
            { setup.Parameters.AddWithValue(matchId); setup.Parameters.AddWithValue(secondId); await setup.ExecuteNonQueryAsync(); }
            await using (var rules = rig.Source.CreateCommand($"INSERT INTO {schema}.tournament_rules(tournament_id,prohibited_fit_types) VALUES (1,ARRAY[]::integer[])"))
                await rules.ExecuteNonQueryAsync();
            foreach (var id in new[] { playerId, secondId })
            {
                var snapshot = (await rig.Ships.LoadAsync(id))!;
                var contents = await rig.Items.InLocationAsync(snapshot.Ship.Id, id);
                var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                { shipId = snapshot.Ship.Id, typeId = snapshot.Ship.TypeId, contents = contents.OrderBy(x => x.Id).Select(x => new { x.Id, x.TypeId, x.Flag, x.Quantity, x.Singleton }).ToArray() }));
                await using var roster = rig.Source.CreateCommand($"""
                    WITH points AS (INSERT INTO {schema}.tournament_ship_points(tournament_id,type_id,points) VALUES (1,$4,10) ON CONFLICT DO NOTHING)
                    INSERT INTO {schema}.match_roster_details(match_id,team,character_id,ship_type_id,ship_id,location_id,points,errors,fit_hash)
                    VALUES ($1,$2,$3,$4,$5,$6,10,ARRAY[]::text[],$7)
                    """);
                roster.Parameters.AddWithValue(matchId); roster.Parameters.AddWithValue(id == playerId ? 0 : 1); roster.Parameters.AddWithValue(id);
                roster.Parameters.AddWithValue(snapshot.Ship.TypeId); roster.Parameters.AddWithValue(snapshot.Ship.Id); roster.Parameters.AddWithValue(station); roster.Parameters.AddWithValue(hash);
                await roster.ExecuteNonQueryAsync();
            }
            await Invoke("TeleportPlayers", new PyInt(0), new PyInt(0));
            await Invoke("TeleportPlayers", new PyInt(1), new PyInt(0));
            await Invoke("ReturnAllPlayers");
            Assert.False(rig.Space.IsInSpace(pilot)); Assert.False(rig.Space.IsInSpace(reconnected));
            await using (var returned=rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.match_participants WHERE match_id=$1"))
            { returned.Parameters.AddWithValue(matchId); Assert.Equal(0L,(long)(await returned.ExecuteScalarAsync())!); }
            await Invoke("TeleportPlayers", new PyInt(0), new PyInt(0));
            await Invoke("TeleportPlayers", new PyInt(1), new PyInt(0));
            Assert.NotNull(await rig.Space.PilotPlaceAsync(pilot, CancellationToken.None));
            Assert.NotNull(await rig.Space.PilotPlaceAsync(reconnected, CancellationToken.None));
            // A public watcher never invokes a camera RPC. The docked and actual
            // private-scene pilots must not receive the public-scene broadcast.
            var watcherRun = await rig.CreateRun(enter: false);
            var watcher = await rig.ConnectPublic(watcherRun);
            var watcherSink = new TournamentWireSink(); watcher.Sink = watcherSink;
            var remoteRun = await rig.CreateRun(enter: false);
            var remote = await rig.ConnectPublic(remoteRun);
            var remotePose = (await rig.Space.PilotPoseAsync(remote, CancellationToken.None))!.Value;
            const int remoteSystem = 30002187;
            Assert.NotEqual(remoteSystem, system);
            Assert.Null(await rig.Space.RelocateAsync(remote, remoteSystem, [100000000000d,200000000000d,300000000000d], remotePose.Direction, CancellationToken.None, Guid.NewGuid().ToString("D")));
            Assert.Equal(remoteSystem, rig.Space.SolarSystemOf(remote));
            var remoteSink = new TournamentWireSink(); remote.Sink = remoteSink;
            // Spoof only the session attribute; the authoritative scene stays remote.
            remote.Set("solarsystemid", new PyInt(system));
            Assert.Contains(remote, rig.Sessions.InSpace(system));
            var dockedRun = await rig.CreateRun(enter: false);
            Assert.NotNull((await rig.Ships.DockAsync(dockedRun.Data.CreatorCharacterId, station, null)).Moved);
            var dockedSink = new TournamentWireSink();
            var docked = new ClientSession(dockedSink) { SessionId = dockedRun.Data.CreatorCharacterId + 1000, ClientId = dockedRun.Data.CreatorCharacterId + 1100 };
            docked.Set("charid", new PyLong(dockedRun.Data.CreatorCharacterId)); docked.Set("shipid", new PyLong(dockedRun.Data.CreatorShipId));
            docked.Set("stationid", new PyLong(station)); docked.Set("stationid2", new PyLong(station)); docked.Set("solarsystemid2", new PyInt(system));
            rig.Sessions.Enter(dockedRun.Data.CreatorCharacterId, docked);
            await rig.Space.OnCharacterEnteredAsync(docked, dockedRun.Data.CreatorCharacterId, CancellationToken.None);
            var savedNow = rig.Now;
            var privateSetup = await rig.CreateRun(enter: false);
            rig.Now = privateSetup.Data.TraceExpiresAtMs + 1;
            await rig.Transaction(async (c, tx) => await rig.Runtime.ExpireIfDueAsync(c, tx, privateSetup.InstanceId));
            await rig.Drain();
            var privateSession = await rig.ConnectPublic(privateSetup);
            var privateCharacter = (await rig.Characters.GetAsync(privateSetup.Data.CreatorCharacterId))!;
            await rig.Transaction((c, tx) => rig.Characters.SetStationAsync(c, tx, privateCharacter.Id, privateCharacter.HomeStationId));
            long filament = 0;
            await rig.Transaction(async (c, tx) => filament = await rig.Items.CreateAsync(c, tx,
                new Eve.Inventory.NewItem(privateSetup.Data.FilamentTypeId, privateSetup.Data.CreatorCharacterId,
                    privateSetup.Data.CreatorShipId, Eve.Inventory.ItemFlags.Cargo, 2)));
            await rig.Service.CallAsync(new ServiceCall { Session = privateSession, Method = "AbyssalEntranceDeployment", Args = [new PyLong(filament), new PyInt(1)] });
            await rig.Drain(); Assert.True(rig.Space.IsInAbyssalRun(privateSession)); rig.Now = savedNow;
            var privateSink = new TournamentWireSink(); privateSession.Sink = privateSink;
            privateSession.Set("solarsystemid", new PyInt(system));
            var redSink = new TournamentWireSink(); var blueSink = new TournamentWireSink();
            pilot.Sink = redSink; reconnected.Sink = blueSink;
            async Task<(int State, long? Start)> MatchState()
            {
                await using var command = rig.Source.CreateCommand($"SELECT state,start_ms FROM {schema}.matches WHERE id=$1");
                command.Parameters.AddWithValue(matchId); await using var row = await command.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync()); return (row.GetInt32(0), row.IsDBNull(1) ? null : row.GetInt64(1));
            }
            var sourcePose = (await rig.Space.PilotPoseAsync(pilot, CancellationToken.None))!.Value;
            Assert.Null(await rig.Space.RelocateAsync(pilot, system, [100000002000d,200000000000d,300000000000d], sourcePose.Direction, CancellationToken.None, Guid.NewGuid().ToString("D")));
            var outsideStart = TournamentWireRequest(callId++, null, new PyStr(oid!), "StartCountdown", new PyLong(matchId));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(outsideStart)), referee));
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(sink.Packets[^1]).Args).Items[0]).Value);
            Assert.Equal((2, (long?)null), await MatchState());
            Assert.True(store.IsTournamentRestricted(playerId, TournamentRestriction.Move));
            Assert.True(store.IsTournamentRestricted(playerId, TournamentRestriction.Warp));
            Assert.True(store.IsTournamentRestricted(playerId, TournamentRestriction.Lock));
            Assert.Null(await rig.Space.RelocateAsync(pilot, system, sourcePose.Position, sourcePose.Direction, CancellationToken.None, Guid.NewGuid().ToString("D")));
            rig.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // The opted-in automatic path registers the actual arrived roster with the live simulator.
            if (automaticDeadline)
            {
                await using var duration=rig.Source.CreateCommand($"UPDATE {schema}.matches SET match_length=120 WHERE id=$1");
                duration.Parameters.AddWithValue(matchId); await duration.ExecuteNonQueryAsync();
            }
            await using (var policy=rig.Source.CreateCommand($"INSERT INTO {schema}.tournament_completion_policies(tournament_id,policy) VALUES (1,'alliance_tournament')")) await policy.ExecuteNonQueryAsync();
            await Invoke("StartCountdown");
            var automatic=await MatchState(); Assert.Equal(3,automatic.State); Assert.NotNull(automatic.Start);
            await using (var intent=rig.Source.CreateCommand($"SELECT state,start_ms FROM {schema}.tournament_cutoff_intents WHERE match_id=$1"))
            { intent.Parameters.AddWithValue(matchId); await using var row=await intent.ExecuteReaderAsync(); Assert.True(await row.ReadAsync()); Assert.Equal("committed",row.GetString(0)); Assert.Equal(automatic.Start.Value,row.GetInt64(1)); }
            if (automaticDeadline)
            {
                var startMs=automatic.Start!.Value;
                var waitStart=startMs-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+200;
                if (waitStart > 0) await Task.Delay(TimeSpan.FromMilliseconds(waitStart));
                rig.Now=startMs; await rig.Foundations.Scheduler.RunDueAsync();
                Assert.Equal(5,(await MatchState()).State);
                var deadline=startMs+120000;
                var waitDeadline=deadline-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000;
                if (waitDeadline > 0) await Task.Delay(TimeSpan.FromMilliseconds(waitDeadline));
                long generation;
                await using (var lookup=rig.Source.CreateCommand($"SELECT generation FROM {schema}.tournament_cutoff_intents WHERE match_id=$1 AND state='committed'"))
                { lookup.Parameters.AddWithValue(matchId); generation=(long)(await lookup.ExecuteScalarAsync())!; }
                var evidence=await simulation.ReadTournamentCutoffAsync(new SimReadTournamentCutoff(SimClient.NextRequestId(),SimSceneKey.SolarSystem(system),matchId,generation,Py.FileTimeFromUnixMs(startMs),Py.FileTimeFromUnixMs(deadline)));
                Assert.Equal(SimTournamentCutoffStatus.Ready,evidence.Status); Assert.Equal(2,evidence.Evidence.Count);
                Assert.All(evidence.Evidence, item => Assert.Equal(SimTournamentCutoffEvidence.Alive,item.Evidence));
                rig.Now=deadline+1000; await rig.Foundations.Scheduler.RunDueAsync();
                await rig.Foundations.Scheduler.RunDueAsync();
                await using var pending=rig.Source.CreateCommand($"SELECT state,settlement_pending,winner_team FROM {schema}.matches WHERE id=$1");
                pending.Parameters.AddWithValue(matchId); await using var row=await pending.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync()); Assert.Equal(5,row.GetInt32(0)); Assert.True(row.GetBoolean(1)); Assert.True(row.IsDBNull(2));
                return;
            }
            Assert.Equal(PyBool.True,await Invoke("AbortCountdown"));
            await using (var intent=rig.Source.CreateCommand($"SELECT state FROM {schema}.tournament_cutoff_intents WHERE match_id=$1"))
            { intent.Parameters.AddWithValue(matchId); Assert.Equal("release_pending",await intent.ExecuteScalarAsync()); }
            await using (var policy=rig.Source.CreateCommand($"DELETE FROM {schema}.tournament_completion_policies WHERE tournament_id=1")) await policy.ExecuteNonQueryAsync();
            store.CutoffRegistrar=null; await rig.Foundations.Events.PumpAsync();
            foreach (var receiver in new[] { sink,watcherSink,dockedSink,privateSink,remoteSink,redSink,blueSink }) receiver.Packets.Clear();
            await Invoke("StartCountdown");
            var firstStart = await MatchState(); Assert.Equal(3, firstStart.State); Assert.NotNull(firstStart.Start);
            await rig.Foundations.Events.PumpAsync();
            foreach (var (receiver, team) in new[] { (redSink, 0), (blueSink, 1) })
            {
                var status = Assert.Single(TournamentWireNotifications(receiver), x => x.Name == "OnTournamentMatchStateChange");
                Assert.Equal(4, status.Args.Length); Assert.Equal(new PyInt(90), status.Args[0]);
                Assert.Equal(new PyInt(90), status.Args[1]); Assert.Equal(new PyInt(team), status.Args[2]);
                var fields = Assert.IsType<PyDict>(status.Args[3]); Assert.Equal(5, fields.Entries.Count);
                Assert.Contains(fields.Entries, x => (x.Key is PyStr { Value: "matchStartTime" } || x.Key is PyBytes bytes && System.Text.Encoding.UTF8.GetString(bytes.Value.Span) == "matchStartTime") && x.Value.Equals(new PyLong(Py.FileTimeFromUnixMs(firstStart.Start!.Value))));
            }
            var watcherEvent = Assert.Single(TournamentWireNotifications(watcherSink), x => x.Name == "OnPreMatchStart");
            Assert.Equal(4, watcherEvent.Args.Length); Assert.Equal(new PyLong(matchId), watcherEvent.Args[0]);
            Assert.Equal("Red", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(watcherEvent.Args[1]).Value.Span));
            Assert.Equal("Blue", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(watcherEvent.Args[2]).Value.Span));
            Assert.IsType<PyInt>(watcherEvent.Args[3]);
            Assert.DoesNotContain(TournamentWireNotifications(dockedSink), x => x.Name == "OnPreMatchStart");
            Assert.DoesNotContain(TournamentWireNotifications(privateSink), x => x.Name == "OnPreMatchStart");
            Assert.DoesNotContain(TournamentWireNotifications(remoteSink), x => x.Name == "OnPreMatchStart");
            var camera = Assert.Single(TournamentWireNotifications(sink), x => x.Name == "OnPreMatchStart");
            Assert.Equal(4, camera.Args.Length); Assert.Equal(new PyLong(matchId), camera.Args[0]);
            Assert.Equal("Red", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(camera.Args[1]).Value.Span));
            Assert.Equal("Blue", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(camera.Args[2]).Value.Span)); Assert.IsType<PyInt>(camera.Args[3]);
            await Invoke("StartCountdown"); await rig.Foundations.Events.PumpAsync();
            Assert.Equal(firstStart, await MatchState());
            Assert.Single(TournamentWireNotifications(sink), x => x.Name == "OnPreMatchStart");
            Assert.Single(TournamentWireNotifications(watcherSink), x => x.Name == "OnPreMatchStart");
            Assert.DoesNotContain(TournamentWireNotifications(dockedSink), x => x.Name == "OnPreMatchStart");
            Assert.DoesNotContain(TournamentWireNotifications(privateSink), x => x.Name == "OnPreMatchStart");
            Assert.DoesNotContain(TournamentWireNotifications(remoteSink), x => x.Name == "OnPreMatchStart");
            Assert.Single(TournamentWireNotifications(redSink), x => x.Name == "OnTournamentMatchStateChange");
            Assert.Equal(PyBool.True, await Invoke("AbortCountdown"));
            Assert.Equal((2, (long?)null), await MatchState());
            rig.Now += 5000; await Invoke("StartCountdown");
            var restarted = await MatchState(); Assert.Equal(3, restarted.State); Assert.NotNull(restarted.Start);
            rig.Now = restarted.Start!.Value - 2000;
            Assert.Equal(PyBool.False, await Invoke("AbortCountdown"));
            rig.Now = restarted.Start.Value; await rig.Foundations.Scheduler.RunDueAsync();
            Assert.Equal((5, restarted.Start), await MatchState());
            await Invoke("MatchCompleted");
            Assert.Equal((6, restarted.Start), await MatchState());
            await using (var count = rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.match_participants"))
                Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
        }
    }
    private sealed class TournamentWireSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static IEnumerable<(string Name, PyValue[] Args)> TournamentWireNotifications(TournamentWireSink sink)
    {
        foreach (var packet in sink.Packets.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification }) continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var args = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items;
            yield return (((PyStr)address[1]).Value, [.. args]);
        }
    }

    private static PyValue TournamentWireResult(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        var payload = Assert.IsType<PyTuple>(packet[4]);
        return Assert.IsType<PySubStream>(payload.Items[0]).Decoded!;
    }

    private static PyValue TournamentWireRequest(long callId, string? service, PyValue remote, string method, params PyValue[] args)
    {
        var body = new PyTuple([remote, new PyStr(method), new PyTuple(args), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(42));
        var destination = new MachoAddress(AddressKind.Node, Service: service, CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(), new PyInt(7), payload,
            new PyDict([]), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            PyNone.Instance, PyNone.Instance, PyNone.Instance]));
    }

}
