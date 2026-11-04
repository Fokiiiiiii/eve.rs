using Eve.Structures;
using Npgsql;
using static Eve.Structures.StructureTimes;

namespace Eve.Structures.Tests;

public sealed class StructureTimerAnchoringDelayTests
{
    private const long StructureId = 903_001;
    private const long FailedStructureId = 903_002;
    private const long LegacyStructureId = 903_003;
    private const int Astrahus = 35832;
    private const int Jita = 30000142;
    private static readonly long DeploymentAnchoringMs = AnchoringMs + 2 * Day;

    [PgFact]
    public async Task Deployment_duration_survives_the_15_minute_transition_restart_and_timer_rebuild()
    {
        await using var rig = await Rig.Create();
        await rig.Commit((connection, transaction) => rig.Timers.AnchorAsync(connection, transaction,
            new NewStructure(StructureId, Astrahus, Jita, 1, 2, 3, 0, 98_000_001, 99_000_001,
                "Sovereignty delay", "", AnchoringMs: DeploymentAnchoringMs)));
        await rig.Tick();

        var vulnerable = await rig.Snapshot(StructureId);
        Assert.Equal(DeploymentAnchoringMs, vulnerable.AnchoringMs);
        Assert.Equal(StructureState.AnchorVulnerable, vulnerable.State);
        Assert.Equal(rig.Now + RepairMs, vulnerable.StateEndsMs);
        Assert.Equal(rig.Now + RepairMs, (await rig.Job(StructureId))!.RunAtMs);

        // The current rule input has changed since deployment. A deployed structure keeps its stored duration.
        rig.Rules.Default = StructureRules.Of(StructureSize.Medium, SecurityBand.HighSec, anchoringMs: 5 * Day);
        rig.Now += RepairMs;
        await rig.Tick();

        var anchoring = await rig.Snapshot(StructureId);
        Assert.Equal(DeploymentAnchoringMs, anchoring.AnchoringMs);
        Assert.Equal(StructureState.Anchoring, anchoring.State);
        Assert.Equal(rig.Now + DeploymentAnchoringMs, anchoring.StateEndsMs);
        Assert.Equal(rig.Now + DeploymentAnchoringMs, (await rig.Job(StructureId))!.RunAtMs);

        await rig.Restart();
        var scheduled = await rig.Commit((connection, transaction) => rig.Timers.EnsureScheduledAsync(connection, transaction));
        Assert.Equal(1, scheduled);
        Assert.Equal(rig.Now + DeploymentAnchoringMs, (await rig.Job(StructureId))!.RunAtMs);

        var eventsBeforeReplay = await rig.EventCount();
        await rig.Apply(StructureId, new TimerDue());
        await rig.Tick();
        Assert.Equal(anchoring, await rig.Snapshot(StructureId));
        Assert.Equal(eventsBeforeReplay, await rig.EventCount());
    }

    [PgFact]
    public async Task A_failed_timer_transaction_keeps_the_persisted_duration_state_job_and_outbox_unchanged()
    {
        await using var rig = await Rig.Create();
        await rig.Commit((connection, transaction) => rig.Timers.AnchorAsync(connection, transaction,
            new NewStructure(FailedStructureId, Astrahus, Jita, 1, 2, 3, 0, 98_000_001, 99_000_001,
                "Rollback delay", "", AnchoringMs: DeploymentAnchoringMs)));
        await rig.Tick();
        var before = await rig.Snapshot(FailedStructureId);
        var jobBefore = await rig.Job(FailedStructureId);
        var eventCountBefore = await rig.EventCount();
        rig.Now += RepairMs;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var transition = await rig.Timers.ApplyAsync(connection, transaction, FailedStructureId, new TimerDue());
            Assert.Equal(StructureState.Anchoring, transition!.Next.State);
            throw new InvalidOperationException("simulate failure after transition before commit");
        });

        Assert.Equal(before, await rig.Snapshot(FailedStructureId));
        Assert.Equal(jobBefore, await rig.Job(FailedStructureId));
        Assert.Equal(eventCountBefore, await rig.EventCount());
        Assert.Single(rig.Changes);
    }

    [PgFact]
    public async Task Existing_rows_without_an_explicit_duration_keep_the_base_rule_default()
    {
        await using var rig = await Rig.Create();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {rig.StructuresSchema}.structures (structure_id, type_id, solar_system_id) VALUES ($1, $2, $3)",
                connection, transaction);
            command.Parameters.AddWithValue(LegacyStructureId);
            command.Parameters.AddWithValue(Astrahus);
            command.Parameters.AddWithValue(Jita);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(StructureTimes.AnchoringMs, (await rig.Store.GetAsync(LegacyStructureId))!.Snapshot.AnchoringMs);
        Assert.Equal(StructureTimes.AnchoringMs, new NewStructure(LegacyStructureId + 1, Astrahus, Jita, 1, 2, 3, 0,
            98_000_001, 99_000_001, "Default delay", "").AnchoringMs);
    }
}
