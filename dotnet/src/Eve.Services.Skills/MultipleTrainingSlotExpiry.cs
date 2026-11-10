using Eve.Scheduler;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills;

/// <summary>Pauses over-capacity training queues when an account's extra slots expire; one durable job runs per slot.</summary>
public sealed class MultipleTrainingSlotExpiry(
    DurableScheduler scheduler,
    CharacterTraining training,
    TrainingAnnouncer announcer,
    CharacterSessions sessions,
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> accountCharacters,
    Func<CancellationToken, Task<IReadOnlyList<(long AccountId, DateTimeOffset EndsAt)>>>? savedSlotExpiries = null)
{
    public const string JobKind = "skills.mctSlotExpiry";

    public sealed record AccountJob(long AccountId, long ExpiryFileTime = 0);

    /// <summary>Registers the job kind with the scheduler; the Host calls it once at composition.</summary>
    public void Register() => scheduler.Register(JobKind, (job, token) =>
    {
        var payload = job.Payload<AccountJob>()!;
        return payload.ExpiryFileTime > 0
            ? PauseOverLimitAtAsync(payload.AccountId, payload.ExpiryFileTime, token)
            : PauseOverLimitAsync(payload.AccountId, token);
    });

    /// <summary>Schedules the expiry of a slot granted in the caller's transaction, so the grant and its job commit together.</summary>
    public Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId, DateTimeOffset endsAt, CancellationToken cancellationToken)
    {
        var endsMs = endsAt.ToUnixTimeMilliseconds();
        return scheduler.ScheduleAsync(
            connection, transaction,
            new JobSpec(JobKind, endsMs) { Key = $"{accountId}:{endsMs}", Payload = new AccountJob(accountId, endsAt.ToFileTime()) },
            cancellationToken);
    }

    /// <summary>Rebuilds missing expiry jobs for persisted slots and applies expiries that elapsed while the host was stopped.</summary>
    public async Task ReconcileSavedSlotsAsync(CancellationToken cancellationToken)
    {
        if (savedSlotExpiries is null)
            return;

        var slots = await savedSlotExpiries(cancellationToken);
        var expiredSlots = new List<(long AccountId, DateTimeOffset EndsAt)>();
        foreach (var (accountId, endsAt) in slots)
        {
            var endsMs = endsAt.ToUnixTimeMilliseconds();
            if (endsMs <= scheduler.Now)
            {
                expiredSlots.Add((accountId, endsAt));
                continue;
            }

            await scheduler.ScheduleAsync(
                new JobSpec(JobKind, endsMs)
                {
                    Key = $"{accountId}:{endsMs}",
                    Payload = new AccountJob(accountId, endsAt.ToFileTime()),
                },
                cancellationToken);
        }

        foreach (var (accountId, endsAt) in expiredSlots
            .Distinct()
            .OrderBy(slot => slot.EndsAt)
            .ThenBy(slot => slot.AccountId))
            await PauseOverLimitAtAsync(accountId, endsAt.ToFileTime(), cancellationToken);
    }

    /// <summary>Pauses the running queues that the account's remaining slots no longer cover, then tells the account's clients.</summary>
    public async Task PauseOverLimitAsync(long accountId, CancellationToken cancellationToken)
        => await PauseOverLimitAsync(accountId, null, cancellationToken);

    private async Task PauseOverLimitAtAsync(long accountId, long expiryFileTime, CancellationToken cancellationToken)
        => await PauseOverLimitAsync(accountId, expiryFileTime, cancellationToken);

    private async Task PauseOverLimitAsync(long accountId, long? expiryFileTime, CancellationToken cancellationToken)
    {
        var characters = await accountCharacters(accountId, cancellationToken);
        var savedSlots = savedSlotExpiries is null ? [] : await savedSlotExpiries(cancellationToken);
        var historicalBoundary = savedSlotExpiries is null ? null : expiryFileTime;
        if (historicalBoundary is { } boundary && !savedSlots.Any(slot =>
                slot.AccountId == accountId && slot.EndsAt.ToFileTime() == boundary))
            return; // a previous slot expiry was extended and its scheduled job is now stale

        var allowed = historicalBoundary is { } boundaryAt
            ? 1 + savedSlots.Count(slot => slot.AccountId == accountId && slot.EndsAt.ToFileTime() > boundaryAt)
            : 1 + training.Settings.AdditionalTrainingSlots(accountId);
        var running = new List<(long CharacterId, long Points)>();
        foreach (var characterId in characters)
        {
            var view = historicalBoundary is { } viewAt
                ? await training.ViewAtAsync(characterId, accountId, viewAt, cancellationToken)
                : await training.ViewAsync(characterId, accountId, cancellationToken);
            if (view.State.Running)
                running.Add((characterId, view.TotalPoints));
        }

        var told = false;
        foreach (var (characterId, _) in running.OrderBy(r => r.Points).ThenBy(r => r.CharacterId).Take(Math.Max(0, running.Count - allowed)))
        {
            TrainingUpdate? stopped;
            if (historicalBoundary is { } abortAt)
                stopped = await training.AbortAtAsync(characterId, accountId, abortAt, cancellationToken);
            else
                stopped = await training.AbortAsync(characterId, accountId, cancellationToken);
            if (stopped is { } update && sessions.Of(characterId) is { } session)
            {
                // The paused character hears the stop and every client of the account refreshes its available queue count.
                await announcer.AnnounceStoppedAsync(session, update);
                told = true;
            }
        }

        if (!told)
            announcer.AnnounceExtraSlotsChanged(accountId);
    }
}
