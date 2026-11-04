using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Structures;

/// <summary>Start-time refinery rules: automatic fracture delay, field lifetime and fitted-rig yield multiplier.</summary>
public sealed record MoonRules(long AutoFractureDelayMs, long FieldDecayMs, double YieldMultiplier = 1);

/// <summary>The moon rules of a structure type. The Host implements it over the static data (SDE attributes 2698, 2728, 2706).</summary>
public interface IMoonRulesSource
{
    MoonRules RulesOf(int typeId);
}

/// <summary>The payload of <see cref="MoonExtractions.ChunkArrivedTopic"/>: the chunk is ready to fracture.</summary>
public sealed record MoonChunkArrived(long ExtractionId, long StructureId, long SolarSystemId, long OwnerCorporationId);

/// <summary>The payload of <see cref="MoonExtractions.StartedTopic"/>.</summary>
public sealed record MoonExtractionStarted(long ExtractionId, long StructureId, long SolarSystemId, long OwnerCorporationId, long ActorCharacterId);

/// <summary>The payload of <see cref="MoonExtractions.FractureStartedTopic"/>.</summary>
public sealed record MoonFractureStarted(long ExtractionId, long StructureId, long SolarSystemId, long OwnerCorporationId, long ActorCharacterId);

/// <summary>The payload of <see cref="MoonExtractions.EndedTopic"/>: the extraction is completed (<c>fractured</c>, <c>cancelled</c>, <c>decayed</c> or <c>offlined</c>).</summary>
public sealed record MoonExtractionEnded(long ExtractionId, long StructureId, long SolarSystemId, long OwnerCorporationId, string Reason, long EndedMs, long ActorCharacterId = 0);

/// <summary>The payload of <see cref="MoonExtractions.FieldDecayedTopic"/>: the ore field of a fractured or decayed chunk has decayed.</summary>
public sealed record MoonFieldDecayed(long ExtractionId, long StructureId, long SolarSystemId);

/// <summary>
/// The moon extractions of the Standup Moon Drill (ST-6a; EVE.js moonExtractionsService): a refinery whose moon drill is online and whose moon has resources starts a chunk
/// that grows for 6 to 56 days at 720 000 m3 a day (30 000 m3 an hour) of the moon's ores. The chunk turns ready at its arrival, and it fractures by itself at its natural
/// decay time, or after the drill fires (ST-6a d). Each change is one transaction with its event and its deadline job (FOUNDATIONS rule 1); the jobs are keyed by extraction
/// (<c>extraction:{id}</c>) and each handler is idempotent (a replay finds the change done, or the deadline not yet due and asks to run again then).
/// </summary>
public sealed class MoonExtractions
{
    public const string ArrivalJobKind = "moon.chunk_arrival";
    public const string FractureJobKind = "moon.auto_fracture";
    public const string FieldDecayJobKind = "moon.field_decay";
    public const string ChunkArrivedTopic = "moon.chunk_arrived";
    public const string StartedTopic = "moon.extraction_started";
    public const string FractureStartedTopic = "moon.fracture_started";
    public const string EndedTopic = "moon.extraction_ended";
    public const string FieldDecayedTopic = "moon.field_decayed";

    public const long MinimumDurationSeconds = 518_460;
    public const long MaximumDurationSeconds = 4_838_400;
    /// <summary>EVE.js moonExtractionRules YIELD_PER_DAY_M3: 720 000 m3 a day of extraction, times the structure's yield multiplier.</summary>
    public const double YieldPerDayM3 = 720_000;
    /// <summary>The time a fracture takes from the fire to the chunk's fracture (EVE.js FRACTURE_TOTAL_DURATION_SECONDS).</summary>
    public const long FractureMs = 30_000;

    public const string Fractured = "fractured";
    public const string Cancelled = "cancelled";
    public const string Decayed = "decayed";
    public const string Offlined = "offlined";

    public enum StartOutcome { Started, Existing, NoStructure, DrillOffline, NoMoonResources }

    public sealed record StartResult(StartOutcome Outcome, MoonExtraction? Extraction);

    private sealed record ExtractionJob(long ExtractionId);

    private readonly StructureStore _structures;
    private readonly MoonExtractionStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _events;
    private readonly IMoonRulesSource _rules;
    private readonly Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<MoonRules>>? _effectiveRules;

