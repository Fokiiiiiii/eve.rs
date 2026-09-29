namespace Eve.Skills;

/// <summary>One line of a queue: train this skill to this level.</summary>
public sealed record QueueEntry(int TypeId, int ToLevel);

/// <summary>A character's queue: what is lined up, whether it is running, and when the running line began (a FILETIME).</summary>
public sealed record QueueState(IReadOnlyList<QueueEntry> Entries, bool Active, long? ActiveStart)
{
    public static readonly QueueState Empty = new([], false, null);

    /// <summary>Whether something is training: the queue runs and has a line.</summary>
    public bool Running => Active && Entries.Count > 0;

    /// <summary>Two queues are equal when they line up the same skills and run the same way (whatever list holds them).</summary>
    public bool Equals(QueueState? other) => other is not null && Active == other.Active && ActiveStart == other.ActiveStart && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => HashCode.Combine(Active, ActiveStart, Entries.Count);
}

/// <summary>
/// A line of the queue worked out: the points it starts and ends at and, when the queue runs, when. A line whose skill the character does not
/// have is not listed (its position is).
/// </summary>
public sealed record ProjectedEntry(int Position, int TypeId, int ToLevel, long StartPoints, long DestinationPoints, long? Start, long? End, double PointsPerMinute);

/// <summary>The queue worked out from a moment: its lines, when the last ends, and the skills as they will be when it has run.</summary>
public sealed record Projection(IReadOnlyList<ProjectedEntry> Entries, long? EndTime, IReadOnlyDictionary<int, CharacterSkill> Skills);

/// <summary>Why a queue is refused (the client has a message for each).</summary>
public enum QueueError
{
    TooManySkills,
    TooLong,
    SkillNotUploaded,
    PastMaximumLevel,
    PreviouslyTrained,
    OutOfOrder,
    BeforeRequirements,
}

/// <summary>What the skill history says happened.</summary>
public static class SkillEvents
{
    public const int TrainingStarted = 36;
    public const int TrainingComplete = 37;
    public const int TrainingCancelled = 38;

    /// <summary>A level finished with more lines waiting behind it.</summary>
    public const int QueueTrainingCompleted = 53;
}

/// <summary>A line of the history (a skill started, finished or stopped).</summary>
public sealed record HistoryEntry(long LogDate, int EventType, int TypeId, long Points, int Level);

/// <summary>What settling a queue found: the queue and skills as they are now, what finished, and what to write in the history.</summary>
public sealed record Settlement(QueueState State, IReadOnlyDictionary<int, CharacterSkill> Skills, IReadOnlyList<CharacterSkill> Finished, IReadOnlyList<HistoryEntry> History, bool QueueChanged);

/// <summary>
/// How a queue runs: what training costs in points and time, how the lines follow one another, which queues are refused, and what has
/// finished by a moment. Pure: the caller keeps the state and knows the time (a FILETIME, 100 ns ticks).
/// </summary>
public static class Training
{
    public const long TicksPerMinute = 600_000_000;
    public const long TicksPerSecond = 10_000_000;

    /// <summary>The lines a queue can have.</summary>
    public const int MaxQueueLength = 150;

    /// <summary>The longest a queue may take to run: ten years.</summary>
    public const long MaxQueueTicks = 10L * 365 * 24 * 60 * 60 * TicksPerSecond;

    /// <summary>The points a minute a skill trains at: the primary attribute plus half the secondary, times what the clone and the server's speed make of it.</summary>
    public static double PointsPerMinute(SkillType skill, Func<int, double> attribute, double multiplier) =>
        (attribute(skill.PrimaryAttribute) + (attribute(skill.SecondaryAttribute) / 2)) * multiplier;

    /// <summary>How long it takes to train <paramref name="remaining"/> points at <paramref name="pointsPerMinute"/>, in ticks (rounded down; nothing to train or no speed is no time).</summary>
    public static long DurationTicks(long remaining, double pointsPerMinute) =>
        remaining <= 0 || pointsPerMinute <= 0 ? 0 : (long)Math.Floor(remaining / pointsPerMinute * TicksPerMinute);

    /// <summary>The points a skill has at <paramref name="sample"/> while training from <paramref name="start"/> points (at <paramref name="startTime"/>) towards <paramref name="destination"/>.</summary>
    public static long PointsAt(long start, long destination, long startTime, long sample, double pointsPerMinute)
    {
        var from = Math.Max(0, start);
        var to = Math.Max(from, destination);
        if (sample <= startTime || pointsPerMinute <= 0)
        {
            return from;
        }
        var gained = (long)Math.Floor((double)(sample - startTime) / TicksPerMinute * pointsPerMinute);
        return Math.Max(from, Math.Min(to, from + Math.Max(0, gained)));
    }

