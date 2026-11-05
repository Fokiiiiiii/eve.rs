using Eve.Data;
using Eve.Host;
using Eve.Host.Persistence;
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
    public async Task Tournament_referee_home_returns_origin_through_registered_encoded_rpc()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Simulation failed to start.");
        var schema = "rh_" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            string? endpoint = null;
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
            {
                if (!line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal)) continue;
                endpoint = line["eve-sim: listening on ".Length..];
                break;
            }

            Assert.NotNull(endpoint);
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            await using var simulation = SimClient.ToTcpSocket(endpoint);
            simulation.Start();
            using var connected = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!simulation.IsConnected) await Task.Delay(20, connected.Token);

            await using var rig = await Rig.Create(simulation);
            rig.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var run = await rig.CreateRun(enter: false);
            var character = run.Data.CreatorCharacterId;
            var ship = run.Data.CreatorShipId;
            const long station = 60003760;
            var stationData = rig.Stations.Get(station);
            Assert.NotNull(stationData);
            Assert.NotNull(await rig.Ships.DockAsync(character, station, null));

            var session = new ClientSession(new RecordingSink(rig.Order))
            {
                SessionId = character + 500,
                ClientId = character + 600,
            };
            session.Set("charid", new PyLong(character));
            session.Set("shipid", new PyLong(ship));
            session.Set("stationid", new PyLong(station));
            session.Set("stationid2", new PyLong(station));
            session.Set("structureid", PyNone.Instance);
            session.Set("solarsystemid", PyNone.Instance);
            session.Set("solarsystemid2", new PyInt(stationData.SolarSystemId));
            session.Set("locationid", new PyLong(station));
            session.Set("accountRole", new PyLong(GameMasterRoles.Qa));
            rig.Sessions.Enter(character, session);
            await rig.Space.OnCharacterEnteredAsync(session, character, CancellationToken.None);

            await using (var seed = rig.Source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments (
                    id bigint PRIMARY KEY,
                    ship_types integer[] NOT NULL,
                    maximum_points integer NOT NULL,
                    external_ruleset_id bigint,
                    name text NOT NULL DEFAULT 'Test',
                    match_length integer NOT NULL DEFAULT 600,
                    overtime_length integer NOT NULL DEFAULT 60);
                CREATE TABLE {schema}.series (
                    tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id),
                    id bigint NOT NULL,
                    red_team text NOT NULL DEFAULT 'Red',
                    blue_team text NOT NULL DEFAULT 'Blue',
                    system_id bigint NOT NULL DEFAULT 0,
                    current_match integer NOT NULL DEFAULT 0,
                    match_count integer NOT NULL DEFAULT 1,
                    PRIMARY KEY (tournament_id, id));
                INSERT INTO {schema}.tournaments (id, ship_types, maximum_points, external_ruleset_id)
                VALUES (1, ARRAY[601, 602], 100, NULL);
                INSERT INTO {schema}.series (tournament_id, id) VALUES (1, 1);
                """))
                await seed.ExecuteNonQueryAsync();

            var deployed = new PgDeployedStore(rig.Source, rig.Schemas[4]);
            await PersistenceTournamentArenas.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRecoveryAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateCutoffIntentAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRefereeTravelAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRefereeDirectionAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRefereeMoveIdsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRefereeLeaseGenerationAsync(rig.Source, schema);
            const int target = 30000144;
            var arenas = new PersistenceTournamentArenas(
                rig.Source, rig.Sde, rig.Characters, rig.Items, deployed, rig.Foundations.Events, schema);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(
                target, character, [new TournamentBeaconImport(10645, "Referee Arena", [1000, 2000, 3000], 5)]));
            var store = new PersistenceTournamentMatches(
                rig.Source, arenas, rig.Items, deployed, rig.Foundations.Events, simulation, rig.Sde,
                schema, rig.Foundations.Scheduler, rig.Sessions, space: rig.Space);
            store.RegisterActionHandlers();
            await rig.Foundations.Events.PumpAsync();

            var sink = new TournamentWireSink();
            session.Sink = sink;
            var service = new TourneyMgrService(matches: new TourneyMatchCallbacks(
                store.CreateAsync,
                store.ReleaseAsync,
                store.ActionAsync,
                RefTravelAsync: store.RefTravelAsync,
                RefHomeAsync: store.RefHomeAsync,
                ReleaseStampedAsync: store.ReleaseStampedAsync));
            var registry = new ServiceRegistry();
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => rig.Now);
            long callId = 1;

            async Task<string> Bind()
            {
                var packet = TournamentWireRequest(
                    callId++, "tourneyMgr", new PyInt(1), "MachoBindObject", new PyInt(target));
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(packet)), session));
                var result = Assert.IsType<PyTuple>(TournamentWireResult(sink.Packets[^1]));
                return BoundObjects.ExtractName(Assert.IsType<PySubStruct>(result.Items[0]))!;
            }

            async Task<PyValue> Invoke(string oid, string method, params PyValue[] args)
            {
                var packet = TournamentWireRequest(callId++, null, new PyStr(oid), method, args);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(packet)), session));
                return TournamentWireResult(sink.Packets[^1]);
            }

            var matchOid = await Bind();
            var travelOid = await Bind();
            Assert.NotEqual(matchOid, travelOid);
            Assert.IsType<PyNone>(await Invoke(travelOid, "TeleportToArena"));
            Assert.Equal(target, rig.Space.SolarSystemOf(session));

            var created = Assert.IsType<PyTuple>(await Invoke(
                matchOid,
                "CreateMatch",
                new PyLong(1),
                new PyLong(1),
                new PyInt(100),
                new PyInt(600),
                new PyInt(0),
                PyNone.Instance));
            Assert.Equal(3, created.Items.Count);
            var matchId = created.Items[0] switch
            {
                PyLong value => value.Value,
                PyInt value => value.Value,
                _ => throw new Xunit.Sdk.XunitException("CreateMatch returned a non-integer match ID."),
            };
            Assert.True(matchId > 0);

            Assert.IsType<PyNone>(await Invoke(matchOid, "TeleportRefHome", new PyLong(matchId)));
            Assert.Equal(station, session.GetInt("stationid"));
            Assert.Null(session.GetInt("structureid"));
            Assert.Null(session.GetInt("solarsystemid"));
            Assert.Equal(ship, session.GetInt("shipid"));
            var item = await rig.Items.GetAsync(ship);
            Assert.NotNull(item);
            Assert.Equal(character, item.OwnerId);
            Assert.Equal(station, item.LocationId);
            var snapshot = await rig.Ships.LoadAsync(character);
            Assert.NotNull(snapshot);
            Assert.Equal(station, snapshot.Character.StationId);
            Assert.Equal(ship, snapshot.Ship.Id);
            await using var travelCount = rig.Source.CreateCommand(
                $"SELECT count(*) FROM {schema}.referee_travel WHERE character_id=$1");
            travelCount.Parameters.AddWithValue(character);
            Assert.Equal(0L, (long)(await travelCount.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var source = NpgsqlDataSource.Create(
                PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
