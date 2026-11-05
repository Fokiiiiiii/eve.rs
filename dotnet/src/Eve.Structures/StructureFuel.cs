using Eve.Scheduler;
using Npgsql;

namespace Eve.Structures;

public static class StructureServiceIds
{
    public const int Docking = 1;
    public const int Fitting = 2;
    public const int Offices = 3;
    public const int Reprocessing = 4;
    public const int Market = 5;
    public const int Medical = 6;
    public const int Repair = 8;
    public const int Insurance = 9;
    public const int JumpClone = 10;
    public const int LoyaltyStore = 11;
    public const int MoonMining = 14;
    public const int JumpBridge = 15;
    public const int CynoBeacon = 16;
    public const int CynoJammer = 17;
    public const int AutoMoonMining = 18;
    public const int Industry = 20;
    public const int Manufacturing = 21;
    public const int ManufacturingBasic = 22;
    public const int ManufacturingCapital = 23;
    public const int ManufacturingSupercapital = 24;
    public const int Laboratory = 30;
    public const int LaboratoryResearchTime = 31;
    public const int LaboratoryResearchMaterial = 32;
    public const int LaboratoryCopying = 33;
    public const int LaboratoryInvention = 34;
    public const int Reactions = 40;
    public const int ReactionsComposite = 41;
    public const int ReactionsBiochemical = 42;
    public const int ReactionsHybrid = 43;

    public const int ServiceSlotFirst = 164;
    public const int ServiceSlotLast = 171;

    /// <summary>Client service routing by service module names in the pinned SDE.</summary>
    public static readonly IReadOnlyDictionary<string, int[]> ServicesByModuleTypeName = new Dictionary<string, int[]>(StringComparer.Ordinal)
    {
        ["Standup Market Hub I"] = [Market],
        ["Standup Cloning Center I"] = [Medical],
        ["Standup Reprocessing Facility I"] = [Reprocessing],
        ["Standup Research Lab I"] = [LaboratoryCopying, LaboratoryResearchMaterial, LaboratoryResearchTime],
        ["Standup Invention Lab I"] = [LaboratoryInvention],
        ["Standup Manufacturing Plant I"] = [ManufacturingBasic],
        ["Standup Capital Shipyard I"] = [ManufacturingCapital],
        ["Standup Supercapital Shipyard I"] = [ManufacturingSupercapital, ManufacturingCapital],
        ["Standup Hyasyoda Research Lab"] = [LaboratoryCopying, LaboratoryResearchMaterial, LaboratoryResearchTime],
        ["Standup Hybrid Reactor I"] = [ReactionsHybrid],
        ["Standup Composite Reactor I"] = [ReactionsComposite],
        ["Standup Biochemical Reactor I"] = [ReactionsBiochemical],
        ["Standup Moon Drill I"] = [MoonMining],
        ["Standup Conduit Generator I"] = [JumpBridge],
        ["Standup Cynosural Field Generator I"] = [CynoBeacon],
        ["Standup Cynosural System Jammer I"] = [CynoJammer],
        ["LP Store"] = [LoyaltyStore],
        ["Standup Metenox Moon Drill"] = [AutoMoonMining],
    };
}

/// <summary>Legacy service fuel rule used by non-module callers.</summary>
public sealed record ServiceFuelRule(int ServiceId, int FuelPerHour, int FuelToOnline);

/// <summary>Fuel values read from service-module dogma after applying its structure's SDE modifiers.</summary>
public sealed record StructureServiceModuleFuelRule(double FuelPerHour, double FuelToOnline);

/// <summary>Works out the fuel rule of a service id and a fitted service-module type.</summary>
public interface IServiceFuelRules
{
    ServiceFuelRule? RuleOf(int serviceId);

    StructureServiceModuleFuelRule? RuleOfModule(int moduleTypeId, int structureTypeId) => null;

    IReadOnlyList<int> ServicesOfModule(int moduleTypeId) => [];

    int? ModuleTypeOf(int serviceId) => null;
}

/// <summary>The fitting module-state table is the persisted source for the module's dogma state.</summary>
public interface IStructureServiceModuleStates
{
    Task SetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long moduleId, bool online, CancellationToken cancellationToken);
}

/// <summary>The final and prior inventory quantities changed by a fuel-bay take.</summary>
public sealed record StructureFuelItemChange(long ItemId, int TypeId, long OwnerId, long LocationId, int Flag, long Quantity, bool Singleton, long PreviousLocationId, int PreviousFlag, long PreviousQuantity);

