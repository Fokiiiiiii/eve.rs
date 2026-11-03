using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class TournamentRegionCompositionTests
{
    [PgFact]
    public async Task Region_file_is_loaded_at_host_start_and_identical_restart_is_safe()
    {
        var originalUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var template = new NpgsqlConnectionStringBuilder(PgConnection.ForTest(originalUrl));
        await using var occupiedTemplate = new NpgsqlConnection(template.ConnectionString);
        await occupiedTemplate.OpenAsync();
        var database = "tourney_region_" + Guid.NewGuid().ToString("N")[..12];
        var adminUrl = new UriBuilder(originalUrl) { Path = "/postgres" }.Uri.ToString();
        var testUrl = new UriBuilder(originalUrl) { Path = "/" + database }.Uri.ToString();
        var folder = Path.Combine(Path.GetTempPath(), database);
        Directory.CreateDirectory(folder);
        var arenaPath = Path.Combine(folder, "arena.json");
        var regionPath = Path.Combine(folder, "region.json");
        var names = new[] { "EVE_PG_URL", "EVE_SIM", "EVE_MARKET_SEED", "EVE_WORMHOLES", "EVE_TOURNAMENT_ARENA_IMPORT", "EVE_TOURNAMENT_REGION_IMPORT" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        HostRuntime? host = null;
        var created = false;
        try
        {
            await using (var admin = NpgsqlDataSource.Create(PgConnection.ForTest(adminUrl)))
            await using (var command = admin.CreateCommand($"CREATE DATABASE {database} TEMPLATE template0"))
            { await command.ExecuteNonQueryAsync(); created = true; }
            const long owner = 140_091_002;
            await using (var source = NpgsqlDataSource.Create(PgConnection.ForTest(testUrl)))
            {
                await HostTestDatabase.AttachSdeAsync(source, template);
                await PgCharacterStore.MigrateAsync(source);
                await using var seed = source.CreateCommand($"""
                    INSERT INTO characters.characters (id,account_id,name,name_key,gender,race_id,bloodline_id,ancestry_id,school_id,type_id,
                        corporation_id,station_id,home_station_id,solar_system_id,constellation_id,region_id)
                    VALUES ({owner},1,'Region Composition Pilot','region composition pilot',0,1,1,1,1,1,98000001,60000001,60000001,30000142,20000001,10000001)
                    ON CONFLICT (id) DO NOTHING
                    """);
                await seed.ExecuteNonQueryAsync();
            }
            await File.WriteAllTextAsync(arenaPath, $$"""
                [{"solarSystemId":30000142,"ownerCharacterId":{{owner}},"beacons":[{"typeId":10645,"name":"Region Center","position":[100000000000.0,0,0],"radius":5}]}]
                """);
            await File.WriteAllTextAsync(regionPath, "[{\"solarSystemId\":30000142,\"centerBeaconOrdinal\":0,\"radiusMeters\":125000}]");
            Environment.SetEnvironmentVariable("EVE_PG_URL", testUrl);
            Environment.SetEnvironmentVariable("EVE_SIM", "off");
            Environment.SetEnvironmentVariable("EVE_MARKET_SEED", "skip");
            Environment.SetEnvironmentVariable("EVE_WORMHOLES", "off");
            Environment.SetEnvironmentVariable("EVE_TOURNAMENT_ARENA_IMPORT", arenaPath);
            Environment.SetEnvironmentVariable("EVE_TOURNAMENT_REGION_IMPORT", regionPath);
            var settings = new HostSettings("", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" }, new AccountOptions { AutoCreate = true }, StrictRpc: true);
            host = await HostComposition.BuildAsync(settings);
            await host.ShutdownAsync(); host = null;
            // A persisted release remains recoverable even when the simulator is disabled at restart.
            await using (var source = NpgsqlDataSource.Create(PgConnection.ForTest(testUrl)))
            await using (var intent = source.CreateCommand("""
                INSERT INTO tournament.tournament_cutoff_intents(match_id,system_id,start_ms,deadline_ms,characters,ships,state)
                VALUES (90001,30000142,1000,2000,ARRAY[140091002]::bigint[],ARRAY[990001]::bigint[],'release_pending')
                """)) await intent.ExecuteNonQueryAsync();
            host = await HostComposition.BuildAsync(settings);
            await using (var source = NpgsqlDataSource.Create(PgConnection.ForTest(testUrl)))
            await using (var sweep = source.CreateCommand("SELECT count(*) FROM scheduler.jobs WHERE kind='tournament.cutoff.sweep' AND state='pending'"))
                Assert.Equal(1L,(long)(await sweep.ExecuteScalarAsync())!);
            await using (var source = NpgsqlDataSource.Create(PgConnection.ForTest(testUrl)))
            await using (var intent = source.CreateCommand("SELECT count(*) FROM tournament.tournament_cutoff_intents WHERE match_id=90001 AND state='release_pending'"))
                Assert.Equal(1L,(long)(await intent.ExecuteScalarAsync())!);
            await using (var source = NpgsqlDataSource.Create(PgConnection.ForTest(testUrl)))
            await using (var command = source.CreateCommand("SELECT r.center_beacon_id,r.radius_meters,b.item_id FROM tournament.arena_physical_regions r JOIN tournament.arena_beacons b ON b.system_id=r.system_id AND b.ordinal=0 WHERE r.system_id=30000142"))
            await using (var row = await command.ExecuteReaderAsync())
            {
                Assert.True(await row.ReadAsync());
                Assert.Equal(row.GetInt64(2), row.GetInt64(0));
                Assert.Equal(125000d, row.GetDouble(1));
                Assert.False(await row.ReadAsync());
            }

            // Exercise the real Host's shared character-entry hook list through SelectCharacterID.
            const long nextCharacter = 140_091_003;
            const long sessionId = 9_123_456;
            await using (var source = NpgsqlDataSource.Create(PgConnection.ForTest(testUrl)))
            {
                await using var seed = source.CreateCommand($"""
                    INSERT INTO characters.characters (id,account_id,name,name_key,gender,race_id,bloodline_id,ancestry_id,school_id,type_id,
                        corporation_id,station_id,home_station_id,solar_system_id,constellation_id,region_id)
                    VALUES ({nextCharacter},1,'Region Switch Pilot','region switch pilot',0,1,1,1,1,1,98000001,60000001,60000001,30000142,20000001,10000001)
                    ON CONFLICT (id) DO NOTHING;
                    INSERT INTO tournament.tournaments(id,name,maximum_points,match_length,overtime_length,ship_types) VALUES (88001,'Switch',100,600,0,ARRAY[587]);
                    INSERT INTO tournament.series(tournament_id,id,red_team,blue_team,system_id,current_match,match_count) VALUES (88001,0,'Red','Blue',0,0,1);
                    INSERT INTO tournament.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state)
                    VALUES (88001,0,30000142,100,600,0,1);
                    INSERT INTO tournament.match_referees(oid,match_id,session_id,character_id,lease_generation)
                    SELECT 'N=1:switch',id,{sessionId},{owner},'11111111-1111-1111-1111-111111111111' FROM tournament.matches WHERE tournament_id=88001;
                    """);
                await seed.ExecuteNonQueryAsync();
            }
            var session = new ClientSession { UserId = 1, SessionId = sessionId, CharacterId = owner };
            var characterService = Assert.IsAssignableFrom<IService>(host.Registry.Lookup("charUnboundMgr"));
            await characterService.CallAsync(new ServiceCall
            {
                Method = "SelectCharacterID", Session = session,
                Args = [new PyLong(nextCharacter), PyNone.Instance, PyBool.True],
            });
            Assert.Equal(nextCharacter, session.CharacterId);
            await using (var source = NpgsqlDataSource.Create(PgConnection.ForTest(testUrl)))
            await using (var command = source.CreateCommand("SELECT count(*) FROM tournament.match_referees WHERE oid='N=1:switch'"))
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            if (created)
            {
                await using var admin = NpgsqlDataSource.Create(PgConnection.ForTest(adminUrl));
                await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
                await drop.ExecuteNonQueryAsync();
            }
            Directory.Delete(folder, recursive: true);
        }
    }
}
