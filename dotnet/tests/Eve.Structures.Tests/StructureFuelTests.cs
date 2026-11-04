using static Eve.Structures.StructureFuel;

namespace Eve.Structures.Tests;

/// <summary>The service fuel on PostgreSQL: onlining needs three days of fuel (SDE 2110), every full hour burns each online service's hourly amount (SDE 2109), and a short hour takes every service offline.</summary>
public class StructureFuelTests
{
    private const long Structure = 1001;

    private sealed class FakeRules : IServiceFuelRules
    {
        public ServiceFuelRule? RuleOf(int serviceId) => serviceId switch
        {
            StructureServiceIds.CynoJammer => new ServiceFuelRule(StructureServiceIds.CynoJammer, 40, 2880),
            StructureServiceIds.Reprocessing => new ServiceFuelRule(StructureServiceIds.Reprocessing, 10, 720),
            _ => null,
        };

        public StructureServiceModuleFuelRule? RuleOfModule(int moduleTypeId, int structureTypeId) =>
            moduleTypeId == 35892 ? new StructureServiceModuleFuelRule(2.5, 2.5) : null;

        public IReadOnlyList<int> ServicesOfModule(int moduleTypeId) =>
            moduleTypeId == 35892 ? [StructureServiceIds.Market] : [];
    }

    private sealed class FakeModuleStates : IStructureServiceModuleStates
    {
        public Dictionary<long, bool> States { get; } = [];