    /// <summary>The queue worked out. The lines run one after the other from the moment the running one began (or from <paramref name="now"/>).</summary>
    public static Projection Project(QueueState state, IReadOnlyDictionary<int, CharacterSkill> skills, SkillCatalog catalog, Func<SkillType, double> pointsPerMinute, long now)
    {
        var projected = skills.ToDictionary(s => s.Key, s => s.Value);
        var entries = new List<ProjectedEntry>();
        var running = state.Running;
        long? nextStart = running ? state.ActiveStart ?? now : null;
        for (var position = 0; position < state.Entries.Count; position++)
        {
            var entry = state.Entries[position];
            if (!projected.TryGetValue(entry.TypeId, out var skill) || catalog.Get(entry.TypeId) is not { } type)
            {
                continue;
            }
            var startPoints = skill.Level == entry.ToLevel - 1
                ? Math.Max(SkillPoints.ForTraining(type.Rank, entry.ToLevel - 1), skill.SkillPoints)
                : SkillPoints.ForTraining(type.Rank, entry.ToLevel - 1);
            var destination = SkillPoints.ForTraining(type.Rank, entry.ToLevel);
            var rate = pointsPerMinute(type);
            var duration = running ? DurationTicks(destination - startPoints, rate) : 0;
            long? start = running ? nextStart : null;
            long? end = running && start is { } begun ? begun + duration : null;
            entries.Add(new ProjectedEntry(position, entry.TypeId, entry.ToLevel, startPoints, destination, start, end, rate));
            projected[entry.TypeId] = new CharacterSkill(entry.TypeId, SkillPoints.LevelAt(type.Rank, destination), destination);
            if (running)
            {
                nextStart = end;
            }
        }
        return new Projection(entries, entries.Count > 0 ? entries[^1].End : null, projected);
    }

    /// <summary>
    /// Whether a queue can be saved, given the skills the character has (as they are now, not as they will be): the first rule it breaks, or null.
    /// The lines are checked in order, each against what the lines before it will have trained.
    /// </summary>
    public static QueueError? Validate(IReadOnlyList<QueueEntry> entries, IReadOnlyDictionary<int, CharacterSkill> skills, SkillCatalog catalog, Func<SkillType, double> pointsPerMinute, long now)
    {
        if (entries.Count > MaxQueueLength)
        {
            return QueueError.TooManySkills;
        }
        var whole = Project(new QueueState(entries, true, now), skills, catalog, pointsPerMinute, now);
        if (whole.EndTime is { } end && end - now > MaxQueueTicks)
        {
            return QueueError.TooLong;
        }

        var levels = skills.ToDictionary(s => s.Key, s => s.Value.Level);
        foreach (var entry in entries)
        {
            if (!skills.ContainsKey(entry.TypeId) || catalog.Get(entry.TypeId) is not { } type)
            {
                return QueueError.SkillNotUploaded;
            }
            if (entry.ToLevel > SkillPoints.MaxLevel)
            {
                return QueueError.PastMaximumLevel;
            }
            var level = levels.GetValueOrDefault(entry.TypeId);
            if (level >= entry.ToLevel)
            {
                return QueueError.PreviouslyTrained;
            }
            if (level < entry.ToLevel - 1)
            {
                return QueueError.OutOfOrder;
            }
            if (type.Needs.Any(need => levels.GetValueOrDefault(need.TypeId) < need.Level))
            {
                return QueueError.BeforeRequirements;
            }
            levels[entry.TypeId] = entry.ToLevel;
        }
        return null;
    }

    /// <summary>
    /// What has finished by <paramref name="now"/>: lines whose end has passed become trained levels (the next line begins where the one before
    /// ended), lines whose skill is gone or already at the level are dropped.
    /// </summary>
    public static Settlement Settle(QueueState state, IReadOnlyDictionary<int, CharacterSkill> skills, SkillCatalog catalog, Func<SkillType, double> pointsPerMinute, long now)
    {
        var queue = state.Entries.ToList();
        var active = state.Active;
        var activeStart = state.ActiveStart;
        var current = skills.ToDictionary(s => s.Key, s => s.Value);
        var finished = new List<CharacterSkill>();
        var history = new List<HistoryEntry>();
        var changed = false;

        while (queue.Count > 0)
        {
            var projection = Project(new QueueState(queue, active, activeStart), current, catalog, pointsPerMinute, now);
            if (projection.Entries.FirstOrDefault() is not { } line)
            {
                break;
            }
            if (!current.TryGetValue(line.TypeId, out var skill))
            {
                Drop();
                continue;
            }
            if (skill.Level >= line.ToLevel || skill.SkillPoints >= line.DestinationPoints)
            {
                Drop();
                continue;
            }
            if (!active || line.End is not { } end || end > now)
            {
                break;
            }

            var rank = catalog.Get(line.TypeId)?.Rank ?? 1;
            var trained = new CharacterSkill(line.TypeId, SkillPoints.LevelAt(rank, line.DestinationPoints), line.DestinationPoints);
            current[line.TypeId] = trained;
            finished.Add(trained);
            history.Add(new HistoryEntry(now, queue.Count > 1 ? SkillEvents.QueueTrainingCompleted : SkillEvents.TrainingComplete, line.TypeId, line.DestinationPoints, line.ToLevel));
            queue.RemoveAt(0);
            active = queue.Count > 0;
            activeStart = active ? end : null;
            changed = true;
        }

        return new Settlement(new QueueState(queue, active, activeStart), current, finished, history, changed);

        void Drop()
        {
            queue.RemoveAt(0);
            active = queue.Count > 0;
            activeStart = active ? now : null;
            changed = true;
        }
    }

    /// <summary>The skill as it is at <paramref name="now"/> while it trains: the points gained so far kept in it, and the level they make.</summary>
    public static CharacterSkill Rebase(ProjectedEntry line, CharacterSkill skill, int rank, long now)
    {
        var estimate = line is { Start: { } start, End: not null } ? PointsAt(line.StartPoints, line.DestinationPoints, start, now, line.PointsPerMinute) : line.StartPoints;
        var points = Math.Max(line.StartPoints, Math.Min(line.DestinationPoints, estimate));
        return skill with { Level = SkillPoints.LevelAt(rank, points), SkillPoints = points };
    }
}
