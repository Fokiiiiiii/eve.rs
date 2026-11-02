using Eve.Data;
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
    public async Task Tournament_camera_rejects_an_actual_private_scene()
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
            var player = await rig.CreateRun(enter: false);
            var system = rig.Space.SolarSystemOf(referee)!.Value;
            await using (var seed = rig.Source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments (id bigint PRIMARY KEY, ship_types integer[] NOT NULL, maximum_points integer NOT NULL, external_ruleset_id bigint);
                CREATE TABLE {schema}.series (tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id), id bigint NOT NULL, red_team text NOT NULL DEFAULT 'Red', blue_team text NOT NULL DEFAULT 'Blue', PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES (1, ARRAY[601],100,NULL); INSERT INTO {schema}.series(tournament_id,id) VALUES (1,1);
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
            await PersistenceTournamentMatches.MigrateRefereeLeaseGenerationAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(rig.Source, schema);
            var arenas = new PersistenceTournamentArenas(rig.Source, rig.Sde, rig.Characters, rig.Items, deployed, rig.Foundations.Events, schema);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(system, run.Data.CreatorCharacterId,
                [new TournamentBeaconImport(10645, "Camera Arena", [100000000000d,200000000000d,300000000000d], 5)]));
            var store = new PersistenceTournamentMatches(rig.Source, arenas, rig.Items, deployed, rig.Foundations.Events,
                simulation, rig.Sde, schema, rig.Foundations.Scheduler, rig.Sessions, space: rig.Space);
            store.RegisterActionHandlers(); await rig.Foundations.Events.PumpAsync();
            var sink = new TournamentWireSink(); referee.Sink = sink; referee.Set("accountRole", new PyLong(GameMasterRoles.Qa));
            var service = new TourneyMgrService(matches: new TourneyMatchCallbacks(store.CreateAsync, store.ReleaseAsync, store.ActionAsync,
                CameraDetailsAsync: store.CameraDetailsAsync, OtherMatchAsync: store.OtherMatchAsync,
                ReleaseStampedAsync: store.ReleaseStampedAsync, RemoveRefereeAsync: store.RemoveRefereeAsync));
            var registry = new ServiceRegistry(); registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => rig.Now);
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                TournamentWireRequest(1, "tourneyMgr", new PyInt(1), "MachoBindObject", new PyInt(system)))), referee));
            var binding = Assert.IsType<PyTuple>(TournamentWireResult(sink.Packets[^1]));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0])); Assert.NotNull(oid);
            long matchId;
            await using (var seed = rig.Source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state,start_ms)
                VALUES (1,1,$1,100,600,60,1,$2) RETURNING id
                """)) { seed.Parameters.AddWithValue((long)system); seed.Parameters.AddWithValue(rig.Now); matchId = (long)(await seed.ExecuteScalarAsync())!; }
            long call = 2;
            async Task<PyValue> Invoke(string method, params PyValue[] args)
            {
                var request = TournamentWireRequest(call++, null, new PyStr(oid!), method, args);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), referee));
                return TournamentWireResult(sink.Packets[^1]);
            }
            Assert.Same(PyBool.True, await Invoke("IsOtherMatchInProgress"));
            var early = TournamentWireRequest(call++, null, new PyStr(oid!), "GetTeamMembers", new PyLong(matchId));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(early)), referee));
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(sink.Packets[^1]).Args).Items[0]).Value);
            await using (var clear = rig.Source.CreateCommand($"DELETE FROM {schema}.matches WHERE id=$1"))
            { clear.Parameters.AddWithValue(matchId); await clear.ExecuteNonQueryAsync(); }
            var created = Assert.IsType<PyTuple>(await Invoke("CreateMatch", new PyInt(1), new PyInt(1), new PyInt(100), new PyInt(600), new PyInt(60), PyNone.Instance));
            matchId = created.Items[0] switch { PyLong value => value.Value, PyInt value => value.Value, _ => throw new InvalidOperationException("Invalid match ID") };
            Assert.Single(Assert.IsType<PyList>(created.Items[1]).Items);
            Assert.Same(PyBool.False, await Invoke("IsOtherMatchInProgress"));
            await using (var seed = rig.Source.CreateCommand($"""
                WITH referee AS (SELECT $1::text,$3::bigint,$4::bigint),
                member AS (INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES ($2,0,$5,true)),
                detail AS (INSERT INTO {schema}.match_roster_details VALUES ($2,0,$5,601,$6,30000142,7,ARRAY[]::text[],decode('00','hex'))),
                ban AS (INSERT INTO {schema}.match_bans VALUES ($2,1,601))
                UPDATE {schema}.matches SET state=3,start_ms=$7 WHERE id=$2
                """))
            {
                seed.Parameters.AddWithValue(oid!); seed.Parameters.AddWithValue(matchId); seed.Parameters.AddWithValue(referee.SessionId);
                seed.Parameters.AddWithValue(run.Data.CreatorCharacterId); seed.Parameters.AddWithValue(player.Data.CreatorCharacterId); seed.Parameters.AddWithValue(player.Data.CreatorShipId); seed.Parameters.AddWithValue(rig.Now);
                await seed.ExecuteNonQueryAsync();
            }
            Assert.Same(PyBool.False, await Invoke("IsOtherMatchInProgress"));
            // Seeded state exercises camera reads only; actual countdown requires both real teams to arrive.
            var matchStart = rig.Now;
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
            await rig.Drain();
            Assert.True(rig.Space.IsInAbyssalRun(privateSession));
            var privateSink = new TournamentWireSink(); privateSession.Sink = privateSink;
            privateSession.Set("solarsystemid", new PyInt(system));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                TournamentWireRequest(call++, "tourneyMgr", new PyInt(1), "MachoBindObject", new PyInt(system)))), privateSession));
            var privateBinding = Assert.IsType<PyTuple>(TournamentWireResult(privateSink.Packets[^1]));
            var privateOid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(privateBinding.Items[0])); Assert.NotNull(privateOid);
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                TournamentWireRequest(call++, null, new PyStr(privateOid!), "GetTeamMembers", new PyLong(matchId)))), privateSession));
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(privateSink.Packets[^1]).Args).Items[0]).Value);
            var teams = Assert.IsType<PyTuple>(await Invoke("GetTeamMembers", new PyLong(matchId)));
            var red = Assert.IsType<PyList>(teams.Items[0]); var member = Assert.IsType<PyTuple>(Assert.Single(red.Items));
            Assert.Equal(player.Data.CreatorCharacterId, Assert.IsType<PyLong>(member.Items[0]).Value);
            Assert.Equal(player.Data.CreatorShipId, Assert.IsType<PyLong>(member.Items[2]).Value);
            Assert.Equal(7, Assert.IsType<PyInt>(member.Items[3]).Value);
            Assert.Empty(Assert.IsType<PyList>(teams.Items[1]).Items);
            var fancy = Assert.IsType<PyTuple>(await Invoke("GetFancyDetails", new PyLong(matchId)));
            Assert.IsType<PyNone>(fancy.Items[0]); Assert.IsType<PyNone>(fancy.Items[1]);
            var details = Assert.IsType<PyTuple>(fancy.Items[2]); Assert.Equal(3, Assert.IsType<PyInt>(details.Items[0]).Value);
            Assert.Equal(Py.FileTimeFromUnixMs(matchStart), Assert.IsType<PyLong>(details.Items[1]).Value);
            Assert.Empty(Assert.IsType<PyList>(Assert.IsType<PyTuple>(fancy.Items[3]).Items[0]).Items);
            Assert.Single(Assert.IsType<PyList>(Assert.IsType<PyTuple>(fancy.Items[3]).Items[1]).Items);
            var outsiderSink = new TournamentWireSink();
            var outsider = new ClientSession(outsiderSink) { SessionId = referee.SessionId + 1, ClientId = referee.ClientId + 1, CharacterId = run.Data.CreatorCharacterId };
            outsider.Set("solarsystemid", new PyInt(system)); outsider.Set("accountRole", new PyLong(GameMasterRoles.Qa));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                TournamentWireRequest(call++, null, new PyStr(oid!), "RemoveSelfFromReferees", new PyLong(matchId)))), outsider));
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(outsiderSink.Packets[^1]).Args).Items[0]).Value);
            Assert.IsType<PyNone>(await Invoke("RemoveSelfFromReferees", new PyLong(matchId + 1)));
            await using (var count = rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.match_referees WHERE oid=$1"))
            { count.Parameters.AddWithValue(oid!); Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!); }
            Assert.IsType<PyNone>(await Invoke("RemoveSelfFromReferees", new PyLong(matchId)));
            await using (var count = rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.match_referees WHERE oid=$1"))
            { count.Parameters.AddWithValue(oid!); Assert.Equal  (0L, (long)(await count.ExecuteScalarAsync())!); }
        }
        finally
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
        }
    }
}
