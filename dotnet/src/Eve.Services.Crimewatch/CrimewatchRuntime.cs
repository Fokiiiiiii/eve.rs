using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

/// <summary>An attack on the ship of another pilot, and what it made of the attacker (see <see cref="Aggression"/>).</summary>
public sealed record AggressionResult(AggressionRequest Request, AggressionOutcome Outcome);

/// <summary>
/// What crimewatch does as pilots attack one another: it keeps the timers and flags (<see cref="PgCrimewatchStore"/>, by the rules of <see cref="Aggression"/>) and tells the clients what
/// changed, as the reference does: the pilot its own timers (<c>OnWeaponsTimerUpdate</c>, <c>OnPvpTimerUpdate</c>, <c>OnNpcTimerUpdate</c>, <c>OnCriminalTimerUpdate</c>,
/// <c>OnDisapprovalTimerUpdate</c>, each <c>(state, expiry)</c>, only for the ones that changed) and everyone in space in the system who is flagged now and who is not any more
/// (<c>OnSystemCriminalFlagUpdates</c>: the ones that went idle, the new suspects, the new criminals). A timer that runs out is told when it does: the state is in PostgreSQL and the wait
/// for its end is <see cref="LocalTimers"/> (a process-local wait; a process that starts takes it up again with <see cref="ResumeAsync"/>). One step at a time: crimewatch is told of
/// an attack and of an end by the same door, and what it told last is what it compares with.
/// </summary>
public sealed class CrimewatchRuntime(PgCrimewatchStore store, CharacterSessions sessions, Func<long> nowUnixMs, Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly LocalTimers _timers = new(log, delay);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, CombatTimers> _told = new();
    private readonly Dictionary<long, (IReadOnlyList<long> Criminals, IReadOnlyList<long> Suspects)> _flagsTold = [];

    /// <summary>Whether the law lets the first pilot attack the second at this time (a war, a limited engagement, a corporation that may shoot its own). Nothing is legal until something says so.</summary>
    public Func<long, long, long, bool>? Legal { get; set; }

    /// <summary>Raised after an attack was recorded and before the clients are told the timers: what a crime brings (the security status, a kill right, CONCORD) hangs here.</summary>
    public event Func<AggressionResult, Task>? Aggressed;

    private static string Key(long characterId) => $"crimewatch.end:{characterId}";

    /// <summary>
    /// A pilot attacked the ship of another pilot: its timers run and it may be flagged; what a crime brings (<see cref="Aggressed"/>) is done, and then the clients are told the timers
    /// (as in the reference, whose security status and kill right come before the timers).
    /// </summary>
    public async Task<AggressionResult> RecordAggressionAsync(AggressionRequest request, CancellationToken cancellationToken = default)
    {
        var now = nowUnixMs();
        var targetState = await store.StateOfAsync(request.Target, cancellationToken);
        var legal = Legal?.Invoke(request.Attacker, request.Target, now) ?? false;
        AggressionOutcome? outcome = null;
        await store.UpdateAsync(request.Attacker, state => (outcome = Aggression.Record(state, targetState, request.Where, request.SolarSystemId, request.TargetIsCapsule, legal, now)).Attacker, now, cancellationToken);
        var result = new AggressionResult(request, outcome!);
        try
        {
            if (Aggressed is { } handlers)
            {
                foreach (var handler in handlers.GetInvocationList().Cast<Func<AggressionResult, Task>>())
                {
                    await handler(result);
                }
            }
        }
        finally
        {
            await SyncAsync(request.Attacker, cancellationToken);
        }
        return result;
    }

    /// <summary>The pilot's state at this time (what <c>GetClientStates</c> answers from).</summary>
    public Task<CombatState> StateOfAsync(long characterId, CancellationToken cancellationToken = default) => store.StateOfAsync(characterId, cancellationToken);

    /// <summary>Tells the pilot's client the timers that are not what it was told, tells the system the flags that changed, and waits for the next end.</summary>
    public async Task SyncAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = nowUnixMs();
            var state = await store.StateOfAsync(characterId, cancellationToken);
            var timers = state.TimersAt(now);
            var told = _told.GetValueOrDefault(characterId, CombatTimers.Idle);
            if (sessions.Of(characterId) is { } session)
            {
                Tell(session, "OnWeaponsTimerUpdate", told.Weapons, timers.Weapons);
                Tell(session, "OnPvpTimerUpdate", told.Pvp, timers.Pvp);
                Tell(session, "OnNpcTimerUpdate", told.Npc, timers.Npc);
                Tell(session, "OnCriminalTimerUpdate", told.Criminal, timers.Criminal);
                Tell(session, "OnDisapprovalTimerUpdate", told.Disapproval, timers.Disapproval);
            }
            if (timers == CombatTimers.Idle)
            {
                _told.TryRemove(characterId, out _);
            }
            else
            {
                _told[characterId] = timers;
            }
            await TellFlagsAsync(state.SolarSystemId, now, cancellationToken);
            if (state.NextEndAfter(now) is { } next)
            {
                // a moment after the end, so that what the clock says then is past it
                _timers.Set(Key(characterId), TimeSpan.FromMilliseconds(next - now + 5), () => SyncAsync(characterId));
            }
            else
            {
                _timers.Cancel(Key(characterId));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A process that starts: takes up the wait for the end of every timer and flag that still runs.</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (characterId, _) in await store.ActiveAsync(nowUnixMs(), cancellationToken))
        {
            await SyncAsync(characterId, cancellationToken);
        }
    }

    private static void Tell(ClientSession session, string name, TimerView was, TimerView now)
    {
        if (was != now)
        {
            session.SendNotification(name, "charid", new PyInt(now.State), Expiry(now));
        }
    }

    /// <summary>When a timer ends as the client is told it (a file time), or None while it is idle.</summary>
    internal static PyValue Expiry(TimerView timer) => timer.ExpiresAtMs is { } until ? new PyLong(Py.FileTimeFromUnixMs(until)) : PyNone.Instance;

    private async Task TellFlagsAsync(long solarSystemId, long now, CancellationToken cancellationToken)
    {
        if (solarSystemId <= 0)
        {
            return;
        }
        var (criminals, suspects) = await store.FlaggedInAsync(solarSystemId, now, cancellationToken);
        var (wasCriminals, wasSuspects) = _flagsTold.GetValueOrDefault(solarSystemId, ([], []));
        var idle = wasCriminals.Except(criminals).Concat(wasSuspects.Except(suspects)).Distinct().Order().ToList();
        var newSuspects = suspects.Except(wasSuspects).ToList();
        var newCriminals = criminals.Except(wasCriminals).ToList();
        if (criminals.Count == 0 && suspects.Count == 0)
        {
            _flagsTold.Remove(solarSystemId);
        }
        else
        {
            _flagsTold[solarSystemId] = (criminals, suspects);
        }
        if (idle.Count == 0 && newSuspects.Count == 0 && newCriminals.Count == 0)
        {
            return;
        }
        foreach (var session in sessions.InSpace(solarSystemId))
        {
            session.SendNotification("OnSystemCriminalFlagUpdates", "solarsystemid2", Ids(idle), Ids(newSuspects), Ids(newCriminals));
        }
    }

    private static PyValue Ids(IEnumerable<long> ids) => Py.Tuple([.. ids.Select(InventoryWire.Id)]);

    public void Dispose() => _timers.Dispose();
}
