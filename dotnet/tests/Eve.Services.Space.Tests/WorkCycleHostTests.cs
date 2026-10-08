using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The host's side of the work cycles (ADR 0013): a module's cycle is settled by the dispatcher the drone's cycle uses, each owner's last settled cycle is kept in
/// PostgreSQL (so a repeat is answered from it after a restart), and a module the simulation ends is taken out of the ship the host sends (FP-C1).
/// </summary>
public class WorkCycleHostTests
{
    private const byte Mine = SimDroneCommands.MineRepeatedly;

    [PgFact]
    public async Task A_module_cycle_for_a_module_that_is_not_running_is_refused_as_not_controlled()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, rock) = await MiningPilotAsync(rig);

        var result = await SettleAsync(rig, ModuleCycle(flying, flying.Module, rock, 1));

        Assert.False(result.Accepted);
        Assert.Equal("NOT_CONTROLLED", result.Reason);
    }

    [PgFact]
    public async Task A_module_cycle_from_a_session_that_is_not_in_space_is_refused_as_no_pilot()
    {
        await using var rig = await Rig.CreateAsync();

        var result = await SettleAsync(rig, new SimModuleWorkCycle(999, 1, 2, 3, 4, 5, 6, Mine, 20, 1.0, 0.1, 0.05, 2.0, 0, 0.25, 0.5, 0.75));

        Assert.Equal("NO_PILOT", result.Reason);
    }

    [PgFact]
    public async Task A_module_cycle_whose_target_is_not_there_is_refused_as_no_target()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, _) = await MiningPilotAsync(rig);
        StartModule(rig, flying);

        var result = await SettleAsync(rig, ModuleCycle(flying, flying.Module, 987_654_321, 2));

        Assert.Equal("NO_TARGET", result.Reason);
    }

    [PgFact]
    public async Task A_module_cycle_whose_rock_is_beyond_the_module_reach_is_refused_as_out_of_range()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, rock) = await MiningPilotAsync(rig);
        StartModule(rig, flying);
        rig.Sim.MeasureAnswer = 1e9;

        var result = await SettleAsync(rig, ModuleCycle(flying, flying.Module, rock, 3));

        Assert.Equal("OUT_OF_RANGE", result.Reason);
    }

    [PgFact]
    public async Task A_module_cycle_in_reach_is_refused_by_name_until_its_yield_is_wired_and_the_refusal_is_recorded()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, rock) = await MiningPilotAsync(rig);
        StartModule(rig, flying);

        var result = await SettleAsync(rig, ModuleCycle(flying, flying.Module, rock, 4));

        Assert.False(result.Accepted);
        Assert.Equal("NOT_IMPLEMENTED", result.Reason);
        var recorded = await new PgWorkCycleLedger(rig.Source, rig.SpaceSchema).LastAsync(flying.Module);
        Assert.Equal((4L, "NOT_IMPLEMENTED"), (recorded?.CycleId, recorded?.Reason));
    }

    [PgFact]
    public async Task A_repeat_of_the_owners_last_cycle_is_answered_from_the_record_and_never_settled_again()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, rock) = await MiningPilotAsync(rig);
        StartModule(rig, flying);
        rig.Sim.MeasureAnswer = 1e9;
        var first = await SettleAsync(rig, ModuleCycle(flying, flying.Module, rock, 31));
        Assert.Equal("OUT_OF_RANGE", first.Reason);

        // the rock is in reach now, but the cycle was settled: the repeat gets the same answer
        rig.Sim.MeasureAnswer = 100;
        var repeat = await SettleAsync(rig, ModuleCycle(flying, flying.Module, rock, 31));
        Assert.Equal("OUT_OF_RANGE", repeat.Reason);

        // a new cycle of the same module is settled afresh
        var next = await SettleAsync(rig, ModuleCycle(flying, flying.Module, rock, 32));
        Assert.Equal("NOT_IMPLEMENTED", next.Reason);
    }

    [PgFact]
    public async Task A_restarted_host_answers_a_repeat_from_the_ledger_in_postgresql()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, rock) = await MiningPilotAsync(rig);
        StartModule(rig, flying);
        rig.Sim.MeasureAnswer = 1e9;
        Assert.Equal("OUT_OF_RANGE", (await SettleAsync(rig, ModuleCycle(flying, flying.Module, rock, 41))).Reason);

        // the host restarts: the old dispatcher goes, a new one reads the same schema
        rig.Containers.Dispose();
        using var restarted = new SpaceContainers(
            rig.Sim, rig.Ships, rig.Items, rig.SpaceItems, new PgWorkCycleLedger(rig.Source, rig.SpaceSchema), rig.Statics, rig.Space, () => rig.Now);
        rig.Sim.MeasureAnswer = 100;

        var repeat = await SettleAsync(rig, ModuleCycle(flying, flying.Module, rock, 41));

        Assert.Equal("OUT_OF_RANGE", repeat.Reason);
    }

    [PgFact]
    public async Task A_module_the_simulation_ends_is_taken_out_of_the_ship_the_host_sends()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, _) = await MiningPilotAsync(rig);
        var session = (ulong)flying.Session.SessionId;
        StartModule(rig, flying);
        var sent = rig.Sim.Heard.OfType<SimShipChanged>().Count();

        rig.Sim.Say(new SimModuleEnded(session, flying.Module, SimModuleEndReasons.Done));

        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimShipChanged>().Count() > sent);
        Assert.False(rig.Space.IsModuleActive(session, flying.Module));
    }

    /// <summary>A pilot in space with a Miner I fitted and online, and a rock of Veldspar in its system.</summary>
    private static async Task<(DroneHostTests.Flying Flying, long Rock)> MiningPilotAsync(Rig rig)
    {
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var minerTypeId = DroneHostTests.SdeTypeId(types, "Miner I");
        var oreTypeId = DroneHostTests.SdeTypeId(types, "Veldspar");
        await rig.Statics.EnsureTypesAsync([minerTypeId, oreTypeId]);
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: minerTypeId);
        var rock = await rig.Ships.CreateAsteroidAsync(oreTypeId, flying.System, 0, 1, [0d, 0d, 0d], [0d, 0d, 1d], 10d, rig.SpaceItems);
        return (flying, rock.Item.Id);
    }

    /// <summary>The pilot's module runs, as an activation the simulation accepted leaves it.</summary>
    private static void StartModule(Rig rig, DroneHostTests.Flying flying) =>
        rig.Space.StartModuleForTest((ulong)flying.Session.SessionId, flying.Module);

    private static SimModuleWorkCycle ModuleCycle(DroneHostTests.Flying flying, long moduleId, long targetId, ulong cycleId) => new(
        (ulong)flying.Session.SessionId, flying.Character, flying.Session.GetInt("shipid")!.Value, flying.System, moduleId, targetId, cycleId, Mine,
        20, 1.0, 0.1, 0.05, 2.0, 0, 0.25, 0.5, 0.75);

    /// <summary>Sends a cycle and waits for the answer to it (a repeat waits for a second answer).</summary>
    private static async Task<SimModuleWorkResult> SettleAsync(Rig rig, SimModuleWorkCycle cycle)
    {
        var answered = rig.Sim.Heard.OfType<SimModuleWorkResult>().Count(result => result.CycleId == cycle.CycleId);
        rig.Sim.Say(cycle);
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimModuleWorkResult>().Count(result => result.CycleId == cycle.CycleId) > answered);
        return rig.Sim.Heard.OfType<SimModuleWorkResult>().Last(result => result.CycleId == cycle.CycleId);
    }
}
