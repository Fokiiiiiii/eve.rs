using System.Collections.Concurrent;
using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>Tells a client how its character and its ship came out after its skills changed (the dogma side of a skill changing).</summary>
public interface ICharacterDogmaSync
{
    /// <summary>The character's skills went from <paramref name="before"/> to what they are now: the ship's attributes that changed, the character's attributes and its brain.</summary>
    Task SkillsChangedAsync(ClientSession session, long characterId, IReadOnlyList<CharacterSkill> before, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns what happens to a character's training into what the client is told, in the order the reference tells it, and wakes the character's
/// queue when its running line is due so the client hears about a level as it finishes.
/// </summary>
public sealed class TrainingAnnouncer(CharacterTraining training, SkillCatalog catalog, CharacterSessions sessions, ICharacterDogmaSync? dogma, Func<long> nowUnixMs, bool schedule = true)
{
    /// <summary>How long after a line is due the queue is settled (the clock and the timer may not agree to the millisecond).</summary>
    private const int GraceMs = 100;

    private readonly ConcurrentDictionary<long, Timer> _timers = new();

    private long NowFileTime => Py.FileTimeFromUnixMs(nowUnixMs());

    /// <summary>Starts listening to the clock: a level that finishes is told to the client that plays the character.</summary>
    public void Attach() => training.Settled += OnSettledAsync;

    /// <summary>A queue that finished levels: the skills, what the ship gained, the queue that is left.</summary>
    public async Task OnSettledAsync(TrainingUpdate update)
    {
        Reschedule(update.CharacterId, update.AccountId, update.View);
        if (sessions.Of(update.CharacterId) is not { } session)
        {
            return;
        }
        var skills = SkillWire.EntryDictionary(update.CharacterId, catalog, update.Finished);
        SkillNotifications.ServerSkillsChanged(session, skills, null, NowFileTime);
        SkillNotifications.LevelsTrained(session, update.Finished);
        SkillNotifications.SkillsChanged(session, skills);
        await SyncDogmaAsync(session, update);
        SkillNotifications.QueueSaved(session, update.View.Queue);
        if (!update.View.State.Running)
        {
            SkillNotifications.QueuePaused(session);
        }
    }

    /// <summary>A queue was saved: the skill that gave way (when it changed), the queue, whether it runs, and every client of the account.</summary>
    public async Task AnnounceSavedAsync(ClientSession session, QueueSaved saved)
    {
        var update = saved.Update;
        Reschedule(update.CharacterId, update.AccountId, update.View);
        if (update.Rebased is { } rebased && update.RebaseChanged)
        {
            SkillNotifications.ServerSkillsChanged(session, SkillWire.EntryDictionary(update.CharacterId, catalog, [rebased], metadata: false), null, NowFileTime);
            await SyncDogmaAsync(session, update);
        }
        SkillNotifications.QueueSaved(session, update.View.Queue);
        if (saved.Paused)
        {
            SkillNotifications.QueuePaused(session);
        }
        TellTheAccount(session, update.AccountId);
    }

    /// <summary>Training was stopped: the skill keeps the points it gained, and the client hears that the queue is paused.</summary>
    public async Task AnnounceStoppedAsync(ClientSession session, TrainingUpdate update)
    {
        Reschedule(update.CharacterId, update.AccountId, update.View);
        if (update.Rebased is { } rebased)
        {
            SkillNotifications.ServerSkillsChanged(session, SkillWire.EntryDictionary(update.CharacterId, catalog, [rebased], metadata: false), "OnSkillQueuePausedServer", NowFileTime);
            await SyncDogmaAsync(session, update);
        }
        else
        {
            SkillNotifications.QueuePaused(session);
        }
        TellTheAccount(session, update.AccountId);
    }

    private async Task SyncDogmaAsync(ClientSession session, TrainingUpdate update)
    {
        if (dogma is not null)
        {
            await dogma.SkillsChangedAsync(session, update.CharacterId, update.SkillsBefore);
        }
    }

    private void TellTheAccount(ClientSession caller, long accountId)
    {
        SkillNotifications.MultipleCharactersTrainingUpdated(caller);
        foreach (var other in sessions.OfUser(accountId).Where(s => !ReferenceEquals(s, caller)))
        {
            SkillNotifications.MultipleCharactersTrainingUpdated(other);
        }
    }

    /// <summary>Arms (or clears) the timer that settles the queue when its running line is due.</summary>
    public void Reschedule(long characterId, long accountId, TrainingView view)
    {
        if (!schedule)
        {
            return;
        }
        if (_timers.TryRemove(characterId, out var old))
        {
            old.Dispose();
        }
        if (view.Current?.End is not { } end)
        {
            return;
        }
        var dueMs = Math.Clamp(((end - Py.FileTimeFromUnixMs(nowUnixMs())) / 10_000) + GraceMs, GraceMs, int.MaxValue - 1);
        _timers[characterId] = new Timer(_ => _ = FireAsync(characterId, accountId), null, dueMs, Timeout.Infinite);
    }

    private async Task FireAsync(long characterId, long accountId)
    {
        try
        {
            _timers.TryRemove(characterId, out _);
            if (await training.SettleAsync(characterId, accountId) is null)
            {
                Reschedule(characterId, accountId, await training.ViewAsync(characterId, accountId)); // not due yet after all
            }
        }
        catch (Exception)
        {
            // the queue is settled by the next call that asks; a timer must not bring the host down
        }
    }
}
