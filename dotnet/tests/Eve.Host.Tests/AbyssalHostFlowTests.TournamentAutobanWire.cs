using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
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
    public async Task Tournament_autobans_registered_wire_prompts_connected_captains()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        var suffix = Guid.NewGuid().ToString("N")[..10]; var schema = "tw_" + suffix; var fleet = "wf_" + suffix;
        try
        {
            string? endpoint = null; using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal)) { endpoint = line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint); _ = process.StandardError.ReadToEndAsync(); _ = process.StandardOutput.ReadToEndAsync();
            await using var simulation = SimClient.ToTcpSocket(endpoint); simulation.Start();
            using var connected = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!simulation.IsConnected) await Task.Delay(20, connected.Token);
            await using var rig = await Rig.Create(simulation); rig.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var refereeRun = await rig.CreateRun(enter: false); var referee = await rig.ConnectPublic(refereeRun);
            var pilotRun = await rig.CreateRun(enter: false); var pilotId = pilotRun.Data.CreatorCharacterId; var shipId = pilotRun.Data.CreatorShipId;
            await rig.Transaction(async (c, tx) => { await rig.Items.CreateAsync(c, tx,
                new NewItem(2048, pilotId, shipId, ItemFlags.HiSlot0, 1, Singleton: true)); });
            var pilot = await rig.ConnectPublic(pilotRun);
            var ship = (await rig.Items.GetAsync(shipId))!;
            var system = rig.Space.SolarSystemOf(referee)!.Value;
            await using (var seed = rig.Source.CreateCommand($"""
                CREATE SCHEMA {schema}; CREATE SCHEMA {fleet};
                CREATE TABLE {schema}.tournaments(id bigint PRIMARY KEY,ship_types integer[] NOT NULL,maximum_points integer NOT NULL,external_ruleset_id bigint);
                CREATE TABLE {schema}.series(tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id),id bigint NOT NULL,PRIMARY KEY(tournament_id,id));
                CREATE TABLE {fleet}.fleets(fleet_id bigint PRIMARY KEY,auto_join_squad_id bigint,max_member_count smallint NOT NULL DEFAULT 256,
                    is_free_move boolean NOT NULL DEFAULT false,advert_active boolean NOT NULL DEFAULT false,motd text NOT NULL DEFAULT '');
                CREATE TABLE {fleet}.fleet_members(fleet_id bigint NOT NULL REFERENCES {fleet}.fleets,character_id bigint PRIMARY KEY,wing_id bigint NOT NULL DEFAULT -1,
                    squad_id bigint NOT NULL DEFAULT -1,role smallint NOT NULL DEFAULT 4,job smallint NOT NULL DEFAULT 0,
                    accepts_fleet_warp boolean NOT NULL DEFAULT true,accepts_conduit_jumps boolean NOT NULL DEFAULT true,
                    accepts_fleet_regroups boolean NOT NULL DEFAULT true,ship_type_id bigint,joined_at timestamptz NOT NULL DEFAULT now());
                CREATE TABLE {fleet}.fleet_wings(fleet_id bigint,wing_id bigint,name text,position integer);
                CREATE TABLE {fleet}.fleet_squads(fleet_id bigint,squad_id bigint,wing_id bigint,name text,position integer);
                CREATE TABLE {fleet}.fleet_join_requests(fleet_id bigint,character_id bigint,corporation_id bigint,alliance_id bigint,war_faction_id bigint,security_status double precision,created_at timestamptz);
                INSERT INTO {schema}.tournaments VALUES(1,ARRAY[{ship.TypeId}],100,NULL); INSERT INTO {schema}.series VALUES(1,1);
                INSERT INTO {fleet}.fleets(fleet_id) VALUES(10); INSERT INTO {fleet}.fleet_members(fleet_id,character_id) VALUES(10,{pilotId});
                """)) await seed.ExecuteNonQueryAsync();
            var deployed = new PgDeployedStore(rig.Source, rig.Schemas[4]);
            await PersistenceTournamentArenas.MigrateAsync(rig.Source, schema); await PersistenceTournamentMatches.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateFleetAsync(rig.Source, schema); await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateAutobansAsync(rig.Source, schema); await PersistenceTournamentMatches.MigrateRulesAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(rig.Source, schema); await PersistenceTournamentMatches.MigrateRosterAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(rig.Source, schema); await PersistenceTournamentMatches.MigratePostgameAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(rig.Source, schema); await PersistenceTournamentMatches.MigrateCutoffIntentAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRefereeLeaseGenerationAsync(rig.Source, schema);
            await using (var rules = rig.Source.CreateCommand($"INSERT INTO {schema}.tournament_rules VALUES (1,ARRAY[2048]); INSERT INTO {schema}.tournament_ship_points VALUES (1,{ship.TypeId},5)")) await rules.ExecuteNonQueryAsync();
            var arenas = new PersistenceTournamentArenas(rig.Source, rig.Sde, rig.Characters, rig.Items, deployed, rig.Foundations.Events, schema);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(system, refereeRun.Data.CreatorCharacterId,
                [new TournamentBeaconImport(10645, "Roster Arena", [100000000000d,200000000000d,300000000000d], 5)]));
            var store = new PersistenceTournamentMatches(rig.Source, arenas, rig.Items, deployed, rig.Foundations.Events,
                simulation, rig.Sde, schema, rig.Foundations.Scheduler, rig.Sessions, fleet, rig.Space, rig.Ships, rig.Characters);
            store.RegisterFleetHandlers(); store.RegisterActionHandlers(); await rig.Foundations.Events.PumpAsync();
            var refSink = new TournamentWireSink(); referee.Sink = refSink; referee.Set("accountRole", new PyLong(GameMasterRoles.Qa));
            var pilotSink = new TournamentWireSink(); pilot.Sink = pilotSink;
            var service = new TourneyMgrService(matches: new TourneyMatchCallbacks(store.CreateAsync, store.ReleaseAsync, store.ActionAsync,
                NominateFleetAsync: store.NominateFleetAsync, AcceptFleetAsync: store.AcceptFleetAsync, RosterActionAsync: store.RosterActionAsync,
                ReleaseStampedAsync: store.ReleaseStampedAsync));
            var registry = new ServiceRegistry(); registry.Register(service); var dispatcher = new PacketDispatcher(registry, () => rig.Now);
            long call = 1;
            async Task<PyValue> Dispatch(ClientSession session, string? svc, PyValue remote, string method, params PyValue[] args)
            {
                var request = TournamentWireRequest(call++, svc, remote, method, args);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
                var sink = ReferenceEquals(session, referee) ? refSink : pilotSink;
                return TournamentWireResult(sink.Packets[^1]);
            }
            var binding = Assert.IsType<PyTuple>(await Dispatch(referee, "tourneyMgr", new PyInt(1), "MachoBindObject", new PyInt(system)));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0])); Assert.NotNull(oid);
            var created = Assert.IsType<PyTuple>(await Dispatch(referee, null, new PyStr(oid!), "CreateMatch", new PyInt(1), new PyInt(1),
                new PyInt(100), new PyInt(600), new PyInt(0), PyNone.Instance));
            var matchId = created.Items[0] switch { PyLong value => value.Value, PyInt value => value.Value, _ => throw new InvalidOperationException("Invalid match ID") };
            var redId = pilotId;
            var blueId = refereeRun.Data.CreatorCharacterId;
            var kwargs = Py.Dict(("redCharID",new PyLong(redId)),("blueCharID",new PyLong(blueId)),("totalNumBans",new PyInt(1)));
            var sourceAddress = new MachoAddress(AddressKind.Client, CallId: new PyInt(call), ClientId: new PyInt(42));
            var destination = new MachoAddress(AddressKind.Node, Service: null, CallId: new PyInt(call++), NodeId: new PyInt(MachoMessage.ProxyNodeId));
            var body = new PyTuple([new PyStr(oid!), new PyStr("StartAutobanning"), new PyTuple([new PyLong(matchId),new PyInt(1)]), kwargs]);
            var payload = new PyTuple([new PyTuple([PyBool.True,new PySubStream(body)])]);
            var request = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),new PyTuple([
                new PyInt(MachoMessage.CallReq),sourceAddress.Encode(),destination.Encode(),new PyInt(7),payload,new PyDict([]),
                PyNone.Instance,PyNone.Instance,PyNone.Instance,PyNone.Instance,PyNone.Instance,PyNone.Instance,PyNone.Instance,PyNone.Instance]));
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)),referee));
            Assert.IsType<PyNone>(TournamentWireResult(refSink.Packets[^1]));
            await rig.Foundations.Events.PumpAsync();
            var redPrompt = Assert.Single(TournamentWireNotifications(pilotSink), n => n.Name == "OnTournamentPerformBan");
            var bluePrompt = Assert.Single(TournamentWireNotifications(refSink), n => n.Name == "OnTournamentPerformBan");
            Assert.Equal(new PyInt(1), redPrompt.Args[1]); Assert.Equal(new PyInt(1), bluePrompt.Args[1]);
            Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(rig.Now + 60000)), redPrompt.Args[3]);
            await using (var count = rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.autoban_choices WHERE round_id IN (SELECT id FROM {schema}.autoban_rounds WHERE match_id=$1 AND NOT finished)"))
            { count.Parameters.AddWithValue(matchId); Assert.Equal(2L,(long)(await count.ExecuteScalarAsync())!); }
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)),referee));
            Assert.IsType<PyNone>(TournamentWireResult(refSink.Packets[^1]));
            await rig.Foundations.Events.PumpAsync();
            Assert.Single(TournamentWireNotifications(pilotSink), n => n.Name == "OnTournamentPerformBan");
            Assert.Single(TournamentWireNotifications(refSink), n => n.Name == "OnTournamentPerformBan");
        }
        finally
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{fleet} CASCADE"); await cleanup.ExecuteNonQueryAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
        }
    }
}
