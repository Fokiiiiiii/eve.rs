using Eve.Structures;
using static Eve.Structures.StructureTimes;

namespace Eve.Structures.Tests;

public sealed class StructureUnanchorCompletionTests
{
    private const long StructureId = 901_001;
    private const int TypeId = 35832;
    private const int SolarSystemId = 30000142;

    [PgFact]
    public async Task Due_unanchor_hands_off_once_and_replays_the_committed_drop_after_restart()
    {
        await using var rig = await Rig.Create();
        await SeedDueUnanchorAsync(rig);
        var calls = 0;
        var timers = new StructureTimers(
            rig.Store, rig.Scheduler, rig.Bus, rig.Rules, () => rig.Roll,
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(new StructureUnanchorDrop(8_100_001, 8_100_002));
            });

        await rig.Commit((connection, transaction) => timers.ApplyAsync(connection, transaction, StructureId, new TimerDue()));
        await rig.Restart();

        var change = Assert.Single(rig.Changes);
        Assert.Equal(1, calls);
        Assert.Equal("unanchored", change.Removal);
        Assert.Equal(new StructureUnanchorDrop(8_100_001, 8_100_002), change.UnanchorDrop);
        Assert.True((await rig.Snapshot(StructureId)).DestroyedMs > 0);
        Assert.Equal(1, await rig.EventCount());

        await rig.Apply(StructureId, new TimerDue());
        await rig.Tick();
        Assert.Equal(1, calls);
        Assert.Single(rig.Changes);
        Assert.Equal(1, await rig.EventCount());
    }

    [PgFact]
    public async Task Failed_drop_handoff_rolls_back_the_state_and_outbox_together()
    {
        await using var rig = await Rig.Create();
        await SeedDueUnanchorAsync(rig);
        var before = await rig.Snapshot(StructureId);
        var timers = new StructureTimers(
            rig.Store, rig.Scheduler, rig.Bus, rig.Rules, () => rig.Roll,
            (_, _, _, _) => Task.FromException<StructureUnanchorDrop>(new InvalidOperationException("drop persistence failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Commit(
            (connection, transaction) => timers.ApplyAsync(connection, transaction, StructureId, new TimerDue())));

        Assert.Equal(before, await rig.Snapshot(StructureId));
        Assert.Equal(0, await rig.EventCount());
        Assert.Empty(rig.Changes);
    }

    private static async Task SeedDueUnanchorAsync(Rig rig)
    {
        await rig.Commit(async (connection, transaction) =>
        {
            Assert.True(await rig.Store.CreateAsync(connection, transaction,
                new NewStructure(StructureId, TypeId, SolarSystemId, 1.25, 2.5, -3.75, 0.5, 98_000_001, 99_000_001, "Unanchor test", "")));
            var record = await rig.Store.GetAsync(connection, transaction, StructureId, true);
            Assert.NotNull(record);
            await rig.Store.SaveAsync(connection, transaction, record!.Snapshot with
            {
                State = StructureState.ShieldVulnerable,
                StateStartedMs = rig.Now,
                UnanchorEndsMs = rig.Now,
            });
            return true;
        });
    }
}
