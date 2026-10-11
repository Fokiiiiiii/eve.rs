using Eve.Dogma;
using Eve.Space;
using Eve.Structures;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-1 d: the structures of a system in the simulation. A system's structures are put in space with the static data's vitals and the record's
/// state, a record's state change reaches the simulation through the bus, and a hit is a state event of the record. The store, the timers and the bus
/// are the real ones; the simulation is the fake one.
/// </summary>
public sealed class SpaceStructuresTests
{
    private const int Astrahus = 35_832;
    private const int System = 30_100_032;
    private const long StructureId = 1_040_000_000_001;
    private const long Corporation = 98_000_001;
    private const long UnixEpochFileTime = 116_444_736_000_000_000L;

    private sealed class Rules : IStructureRulesSource
    {
        public StructureRules RulesOf(int typeId, int solarSystemId) =>
            new(StructureSize.Medium, SecurityBand.HighSec, true, 86_400_000L, 604_800_000L);
    }

    private static async Task<(StructureStore Store, StructureTimers Timers)> RecordsAsync(Rig rig)
    {
        await StructureStore.MigrateAsync(rig.Source, rig.StructureSchema);
        var store = new StructureStore(rig.Source, rig.StructureSchema);
        return (store, new StructureTimers(store, rig.Scheduler, rig.Events, new Rules(), () => 0.5));
    }

