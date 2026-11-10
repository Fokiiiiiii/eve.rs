using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Structures;

/// <summary>Works out the rules of a structure from its type and its place. The Host implements it over the static data; the timeline reads no static data itself.</summary>
public interface IStructureRulesSource
{
    StructureRules RulesOf(int typeId, int solarSystemId);
}

/// <summary>The running timer of a structure, as the event and the client's slim item show it.</summary>
public sealed record StructureTimerInfo(long StartedMs, long EndsMs, long PausedMs);

/// <summary>The payload of <see cref="StructureTimers.StateChangedTopic"/>. <c>PreviousState</c>/<c>PreviousUpkeep</c>: what the structure was before the change. <c>Repaired</c>: a repair countdown completed, the simulation must make the layers whole. <c>Removal</c>: "unanchored" or "destroyed" when the structure is gone.</summary>
public sealed record StructureStateChanged(long StructureId, int SolarSystemId, int State, int Upkeep, StructureTimerInfo Timer, bool Repaired, string? Removal, int PreviousState = 0, int PreviousUpkeep = 0, StructureUnanchorDrop? UnanchorDrop = null);

/// <summary>The payload of <c>structure.anchored</c> (<c>StructureAnchorAuthority.AnchoredTopic</c>): an Upwell structure was anchored from an item of the pilot's ship.</summary>
public sealed record StructureAnchored(long StructureId, int SolarSystemId, int TypeId, long OwnerCorporationId);

/// <summary>The payload of <see cref="StructureTimers.ServiceChangedTopic"/>: a service module went online (<c>OnlineSinceMs</c> is when) or offline (0).</summary>
public sealed record StructureServiceChanged(long StructureId, int SolarSystemId, int ServiceId, int State, long OnlineSinceMs);

/// <summary>The service modules taken offline when a structure enters Hull Reinforce or runs out of fuel.</summary>
public sealed record StructureServiceModulesOfflined(long StructureId, int SolarSystemId, long[] ModuleIds, long OccurredAtMs);

/// <summary>
/// The glue of the timeline: loads a structure, applies an event with <see cref="StructureStateMachine"/>, saves the result, publishes <c>structure.state_changed</c> and schedules the next
/// deadline as the durable job <c>structure.timer</c> (key <c>structure:{id}</c>), all in the caller's transaction (FOUNDATIONS rules 1 to 3). The job handler is idempotent: it applies
/// what is due, so a second run of the same job finds nothing due and changes nothing.
/// </summary>
public sealed class StructureTimers
{
    public const string JobKind = "structure.timer";
    public const string StateChangedTopic = "structure.state_changed";
    public const string ServiceChangedTopic = "structure.service_changed";
    public const string ServiceModulesOfflinedTopic = "structure.service_modules_offlined";

    private sealed record TimerJob(long StructureId);

    private readonly StructureStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _events;
    private readonly IStructureRulesSource _rules;
    private readonly IStructureServiceModuleStates? _moduleStates;
    private readonly Func<double> _roll;
    private readonly Func<NpgsqlConnection, NpgsqlTransaction, StructureRecord, CancellationToken, Task<StructureUnanchorDrop>>? _completeUnanchor;
    private readonly Func<NpgsqlConnection, NpgsqlTransaction, StructureRecord, CancellationToken, Task<StructureUnanchorDrop?>>? _completeDestruction;

    public StructureTimers(
        StructureStore store,
        DurableScheduler scheduler,
        EventBus events,
        IStructureRulesSource rules,
        Func<double>? roll = null,
        Func<NpgsqlConnection, NpgsqlTransaction, StructureRecord, CancellationToken, Task<StructureUnanchorDrop>>? completeUnanchor = null,
        Func<NpgsqlConnection, NpgsqlTransaction, StructureRecord, CancellationToken, Task<StructureUnanchorDrop?>>? completeDestruction = null,
        IStructureServiceModuleStates? moduleStates = null)
    {
        _store = store;
        _scheduler = scheduler;
        _events = events;
        _rules = rules;
        _moduleStates = moduleStates;
        _roll = roll ?? Random.Shared.NextDouble;
        _completeUnanchor = completeUnanchor;
        _completeDestruction = completeDestruction;
    }

    /// <summary>Registers the job handler. Call it once, before the foundations start.</summary>
    public void Register() => _scheduler.Register(JobKind, (job, token) => HandleAsync(job, token));

