using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Scheduler;
using Eve.Services;
using Eve.Ships;
using Eve.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class TournamentSettlementTests
{
    [PgFact]
    public async Task Only_matching_complete_cutoff_can_finalize_and_late_death_keeps_final_score()
    {
        await using var source=NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix=Guid.NewGuid().ToString("N")[..10]; var schema="ts_"+suffix; var jobs="tj_"+suffix; var outbox="te_"+suffix;
        long now=1_700_000_000_000; var start=now-120000;
        try
        {
            await DurableScheduler.MigrateAsync(source,jobs); await EventBus.MigrateAsync(source,outbox);
            await using (var init=source.CreateCommand($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.tournaments(id bigint PRIMARY KEY,ship_types integer[] NOT NULL); CREATE TABLE {schema}.series(tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id),id bigint NOT NULL,PRIMARY KEY(tournament_id,id)); INSERT INTO {schema}.tournaments VALUES(1,ARRAY[601]); INSERT INTO {schema}.series VALUES(1,0)")) await init.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateAsync(source,schema); await PersistenceTournamentMatches.MigrateSpaceAsync(source,schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(source,schema); await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(source,schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(source,schema); await PersistenceTournamentMatches.MigrateRefereeTravelAsync(source,schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(source,schema); await PersistenceTournamentMatches.MigrateParticipantMoveIdsAsync(source,schema);
            await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(source,schema); await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(source,schema);
            await PersistenceTournamentMatches.MigrateCutoffIntentAsync(source,schema);
            await using var scheduler=new DurableScheduler(source,()=>now,schema:jobs);
            var events=new EventBus(source,scheduler,schema:outbox); var sessions=new CharacterSessions();
            var store=new PersistenceTournamentMatches(source,null!,null!,null!,events,null,null!,schema,scheduler,sessions);
            store.RegisterActionHandlers(); store.RegisterParticipantDeathHandler();
            await using (var seed=source.CreateCommand($"""
                INSERT INTO {schema}.tournament_completion_policies VALUES(1,'alliance_tournament');
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state,start_ms,settlement_pending)
                  VALUES(1,0,30000142,100,120,0,5,{start},true);
                INSERT INTO {schema}.match_teams VALUES(1,0,true),(1,1,true);
                INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES(1,0,101,true),(1,1,102,true);
                INSERT INTO {schema}.match_roster_details VALUES(1,0,101,601,201,30000142,10,ARRAY[]::text[],decode('00','hex')),
                  (1,1,102,601,202,30000142,10,ARRAY[]::text[],decode('00','hex'));
                INSERT INTO {schema}.match_participants(match_id,team,character_id,ship_id,session_id,original_system_id,outbound_operation_id,return_operation_id)
                  VALUES(1,0,101,201,1001,30000142,gen_random_uuid(),gen_random_uuid());
                INSERT INTO {schema}.tournament_cutoff_intents(match_id,system_id,start_ms,deadline_ms,characters,ships,state)
                  VALUES(1,30000142,{start},{now},ARRAY[101,102]::bigint[],ARRAY[201,202]::bigint[],'committed');
                """)) await seed.ExecuteNonQueryAsync();
            var mode=0;
            store.CutoffReader=(request,_) =>
            {
                var status=mode switch { 0=>SimTournamentCutoffStatus.Pending, 1=>SimTournamentCutoffStatus.Unavailable, _=>SimTournamentCutoffStatus.Ready };
                SimTournamentCutoffItem[] evidence=mode switch
                {
                    2=>[new(101,201,SimTournamentCutoffEvidence.Alive),new(102,202,SimTournamentCutoffEvidence.Unknown)],
                    3=>[new(101,201,SimTournamentCutoffEvidence.Alive),new(102,999,SimTournamentCutoffEvidence.Destroyed)],
                    4=>[new(101,201,SimTournamentCutoffEvidence.Alive),new(102,202,SimTournamentCutoffEvidence.Alive)],
                    >=5=>[new(101,201,SimTournamentCutoffEvidence.Alive),new(102,202,SimTournamentCutoffEvidence.Destroyed)],
                    _=>[]
                };
                return Task.FromResult(new SimReadTournamentCutoffReply(request.RequestId,request.Scene,request.MatchId,
                    mode==6 ? request.Generation+1 : request.Generation,request.Start,request.Deadline,status,evidence,""));
            };
            await scheduler.ScheduleAsync(new JobSpec("tournament.match.settle",now) { Key="match:1",Payload=new { MatchId=1,StartMs=start } });
            for (mode=0;mode<=4;mode++)
            {
                await scheduler.RunDueAsync();
                await using var state=source.CreateCommand($"SELECT state,settlement_pending,winner_team FROM {schema}.matches WHERE id=1");
                await using var row=await state.ExecuteReaderAsync(); Assert.True(await row.ReadAsync()); Assert.Equal(5,row.GetInt32(0)); Assert.True(row.GetBoolean(1)); Assert.True(row.IsDBNull(2));
                now+=5001;
            }
            mode=6; await scheduler.RunDueAsync();
            await using (var state=source.CreateCommand($"SELECT state FROM {schema}.matches WHERE id=1")) Assert.Equal(5,(int)(await state.ExecuteScalarAsync())!);
            now+=5001; mode=5; await scheduler.RunDueAsync();
            await using (var state=source.CreateCommand($"SELECT state,winner_team,completion_reason,settlement_pending FROM {schema}.matches WHERE id=1"))
            await using (var row=await state.ExecuteReaderAsync()) { Assert.True(await row.ReadAsync()); Assert.Equal(6,row.GetInt32(0)); Assert.Equal(0,row.GetInt32(1)); Assert.Equal("score",row.GetString(2)); Assert.False(row.GetBoolean(3)); }
            await using (var connection=await source.OpenConnectionAsync()) await using (var tx=await connection.BeginTransactionAsync())
            {
                await events.PublishAsync(connection,tx,"space.transition",new ShipTransitionEvent("ship-destroyed",101,201,301,30000142,CharacterShips.InSpaceFlag,30000142,CharacterShips.InSpaceFlag));
                await tx.CommitAsync();
            }
            await events.PumpAsync();
            await using (var check=source.CreateCommand($"SELECT m.eliminated,p.return_ship_id FROM {schema}.match_members m JOIN {schema}.match_participants p USING(match_id,character_id) WHERE m.character_id=101"))
            await using (var row=await check.ExecuteReaderAsync()) { Assert.True(await row.ReadAsync()); Assert.False(row.GetBoolean(0)); Assert.Equal(301,row.GetInt64(1)); }
        }
        finally { await using var cleanup=source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{jobs},{outbox} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
    }
}