    public MoonExtractions(StructureStore structures, MoonExtractionStore store, DurableScheduler scheduler, EventBus events, IMoonRulesSource rules,
        Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<MoonRules>>? effectiveRules = null)
    {
        _structures = structures;
        _store = store;
        _scheduler = scheduler;
        _events = events;
        _rules = rules;
        _effectiveRules = effectiveRules;
    }

    private static string KeyOf(long extractionId) => $"extraction:{extractionId}";

    /// <summary>Registers the three job handlers. Call it once, before the foundations start.</summary>
    public void Register()
    {
        _scheduler.Register(ArrivalJobKind, (job, token) => ArrivalAsync(job, token));
        _scheduler.Register(FractureJobKind, (job, token) => FractureDueAsync(job, token));
        _scheduler.Register(FieldDecayJobKind, (job, token) => FieldDecayAsync(job, token));
    }

    /// <summary>
    /// Subscribes the reactions that end an extraction on its own: a drill that goes offline stops a chunk that has not arrived yet, and a destroyed structure
    /// stops any extraction (EVE.js isExtractionPullDisrupted). Call it once, before the foundations start.
    /// </summary>
    public void Subscribe()
    {
        _events.Subscribe("moon.drill_offline", [StructureTimers.ServiceChangedTopic], async (context, cancellation) =>
        {
            var change = context.Event.Payload<StructureServiceChanged>()!;
            if (change.ServiceId == StructureServiceIds.MoonMining && change.State == ServiceState.Offline)
                await OfflineAsync(context.Connection, context.Transaction, change.StructureId, false, cancellation);
        });
        _events.Subscribe("moon.structure_destroyed", [StructureTimers.StateChangedTopic], async (context, cancellation) =>
        {
            var change = context.Event.Payload<StructureStateChanged>()!;
            if (change.Removal == "destroyed")
                await OfflineAsync(context.Connection, context.Transaction, change.StructureId, true, cancellation);
        });
    }

