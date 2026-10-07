using Npgsql;

namespace Eve.Skills;

/// <summary>What sets how fast characters train: the server's speed, what an account's subscription makes of it, and the character's attributes.</summary>
public sealed class TrainingSettings
{
    /// <summary>The value of each of the five learning attributes a character has when nothing has changed them.</summary>
    public const double BaseAttribute = 20;

    /// <summary>The server's multiplier on every character's points a minute (1 is the game's own speed).</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Whether an account trains at full speed (a lapsed subscription trains at half).</summary>
    public Func<long, bool> HasOmega { get; init; } = _ => true;

    /// <summary>The value of a learning attribute of a character (charisma, intelligence, memory, perception, willpower).</summary>
    public Func<long, int, double> Attribute { get; init; } = (_, _) => BaseAttribute;
}

/// <summary>The queue and skills of a character as they are at a moment: the running skill has the points it has gained by then.</summary>
public sealed record TrainingView(QueueState State, IReadOnlyList<ProjectedEntry> Queue, IReadOnlyList<CharacterSkill> Skills, long TotalPoints)
{
    /// <summary>The line that trains now, or null when nothing runs.</summary>
    public ProjectedEntry? Current => State.Running ? Queue.FirstOrDefault() : null;

    /// <summary>The type of the skill in training, or null.</summary>
    public int? InTraining => Current?.TypeId;
}

/// <summary>
/// What a change to a character's training did: the skills before it (for what the ship's numbers were), the levels that finished, the skill
/// whose points were carried when its training stopped or gave way, and the queue as it stands now.
/// </summary>
public sealed record TrainingUpdate(long CharacterId, long AccountId, IReadOnlyList<CharacterSkill> SkillsBefore, IReadOnlyList<CharacterSkill> Finished, CharacterSkill? Rebased, bool RebaseChanged, TrainingView View);

/// <summary>What became of a queue that was offered.</summary>
public abstract record SaveOutcome;

/// <summary>The queue was refused.</summary>
public sealed record QueueRefused(QueueError Error) : SaveOutcome;

/// <summary>The account is already training another character (each account trains one at a time).</summary>
public sealed record TrainingBusy(int Training) : SaveOutcome;

/// <summary>The queue was kept; <paramref name="Paused"/> when it was kept without running.</summary>
public sealed record QueueSaved(TrainingUpdate Update, bool Paused) : SaveOutcome;