/// <summary>The amount and item changes from a fuel-bay take in the caller's transaction.</summary>
public sealed record StructureFuelTake(long Taken, IReadOnlyList<StructureFuelItemChange> Changes);

/// <summary>The fuel bay of a structure: the fuel blocks in it (inventory items at the structure, flag 172). The Host implements it over the item store.</summary>
public interface IStructureFuelBay
{
    Task<long> QuantityAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, CancellationToken cancellationToken);

    /// <summary>Takes the requested fuel in the caller's transaction; when partial takes are allowed, returns the available amount and exact item deltas.</summary>
    Task<StructureFuelTake> TakeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, long quantity, bool allowPartial, CancellationToken cancellationToken);
}

public enum ServiceOnlineOutcome
{
    Online,
    NoStructure,
    UnknownService,
    NotEnoughFuel,
}

public enum StructureServiceModuleOutcome
{
    Changed,
    Unchanged,
    NoStructure,
    UnknownService,
    NotEnoughFuel,
}

/// <summary>The durable fuel cycle of one fitted service module, which may provide several client services but is charged once per cycle.</summary>
public sealed record StructureServiceModuleFuelCycle(
    long ModuleId,
    int ModuleTypeId,
    int[] ServiceIds,
    long OnlineSinceMs,
    long NextFuelCycleMs,
    double FuelRemainder);

/// <summary>
/// The durable job <c>structure.fuel_hour</c> pays module and legacy service cycles in the caller's transaction. If the bay cannot pay a due cycle, every service goes
/// offline and the structure enters Low Power.
/// </summary>
public sealed class StructureFuel
{
    public const string JobKind = "structure.fuel_hour";
    public const long HourMs = 3_600_000;

    private sealed record HourJob(long StructureId);

    private readonly StructureStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly StructureTimers _timers;
    private readonly IServiceFuelRules _rules;
    private readonly IStructureFuelBay _bay;
    private readonly IStructureServiceModuleStates? _moduleStates;

    public StructureFuel(
        StructureStore store,
        DurableScheduler scheduler,
        StructureTimers timers,
        IServiceFuelRules rules,
        IStructureFuelBay bay,
        IStructureServiceModuleStates? moduleStates = null)
    {
        _store = store;
        _scheduler = scheduler;
        _timers = timers;
        _rules = rules;
        _bay = bay;
        _moduleStates = moduleStates;
    }

    /// <summary>Service ids provided by the SDE type of a fitted module.</summary>
    public IReadOnlyList<int> ServicesOfModule(int moduleTypeId) => _rules.ServicesOfModule(moduleTypeId);

    /// <summary>Registers the hourly job's handler. Call it once, before the foundations start.</summary>
    public void Register() => _scheduler.Register(JobKind, (job, token) => HandleAsync(job, token));

