using Eve.Scheduler;
using Npgsql;

namespace Eve.Structures;

/// <summary>What a refinery's moon rules are: the delay before a ready chunk fractures on its own, and how long the ore field lasts.</summary>
public sealed record MoonRules(long AutoFractureDelayMs, long FieldDecayMs);

/// <summary>The moon rules of a structure type. The Host implements it over the static data (SDE attributes 2698, 2728, 2706).</summary>
public interface IMoonRulesSource
{
    MoonRules RulesOf(int typeId);
}

/// <summary>
/// The moon extractions of the Standup Moon Drill (ST-6a; EVE.js moonExtractionsService): a refinery whose moon drill is online and whose moon has resources starts a chunk that
/// grows for 6 to 56 days at 720 000 m3 a day (30 000 m3 an hour) of the moon's ores. The record and the start are one transaction (FOUNDATIONS rule 1).
/// </summary>
public sealed class MoonExtractions
{
    public const long MinimumDurationSeconds = 518_460;
    public const long MaximumDurationSeconds = 4_838_400;
    /// <summary>EVE.js moonExtractionRules YIELD_PER_DAY_M3: 720 000 m3 a day of extraction, times the structure's yield multiplier.</summary>
    public const double YieldPerDayM3 = 720_000;

    public enum StartOutcome { Started, Existing, NoStructure, DrillOffline, NoMoonResources }

    public sealed record StartResult(StartOutcome Outcome, MoonExtraction? Extraction);

    private readonly StructureStore _structures;
    private readonly MoonExtractionStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly IMoonRulesSource _rules;

    public MoonExtractions(StructureStore structures, MoonExtractionStore store, DurableScheduler scheduler, IMoonRulesSource rules)
    {
        _structures = structures;
        _store = store;
        _scheduler = scheduler;
        _rules = rules;
    }

    /// <summary>
    /// StartNewExtraction. The order is EVE.js's: the structure and its moon drill, then the moon's resources, then an extraction already running (answered as it is).
    /// The duration is clamped to the 6 to 56 days of the client; the caller refuses a duration that is not positive.
    /// </summary>
    public async Task<StartResult> StartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, long durationSeconds, bool addToCalendar, CancellationToken cancellationToken = default)
    {
        var structure = await _structures.GetAsync(connection, transaction, structureId, true, cancellationToken);
        if (structure is null || structure.Snapshot.DestroyedMs > 0)
            return new StartResult(StartOutcome.NoStructure, null);
        var services = await _structures.ServicesAsync(connection, transaction, structureId, cancellationToken);
        if (services.GetValueOrDefault(StructureServiceIds.MoonMining) != ServiceState.Online)
            return new StartResult(StartOutcome.DrillOffline, null);
        if (await _store.ProfileAsync(connection, transaction, structureId, cancellationToken) is not { Resources.Count: > 0 } profile)
            return new StartResult(StartOutcome.NoMoonResources, null);
        if (await _store.ActiveForStructureAsync(connection, transaction, structureId, cancellationToken) is { } running)
            return new StartResult(StartOutcome.Existing, running);

        var duration = Math.Clamp(durationSeconds, MinimumDurationSeconds, MaximumDurationSeconds);
        var now = _scheduler.Now;
        var chunkAvailable = now + duration * 1000;
        var rules = _rules.RulesOf(structure.TypeId);
        var volume = YieldPerDayM3 * duration / 86_400 * profile.YieldMultiplier;
        var ore = profile.Resources.ToDictionary(share => share.Key, share => share.Value * volume);
        var (extractionId, eventId) = await _store.NextIdsAsync(connection, transaction, cancellationToken);
        var extraction = new MoonExtraction(extractionId, structureId, profile.MoonId, structure.SolarSystemId, structure.OwnerCorporationId, now, chunkAvailable,
            chunkAvailable + rules.AutoFractureDelayMs, duration, profile.YieldMultiplier, addToCalendar ? eventId : 0, "active", 0, 0, 0, ore, now, 0, "");
        await _store.InsertExtractionAsync(connection, transaction, extraction, cancellationToken);
        return new StartResult(StartOutcome.Started, extraction);
    }
}