    /// <summary>
    /// StartNewExtraction. The order is EVE.js's: the structure and its moon drill, then the moon's resources, then an extraction already running (answered as it is).
    /// The duration is clamped to the 6 to 56 days of the client; the caller refuses a duration that is not positive.
    /// </summary>
    public async Task<StartResult> StartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, long durationSeconds, bool addToCalendar, CancellationToken cancellationToken = default, long actorCharacterId = 0)
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

        var rules = _effectiveRules is null
            ? _rules.RulesOf(structure.TypeId)
            : await _effectiveRules(connection, transaction, structureId, cancellationToken);
        var duration = Math.Clamp(durationSeconds, MinimumDurationSeconds, MaximumDurationSeconds);
        var now = _scheduler.Now;
        var chunkAvailable = now + duration * 1000;
        var yieldMultiplier = profile.YieldMultiplier * rules.YieldMultiplier;
        var volume = YieldPerDayM3 * duration / 86_400 * yieldMultiplier;
        var ore = profile.Resources.ToDictionary(share => share.Key, share => share.Value * volume);
        var (extractionId, eventId) = await _store.NextIdsAsync(connection, transaction, cancellationToken);
        var extraction = new MoonExtraction(extractionId, structureId, profile.MoonId, structure.SolarSystemId, structure.OwnerCorporationId, now, chunkAvailable,
            chunkAvailable + rules.AutoFractureDelayMs, duration, yieldMultiplier, addToCalendar ? eventId : 0, "active", 0, 0, 0, ore, now, 0, "", 0, rules.FieldDecayMs);
        await _store.InsertExtractionAsync(connection, transaction, extraction, cancellationToken);
        await ScheduleAsync(connection, transaction, extraction, cancellationToken);
        await _events.PublishInTransactionAsync(connection, transaction, StartedTopic,
            new MoonExtractionStarted(extractionId, structureId, extraction.SolarSystemId, extraction.OwnerCorporationId, actorCharacterId), $"structure:{structureId}", cancellationToken);
        return new StartResult(StartOutcome.Started, extraction);
    }

    public enum FractureOutcome { Fired, Firing, NotReady, NoExtraction }

    /// <summary>
    /// FractureChunkWithStructure (EVE.js moonExtractionsService): a ready chunk is fired on. The fracture takes <see cref="FractureMs"/>; its deadline replaces the chunk's
    /// fracture job, so the fracture completes at its time (<see cref="FractureDueAsync"/>). A fracture already running is left as it is, as EVE.js does.
    /// </summary>
    public async Task<FractureOutcome> FractureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken = default, long actorCharacterId = 0)
    {
        if ((await _store.OpenAsync(connection, transaction, structureId, true, cancellationToken)).FirstOrDefault() is not { } e)
            return FractureOutcome.NoExtraction;
        if (e.State == "fracturing")
            return FractureOutcome.Firing;
        var now = _scheduler.Now;
        if (e.ChunkAvailableMs > now)
            return FractureOutcome.NotReady;
        var fired = e with { State = "fracturing", FractureStartedMs = now, FractureCompleteMs = now + FractureMs };
        await _store.UpdateAsync(connection, transaction, fired, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(FractureJobKind, fired.FractureCompleteMs) { Key = KeyOf(e.ExtractionId), Payload = new ExtractionJob(e.ExtractionId) }, cancellationToken);
        await _events.PublishInTransactionAsync(connection, transaction, FractureStartedTopic,
            new MoonFractureStarted(e.ExtractionId, e.StructureId, e.SolarSystemId, e.OwnerCorporationId, actorCharacterId), $"structure:{e.StructureId}", cancellationToken);
        return FractureOutcome.Fired;
    }

    /// <summary>
    /// CancelExtraction: the open extraction of the structure ends as cancelled (its chunk leaves; no field). Returns false when there is none.
    /// </summary>
    public async Task<bool> CancelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken = default, long actorCharacterId = 0)
    {
        if ((await _store.OpenAsync(connection, transaction, structureId, true, cancellationToken)).FirstOrDefault() is not { } e)
            return false;
        await CompleteAsync(connection, transaction, e, Cancelled, true, cancellationToken, actorCharacterId);
        return true;
    }

    /// <summary>
    /// Makes sure every open extraction has its deadline jobs (after the import of the game store's table). The jobs are replaced by the same times, so a second run
    /// changes nothing. Returns how many extractions it scheduled.
    /// </summary>
    public async Task<int> EnsureScheduledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        var open = await _store.OpenAsync(connection, transaction, null, false, cancellationToken);
        foreach (var extraction in open)
        {
            await ScheduleAsync(connection, transaction, extraction, cancellationToken);
        }
        return open.Count;
    }

    /// <summary>The deadlines of an open extraction: its arrival while the chunk is growing, and its fracture (the natural decay, or the end of a fracture in progress).</summary>
    private async Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MoonExtraction e, CancellationToken cancellationToken)
    {
        var key = KeyOf(e.ExtractionId);
        var payload = new ExtractionJob(e.ExtractionId);
        if (e.State == "active" && e.ReadyNotifiedMs == 0)
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ArrivalJobKind, e.ChunkAvailableMs) { Key = key, Payload = payload }, cancellationToken);
        var fractureAt = e.State == "fracturing" ? e.FractureCompleteMs : e.NaturalDecayMs;
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(FractureJobKind, fractureAt) { Key = key, Payload = payload }, cancellationToken);
    }

    private async Task ArrivalAsync(JobContext job, CancellationToken cancellationToken)
    {
        var id = (job.Payload<ExtractionJob>() ?? throw new InvalidOperationException("moon.chunk_arrival job without a payload")).ExtractionId;
        var e = await _store.GetAsync(job.Connection, job.Transaction, id, true, cancellationToken);
        if (e is null || e.State != "active" || e.ReadyNotifiedMs > 0)
            return;
        var now = _scheduler.Now;
        if (e.ChunkAvailableMs > now)
        {
            job.RunAgainAt(e.ChunkAvailableMs, new ExtractionJob(id));
            return;
        }
        await _store.UpdateAsync(job.Connection, job.Transaction, e with { ReadyNotifiedMs = now }, cancellationToken);
        await _events.PublishInTransactionAsync(job.Connection, job.Transaction, ChunkArrivedTopic,
            new MoonChunkArrived(id, e.StructureId, e.SolarSystemId, e.OwnerCorporationId), $"structure:{e.StructureId}", cancellationToken);
    }

    /// <summary>
    /// The fracture deadline: a fracture in progress completes (the drill fired 30 s before), and a chunk nobody fired on decays by itself at its natural time.
    /// A deadline that is not due yet asks to run again at the due time.
    /// </summary>
    private async Task FractureDueAsync(JobContext job, CancellationToken cancellationToken)
    {
        var id = (job.Payload<ExtractionJob>() ?? throw new InvalidOperationException("moon.auto_fracture job without a payload")).ExtractionId;
        var e = await _store.GetAsync(job.Connection, job.Transaction, id, true, cancellationToken);
        if (e is null || e.State == "completed")
            return;
        var now = _scheduler.Now;
        if (e.State == "fracturing")
        {
            if (e.FractureCompleteMs > now)
            {
                job.RunAgainAt(e.FractureCompleteMs, new ExtractionJob(id));
                return;
            }
            await CompleteAsync(job.Connection, job.Transaction, e, Fractured, false, cancellationToken);
            return;
        }
        if (e.NaturalDecayMs > now)
        {
            job.RunAgainAt(e.NaturalDecayMs, new ExtractionJob(id));
            return;
        }
        await CompleteAsync(job.Connection, job.Transaction, e, Decayed, false, cancellationToken);
    }

    /// <summary>The ore field of a fractured or decayed chunk lasts for the refinery's field lifetime; then it is marked decayed.</summary>
    private async Task FieldDecayAsync(JobContext job, CancellationToken cancellationToken)
    {
        var id = (job.Payload<ExtractionJob>() ?? throw new InvalidOperationException("moon.field_decay job without a payload")).ExtractionId;
        var e = await _store.GetAsync(job.Connection, job.Transaction, id, true, cancellationToken);
        if (e is null || e.FieldDecayedMs > 0)
            return;
        var now = _scheduler.Now;
        await _store.UpdateAsync(job.Connection, job.Transaction, e with { FieldDecayedMs = now }, cancellationToken);
        await _events.PublishInTransactionAsync(job.Connection, job.Transaction, FieldDecayedTopic,
            new MoonFieldDecayed(id, e.StructureId, e.SolarSystemId), $"structure:{e.StructureId}", cancellationToken);
    }

    /// <summary>
    /// Completes an open extraction: the record, the arrival job (and the fracture job when <paramref name="cancelFractureJob"/>), the field's decay job for a
    /// fractured or decayed chunk, and the <see cref="EndedTopic"/> event, all in the caller's transaction.
    /// </summary>
    private async Task CompleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MoonExtraction e, string reason, bool cancelFractureJob, CancellationToken cancellationToken, long actorCharacterId = 0)
    {
        var now = _scheduler.Now;
        var key = KeyOf(e.ExtractionId);
        await _store.UpdateAsync(connection, transaction, e with { State = "completed", CompletedReason = reason, CompletedMs = now }, cancellationToken);
        await _scheduler.CancelAsync(connection, transaction, ArrivalJobKind, key, cancellationToken);
        if (cancelFractureJob)
            await _scheduler.CancelAsync(connection, transaction, FractureJobKind, key, cancellationToken);
        if (reason is Fractured or Decayed)
        {
            var decayMs = e.FieldDecayMs >= 0
                ? e.FieldDecayMs
                : _rules.RulesOf((await _structures.GetAsync(connection, transaction, e.StructureId, false, cancellationToken))?.TypeId ?? 0).FieldDecayMs;
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(FieldDecayJobKind, now + decayMs) { Key = key, Payload = new ExtractionJob(e.ExtractionId) }, cancellationToken);
        }
        await _events.PublishInTransactionAsync(connection, transaction, EndedTopic,
            new MoonExtractionEnded(e.ExtractionId, e.StructureId, e.SolarSystemId, e.OwnerCorporationId, reason, now, actorCharacterId), $"structure:{e.StructureId}", cancellationToken);
    }

    /// <summary>
    /// The drill went offline or the structure was destroyed: the open extractions end as offlined. A drill that goes offline stops only a chunk that has not arrived
    /// (EVE.js); a destroyed structure stops any open extraction.
    /// </summary>
    private async Task OfflineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, bool destroyed, CancellationToken cancellationToken)
    {
        var now = _scheduler.Now;
        foreach (var e in await _store.OpenAsync(connection, transaction, structureId, true, cancellationToken))
        {
            if (!destroyed && (e.State != "active" || e.ChunkAvailableMs <= now))
                continue;
            await CompleteAsync(connection, transaction, e, Offlined, true, cancellationToken);
        }
    }
}
