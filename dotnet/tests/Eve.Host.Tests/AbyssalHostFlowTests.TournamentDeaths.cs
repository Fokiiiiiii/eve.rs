using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Ships;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [PgFact]
    public async Task Tournament_death_chain_follows_committed_ship_and_pod_deaths_before_outbox_pump()
    {
        await using var rig = await Rig.Create();
        var schema = "td_" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            var run = await rig.CreateRun(enter: false);
            var characterId = run.Data.CreatorCharacterId; var originalShip = run.Data.CreatorShipId;
            var character = (await rig.Characters.GetAsync(characterId))!;
            var originalItem = (await rig.Items.GetAsync(originalShip))!;
            const long homeStation = 60003760;
            await using (var seed = rig.Source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments (id bigint PRIMARY KEY, ship_types integer[] NOT NULL);
                CREATE TABLE {schema}.series (tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id), id bigint NOT NULL, PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES (1, ARRAY[601]); INSERT INTO {schema}.series VALUES (1,1);
                """)) await seed.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateSpaceAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRecoveryAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateParticipantMoveIdsAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateParticipantIntentAsync(rig.Source, schema);
            await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(rig.Source, schema);
            var store = new PersistenceTournamentMatches(rig.Source, null!, rig.Items, null!, rig.Foundations.Events,
                null, rig.Sde, schema, rig.Foundations.Scheduler, rig.Sessions, space: rig.Space, tournamentCharacters: rig.Characters);
            store.RegisterSpaceHandlers();
            store.RegisterActionHandlers();
            var redSink = new DeathScoreSink(); var blueSink = new DeathScoreSink();
            var blueCharacter = characterId + 999999;
            rig.Sessions.Enter(characterId, new ClientSession { SessionId = characterId + 500, CharacterId = characterId, UserId = 11, Sink = redSink });
            rig.Sessions.Enter(blueCharacter, new ClientSession { SessionId = characterId + 501, CharacterId = blueCharacter, UserId = 12, Sink = blueSink });
            rig.Space.Events = rig.Foundations.Events;
            rig.Ships.TransitionEventWriter = rig.Space.Transitions.WriteEventAsync;
            await rig.Foundations.Events.PumpAsync();
            long matchId;
            await using (var seed = rig.Source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state,start_ms)
                VALUES (1,1,$1,100,600,0,5,1700000000000) RETURNING id
                """)) { seed.Parameters.AddWithValue((long)character.SolarSystemId); matchId = (long)(await seed.ExecuteScalarAsync())!; }
            await using (var seed = rig.Source.CreateCommand($"""
                WITH roster AS (INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES($1,0,$2,true) RETURNING 1)
                INSERT INTO {schema}.match_participants(match_id,team,character_id,ship_id,session_id,original_system_id,original_station_id,outbound_complete)
                VALUES($1,0,$2,$3,$4,$5,$6,true)
                """))
            {
                seed.Parameters.AddWithValue(matchId); seed.Parameters.AddWithValue(characterId); seed.Parameters.AddWithValue(originalShip);
                seed.Parameters.AddWithValue(characterId + 500); seed.Parameters.AddWithValue((long)character.SolarSystemId);
                seed.Parameters.AddWithValue(homeStation); await seed.ExecuteNonQueryAsync();
            }
            await using (var roster = rig.Source.CreateCommand($"""
                INSERT INTO {schema}.match_roster_details(match_id,team,character_id,ship_type_id,ship_id,location_id,points,errors,fit_hash)
                VALUES ($1,0,$2,$3,$4,$5,1,ARRAY[]::text[],$6)
                """))
            {
                roster.Parameters.AddWithValue(matchId); roster.Parameters.AddWithValue(characterId); roster.Parameters.AddWithValue(originalItem.TypeId);
                roster.Parameters.AddWithValue(originalShip); roster.Parameters.AddWithValue((long)character.SolarSystemId);
                roster.Parameters.AddWithValue(System.Security.Cryptography.SHA256.HashData([])); await roster.ExecuteNonQueryAsync();
            }
            await using (var teams = rig.Source.CreateCommand($"""
                WITH teams AS (INSERT INTO {schema}.match_teams(match_id,team,is_locked) VALUES ($1,0,true),($1,1,true)),
                     member AS (INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES ($1,1,$2,true))
                INSERT INTO {schema}.match_roster_details(match_id,team,character_id,ship_type_id,ship_id,location_id,points,errors,fit_hash)
                VALUES ($1,1,$2,$3,$4,$5,20,ARRAY[]::text[],$6)
                """))
            {
                teams.Parameters.AddWithValue(matchId); teams.Parameters.AddWithValue(blueCharacter); teams.Parameters.AddWithValue(originalItem.TypeId);
                teams.Parameters.AddWithValue(originalShip + 999999); teams.Parameters.AddWithValue((long)character.SolarSystemId);
                teams.Parameters.AddWithValue(System.Security.Cryptography.SHA256.HashData([])); await teams.ExecuteNonQueryAsync();
            }
            var shipDeath = await rig.Ships.DestroyShipAsync(characterId, originalShip, null, "tournament-real-ship-death");
            Assert.NotNull(shipDeath); Assert.NotNull(shipDeath.Capsule);
            var capsuleId = shipDeath.Capsule.Id;
            var clone = await rig.Ships.DestroyCapsuleAsync(characterId, capsuleId, null, "", rig.Creation.Races.First().ShipTypeId,
                new HomePlace(homeStation, character.SolarSystemId, character.ConstellationId, character.RegionId));
            Assert.NotNull(clone);
            Assert.Null(await rig.Items.GetAsync(originalShip)); Assert.Null(await rig.Items.GetAsync(capsuleId));
            var afterPod = (await rig.Characters.GetAsync(characterId))!;
            var initialRookie = afterPod.ShipId!.Value;
            var rookieType = rig.Creation.Races.First().ShipTypeId;
            long firstBoard; long secondBoard;
            await using (var c = await rig.Source.OpenConnectionAsync())
            await using (var tx = await c.BeginTransactionAsync())
            {
                firstBoard = await rig.Items.CreateAsync(c, tx, new NewItem(rookieType, characterId, homeStation, ItemFlags.Hangar, 1, true));
                secondBoard = await rig.Items.CreateAsync(c, tx, new NewItem(rookieType, characterId, homeStation, ItemFlags.Hangar, 1, true));
                await tx.CommitAsync();
            }
            Assert.True(await rig.Ships.BoardAsync(characterId, homeStation, initialRookie, firstBoard));
            Assert.True(await rig.Ships.BoardAsync(characterId, homeStation, firstBoard, secondBoard));
            var leftShip = await rig.Ships.LeaveShipAsync(characterId, homeStation, secondBoard);
            Assert.NotNull(leftShip);
            var actual = (await rig.Characters.GetAsync(characterId))!;
            Assert.Equal(leftShip.Id, actual.ShipId);
            Assert.Equal(homeStation, actual.StationId); Assert.NotEqual(originalShip, actual.ShipId);
            var rookie = (await rig.Items.GetAsync(actual.ShipId!.Value))!;
            Assert.Equal(characterId, rookie.OwnerId); Assert.Equal(homeStation, rookie.LocationId);
            // Both deaths, two boards and leave-ship committed before the first pump.
            await rig.Drain();
            await rig.Foundations.Events.PumpAsync();
            foreach (var (sink, team) in new[] { (redSink, 0), (blueSink, 1) })
            {
                var status = DeathNotifications(sink).Last(x => x.Name == "OnTournamentMatchStateChange");
                Assert.Equal(4, status.Args.Length);
                Assert.Equal(new PyInt(80), status.Args[0]); Assert.Equal(new PyInt(100), status.Args[1]);
                Assert.Equal(new PyInt(team), status.Args[2]);
                Assert.Equal(5, Assert.IsType<PyDict>(status.Args[3]).Entries.Count);
            }
            await using (var query = rig.Source.CreateCommand($"""
                SELECT p.ship_id,p.return_ship_id,p.death_home_id,m.eliminated
                FROM {schema}.match_participants p JOIN {schema}.match_members m ON m.match_id=p.match_id AND m.character_id=p.character_id
                WHERE p.match_id=$1 AND p.character_id=$2
                """))
            {
                query.Parameters.AddWithValue(matchId); query.Parameters.AddWithValue(characterId);
                await using var row = await query.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
                Assert.Equal(originalShip, row.GetInt64(0)); Assert.Equal(actual.ShipId, row.GetInt64(1));
                Assert.Equal(homeStation, row.GetInt64(2)); Assert.True(row.GetBoolean(3));
            }
            await using (var complete = rig.Source.CreateCommand($"UPDATE {schema}.matches SET state=6 WHERE id=$1 AND state=5"))
            { complete.Parameters.AddWithValue(matchId); Assert.Equal(1, await complete.ExecuteNonQueryAsync()); }
            Assert.False(await store.ReturnCompletedParticipantsAsync(matchId, CancellationToken.None));
            await rig.Foundations.Scheduler.RunDueAsync();
            Assert.True(await store.ReturnCompletedParticipantsAsync(matchId, CancellationToken.None));
            await using (var query = rig.Source.CreateCommand($"""
                SELECT m.eliminated,r.ship_id FROM {schema}.match_members m JOIN {schema}.match_roster_details r
                ON r.match_id=m.match_id AND r.character_id=m.character_id WHERE m.match_id=$1 AND m.character_id=$2
                """))
            {
                query.Parameters.AddWithValue(matchId); query.Parameters.AddWithValue(characterId);
                await using var row = await query.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
                Assert.True(row.GetBoolean(0)); Assert.Equal(originalShip, row.GetInt64(1));
            }
            Assert.NotNull(await rig.Items.GetAsync(rookie.Id));
            Assert.NotNull(await rig.Items.GetAsync(initialRookie));
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class DeathScoreSink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    private static IEnumerable<(string Name, PyValue[] Args)> DeathNotifications(DeathScoreSink sink)
    {
        foreach (var packet in sink.Sent.OfType<PyObject>())
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
}
