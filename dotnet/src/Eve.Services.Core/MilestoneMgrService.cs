using Eve.Characters;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The login milestone (EveJS <c>milestoneMgrService</c>): the online time of an account counts toward a two-hour milestone. When it is reached
/// the character is told, and <c>ClaimRewards</c> pays the reward once. The timer lives in this process; the progress is kept in PostgreSQL and
/// written when the character leaves (a restart without a leave loses the time since the last write, as the reference does).
/// </summary>
[Service("milestoneMgr")]
public sealed partial class MilestoneMgrService(LoginMilestoneStore store, LocalTimers timers, Func<long> nowMs) : ICharacterEntryHook
{
    public const int MilestoneId = 1;
    public const int RewardIsk = 500_000;

    private readonly Dictionary<long, Running> _running = [];
    private sealed record Running(ClientSession Session, long StartedMs);

    private static string TimerKey(long accountId) => $"milestone:{accountId}";

    [Method("ProcessCharacterLogon")]
    public async ValueTask<PyValue> ProcessCharacterLogon(ServiceCall call)
    {
        if (call.Session.UserId is { } account and > 0)
            await StartTimerAsync(account, call.Session, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("ClaimRewards")]
    public async ValueTask<PyValue> ClaimRewards(ServiceCall call)
    {
        if (call.Arg(0) is not PyInt { Value: MilestoneId } || call.Session.UserId is not { } account || account <= 0
            || call.Session.CharacterId is not { } character)
            return PyNone.Instance;
        await AchieveAsync(account, call.Session, call.Cancellation);
        await store.ClaimAsync(account, character, MilestoneId, RewardIsk, nowMs(), call.Cancellation);
        return PyNone.Instance;
    }

    public ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>The character leaves: its time is written, and a milestone it reached meanwhile is kept as reached.</summary>
    public async ValueTask OnCharacterLeavingAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        if (session.UserId is not { } account || !IsRunningFor(account, session))
            return;
        var progress = await CurrentAsync(account, cancellationToken);
        ClearRunning(account);
        await store.WriteAsync(account, progress, nowMs(), cancellationToken);
    }

    private async Task StartTimerAsync(long accountId, ClientSession session, CancellationToken cancellationToken)
    {
        // the time of a timer that was running is counted first (the same account logged on again, or on another character)
        var progress = await CurrentAsync(accountId, cancellationToken);
        ClearRunning(accountId);
        if (progress.Claimed)
            return;
        if (progress.Achieved)
        {
            NotifyAchieved(session);
            return;
        }
        if (progress.OnlineMs >= LoginMilestoneStore.DurationMs)
        {
            await AchieveAsync(accountId, session, cancellationToken);
            return;
        }
        await store.WriteAsync(accountId, progress, nowMs(), cancellationToken);
        var remaining = LoginMilestoneStore.DurationMs - progress.OnlineMs;
        var started = nowMs();
        lock (_running) _running[accountId] = new Running(session, started);
        timers.Set(TimerKey(accountId), TimeSpan.FromMilliseconds(remaining), () => AchieveAsync(accountId, session, CancellationToken.None));
        session.SendNotification("OnMilestoneTimerStarted", "clientID", new PyLong(Py.FileTimeFromUnixMs(started + remaining)));
    }

    /// <summary>Marks the milestone reached when its time is complete (a timer that fires early changes nothing).</summary>
    private async Task AchieveAsync(long accountId, ClientSession? session, CancellationToken cancellationToken)
    {
        var progress = await CurrentAsync(accountId, cancellationToken);
        if (progress.Claimed || progress.Achieved)
        {
            ClearRunning(accountId);
            return;
        }
        if (progress.OnlineMs < LoginMilestoneStore.DurationMs)
            return;
        var running = ClearRunning(accountId);
        await store.WriteAsync(accountId, progress with { OnlineMs = LoginMilestoneStore.DurationMs, Achieved = true }, nowMs(), cancellationToken);
        if ((session ?? running?.Session) is { } told)
            NotifyAchieved(told);
    }

    /// <summary>The stored progress, plus the time the account's running timer has counted so far.</summary>
    private async Task<LoginMilestoneStore.Progress> CurrentAsync(long accountId, CancellationToken cancellationToken)
    {
        var stored = await store.ReadAsync(accountId, cancellationToken);
        Running? running;
        lock (_running) _running.TryGetValue(accountId, out running);
        if (running is null || stored.Claimed || stored.Achieved)
            return stored;
        return stored with { OnlineMs = Math.Min(LoginMilestoneStore.DurationMs, stored.OnlineMs + Math.Max(0, nowMs() - running.StartedMs)) };
    }

    private Running? ClearRunning(long accountId)
    {
        timers.Cancel(TimerKey(accountId));
        lock (_running)
        {
            if (_running.Remove(accountId, out var running)) return running;
            return null;
        }
    }

    private bool IsRunningFor(long accountId, ClientSession session)
    {
        lock (_running) return _running.TryGetValue(accountId, out var running) && ReferenceEquals(running.Session, session);
    }

    private static void NotifyAchieved(ClientSession session) =>
        session.SendNotification("OnLoginMilestoneAchieved", "clientID", new PyInt(MilestoneId), new PyInt(RewardIsk));
}
