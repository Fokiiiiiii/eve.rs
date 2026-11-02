using Eve.Characters;
using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class TournamentMatchPersistenceTests
{
    [RealAbyssalSimPgFact]
    public async Task Match_creation_uses_real_scene_is_idempotent_and_last_referee_resets_it()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var chars = "mch_" + suffix; var inventory = "min_" + suffix; var space = "msp_" + suffix;
        var schedulerSchema = "msc_" + suffix; var eventSchema = "mev_" + suffix; var tournament = "mtm_" + suffix;
        const int system = 30_000_142; const long owner = 140_000_801;
        try
        {
            string? endpoint = null;
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal))
                { endpoint = line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint);
            _ = process.StandardError.ReadToEndAsync(); _ = process.StandardOutput.ReadToEndAsync();
            await using var sim = SimClient.ToTcpSocket(endpoint); sim.Start();
            using var connected = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!sim.IsConnected) await Task.Delay(20, connected.Token);
            var ship = new SimShip(9988400001939, owner, 601, 237, 6, "Ibis", owner, 1000006, 0, 0, 0, 47, 1163000, 339.25, 4.0185, 3, 860000, 29700, 660, 28, 4, 149.5, 55.25, []);
            Assert.Null(await sim.EnterAsync(new SimEnter(7, SimSceneKey.SolarSystem(system), EnterHow.InSpace, 0, ship, [1, 2, 3], [0, 0, 1]), connected.Token));
            await PgCharacterStore.MigrateAsync(source, chars); await PgItemStore.MigrateAsync(source, inventory);
            await PgSpaceItemStore.MigrateAsync(source, space); await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema); await PersistenceTournamentArenas.MigrateAsync(source, tournament);
            await using (var seed = source.CreateCommand($"""
                CREATE TABLE {tournament}.tournaments (id bigint PRIMARY KEY, maximum_points integer NOT NULL, external_ruleset_id bigint);
                CREATE TABLE {tournament}.series (tournament_id bigint NOT NULL REFERENCES {tournament}.tournaments(id), id bigint NOT NULL, PRIMARY KEY(tournament_id,id));
                INSERT INTO {tournament}.tournaments VALUES (1,100,NULL); INSERT INTO {tournament}.series VALUES (1,0);
                INSERT INTO {chars}.characters (id,account_id,name,name_key,gender,race_id,bloodline_id,ancestry_id,school_id,type_id,
                    corporation_id,station_id,home_station_id,solar_system_id,constellation_id,region_id)
                VALUES ({owner},1,'Arena Owner','arena owner',0,1,1,1,1,1,98000001,60000001,60000001,{system},20000001,10000001);
                """)) await seed.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateAsync(source, tournament);
            await PersistenceTournamentMatches.MigrateFleetAsync(source, tournament);
            await PersistenceTournamentMatches.MigrateSpaceAsync(source, tournament);
            await PersistenceTournamentMatches.MigrateRosterAsync(source, tournament);
            await PersistenceTournamentMatches.MigrateRecoveryAsync(source, tournament);
            await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(source, tournament);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(source, tournament);
            await PersistenceTournamentMatches.MigratePostgameAsync(source, tournament);
            await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(source, tournament); await PersistenceTournamentMatches.MigrateCutoffIntentAsync(source, tournament);
            await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(source, tournament);
            await PersistenceTournamentMatches.MigrateRefereeLeaseGenerationAsync(source, tournament);
            var sde = new SdeReader(source); var items = new PgItemStore(source, inventory); var deployed = new PgDeployedStore(source, space);
            var events = new EventBus(source, new DurableScheduler(source, () => 1_700_000_000_000, schema: schedulerSchema), schema: eventSchema);
            var arenas = new PersistenceTournamentArenas(source, sde, new PgCharacterStore(source, chars), items, deployed, events, tournament);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(system, owner, [new TournamentBeaconImport(10645, "Arena Center", [1, 2, 3], 5)]));
            var beacon = Assert.Single(Assert.Single(await arenas.ReadSystemsAsync()).BeaconIds);
            var store = new PersistenceTournamentMatches(source, arenas, items, deployed, events, sim, sde, tournament);
            await store.ConfigurePhysicalRegionAsync(system, beacon, 125000);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfigurePhysicalRegionAsync(system, beacon + 1, 125000));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ConfigurePhysicalRegionAsync(system, beacon, 0));
            ClientSession Session(long id)
            { var session = new ClientSession { SessionId = id, CharacterId = owner }; session.Set("solarsystemid", new PyInt(system)); return session; }
            var first = new TourneyCreateMatchRequest(1, 0, 100, 540, 0, null, system, "N=1:1", Session(11), Guid.NewGuid());
            var scene = SimSceneKey.SolarSystem(system);
            Assert.Null(await sim.EnsureTournamentBeaconAsync(new SimEnsureTournamentBeacon(
                SimClient.NextRequestId(), scene, beacon, 10645, 310, 2, owner, 1211, "Arena Center", [1, 2, 3], 6)));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CreateAsync(first, CancellationToken.None));
            await using (var count = source.CreateCommand($"SELECT count(*) FROM {tournament}.matches")) Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            Assert.Null(await sim.RemoveTournamentBeaconAsync(new SimRemoveTournamentBeacon(SimClient.NextRequestId(), scene, beacon)));
            var created = await store.CreateAsync(first, CancellationToken.None);
            Assert.Equal([beacon], created.BeaconIds); Assert.Null(created.RedCaptainId); Assert.Null(created.BlueCaptainId);
            await store.ConfigurePhysicalRegionAsync(system, beacon, 125000);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfigurePhysicalRegionAsync(system, beacon, 100000));
            var replay = await store.CreateAsync(first, CancellationToken.None);
            Assert.Equal(created.MatchId, replay.MatchId); Assert.Equal(created.BeaconIds, replay.BeaconIds);
            Assert.Equal(created.RedCaptainId, replay.RedCaptainId); Assert.Equal(created.BlueCaptainId, replay.BlueCaptainId);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.CreateAsync(first with { Session = Session(12) }, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.CreateAsync(first with { MatchLength = 480 }, CancellationToken.None));
            var second = first with { BoundObjectId = "N=1:2", Session = Session(12) };
            Assert.Equal(created.MatchId, (await store.CreateAsync(second, CancellationToken.None)).MatchId);
            await store.ReleaseStampedAsync(first.BoundObjectId, first.LeaseGeneration, CancellationToken.None);
            var replacement = first with { LeaseGeneration = Guid.NewGuid() };
            Assert.Equal(created.MatchId, (await store.CreateAsync(replacement, CancellationToken.None)).MatchId);
            await store.ReleaseStampedAsync(first.BoundObjectId, first.LeaseGeneration, CancellationToken.None);
            await using (var count = source.CreateCommand($"SELECT count(*) FROM {tournament}.match_referees WHERE oid='N=1:1'")) Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
            await store.ReleaseStampedAsync(replacement.BoundObjectId, replacement.LeaseGeneration, CancellationToken.None);
            await using (var count = source.CreateCommand($"SELECT count(*) FROM {tournament}.matches")) Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
            await store.ReleaseSessionAsync(second.Session.SessionId);
            await store.ReleaseSessionAsync(second.Session.SessionId);
            await using (var count = source.CreateCommand($"SELECT count(*) FROM {tournament}.matches")) Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            await store.CreateAsync(first, CancellationToken.None);
            await store.ResetAfterRestartAsync();
            await using (var count = source.CreateCommand($"SELECT count(*) FROM {tournament}.matches")) Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            await using (var count = source.CreateCommand($"SELECT count(*) FROM {tournament}.arena_beacons")) Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {chars},{inventory},{space},{schedulerSchema},{eventSchema},{tournament} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