    private async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.Payload<TimerJob>() ?? throw new InvalidOperationException("structure.timer job without a payload");
        await ApplyAsync(job.Connection, job.Transaction, payload.StructureId, new TimerDue(), cancellationToken);
    }

    /// <summary>
    /// Applies an event to a structure in the caller's transaction. Returns null when there is no such structure. A refused event (see <see cref="StructureTransition.Refusal"/>) changes nothing and
    /// publishes nothing. Call <see cref="DurableScheduler.Wake"/> after the commit to have a new deadline picked up at once.
    /// </summary>
    public async Task<StructureTransition?> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, StructureEvent e, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (record is null)
        {
            return null;
        }
        var rules = _rules.RulesOf(record.TypeId, record.SolarSystemId) with { AnchoringMs = record.Snapshot.AnchoringMs };
        var transition = StructureStateMachine.Apply(record.Snapshot, rules, e, _scheduler.Now, _roll());
        StructureUnanchorDrop? unanchorDrop = null;
        if (transition.Changed && transition.Removal == StructureRemoval.Unanchored && _completeUnanchor is { } complete)
        {
            unanchorDrop = await complete(connection, transaction, record, cancellationToken);
            if (unanchorDrop.HullItemId <= 0 || unanchorDrop.FittingsContainerItemId < 0)
            {
                throw new InvalidOperationException($"Structure {structureId} produced invalid unanchor drop item IDs.");
            }
        }

        if (transition.Changed && transition.Removal == StructureRemoval.Destroyed && _completeDestruction is { } loot)
        {
            unanchorDrop = await loot(connection, transaction, record, cancellationToken);
        }
        var offlinedModuleIds = transition.Changed && record.Snapshot.State != StructureState.HullReinforce &&
            transition.Next.State == StructureState.HullReinforce
                ? await OfflineServiceModulesAsync(connection, transaction, structureId, cancellationToken)
                : [];
        if (transition.Changed)
        {
            await _store.SaveAsync(connection, transaction, transition.Next, cancellationToken);
            var next = transition.Next;
            await _events.PublishInTransactionAsync(connection, transaction, StateChangedTopic,
                new StructureStateChanged(structureId, record.SolarSystemId, next.State, next.Upkeep, new StructureTimerInfo(next.StateStartedMs, next.StateEndsMs, next.TimerPausedMs),
                    transition.Repaired, transition.Removal switch { StructureRemoval.Unanchored => "unanchored", StructureRemoval.Destroyed => "destroyed", _ => null },
                    record.Snapshot.State, record.Snapshot.Upkeep, unanchorDrop),
                $"structure:{structureId}", cancellationToken);
        }
        await PublishServiceModulesOfflinedAsync(connection, transaction, structureId, offlinedModuleIds, cancellationToken);
        await ScheduleAsync(connection, transaction, transition.Next, rules, cancellationToken);
        return transition;
    }

    /// <summary>Sets a structure's state from the V24 GM menu, clearing its current state timer as EveJS does.</summary>
    public Task<StructureTransition?> AdminSetStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, int state, CancellationToken cancellationToken = default) =>
        AdminChangeAsync(connection, transaction, structureId, (snapshot, _, now) => !StructureState.IsKnown(state)
            ? new StructureTransition(snapshot, false, "INVALID_STRUCTURE_STATE")
            : new StructureTransition(snapshot with
            {
                State = state,
                StateStartedMs = now,
                StateEndsMs = 0,
                TimerPausedMs = 0,
                UnanchorEndsMs = StructureState.CancelsUnanchor(state) ? 0 : snapshot.UnanchorEndsMs,
            }, true), cancellationToken);

    /// <summary>Sets the current state's remaining timer from the V24 GM menu.</summary>
    public Task<StructureTransition?> AdminSetStateTimerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, long remainingMs, CancellationToken cancellationToken = default) =>
        AdminChangeAsync(connection, transaction, structureId, (snapshot, _, now) => remainingMs < 0 || now > long.MaxValue - remainingMs
            ? new StructureTransition(snapshot, false, "INVALID_TIMER")
            : new StructureTransition(snapshot with { StateStartedMs = now, StateEndsMs = now + remainingMs, TimerPausedMs = 0 }, true), cancellationToken);

    /// <summary>Sets the deployment timer and state from the V24 GM menu.</summary>
    public Task<StructureTransition?> AdminSetDeployTimerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, long remainingMs, CancellationToken cancellationToken = default) =>
        AdminChangeAsync(connection, transaction, structureId, (snapshot, _, now) => remainingMs < 0 || now > long.MaxValue - remainingMs
            ? new StructureTransition(snapshot, false, "INVALID_TIMER")
            : new StructureTransition(snapshot with
            {
                State = StructureState.DeployVulnerable,
                StateStartedMs = now,
                StateEndsMs = now + remainingMs,
                TimerPausedMs = 0,
                UnanchorEndsMs = 0,
            }, true), cancellationToken);

    /// <summary>Sets the remaining unanchoring time from the V24 GM menu, subject to the client's unanchor-cancelling states.</summary>
    public Task<StructureTransition?> AdminSetUnanchorTimerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, long remainingMs, CancellationToken cancellationToken = default) =>
        AdminChangeAsync(connection, transaction, structureId, (snapshot, _, now) =>
            StructureState.CancelsUnanchor(snapshot.State)
                ? new StructureTransition(snapshot, false, "UNANCHOR_NOT_ALLOWED")
                : remainingMs < 0 || now > long.MaxValue - remainingMs
                    ? new StructureTransition(snapshot, false, "INVALID_TIMER")
                    : new StructureTransition(snapshot with { UnanchorEndsMs = now + remainingMs }, true), cancellationToken);

    /// <summary>Sets the Low Power abandonment timer from the V24 GM menu.</summary>
    public Task<StructureTransition?> AdminSetAbandonTimerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, long remainingMs, CancellationToken cancellationToken = default) =>
        AdminChangeAsync(connection, transaction, structureId, (snapshot, rules, now) =>
            snapshot.Upkeep != UpkeepState.LowPower
                ? new StructureTransition(snapshot, false, "STRUCTURE_NOT_LOW_POWER")
                : !rules.CanBeAbandoned
                    ? new StructureTransition(snapshot, false, "STRUCTURE_CANNOT_BE_ABANDONED")
                    : remainingMs < 0 || now > long.MaxValue - remainingMs
                        ? new StructureTransition(snapshot, false, "INVALID_TIMER")
                        : new StructureTransition(snapshot with { AbandonAtMs = now + remainingMs }, true), cancellationToken);

    private async Task<StructureTransition?> AdminChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        Func<StructureSnapshot, StructureRules, long, StructureTransition> change,
        CancellationToken cancellationToken)
    {
        var record = await _store.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (record is null)
            return null;
        if (record.Snapshot.DestroyedMs > 0)
            return new StructureTransition(record.Snapshot, false, "STRUCTURE_GONE");

        var rules = _rules.RulesOf(record.TypeId, record.SolarSystemId) with { AnchoringMs = record.Snapshot.AnchoringMs };
        var transition = change(record.Snapshot, rules, _scheduler.Now);
        if (transition.Refusal is not null)
            return transition;
        if (transition.Changed)
        {
            await _store.SaveAsync(connection, transaction, transition.Next, cancellationToken);
            var next = transition.Next;
            await _events.PublishInTransactionAsync(connection, transaction, StateChangedTopic,
                new StructureStateChanged(structureId, record.SolarSystemId, next.State, next.Upkeep,
                    new StructureTimerInfo(next.StateStartedMs, next.StateEndsMs, next.TimerPausedMs), false, null,
                    record.Snapshot.State, record.Snapshot.Upkeep),
                $"structure:{structureId}", cancellationToken);
        }
        await ScheduleAsync(connection, transaction, transition.Next, rules, cancellationToken);
        return transition;
    }

    /// <summary>Publishes the modules a fuel job turned off after its structure service/state events, in the same transaction.</summary>
    internal async Task PublishServiceModulesOfflinedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        IReadOnlyList<long> moduleIds,
        CancellationToken cancellationToken = default)
    {
        var ids = moduleIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length == 0 || await _store.GetAsync(connection, transaction, structureId, false, cancellationToken) is not { } record)
            return;
        await _events.PublishInTransactionAsync(connection, transaction, ServiceModulesOfflinedTopic,
            new StructureServiceModulesOfflined(structureId, record.SolarSystemId, ids, _scheduler.Now), $"structure:{structureId}", cancellationToken);
    }

    private async Task<long[]> OfflineServiceModulesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        CancellationToken cancellationToken)
    {
        if (_moduleStates is null)
            return [];
        var cycles = await _store.ServiceModuleFuelCyclesAsync(connection, transaction, structureId, cancellationToken);
        foreach (var cycle in cycles)
            await _moduleStates.SetAsync(connection, transaction, cycle.ModuleId, false, cancellationToken);
        if (cycles.Count > 0)
            await _store.DeleteAllServiceModuleFuelCyclesAsync(connection, transaction, structureId, cancellationToken);
        return [.. cycles.Select(cycle => cycle.ModuleId)];
    }

    /// <summary>Creates the structure and anchors it (the 15 minute anchoring vulnerability begins). Returns null when the id is taken.</summary>
    public async Task<StructureTransition?> AnchorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewStructure structure, CancellationToken cancellationToken = default) =>
        await _store.CreateAsync(connection, transaction, structure, cancellationToken)
            ? await ApplyAsync(connection, transaction, structure.StructureId, new AnchorPlaced(), cancellationToken)
            : null;

    /// <summary>Puts a pirate FOB in space: its row and its deadline job, invulnerable until its insurgency exposes it. Returns null when the id is taken.</summary>
    public async Task<StructureTransition?> DeployFobAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewStructure fob, CancellationToken cancellationToken = default) =>
        await _store.CreateAsync(connection, transaction, fob, cancellationToken)
            ? await ApplyAsync(connection, transaction, fob.StructureId, new FobDeployed(), cancellationToken)
            : null;

    /// <summary>Puts a service module online or offline, then lets the timeline react (Low Power ends or begins).</summary>
    public async Task<StructureTransition?> SetServiceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, int serviceId, bool online, CancellationToken cancellationToken = default, bool manageFuelCycle = true)
    {
        var state = online ? ServiceState.Online : ServiceState.Offline;
        await _store.SetServiceAsync(connection, transaction, structureId, serviceId, state, online ? _scheduler.Now : 0, cancellationToken, manageFuelCycle);
        await PublishServiceAsync(connection, transaction, structureId, serviceId, state, online ? _scheduler.Now : 0, cancellationToken);
        return await ApplyAsync(connection, transaction, structureId, online ? new ServiceOnline() : new ServiceOffline(), cancellationToken);
    }

    /// <summary>Announces a service's change in the caller's transaction (the cyno jammer's notice is one of its subscribers).</summary>
    private async Task PublishServiceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, int serviceId, int state, long onlineSinceMs, CancellationToken cancellationToken)
    {
        if (await _store.GetAsync(connection, transaction, structureId, false, cancellationToken) is not { } record)
        {
            return;
        }
        await _events.PublishInTransactionAsync(connection, transaction, ServiceChangedTopic,
            new StructureServiceChanged(structureId, record.SolarSystemId, serviceId, state, onlineSinceMs), $"structure:{structureId}", cancellationToken);
    }

    /// <summary>The fuel ran out: every service goes offline and the structure goes into Low Power.</summary>
    public async Task<StructureTransition?> FuelOutAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken = default)
    {
        var online = (await _store.ServicesAsync(connection, transaction, structureId, cancellationToken))
            .Where(service => service.Value == ServiceState.Online).Select(service => service.Key).ToList();
        await _store.TakeServicesOfflineAsync(connection, transaction, structureId, cancellationToken);
        foreach (var serviceId in online)
        {
            await PublishServiceAsync(connection, transaction, structureId, serviceId, ServiceState.Offline, 0, cancellationToken);
        }
        return await ApplyAsync(connection, transaction, structureId, new FuelOut(), cancellationToken);
    }

    /// <summary>
    /// Makes sure every structure that has a deadline has its job (after an import, or after a database restore). The job of a structure that already has one is replaced by the same
    /// time. Returns how many jobs it scheduled.
    /// </summary>
    public async Task<int> EnsureScheduledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        var scheduled = 0;
        foreach (var record in await _store.ListAsync(connection, transaction, false, cancellationToken))
        {
            var rules = _rules.RulesOf(record.TypeId, record.SolarSystemId) with { AnchoringMs = record.Snapshot.AnchoringMs };
            var snapshot = record.Snapshot;
            if (snapshot.Upkeep == UpkeepState.LowPower && snapshot.AbandonAtMs == 0 && rules.CanBeAbandoned)
            {
                var lowPowerSince = snapshot.LowPowerSinceMs > 0 ? snapshot.LowPowerSinceMs : _scheduler.Now;
                snapshot = snapshot with
                {
                    LowPowerSinceMs = lowPowerSince,
                    AbandonAtMs = lowPowerSince + StructureTimes.AbandonDelayMs(_roll()),
                };
                await _store.SaveAsync(connection, transaction, snapshot, cancellationToken);
            }
            if (await ScheduleAsync(connection, transaction, snapshot, rules, cancellationToken))
            {
                scheduled++;
            }
        }
        return scheduled;
    }

    private async Task<bool> ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StructureSnapshot snapshot, StructureRules rules, CancellationToken cancellationToken)
    {
        var key = $"structure:{snapshot.StructureId}";
        var due = StructureStateMachine.NextDue(snapshot, rules);
        if (due <= 0)
        {
            await _scheduler.CancelAsync(connection, transaction, JobKind, key, cancellationToken);
            return false;
        }
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(JobKind, due) { Key = key, Payload = new TimerJob(snapshot.StructureId) }, cancellationToken);
        return true;
    }
}
