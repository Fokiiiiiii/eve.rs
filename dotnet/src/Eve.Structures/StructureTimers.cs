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

/// <summary>The payload of <see cref="StructureTimers.StateChangedTopic"/>. <c>Repaired</c>: a repair countdown completed, the simulation must make the layers whole. <c>Removal</c>: "unanchored" or "destroyed" when the structure is gone.</summary>
public sealed record StructureStateChanged(long StructureId, int SolarSystemId, int State, int Upkeep, StructureTimerInfo Timer, bool Repaired, string? Removal);

/// <summary>The payload of <see cref="StructureTimers.ServiceChangedTopic"/>: a service module went online (<c>OnlineSinceMs</c> is when) or offline (0).</summary>
public sealed record StructureServiceChanged(long StructureId, int SolarSystemId, int ServiceId, int State, long OnlineSinceMs);

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

    private sealed record TimerJob(long StructureId);

    private readonly StructureStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _events;
    private readonly IStructureRulesSource _rules;
    private readonly Func<double> _roll;

    public StructureTimers(StructureStore store, DurableScheduler scheduler, EventBus events, IStructureRulesSource rules, Func<double>? roll = null)
    {
        _store = store;
        _scheduler = scheduler;
        _events = events;
        _rules = rules;
        _roll = roll ?? Random.Shared.NextDouble;
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
        var rules = _rules.RulesOf(record.TypeId, record.SolarSystemId);
        var transition = StructureStateMachine.Apply(record.Snapshot, rules, e, _scheduler.Now, _roll());
        if (transition.Changed)
        {
            await _store.SaveAsync(connection, transaction, transition.Next, cancellationToken);
            var next = transition.Next;
            await _events.PublishInTransactionAsync(connection, transaction, StateChangedTopic,
                new StructureStateChanged(structureId, record.SolarSystemId, next.State, next.Upkeep, new StructureTimerInfo(next.StateStartedMs, next.StateEndsMs, next.TimerPausedMs),
                    transition.Repaired, transition.Removal switch { StructureRemoval.Unanchored => "unanchored", StructureRemoval.Destroyed => "destroyed", _ => null }),
                $"structure:{structureId}", cancellationToken);
        }
        await ScheduleAsync(connection, transaction, transition.Next, rules, cancellationToken);
        return transition;
    }

    /// <summary>Creates the structure and anchors it (the 15 minute anchoring vulnerability begins). Returns null when the id is taken.</summary>
    public async Task<StructureTransition?> AnchorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewStructure structure, CancellationToken cancellationToken = default) =>
        await _store.CreateAsync(connection, transaction, structure, cancellationToken)
            ? await ApplyAsync(connection, transaction, structure.StructureId, new AnchorPlaced(), cancellationToken)
            : null;

    /// <summary>Puts a service module online or offline, then lets the timeline react (Low Power ends or begins).</summary>
    public async Task<StructureTransition?> SetServiceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, int serviceId, bool online, CancellationToken cancellationToken = default)
    {
        var state = online ? ServiceState.Online : ServiceState.Offline;
        await _store.SetServiceAsync(connection, transaction, structureId, serviceId, state, online ? _scheduler.Now : 0, cancellationToken);
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
            if (await ScheduleAsync(connection, transaction, record.Snapshot, _rules.RulesOf(record.TypeId, record.SolarSystemId), cancellationToken))
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