        public Task SetAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, long moduleId, bool online, CancellationToken cancellationToken)
        {
            States[moduleId] = online;
            return Task.CompletedTask;
        }
    }

    /// <summary>A bay per structure. The real bay is the item store (the Host's tests cover it); this one only keeps the quantities.</summary>
    private sealed class FakeBay : IStructureFuelBay
    {
        public Dictionary<long, long> Stock { get; } = [];

        public Task<long> QuantityAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction? transaction, long structureId, CancellationToken cancellationToken) =>
            Task.FromResult(Stock.GetValueOrDefault(structureId));

        public Task<StructureFuelTake> TakeAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, long structureId, long quantity, bool allowPartial, CancellationToken cancellationToken)
        {
            var have = Stock.GetValueOrDefault(structureId);
            if (!allowPartial && have < quantity)
                return Task.FromResult(new StructureFuelTake(0, []));
            var taken = Math.Min(have, quantity);
            Stock[structureId] = have - taken;
            return Task.FromResult(new StructureFuelTake(taken, []));
        }
    }

    private static StructureFuel Fuel(Rig rig, FakeBay bay, IStructureServiceModuleStates? moduleStates = null)
    {
        var fuel = new StructureFuel(rig.Store, rig.Scheduler, rig.Timers, new FakeRules(), bay, moduleStates);
        fuel.Register();
        return fuel;
    }

    private static async Task<IReadOnlyDictionary<int, int>> Services(Rig rig, long structureId) =>
        await rig.Commit((c, t) => rig.Store.ServicesAsync(c, t, structureId));

    [PgFact]
    public async Task Onlining_is_refused_without_three_days_of_fuel_and_changes_nothing()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 2879 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);

        var outcome = await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer));

        Assert.Equal(ServiceOnlineOutcome.NotEnoughFuel, outcome);
        Assert.Equal(2879, bay.Stock[Structure]);
        Assert.False((await Services(rig, Structure)).ContainsKey(StructureServiceIds.CynoJammer));
        Assert.Null(await rig.Scheduler.FindAsync(JobKind, $"structure:{Structure}"));
    }

    [PgFact]
    public async Task Onlining_charges_the_online_fuel_and_schedules_the_first_full_hour()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 2880 + 5 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);

        var outcome = await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer));

        Assert.Equal(ServiceOnlineOutcome.Online, outcome);
        Assert.Equal(5, bay.Stock[Structure]);
        Assert.Equal(ServiceState.Online, (await Services(rig, Structure))[StructureServiceIds.CynoJammer]);
        var job = await rig.Scheduler.FindAsync(JobKind, $"structure:{Structure}");
        Assert.NotNull(job);
        Assert.Equal(rig.Now + HourMs, job.RunAtMs);
    }

    [PgFact]
    public async Task First_cycle_is_one_hour_after_service_online_time()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 2880 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);
        rig.Now += 7 * 60_000;

        Assert.Equal(ServiceOnlineOutcome.Online, await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer)));

        Assert.Equal(rig.Now + HourMs, (await rig.Scheduler.FindAsync(JobKind, $"structure:{Structure}"))!.RunAtMs);
    }

    [PgFact]
    public async Task Services_started_at_different_times_pay_only_their_due_cycles()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 2880 + 720 + 1000 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);
        await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer));
        var firstDue = rig.Now + HourMs;

        rig.Now += 15 * 60_000;
        await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.Reprocessing));
        Assert.Equal(firstDue, (await rig.Scheduler.FindAsync(JobKind, $"structure:{Structure}"))!.RunAtMs);

        rig.Now = firstDue;
        await rig.Tick();
        Assert.Equal(1000 - 40, bay.Stock[Structure]);
        var secondDue = firstDue + 15 * 60_000;
        Assert.Equal(secondDue, (await rig.Scheduler.FindAsync(JobKind, $"structure:{Structure}"))!.RunAtMs);

        rig.Now = secondDue;
        await rig.Tick();
        Assert.Equal(1000 - 40 - 10, bay.Stock[Structure]);
    }

    [PgFact]
    public async Task Every_full_hour_burns_the_hourly_fuel_of_each_online_service()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 2880 + 720 + 1000 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);
        await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer));
        await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.Reprocessing));
        Assert.Equal(1000, bay.Stock[Structure]);

        rig.Now += HourMs;
        await rig.Tick();
        Assert.Equal(1000 - (40 + 10), bay.Stock[Structure]);

        rig.Now += HourMs;
        await rig.Tick();
        Assert.Equal(1000 - 2 * (40 + 10), bay.Stock[Structure]);
        Assert.Equal(ServiceState.Online, (await Services(rig, Structure))[StructureServiceIds.CynoJammer]);
    }

    [PgFact]
    public async Task A_short_hour_takes_every_service_offline_and_the_structure_enters_low_power()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 2880 + 20 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);
        await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer));

        rig.Now += HourMs;
        await rig.Tick();

        Assert.Equal(20, bay.Stock[Structure]);
        Assert.Equal(ServiceState.Offline, (await Services(rig, Structure))[StructureServiceIds.CynoJammer]);
        Assert.Equal(UpkeepState.LowPower, (await rig.Snapshot(Structure)).Upkeep);
        Assert.Null(await rig.Scheduler.FindAsync(JobKind, $"structure:{Structure}"));
    }

    [PgFact]
    public async Task A_restart_pays_each_hour_once()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 2880 + 1000 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);
        await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer));

        await rig.Restart();
        fuel = Fuel(rig, bay);
        rig.Now += HourMs;
        await rig.Tick();
        await rig.Tick();

        Assert.Equal(1000 - 40, bay.Stock[Structure]);
    }

    [PgFact]
    public async Task Onlining_an_unknown_service_or_a_missing_structure_is_named_and_charges_nothing()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 5000 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);

        Assert.Equal(ServiceOnlineOutcome.UnknownService, await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, 999)));
        Assert.Equal(ServiceOnlineOutcome.NoStructure, await rig.Commit((c, t) => fuel.OnlineAsync(c, t, 2002, StructureServiceIds.CynoJammer)));
        Assert.Equal(5000, bay.Stock[Structure]);
    }

    [PgFact]
    public async Task Onlining_an_online_service_again_charges_nothing()
    {
        await using var rig = await Rig.Create();
        var bay = new FakeBay { Stock = { [Structure] = 2880 } };
        var fuel = Fuel(rig, bay);
        await rig.Working(Structure);
        await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer));

        Assert.Equal(ServiceOnlineOutcome.Online, await rig.Commit((c, t) => fuel.OnlineAsync(c, t, Structure, StructureServiceIds.CynoJammer)));
        Assert.Equal(0, bay.Stock[Structure]);
    }

    [PgFact]
    public async Task Service_module_fuel_retries_without_charge_and_survives_restart_with_fractional_cycles()
    {
        await using var rig = await Rig.Create();
        const long module = 901;
        const int moduleType = 35892;
        int[] services = [StructureServiceIds.Market];
        var bay = new FakeBay { Stock = { [Structure] = 2 } };
        var moduleStates = new FakeModuleStates();
        var fuel = Fuel(rig, bay, moduleStates);
        await rig.Working(Structure);

        Assert.Equal(StructureServiceModuleOutcome.NotEnoughFuel,
            await rig.Commit((c, t) => fuel.OnlineModuleAsync(c, t, Structure, module, moduleType, services)));
        Assert.Equal(2, bay.Stock[Structure]);
        Assert.Empty(await rig.Commit((c, t) => rig.Store.ServiceModuleFuelCyclesAsync(c, t, Structure)));
        Assert.False((await Services(rig, Structure)).ContainsKey(StructureServiceIds.Market));

        bay.Stock[Structure] = 23;
        Assert.Equal(StructureServiceModuleOutcome.Changed,
            await rig.Commit((c, t) => fuel.OnlineModuleAsync(c, t, Structure, module, moduleType, services)));
        Assert.Equal(20, bay.Stock[Structure]);
        Assert.Equal(ServiceState.Online, (await Services(rig, Structure))[StructureServiceIds.Market]);
        var first = Assert.Single(await rig.Commit((c, t) => rig.Store.ServiceModuleFuelCyclesAsync(c, t, Structure)));
        Assert.Equal(rig.Now + HourMs, first.NextFuelCycleMs);

        Assert.Equal(StructureServiceModuleOutcome.Unchanged,
            await rig.Commit((c, t) => fuel.OnlineModuleAsync(c, t, Structure, module, moduleType, services)));
        Assert.Equal(20, bay.Stock[Structure]);

        await rig.Restart();
        fuel = Fuel(rig, bay, moduleStates);
        rig.Now += HourMs;
        await rig.Tick();
        await rig.Tick();
        Assert.Equal(18, bay.Stock[Structure]);
        var afterFirstCycle = Assert.Single(await rig.Commit((c, t) => rig.Store.ServiceModuleFuelCyclesAsync(c, t, Structure)));
        Assert.Equal(0.5, afterFirstCycle.FuelRemainder);
        Assert.Equal(rig.Now + HourMs, afterFirstCycle.NextFuelCycleMs);

        rig.Now += HourMs;
        await rig.Tick();
        Assert.Equal(15, bay.Stock[Structure]);

        await rig.Commit((c, t) => fuel.OfflineModuleAsync(c, t, Structure, module));
        Assert.Equal(15, bay.Stock[Structure]);
        Assert.Equal(ServiceState.Offline, (await Services(rig, Structure))[StructureServiceIds.Market]);
        Assert.Empty(await rig.Commit((c, t) => rig.Store.ServiceModuleFuelCyclesAsync(c, t, Structure)));
        Assert.Equal(StructureServiceModuleOutcome.Unchanged,
            await rig.Commit((c, t) => fuel.OfflineModuleAsync(c, t, Structure, module)));
        Assert.Equal(15, bay.Stock[Structure]);
    }
}