    /// <summary>A structure of the store in the given state (created, then given the snapshot).</summary>
    private static async Task SaveAsync(Rig rig, StructureStore store, long id, int system, StructureSnapshot snapshot)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var created = new NewStructure(
            id, Astrahus, system, 1_000_000.0, 2_000.0, 3_000.0, 0.0, Corporation, 0, "Test Astrahus", "", 0,
            StructureTimes.DefaultReinforceWeekday, StructureTimes.DefaultReinforceHour);
        await store.CreateAsync(connection, transaction, created);
        await store.SaveAsync(connection, transaction, snapshot with { StructureId = id });
        await transaction.CommitAsync();
    }

    private static long NowMs(Rig rig) => rig.Now;

    [PgFact]
    public async Task A_system_puts_its_structures_in_space_with_the_static_vitals_and_the_record_state()
    {
        await using var rig = await Rig.CreateAsync();
        var (store, timers) = await RecordsAsync(rig);
        var now = NowMs(rig);
        await SaveAsync(rig, store, StructureId, System, new StructureSnapshot
        {
            State = StructureState.Anchoring, Upkeep = UpkeepState.LowPower, StateStartedMs = now, StateEndsMs = now + 3_600_000,
        });
        await SaveAsync(rig, store, StructureId + 1, System, new StructureSnapshot { State = StructureState.Unanchored });
        var structures = new SpaceStructures(rig.Sim, rig.Space, rig.Source, store, timers, rig.Statics);

        await structures.RestoreAsync(System);

        var placed = Assert.Single(rig.Sim.Heard.OfType<SimPlaceStructure>());
        var type = rig.Statics.Type(Astrahus)!;
        Assert.Equal(StructureId, placed.StructureId);
        Assert.Equal(type.GroupId, placed.GroupId);
        Assert.Equal(type.Attributes[263], placed.Health.ShieldCapacity);
        Assert.Equal(type.Attributes[265], placed.Health.ArmorHp);
        Assert.Equal(type.Attributes[9], placed.Health.StructureHp);
        Assert.Equal(type.Attributes[2034], placed.Health.PerSecondLimit[0]);
        Assert.Equal(12, placed.Health.Resonances.Count);
        Assert.Equal(StructureState.Anchoring, placed.Status.State);
        Assert.Equal(now * 10_000 + UnixEpochFileTime, placed.Status.Timer!.Value.Start);

        await structures.RestoreAsync(System + 1);
        Assert.Single(rig.Sim.Heard.OfType<SimPlaceStructure>());
    }

    [PgFact]
    public async Task A_state_change_of_the_record_reaches_the_simulation_through_the_bus()
    {
        await using var rig = await Rig.CreateAsync();
        var (store, timers) = await RecordsAsync(rig);
        var structures = new SpaceStructures(rig.Sim, rig.Space, rig.Source, store, timers, rig.Statics);
        structures.Subscribe(rig.Events);
        await rig.Events.PumpAsync();
        var now = NowMs(rig);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Events.PublishAsync(connection, transaction, StructureTimers.StateChangedTopic, new StructureStateChanged(
                StructureId, System, StructureState.ShieldVulnerable, UpkeepState.FullPower, new StructureTimerInfo(now, now + 3_600_000, 0), false, null));
            await transaction.CommitAsync();
        }
        await rig.Events.PumpAsync();

        var set = Assert.Single(rig.Sim.Heard.OfType<SimSetStructureState>());
        Assert.Equal(System, set.SolarSystemId);
        Assert.Equal(StructureState.ShieldVulnerable, set.Status.State);
        Assert.Equal(now * 10_000 + UnixEpochFileTime, set.Status.Timer!.Value.Start);
        Assert.Equal(3_600_000L * 10_000, set.Status.Timer.Value.End - set.Status.Timer.Value.Start);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Events.PublishAsync(connection, transaction, StructureTimers.StateChangedTopic, new StructureStateChanged(
                StructureId, System, 0, 0, new StructureTimerInfo(0, 0, 0), false, "destroyed"));
            await transaction.CommitAsync();
        }
        await rig.Events.PumpAsync();
        var removed = Assert.Single(rig.Sim.Heard.OfType<SimRemoveStructure>());
        Assert.Equal(StructureId, removed.StructureId);
    }

    [PgFact]
    public async Task A_hit_that_empties_the_shield_is_a_state_event_of_the_record_and_the_simulation_is_told_the_new_state()
    {
        await using var rig = await Rig.CreateAsync();
        var (store, timers) = await RecordsAsync(rig);
        var structures = new SpaceStructures(rig.Sim, rig.Space, rig.Source, store, timers, rig.Statics);
        structures.Subscribe(rig.Events);
        await rig.Events.PumpAsync();
        var now = NowMs(rig);
        await SaveAsync(rig, store, StructureId, System, new StructureSnapshot
        {
            State = StructureState.ShieldVulnerable, Upkeep = UpkeepState.FullPower, OnlineServices = 1, StateStartedMs = now,
        });

        await structures.DamageAsync(new SimStructureDamaged(StructureId, Depleted: 1, OverThreshold: true));
        await rig.Events.PumpAsync();

        var after = (await store.GetAsync(StructureId))!;
        Assert.Equal(StructureState.ArmorReinforce, after.Snapshot.State);
        var told = Assert.Single(rig.Sim.Heard.OfType<SimSetStructureState>());
        Assert.Equal(StructureState.ArmorReinforce, told.Status.State);
        Assert.NotNull(told.Status.Timer);

        // a hit on a structure the store does not have changes nothing
        await structures.DamageAsync(new SimStructureDamaged(StructureId + 99, Depleted: 1, OverThreshold: true));
        await rig.Events.PumpAsync();
        Assert.Single(rig.Sim.Heard.OfType<SimSetStructureState>());
    }

    [PgFact]
    public async Task A_damage_notice_the_simulation_sends_is_applied_to_the_record_through_the_received_message()
    {
        await using var rig = await Rig.CreateAsync();
        var (store, timers) = await RecordsAsync(rig);
        var structures = new SpaceStructures(rig.Sim, rig.Space, rig.Source, store, timers, rig.Statics);
        structures.Subscribe(rig.Events);
        await rig.Events.PumpAsync();
        var now = NowMs(rig);
        await SaveAsync(rig, store, StructureId, System, new StructureSnapshot
        {
            State = StructureState.ShieldVulnerable, Upkeep = UpkeepState.FullPower, OnlineServices = 1, StateStartedMs = now,
        });

        // the notice arrives as the link's receive loop delivers it; the host applies it in the background of the space sessions
        rig.Sim.Deliver(new SimStructureDamaged(StructureId, Depleted: 1, OverThreshold: true));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var after = (await store.GetAsync(StructureId))!;
        while (after.Snapshot.State == StructureState.ShieldVulnerable && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            after = (await store.GetAsync(StructureId))!;
        }
        Assert.Equal(StructureState.ArmorReinforce, after.Snapshot.State);
        await rig.Events.PumpAsync();
        var told = Assert.Single(rig.Sim.Heard.OfType<SimSetStructureState>());
        Assert.Equal(StructureState.ArmorReinforce, told.Status.State);
    }
}
