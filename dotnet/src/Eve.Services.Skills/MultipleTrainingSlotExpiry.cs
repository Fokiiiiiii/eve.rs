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

    public sealed record AccountJob(long AccountId);

    /// <summary>Registers the job kind with the scheduler; the Host calls it once at composition.</summary>
    public void Register() => scheduler.Register(JobKind, (job, token) => PauseOverLimitAsync(job.Payload<AccountJob>()!.AccountId, token));

    /// <summary>Schedules the expiry of a slot granted in the caller's transaction, so the grant and its job commit together.</summary>
    public Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId, DateTimeOffset endsAt, CancellationToken cancellationToken)
    {
        var endsMs = endsAt.ToUnixTimeMilliseconds();
        return scheduler.ScheduleAsync(
            connection, transaction,
            new JobSpec(JobKind, endsMs) { Key = $"{accountId}:{endsMs}", Payload = new AccountJob(accountId) },
            cancellationToken);
    }

    /// <summary>Rebuilds missing expiry jobs for persisted slots and applies expiries that elapsed while the host was stopped.</summary>
    public async Task ReconcileSavedSlotsAsync(CancellationToken cancellationToken)
    {
        if (savedSlotExpiries is null)
            return;

        var slots = await savedSlotExpiries(cancellationToken);
        var expiredAccounts = new HashSet<long>();
        foreach (var (accountId, endsAt) in slots)
        {
            var endsMs = endsAt.ToUnixTimeMilliseconds();
            if (endsMs <= scheduler.Now)
            {
                expiredAccounts.Add(accountId);
                continue;
            }

            await scheduler.ScheduleAsync(
                new JobSpec(JobKind, endsMs) { Key = $"{accountId}:{endsMs}", Payload = new AccountJob(accountId) },
                cancellationToken);
        }

        foreach (var accountId in expiredAccounts.Order())
            await PauseOverLimitAsync(accountId, cancellationToken);
    }

    /// <summary>Pauses the running queues that the account's remaining slots no longer cover, then tells the account's clients.</summary>
    public async Task PauseOverLimitAsync(long accountId, CancellationToken cancellationToken)
    {
        var characters = await accountCharacters(accountId, cancellationToken);
        var running = new List<(long CharacterId, long Points)>();
        foreach (var characterId in characters)
        {
            var view = await training.ViewAsync(characterId, accountId, cancellationToken);
            if (view.State.Running)
                running.Add((characterId, view.TotalPoints));
        }

        var allowed = 1 + training.Settings.AdditionalTrainingSlots(accountId);
        var told = false;
        foreach (var (characterId, _) in running.OrderBy(r => r.Points).ThenBy(r => r.CharacterId).Take(Math.Max(0, running.Count - allowed)))
        {
            var view = await training.ViewAsync(characterId, accountId, cancellationToken);
            if (await training.SaveQueueAsync(characterId, accountId, view.State.Entries, activate: false, characters, cancellationToken) is QueueSaved saved
                && sessions.Of(characterId) is { } session)
            {
                // the paused character's session hears it, and every client of the account is told (AnnounceSavedAsync)
                await announcer.AnnounceSavedAsync(session, saved);
                told = true;
            }
        }

        if (!told)
            announcer.AnnounceExtraSlotsChanged(accountId);
    }
}
