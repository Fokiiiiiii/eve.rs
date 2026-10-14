using Eve.Scheduler;
using Npgsql;

namespace Eve.Structures;

/// <summary>The EveJS service ids (<c>structureConstants.js</c> STRUCTURE_SERVICE_ID) the structure phase uses, and the service module type that carries each one.</summary>
public static class StructureServiceIds
{
    public const int Docking = 1;
    public const int Reprocessing = 4;
    public const int CynoBeacon = 16;
    public const int CynoJammer = 17;
    public const int MoonMining = 14;

    /// <summary>The service module of each service (SDE): Standup Reprocessing Facility I, the Pharolux's built-in Standup Cynosural Field Generator I, Standup Cynosural System Jammer I.</summary>
    public static readonly IReadOnlyDictionary<int, int> ModuleTypeOf = new Dictionary<int, int>
    {
        [Reprocessing] = 35899,
        [CynoBeacon] = 35912,
        [CynoJammer] = 35914,
        [MoonMining] = 45009,
    };
}

/// <summary>What a service costs in fuel: the hourly burn while it is online and the charge to put it online (SDE attributes 2109 and 2110 of its module).</summary>
public sealed record ServiceFuelRule(int ServiceId, int FuelPerHour, int FuelToOnline);

/// <summary>Works out the fuel rule of a service id. The Host reads it from the static data.</summary>
public interface IServiceFuelRules
{
    ServiceFuelRule? RuleOf(int serviceId);
}

/// <summary>The final and prior inventory quantities changed by a fuel-bay take.</summary>
public sealed record StructureFuelItemChange(long ItemId, int TypeId, long OwnerId, long LocationId, int Flag, long Quantity, bool Singleton, long PreviousLocationId, int PreviousFlag, long PreviousQuantity);

/// <summary>The amount and item changes from a fuel-bay take in the caller's transaction.</summary>
public sealed record StructureFuelTake(long Taken, IReadOnlyList<StructureFuelItemChange> Changes);



/// <summary>The fuel bay of a structure: the fuel blocks in it (inventory items at the structure, flag 172). The Host implements it over the item store; this module owns
/// the rule of when fuel is taken, not where it is kept.</summary>
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

/// <summary>
/// The fuel of the service modules: a service goes online against a charge of fuel (EveJS <c>consumeStructureServiceModuleOnlineFuel</c>, the SDE's 2110), then burns
/// its hourly amount (2109) at each full hour. When the bay cannot pay the hour, every service goes offline and the structure enters Low Power
/// (<see cref="StructureTimers.FuelOutAsync"/>). The hourly job is the durable job <c>structure.fuel_hour</c> (key <c>structure:{id}</c>): the same
/// structure has one, and a replay after a restart finds the hour already paid. Every method takes the caller's transaction.
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

    public StructureFuel(StructureStore store, DurableScheduler scheduler, StructureTimers timers, IServiceFuelRules rules, IStructureFuelBay bay)
    {
        _store = store;
        _scheduler = scheduler;
        _timers = timers;
        _rules = rules;
        _bay = bay;
    }

    /// <summary>Registers the hourly job's handler. Call it once, before the foundations start.</summary>
    public void Register() => _scheduler.Register(JobKind, (job, token) => HandleAsync(job, token));

    /// <summary>
    /// Puts a service online, charging its online fuel from the bay. The structure row is locked for the transaction, so an onlining and an hour's burn of the same
    /// structure run one after the other. An already online service is answered without a charge.
    /// </summary>
    public async Task<ServiceOnlineOutcome> OnlineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, int serviceId, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (record is null || record.Snapshot.DestroyedMs > 0)
        {
            return ServiceOnlineOutcome.NoStructure;
        }
        if (_rules.RuleOf(serviceId) is not { } rule)
        {
            return ServiceOnlineOutcome.UnknownService;
        }
        if ((await _store.ServicesAsync(connection, transaction, structureId, cancellationToken)).GetValueOrDefault(serviceId) == ServiceState.Online)
        {
            return ServiceOnlineOutcome.Online;
        }
        if (await _bay.QuantityAsync(connection, transaction, structureId, cancellationToken) < rule.FuelToOnline)
        {
            return ServiceOnlineOutcome.NotEnoughFuel;
        }
        var fuel = await _bay.TakeAsync(connection, transaction, structureId, rule.FuelToOnline, allowPartial: false, cancellationToken: cancellationToken);
        if (fuel.Taken < rule.FuelToOnline)
        {
            return ServiceOnlineOutcome.NotEnoughFuel;
        }
        await _timers.SetServiceAsync(connection, transaction, structureId, serviceId, true, cancellationToken);
        await ScheduleNextHourAsync(connection, transaction, structureId, cancellationToken);
        return ServiceOnlineOutcome.Online;
    }

    private async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.Payload<HourJob>() ?? throw new InvalidOperationException("structure.fuel_hour job without a payload");
        await HourAsync(job.Connection, job.Transaction, payload.StructureId, cancellationToken);
    }

    /// <summary>
    /// Pays one hour for every online service. The bay gives what it has; a shortfall takes every service offline (Low Power). With nothing online the
    /// hour is not scheduled again, and the next online service schedules it.
    /// </summary>
    public async Task HourAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (record is null || record.Snapshot.DestroyedMs > 0)
        {
            return;
        }
        var hourly = 0L;
        foreach (var (serviceId, state) in await _store.ServicesAsync(connection, transaction, structureId, cancellationToken))
        {
            if (state == ServiceState.Online && _rules.RuleOf(serviceId) is { } rule)
            {
                hourly += rule.FuelPerHour;
            }
        }
        if (hourly == 0)
        {
            return;
        }
        var fuel = await _bay.TakeAsync(connection, transaction, structureId, hourly, allowPartial: true, cancellationToken: cancellationToken);
        if (fuel.Taken < hourly)
        {
            await _timers.FuelOutAsync(connection, transaction, structureId, cancellationToken);
            return;
        }
        await ScheduleNextHourAsync(connection, transaction, structureId, cancellationToken);
    }

    private async Task ScheduleNextHourAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken)
    {
        var due = (_scheduler.Now / HourMs + 1) * HourMs;
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(JobKind, due) { Key = $"structure:{structureId}", Payload = new HourJob(structureId) }, cancellationToken);
    }
}
