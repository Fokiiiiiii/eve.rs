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
    }

    /// <summary>A bay per structure. The real bay is the item store (the Host's tests cover it); this one only keeps the quantities.</summary>
    private sealed class FakeBay : IStructureFuelBay
    {
        public Dictionary<long, long> Stock { get; } = [];

        public Task<long> QuantityAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction? transaction, long structureId, CancellationToken cancellationToken) =>
            Task.FromResult(Stock.GetValueOrDefault(structureId));

        public Task<long> TakeAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, long structureId, long quantity, CancellationToken cancellationToken)
        {
            var have = Stock.GetValueOrDefault(structureId);
            var taken = Math.Min(have, quantity);
            Stock[structureId] = have - taken;
            return Task.FromResult(taken);
        }
    }

    private static StructureFuel Fuel(Rig rig, FakeBay bay)
    {
        var fuel = new StructureFuel(rig.Store, rig.Scheduler, rig.Timers, new FakeRules(), bay);
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
        Assert.Equal((rig.Now / HourMs + 1) * HourMs, job.RunAtMs);
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

        Assert.Equal(0, bay.Stock[Structure]);
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
}