/// <summary>
/// A character's skill training: the queue is run by the clock (a skill's points are worked out from when its line began), and what has finished
/// is settled into the character's skills whenever anything asks. A change takes the character's lock and one transaction across the skills and
/// the queue. <see cref="Settled"/> is raised when the clock finished levels, so the client can be told.
/// </summary>
public sealed class CharacterTraining(
    NpgsqlDataSource source, SkillCatalog catalog, PgSkillStore skills, PgTrainingStore store, TrainingSettings settings, Func<long> nowUnixMs)
{
    private const long UnixEpochInFileTime = 116_444_736_000_000_000;
    private const int SlotsPerAccount = 1;

    public TrainingSettings Settings => settings;

    /// <summary>Raised after levels finish (and are kept) outside a call of the character's own; a handler that fails does not fail the call that settled.</summary>
    public event Func<TrainingUpdate, Task>? Settled;

    /// <summary>The time now as a FILETIME.</summary>
    public long Now => UnixEpochInFileTime + (nowUnixMs() * 10_000);

    private Func<SkillType, double> RateOf(long characterId, long accountId)
    {
        var multiplier = (settings.HasOmega(accountId) ? 1d : 0.5d) * (settings.Speed > 0 ? settings.Speed : 1d);
        return type => Training.PointsPerMinute(type, attribute => settings.Attribute(characterId, attribute), multiplier);
    }

    private async Task<Dictionary<int, CharacterSkill>> SkillsOfAsync(long characterId, CancellationToken cancellationToken) =>
        (await skills.ForCharacterAsync(characterId, cancellationToken)).ToDictionary(s => s.TypeId);

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('skills:' || $1::text, 0))", connection, transaction) { Parameters = { new() { Value = characterId } } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The queue and skills as they are at <paramref name="now"/>.</summary>
    public TrainingView BuildView(long characterId, long accountId, QueueState state, IReadOnlyDictionary<int, CharacterSkill> owned, long now)
    {
        var rate = RateOf(characterId, accountId);
        var projection = Training.Project(state, owned, catalog, rate, now);
        var live = owned.ToDictionary(s => s.Key, s => s.Value);
        if (state.Running && projection.Entries.FirstOrDefault() is { Start: { } start, End: not null } line && catalog.Get(line.TypeId) is { } type)
        {
            var points = Training.PointsAt(line.StartPoints, line.DestinationPoints, start, now, line.PointsPerMinute);
            live[line.TypeId] = new CharacterSkill(line.TypeId, SkillPoints.LevelAt(type.Rank, points), points);
        }
        var list = live.Values.OrderBy(s => s.TypeId).ToList();
        return new TrainingView(state, projection.Entries, list, list.Sum(s => s.SkillPoints));
    }

    /// <summary>What the character's skills and queue are now: what has finished is settled first.</summary>
    public async Task<TrainingView> ViewAsync(long characterId, long accountId, CancellationToken cancellationToken = default)
    {
        await SettleAsync(characterId, accountId, cancellationToken);
        return BuildView(characterId, accountId, await store.LoadAsync(characterId, cancellationToken), await SkillsOfAsync(characterId, cancellationToken), Now);
    }

    /// <summary>Previews free points for the saved queue (null) or the supplied skill targets.</summary>
    public async Task<IReadOnlyDictionary<int, long>> FreeSkillPointsAppliedAsync(
        long characterId,
        long accountId,
        IReadOnlyList<(int SkillTypeId, int TargetLevel)>? selectedSkills,
        CancellationToken cancellationToken = default)
    {
        var view = await ViewAsync(characterId, accountId, cancellationToken);
        var owned = view.Skills.ToDictionary(skill => skill.TypeId);
        if (selectedSkills is null &&
            Training.Validate(view.State.Entries, owned, catalog, RateOf(characterId, accountId), Now) is not null)
        {
            return new Dictionary<int, long>();
        }
        var targets = selectedSkills ?? view.Queue.Select(entry => (entry.TypeId, entry.ToLevel)).ToList();
        var balance = await skills.FreeSkillPointsAsync(characterId, cancellationToken);
        return PlanFreeSkillPoints(owned, targets, balance, view.InTraining).Applied;
    }

    /// <summary>Applies available points to validated selected targets, or to the saved queue when targets is null.</summary>
    public async Task<(IReadOnlyDictionary<int, long> Applied, long Remaining)> ApplyFreeSkillPointsAsync(
        long characterId,
        long accountId,
        IReadOnlyList<(int SkillTypeId, int TargetLevel)>? selectedSkills,
        CancellationToken cancellationToken = default)
    {
        await SettleAsync(characterId, accountId, cancellationToken);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var state = await store.LoadAsync(connection, transaction, characterId, cancellationToken);
        var beforeRows = await skills.ForCharacterAsync(connection, transaction, characterId, cancellationToken);
        var owned = beforeRows.ToDictionary(skill => skill.TypeId);
        var balance = await skills.LockFreeSkillPointsAsync(connection, transaction, characterId, cancellationToken);
        if (selectedSkills is null &&
            Training.Validate(state.Entries, owned, catalog, RateOf(characterId, accountId), Now) is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (new Dictionary<int, long>(), balance);
        }

        var targets = selectedSkills ?? state.Entries.Select(entry => (entry.TypeId, entry.ToLevel)).ToList();
        var inTraining = state.Running ? state.Entries.FirstOrDefault()?.TypeId : null;
        var plan = PlanFreeSkillPoints(owned, targets, balance, inTraining);
        if (plan.Spent == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (plan.Applied, balance);
        }

        var remaining = await skills.ApplyFreeSkillPointsAsync(
            connection, transaction, characterId, plan.Before, plan.After, plan.Spent, cancellationToken);
        if (remaining is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (new Dictionary<int, long>(), balance);
        }

        var updated = new Dictionary<int, CharacterSkill>(owned);
        foreach (var skill in plan.After)
        {
            updated[skill.TypeId] = skill;
        }

        var outstanding = state.Entries
            .Where(entry => !updated.TryGetValue(entry.TypeId, out var skill) || skill.Level < entry.ToLevel)
            .ToList();
        var nextState = outstanding.Count == state.Entries.Count
            ? state
            : new QueueState(outstanding, state.Running && outstanding.Count > 0, state.Running && outstanding.Count > 0 ? state.ActiveStart : null);
        if (nextState != state)
        {
            await store.SaveAsync(connection, transaction, characterId, nextState, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await RaiseSettledAsync(new TrainingUpdate(
            characterId,
            accountId,
            [.. beforeRows.OrderBy(skill => skill.TypeId)],
            [],
            null,
            false,
            BuildView(characterId, accountId, nextState, updated, Now)));
        return (plan.Applied, remaining.Value);
    }

    /// <summary>Applies an exact point amount to one skill, capped at its level-V threshold.</summary>
    public async Task<long?> ApplyFreeSkillPointAmountAsync(
        long characterId,
        long accountId,
        int skillTypeId,
        long requestedPoints,
        CancellationToken cancellationToken = default)
    {
        if (requestedPoints <= 0)
        {
            return null;
        }

        await SettleAsync(characterId, accountId, cancellationToken);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var state = await store.LoadAsync(connection, transaction, characterId, cancellationToken);
        var beforeRows = await skills.ForCharacterAsync(connection, transaction, characterId, cancellationToken);
        var owned = beforeRows.ToDictionary(skill => skill.TypeId);
        var balance = await skills.LockFreeSkillPointsAsync(connection, transaction, characterId, cancellationToken);
        if ((state.Running && state.Entries.FirstOrDefault()?.TypeId == skillTypeId) ||
            !owned.TryGetValue(skillTypeId, out var current) ||
            catalog.Get(skillTypeId) is not { } type ||
            balance < requestedPoints)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var points = Math.Min(requestedPoints, Math.Max(0L, SkillPoints.ForTraining(type.Rank, 5) - current.SkillPoints));
        if (points == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return balance;
        }

        var total = checked(current.SkillPoints + points);
        var updatedSkill = current with { SkillPoints = total, Level = SkillPoints.LevelAt(type.Rank, total) };
        var remaining = await skills.ApplyFreeSkillPointsAsync(
            connection,
            transaction,
            characterId,
            new Dictionary<int, CharacterSkill> { [skillTypeId] = current },
            [updatedSkill],
            points,
            cancellationToken);
        if (remaining is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var updated = new Dictionary<int, CharacterSkill>(owned) { [skillTypeId] = updatedSkill };
        var outstanding = state.Entries
            .Where(entry => !updated.TryGetValue(entry.TypeId, out var skill) || skill.Level < entry.ToLevel)
            .ToList();
        var nextState = outstanding.Count == state.Entries.Count
            ? state
            : new QueueState(outstanding, state.Running && outstanding.Count > 0, state.Running && outstanding.Count > 0 ? state.ActiveStart : null);
        if (nextState != state)
        {
            await store.SaveAsync(connection, transaction, characterId, nextState, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await RaiseSettledAsync(new TrainingUpdate(
            characterId,
            accountId,
            [.. beforeRows.OrderBy(skill => skill.TypeId)],
            [],
            null,
            false,
            BuildView(characterId, accountId, nextState, updated, Now)));
        return remaining.Value;
    }

    private (Dictionary<int, long> Applied, Dictionary<int, CharacterSkill> Before, List<CharacterSkill> After, long Spent)
        PlanFreeSkillPoints(
            IReadOnlyDictionary<int, CharacterSkill> owned,
            IReadOnlyList<(int SkillTypeId, int TargetLevel)> targets,
            long balance,
            int? inTraining)
    {
        var current = new Dictionary<int, CharacterSkill>(owned);
        var before = new Dictionary<int, CharacterSkill>();
        var applied = new Dictionary<int, long>();
        var seen = new HashSet<(int SkillTypeId, int TargetLevel)>();
        var remaining = Math.Max(0L, balance);

        foreach (var (skillTypeId, targetLevel) in targets)
        {
            if (!seen.Add((skillTypeId, targetLevel)) ||
                inTraining == skillTypeId ||
                targetLevel is < 1 or > 5 ||
                catalog.Get(skillTypeId) is not { } type ||
                !current.TryGetValue(skillTypeId, out var skill))
            {
                continue;
            }

            var needed = Math.Max(0L, SkillPoints.ForTraining(type.Rank, targetLevel) - skill.SkillPoints);
            var amount = Math.Min(remaining, needed);
            if (amount <= 0)
            {
                continue;
            }

            before.TryAdd(skillTypeId, owned[skillTypeId]);
            var total = checked(skill.SkillPoints + amount);
            current[skillTypeId] = skill with { SkillPoints = total, Level = SkillPoints.LevelAt(type.Rank, total) };
            applied[skillTypeId] = applied.GetValueOrDefault(skillTypeId) + amount;
            remaining -= amount;
            if (remaining == 0)
            {
                break;
            }
        }

        return (applied, before, [.. before.Keys.Order().Select(typeId => current[typeId])], balance - remaining);
    }

    /// <summary>Previews the free points that fit in the saved queue or the selected skill targets.</summary>
    /// <summary>Applies available points to selected targets, or to the saved queue when targets is null.</summary>
    /// <summary>Applies an exact point amount to one owned skill, as requested by the skills service.</summary>


    /// <summary>The history the character's skills have, newest first.</summary>
    public Task<IReadOnlyList<HistoryEntry>> HistoryAsync(long characterId, CancellationToken cancellationToken = default) => store.HistoryAsync(characterId, cancellationToken);

    /// <summary>
    /// Makes what the clock finished real: the levels are in the character's skills, the queue has moved on, and the history says so. Returns
    /// what changed, or null when nothing had. <see cref="Settled"/> is raised for a change.
    /// </summary>
    public async Task<TrainingUpdate?> SettleAsync(long characterId, long accountId, CancellationToken cancellationToken = default)
    {
        var rate = RateOf(characterId, accountId);
        var probe = await store.LoadAsync(characterId, cancellationToken);
        if (probe.Entries.Count == 0 || !Training.Settle(probe, await SkillsOfAsync(characterId, cancellationToken), catalog, rate, Now).QueueChanged)
        {
            return null;
        }

        TrainingUpdate update;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await LockAsync(connection, transaction, characterId, cancellationToken);
            var now = Now;
            var state = await store.LoadAsync(connection, transaction, characterId, cancellationToken);
            var before = await SkillsOfAsync(characterId, cancellationToken);
            var settled = Training.Settle(state, before, catalog, rate, now);
            if (!settled.QueueChanged)
            {
                return null; // another call settled it while this one waited
            }
            await skills.GrantAsync(connection, transaction, characterId, settled.Finished, cancellationToken, trackRecentChanges: true);
            await store.SaveAsync(connection, transaction, characterId, settled.State, cancellationToken);
            await store.AppendHistoryAsync(connection, transaction, characterId, settled.History, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            update = new TrainingUpdate(characterId, accountId, [.. before.Values.OrderBy(s => s.TypeId)], settled.Finished, null, false, BuildView(characterId, accountId, settled.State, settled.Skills, now));
        }
        await RaiseSettledAsync(update);
        return update;
    }

    private async Task RaiseSettledAsync(TrainingUpdate update)
    {
        if (Settled is not { } handlers)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList().Cast<Func<TrainingUpdate, Task>>())
        {
            try
            {
                await handler(update);
            }
            catch (Exception)
            {
                // telling the client failed; the skills are kept
            }
        }
    }

    private static bool Same(QueueEntry? left, QueueEntry? right) => left is not null && right is not null && left.TypeId == right.TypeId && left.ToLevel == right.ToLevel;

    /// <summary>
    /// Replaces the character's queue. The first line runs when <paramref name="activate"/> is set and the queue is not empty; a line that was
    /// already running keeps its start. A running skill that gives way (or is paused) keeps the points it had gained.
    /// </summary>
    public async Task<SaveOutcome> SaveQueueAsync(
        long characterId, long accountId, IReadOnlyList<QueueEntry> entries, bool activate, IReadOnlyList<long> accountCharacters, CancellationToken cancellationToken = default)
    {
        await SettleAsync(characterId, accountId, cancellationToken);
        var rate = RateOf(characterId, accountId);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        var now = Now;
        var previous = await store.LoadAsync(connection, transaction, characterId, cancellationToken);
        var owned = await SkillsOfAsync(characterId, cancellationToken);
        var before = owned.Values.OrderBy(s => s.TypeId).ToList();

        var requestedActive = activate && entries.Count > 0;
        var yields = previous.Running && (!requestedActive || !Same(previous.Entries[0], entries[0]));
        CharacterSkill? rebased = null;
        var rebaseChanged = false;
        var state = previous;
        if (yields && Training.Project(previous, owned, catalog, rate, now).Entries.FirstOrDefault() is { } line && owned.TryGetValue(line.TypeId, out var running))
        {
            rebased = Training.Rebase(line, running, catalog.Get(line.TypeId)?.Rank ?? 1, now);
            rebaseChanged = rebased != running;
            owned[line.TypeId] = rebased;
            await skills.GrantAsync(connection, transaction, characterId, [rebased], cancellationToken, trackRecentChanges: true);
            state = previous with { ActiveStart = now };
        }

        if (Training.Validate(entries, owned, catalog, rate, now) is { } error)
        {
            return new QueueRefused(error);
        }
        if (requestedActive && !previous.Running && await store.RunningCountAsync(accountCharacters.Where(id => id != characterId), cancellationToken) is var others and >= SlotsPerAccount)
        {
            return new TrainingBusy(others);
        }

        var keepsStart = previous.Running && requestedActive && Same(previous.Entries[0], entries[0]) && state.ActiveStart is not null;
        var next = new QueueState(entries, requestedActive, requestedActive ? (keepsStart ? state.ActiveStart : now) : null);
        await store.SaveAsync(connection, transaction, characterId, next, cancellationToken);
        if (requestedActive && Training.Project(next, owned, catalog, rate, now).Entries.FirstOrDefault() is { } first && !keepsStart)
        {
            await store.AppendHistoryAsync(connection, transaction, characterId, [new HistoryEntry(now, SkillEvents.TrainingStarted, first.TypeId, first.StartPoints, first.ToLevel)], cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        var update = new TrainingUpdate(characterId, accountId, before, [], rebased, rebaseChanged, BuildView(characterId, accountId, next, owned, now));
        return new QueueSaved(update, Paused: !requestedActive);
    }

    /// <summary>Stops the running skill, keeping the points it has gained; the queue stays as it is. Null when nothing was running.</summary>
    public async Task<TrainingUpdate?> AbortAsync(long characterId, long accountId, CancellationToken cancellationToken = default)
    {
        await SettleAsync(characterId, accountId, cancellationToken);
        var rate = RateOf(characterId, accountId);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        var now = Now;
        var state = await store.LoadAsync(connection, transaction, characterId, cancellationToken);
        if (!state.Running)
        {
            return null;
        }
        var owned = await SkillsOfAsync(characterId, cancellationToken);
        var before = owned.Values.OrderBy(s => s.TypeId).ToList();
        CharacterSkill? rebased = null;
        var changed = false;
        if (Training.Project(state, owned, catalog, rate, now).Entries.FirstOrDefault() is { } line && owned.TryGetValue(line.TypeId, out var running))
        {
            rebased = Training.Rebase(line, running, catalog.Get(line.TypeId)?.Rank ?? 1, now);
            changed = rebased != running;
            owned[line.TypeId] = rebased;
            await skills.GrantAsync(connection, transaction, characterId, [rebased], cancellationToken, trackRecentChanges: true);
            await store.AppendHistoryAsync(connection, transaction, characterId, [new HistoryEntry(now, SkillEvents.TrainingCancelled, line.TypeId, rebased.SkillPoints, line.ToLevel)], cancellationToken);
        }
        var stopped = state with { Active = false, ActiveStart = null };
        await store.SaveAsync(connection, transaction, characterId, stopped, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TrainingUpdate(characterId, accountId, before, [], rebased, changed, BuildView(characterId, accountId, stopped, owned, now));
    }

    /// <summary>
    /// Puts a skill at the head of the queue and runs it: the level after the one trained (or <paramref name="level"/> when given). Null when the
    /// type is not a skill.
    /// </summary>
    public async Task<SaveOutcome?> StartAsync(long characterId, long accountId, int typeId, int level, IReadOnlyList<long> accountCharacters, CancellationToken cancellationToken = default)
    {
        if (catalog.Get(typeId) is null)
        {
            return null;
        }
        var view = await ViewAsync(characterId, accountId, cancellationToken);
        var target = level > 0
            ? level
            : Math.Clamp((view.Skills.FirstOrDefault(s => s.TypeId == typeId)?.Level ?? 0) + 1, 1, SkillPoints.MaxLevel);
        var next = new List<QueueEntry> { new(typeId, target) };
        next.AddRange(view.Queue.Where(line => line.TypeId != typeId || line.ToLevel != target).Select(line => new QueueEntry(line.TypeId, line.ToLevel)));
        return await SaveQueueAsync(characterId, accountId, next, activate: true, accountCharacters, cancellationToken);
    }
}
