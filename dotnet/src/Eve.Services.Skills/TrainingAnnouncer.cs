using Eve.Scheduler;
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
public sealed class TrainingAnnouncer(CharacterTraining training, SkillCatalog catalog, CharacterSessions sessions, ICharacterDogmaSync? dogma, Func<long> nowUnixMs, bool schedule = true, LocalTimers? localTimers = null)
{
    /// <summary>How long after a line is due the queue is settled (the clock and the timer may not agree to the millisecond).</summary>
    private const int GraceMs = 100;

    private readonly LocalTimers _timers = localTimers ?? new();

    private static string TimerKey(long characterId) => $"skills.training:{characterId}";

    private long NowFileTime => Py.FileTimeFromUnixMs(nowUnixMs());

    /// <summary>Starts listening to the clock: a level that finishes is told to the client that plays the character.</summary>
    public void Attach() => training.Settled += OnSettledAsync;

    /// <summary>A queue that finished levels: the skills, what the ship gained, the queue that is left.</summary>
    public async Task OnSettledAsync(TrainingUpdate update)
    {
        Reschedule(update.CharacterId, update.AccountId, update.View);
        if (sessions.Of(update.CharacterId) is not { } session)
            return;

        if (update.FreeSkillPoints is { } remaining)
        {
            // This is an SP mutation, not a queue level finishing. The client
            // scatters its own skill events from OnServerSkillsChanged.
            var changed = update.ChangedSkills ?? [];
            if (changed.Count == 0) return;
            SkillNotifications.ServerSkillsChanged(session,
                SkillWire.EntryDictionary(update.CharacterId, catalog, changed, metadata: false), null, NowFileTime);
            SkillNotifications.QueueSaved(session, update.View.Queue);
            session.SendNotification("OnFreeSkillPointsChanged", "charid", new PyInt(remaining));
            await SyncDogmaAsync(session, update);
            if (update.WasRunning && !update.View.State.Running)
                SkillNotifications.QueuePaused(session);
            return;
        }

        if (update.Finished.Count == 0) return;
        var skills = SkillWire.EntryDictionary(update.CharacterId, catalog, update.Finished);
        SkillNotifications.ServerSkillsChanged(session, skills, null, NowFileTime);
        SkillNotifications.LevelsTrained(session, update.Finished);
        SkillNotifications.SkillsChanged(session, skills);
        await SyncDogmaAsync(session, update);
        SkillNotifications.QueueSaved(session, update.View.Queue);
        if (!update.View.State.Running)
            SkillNotifications.QueuePaused(session);
    }

    /// <summary>New skills, after their transaction committed; they did not train any levels.</summary>
    public async Task AnnouncePurchasedAsync(ClientSession session, long characterId,
        IReadOnlyList<CharacterSkill> before, IReadOnlyList<CharacterSkill> granted)
    {
        if (granted.Count == 0) return;
        var changed = SkillWire.EntryDictionary(characterId, catalog, granted);
        SkillNotifications.ServerSkillsChanged(session, changed, null, NowFileTime);
        SkillNotifications.SkillsChanged(session, changed);
        if (dogma is not null)
            await dogma.SkillsChangedAsync(session, characterId, before);
    }

    /// <summary>The consumed injector's committed item change precedes the new free-SP balance.</summary>
    public void AnnounceInjectedPoints(ClientSession session, ItemRow before, ItemRow after, long remaining)
    {
        var differences = new List<KeyValuePair<PyValue, PyValue>>();
        if (after.LocationId != before.LocationId)
            differences.Add(new(new PyInt(3), InventoryWire.Id(before.LocationId)));
        if (after.Quantity != before.Quantity)
            differences.Add(new(new PyInt(9), InventoryWire.Id(before.Quantity)));
        if (differences.Count == 0) return;
        session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(after),
            new PyDict(differences), PyNone.Instance);
        session.SendNotification("OnFreeSkillPointsChanged", "charid", new PyInt(remaining));
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

    public async Task AnnounceRemappedAsync(ClientSession session, TrainingUpdate update)
    {
        Reschedule(update.CharacterId, update.AccountId, update.View);
        if (update.Finished.Count > 0)
        {
            var finished = SkillWire.EntryDictionary(update.CharacterId, catalog, update.Finished);
            SkillNotifications.ServerSkillsChanged(session, finished, null, NowFileTime);
            SkillNotifications.LevelsTrained(session, update.Finished);
            SkillNotifications.SkillsChanged(session, finished);
        }
        if (update.Rebased is { } rebased && update.RebaseChanged)
        {
            var changed = SkillWire.EntryDictionary(update.CharacterId, catalog, [rebased], metadata: false);
            SkillNotifications.ServerSkillsChanged(session, changed, null, NowFileTime);
            SkillNotifications.SkillsChanged(session, changed);
        }
        if (update.Finished.Count > 0 || update.RebaseChanged)
            await SyncDogmaAsync(session, update);
        SkillNotifications.QueueSaved(session, update.View.Queue);
        if (!update.View.State.Running)
            SkillNotifications.QueuePaused(session);
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

    /// <summary>The account's extra queues changed without a request of one of its characters (a slot ran out): every client of the account is told.</summary>
    public void AnnounceExtraSlotsChanged(long accountId)
    {
        foreach (var session in sessions.OfUser(accountId))
        {
            SkillNotifications.MultipleCharactersTrainingUpdated(session);
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

    /// <summary>Arms (or clears) the timer that settles the queue when its running line is due (a <see cref="LocalTimers"/> timer: what runs in this process for a logged-in character only).</summary>
    public void Reschedule(long characterId, long accountId, TrainingView view)
    {
        if (!schedule)
        {
            return;
        }
        if (view.Current?.End is not { } end)
        {
            _timers.Cancel(TimerKey(characterId));
            return;
        }
        var dueMs = Math.Clamp(((end - Py.FileTimeFromUnixMs(nowUnixMs())) / 10_000) + GraceMs, GraceMs, int.MaxValue - 1);
        _timers.Set(TimerKey(characterId), TimeSpan.FromMilliseconds(dueMs), () => FireAsync(characterId, accountId));
    }

    private async Task FireAsync(long characterId, long accountId)
    {
        try
        {
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
