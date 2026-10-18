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
    /// <summary>A battlefield's balls and score after a tick (the host places and updates the balls; a closed battlefield's are taken out).</summary>
    public const string BattlefieldStateTopic = "factionwarfare.battlefield_state";
    /// <summary>A battlefield won in a tick (the host notifies its system).</summary>
    public const string BattlefieldWonTopic = "factionwarfare.battlefield_won";

    private readonly FwStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _bus;
    private readonly IFwComplexSites _sites;
    private readonly IFwRewards _rewards;

    public FwComplexDirector(FwStore store, DurableScheduler scheduler, EventBus bus, IFwComplexSites sites, IFwRewards rewards)
    {
        (_store, _scheduler, _bus, _sites, _rewards) = (store, scheduler, bus, sites, rewards);
    }

    /// <summary>Registers the tick job and schedules it. Call it before the scheduler starts.</summary>
    public async Task AttachAsync()
    {
        _scheduler.Register(TickJob, (context, token) => TickAsync(context, token));
        await _scheduler.EnsureRecurringAsync(TickJob, "tick", FwComplexes.Tick);
    }

    private async Task TickAsync(JobContext context, CancellationToken cancellationToken)
    {
        var resolved = await FwComplexTicker.TickAsync(_store, _sites, context.Connection, context.Transaction, context.NowMs, cancellationToken);
        foreach (var resolution in resolved)
            await _bus.PublishInTransactionAsync(context.Connection, context.Transaction, ResolvedTopic, resolution, resolution.ComplexId.ToString(), cancellationToken);
        var (states, wins) = await FwBattlefieldTicker.TickAsync(_store, _sites, _rewards, context.Connection, context.Transaction, context.NowMs, cancellationToken);
        foreach (var state in states)
            await _bus.PublishInTransactionAsync(context.Connection, context.Transaction, BattlefieldStateTopic, state, $"{state.BattlefieldId}:{context.NowMs}", cancellationToken);
        foreach (var win in wins)
            await _bus.PublishInTransactionAsync(context.Connection, context.Transaction, BattlefieldWonTopic, win, win.BattlefieldId.ToString(), cancellationToken);
    }
}
