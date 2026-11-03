using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Scheduler;
using Eve.Services;
using Eve.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class TournamentCutoffIntentTests
{
    [PgFact]
    public async Task Intent_uses_immutable_generation_and_compare_exchange_transitions()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix=Guid.NewGuid().ToString("N")[..10]; var schema="ti_"+suffix; var jobs="tj_"+suffix; var outbox="te_"+suffix;
        long now=1_700_000_000_000;
        try
        {
            await using (var setup=source.CreateCommand($"CREATE SCHEMA {schema}")) await setup.ExecuteNonQueryAsync();
            await DurableScheduler.MigrateAsync(source,jobs); await EventBus.MigrateAsync(source,outbox);
            await PersistenceTournamentMatches.MigrateCutoffIntentAsync(source,schema);
            await PersistenceTournamentMatches.MigrateCutoffIntentAsync(source,schema);
            await using var scheduler=new DurableScheduler(source,()=>now,schema:jobs);
            var store=new PersistenceTournamentMatches(source,null!,null!,null!,new EventBus(source,scheduler,schema:outbox),null,null!,schema,scheduler,new CharacterSessions());
            PersistenceTournamentMatches.CutoffIntent intent;
            await using (var c=await source.OpenConnectionAsync())
            await using (var tx=await c.BeginTransactionAsync())
            {
                intent=await store.PrepareCutoffIntentAsync(c,tx,11,30000142,now+30000,now+150000,[new(102,202),new(101,201)],CancellationToken.None);
                Assert.Equal(new long[]{101,102},intent.Characters); Assert.Equal(new long[]{201,202},intent.Ships);
                Assert.True(await store.TransitionCutoffIntentAsync(c,tx,intent,"prepared","registered",CancellationToken.None));
                Assert.False(await store.TransitionCutoffIntentAsync(c,tx,intent with { DeadlineMs=intent.DeadlineMs+1 },"registered","committed",CancellationToken.None));
                Assert.True(await store.TransitionCutoffIntentAsync(c,tx,intent,"registered","committed",CancellationToken.None));
                Assert.False(await store.TransitionCutoffIntentAsync(c,tx,intent,"registered","committed",CancellationToken.None));
                await store.ScheduleCutoffReleaseAsync(c,tx,intent,"committed",CancellationToken.None);
                await tx.CommitAsync();
            }
            await using var check=source.CreateCommand($"SELECT state,characters,ships FROM {schema}.tournament_cutoff_intents WHERE generation=$1");
            check.Parameters.AddWithValue(intent.Generation); await using var row=await check.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal("release_pending",row.GetString(0)); Assert.Equal(intent.Characters,row.GetFieldValue<long[]>(1)); Assert.Equal(intent.Ships,row.GetFieldValue<long[]>(2));
            await row.CloseAsync();
            // Losing a waiting release job must not lose the durable intent: the startup sweep recreates it.
            Assert.True(await scheduler.CancelAsync("tournament.cutoff.release",$"cutoff:{intent.Generation}"));
            store.RegisterActionHandlers(); await store.InitializeCutoffRecoveryAsync();
            now += 5000; await scheduler.RunDueAsync();
            var recovered=await scheduler.FindAsync("tournament.cutoff.release",$"cutoff:{intent.Generation}");
            Assert.NotNull(recovered); Assert.Equal("pending",recovered.State);
            await using var remains=source.CreateCommand($"SELECT state FROM {schema}.tournament_cutoff_intents WHERE generation=$1");
            remains.Parameters.AddWithValue(intent.Generation); Assert.Equal("release_pending",await remains.ExecuteScalarAsync());
        }
        finally { await using var cleanup=source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{jobs},{outbox} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
    }
}