    /// <summary>Legacy service-id path retained for existing structure callers.</summary>
    public async Task<ServiceOnlineOutcome> OnlineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, int serviceId, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (record is null || record.Snapshot.DestroyedMs > 0)
            return ServiceOnlineOutcome.NoStructure;
        if (_rules.RuleOf(serviceId) is not { } rule)
            return ServiceOnlineOutcome.UnknownService;
        if ((await _store.ServicesAsync(connection, transaction, structureId, cancellationToken)).GetValueOrDefault(serviceId) == ServiceState.Online)
            return ServiceOnlineOutcome.Online;
        if (await _bay.QuantityAsync(connection, transaction, structureId, cancellationToken) < rule.FuelToOnline)
            return ServiceOnlineOutcome.NotEnoughFuel;
        var fuel = await _bay.TakeAsync(connection, transaction, structureId, rule.FuelToOnline, allowPartial: false, cancellationToken);
        if (fuel.Taken < rule.FuelToOnline)
            return ServiceOnlineOutcome.NotEnoughFuel;
        await _timers.SetServiceAsync(connection, transaction, structureId, serviceId, true, cancellationToken);
        await ScheduleNextHourAsync(connection, transaction, structureId, cancellationToken);
        return ServiceOnlineOutcome.Online;
    }

    /// <summary>Onlines a fitted service module, charging its SDE fuel once and exposing every service it provides.</summary>
    public async Task<StructureServiceModuleOutcome> OnlineModuleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        long moduleId,
        int moduleTypeId,
        IReadOnlyList<int> serviceIds,
        CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (record is null || record.Snapshot.DestroyedMs > 0)
            return StructureServiceModuleOutcome.NoStructure;
        var services = serviceIds.Distinct().Order().ToArray();
        var mappedServices = _rules.ServicesOfModule(moduleTypeId).Order().ToArray();
        if (services.Length == 0 || !services.SequenceEqual(mappedServices)
            || _rules.RuleOfModule(moduleTypeId, record.TypeId) is not { } rule
            || !double.IsFinite(rule.FuelPerHour) || rule.FuelPerHour < 0
            || !double.IsFinite(rule.FuelToOnline) || rule.FuelToOnline < 0)
            return StructureServiceModuleOutcome.UnknownService;
        if (await _store.ServiceModuleFuelCycleAsync(connection, transaction, structureId, moduleId, cancellationToken) is not null)
            return StructureServiceModuleOutcome.Unchanged;

        var fuelToOnline = checked((long)Math.Ceiling(Math.Max(0, rule.FuelToOnline - 1e-9)));
        if (await _bay.QuantityAsync(connection, transaction, structureId, cancellationToken) < fuelToOnline)
            return StructureServiceModuleOutcome.NotEnoughFuel;
        var fuel = await _bay.TakeAsync(connection, transaction, structureId, fuelToOnline, allowPartial: false, cancellationToken);
        if (fuel.Taken < fuelToOnline)
            return StructureServiceModuleOutcome.NotEnoughFuel;

        var now = _scheduler.Now;
        await _store.SetServiceModuleFuelCycleAsync(connection, transaction, structureId, moduleId, moduleTypeId, services, now, checked(now + HourMs), 0, cancellationToken);
        var current = await _store.ServicesAsync(connection, transaction, structureId, cancellationToken);
        foreach (var serviceId in services)
            if (current.GetValueOrDefault(serviceId) != ServiceState.Online)
                await _timers.SetServiceAsync(connection, transaction, structureId, serviceId, true, cancellationToken, manageFuelCycle: false);
        await ScheduleNextHourAsync(connection, transaction, structureId, cancellationToken);
        return StructureServiceModuleOutcome.Changed;
    }

    /// <summary>Offlines one service module without charge, leaving shared services online while another module still provides them.</summary>
    public async Task<StructureServiceModuleOutcome> OfflineModuleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        long moduleId,
        CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (record is null || record.Snapshot.DestroyedMs > 0)
            return StructureServiceModuleOutcome.NoStructure;
        var cycle = await _store.ServiceModuleFuelCycleAsync(connection, transaction, structureId, moduleId, cancellationToken);
        if (cycle is null)
            return StructureServiceModuleOutcome.Unchanged;

        await _store.DeleteServiceModuleFuelCycleAsync(connection, transaction, structureId, moduleId, cancellationToken);
        var current = await _store.ServicesAsync(connection, transaction, structureId, cancellationToken);
        foreach (var serviceId in cycle.ServiceIds)
            if (current.GetValueOrDefault(serviceId) == ServiceState.Online
                && !await _store.HasOnlineServiceModuleAsync(connection, transaction, structureId, serviceId, cancellationToken))
                await _timers.SetServiceAsync(connection, transaction, structureId, serviceId, false, cancellationToken, manageFuelCycle: false);
        await ScheduleNextHourAsync(connection, transaction, structureId, cancellationToken);
        return StructureServiceModuleOutcome.Changed;
    }

    private async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.Payload<HourJob>() ?? throw new InvalidOperationException("structure.fuel_hour job without a payload");
        await HourAsync(job.Connection, job.Transaction, payload.StructureId, cancellationToken);
    }

    /// <summary>Pays due cycles. A shortfall consumes none of them and takes every service offline into Low Power.</summary>
    public async Task HourAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (record is null || record.Snapshot.DestroyedMs > 0)
            return;

        var serviceCycles = await _store.ServiceFuelCyclesAsync(connection, transaction, structureId, cancellationToken);
        var dueServices = serviceCycles
            .Where(service => service.State == ServiceState.Online && service.NextFuelCycleMs > 0 && service.NextFuelCycleMs <= _scheduler.Now
                && _rules.RuleOf(service.ServiceId) is not null)
            .Select(service => (service.ServiceId, Cycles: 1 + (_scheduler.Now - service.NextFuelCycleMs) / HourMs))
            .ToArray();
        var moduleCycles = await _store.ServiceModuleFuelCyclesAsync(connection, transaction, structureId, cancellationToken);
        var dueModules = new List<(StructureServiceModuleFuelCycle Cycle, long Cycles, long Fuel, double Remainder)>();
        foreach (var cycle in moduleCycles.Where(cycle => cycle.NextFuelCycleMs > 0 && cycle.NextFuelCycleMs <= _scheduler.Now))
        {
            var rule = _rules.RuleOfModule(cycle.ModuleTypeId, record.TypeId)
                ?? throw new InvalidOperationException($"structure service module {cycle.ModuleTypeId} has no SDE fuel rule");
            var cycles = 1 + (_scheduler.Now - cycle.NextFuelCycleMs) / HourMs;
            var exact = rule.FuelPerHour * cycles + cycle.FuelRemainder;
            var whole = checked((long)Math.Floor(exact + 1e-9));
            dueModules.Add((cycle, cycles, whole, Math.Max(0, exact - whole)));
        }

        var hourly = dueServices.Sum(service => checked((long)_rules.RuleOf(service.ServiceId)!.FuelPerHour * service.Cycles));
        foreach (var module in dueModules)
            hourly = checked(hourly + module.Fuel);
        if (dueServices.Length == 0 && dueModules.Count == 0)
        {
            await ScheduleNextHourAsync(connection, transaction, structureId, cancellationToken);
            return;
        }
        if (await _bay.QuantityAsync(connection, transaction, structureId, cancellationToken) < hourly)
        {
            await ModulesFuelOutAsync(connection, transaction, structureId, moduleCycles, cancellationToken);
            await _timers.FuelOutAsync(connection, transaction, structureId, cancellationToken);
            return;
        }
        var fuel = await _bay.TakeAsync(connection, transaction, structureId, hourly, allowPartial: false, cancellationToken);
        if (fuel.Taken < hourly)
        {
            await ModulesFuelOutAsync(connection, transaction, structureId, moduleCycles, cancellationToken);
            await _timers.FuelOutAsync(connection, transaction, structureId, cancellationToken);
            return;
        }
        foreach (var (serviceId, cycles) in dueServices)
            await _store.AdvanceFuelCycleAsync(connection, transaction, structureId, serviceId, cycles, cancellationToken);
        foreach (var (cycle, cycles, _, remainder) in dueModules)
            await _store.AdvanceServiceModuleFuelCycleAsync(connection, transaction, structureId, cycle.ModuleId, cycles, remainder, cancellationToken);
        await ScheduleNextHourAsync(connection, transaction, structureId, cancellationToken);
    }

    private async Task ModulesFuelOutAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        IReadOnlyList<StructureServiceModuleFuelCycle> moduleCycles,
        CancellationToken cancellationToken)
    {
        if (_moduleStates is not null)
            foreach (var cycle in moduleCycles)
                await _moduleStates.SetAsync(connection, transaction, cycle.ModuleId, false, cancellationToken);
        await _store.DeleteAllServiceModuleFuelCyclesAsync(connection, transaction, structureId, cancellationToken);
    }

    private async Task ScheduleNextHourAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken)
    {
        await _store.InitializeFuelCyclesAsync(connection, transaction, structureId, _scheduler.Now, cancellationToken);
        var record = await _store.GetAsync(connection, transaction, structureId, false, cancellationToken);
        var due = (await _store.ServiceFuelCyclesAsync(connection, transaction, structureId, cancellationToken))
            .Where(service => service.State == ServiceState.Online && _rules.RuleOf(service.ServiceId) is not null)
            .Select(service => service.NextFuelCycleMs)
            .Concat(record is null
                ? Enumerable.Empty<long>()
                : (await _store.ServiceModuleFuelCyclesAsync(connection, transaction, structureId, cancellationToken))
                    .Where(cycle => _rules.RuleOfModule(cycle.ModuleTypeId, record.TypeId) is not null)
                    .Select(cycle => cycle.NextFuelCycleMs))
            .Where(time => time > 0)
            .DefaultIfEmpty(0)
            .Min();
        if (due == 0)
        {
            await _scheduler.CancelAsync(connection, transaction, JobKind, $"structure:{structureId}", cancellationToken);
            return;
        }
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(JobKind, due) { Key = $"structure:{structureId}", Payload = new HourJob(structureId) }, cancellationToken);
    }
}
