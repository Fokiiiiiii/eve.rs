using Eve.Events;
using Eve.Scheduler;

namespace Eve.FactionWarfare;

/// <summary>
/// Runs the complexes on the durable scheduler: a recurring tick (<see cref="FwComplexes.Tick"/>) spawns the missing complexes and advances their capture timers in its
/// job transaction. Each resolved complex is published in that transaction (<see cref="ResolvedTopic"/>), so its notices go out after the commit.
/// </summary>
public sealed class FwComplexDirector
{
    public const string TickJob = "factionwarfare.complexes.tick";
    public const string ResolvedTopic = "factionwarfare.complex_resolved";

    private readonly FwStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _bus;
    private readonly Func<long, CancellationToken, Task<IReadOnlyList<long>>> _factionsInSystem;

    /// <param name="factionsInSystem">The war faction of each pilot in the system now (0 for a pilot with none).</param>
    public FwComplexDirector(FwStore store, DurableScheduler scheduler, EventBus bus, Func<long, CancellationToken, Task<IReadOnlyList<long>>> factionsInSystem)
    {
        (_store, _scheduler, _bus, _factionsInSystem) = (store, scheduler, bus, factionsInSystem);
    }

    /// <summary>Registers the tick job and schedules it. Call it before the scheduler starts.</summary>
    public async Task AttachAsync()
    {
        _scheduler.Register(TickJob, (context, token) => TickAsync(context, token));
        await _scheduler.EnsureRecurringAsync(TickJob, "tick", FwComplexes.Tick);
    }

    private async Task TickAsync(JobContext context, CancellationToken cancellationToken)
    {
        var resolved = await FwComplexTicker.TickAsync(_store, context.Connection, context.Transaction, context.NowMs, _factionsInSystem, cancellationToken);
        foreach (var resolution in resolved)
            await _bus.PublishInTransactionAsync(context.Connection, context.Transaction, ResolvedTopic, resolution, resolution.ComplexId.ToString(), cancellationToken);
    }
}
