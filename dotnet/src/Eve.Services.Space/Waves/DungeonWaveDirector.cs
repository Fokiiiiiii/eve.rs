using System.Text.Json;
using Eve.Content;
using Eve.Data;
using Eve.Dungeons;
using Eve.Events;
using Eve.Npc;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Space.Waves;

/// <summary>What the director asks of the space: the NPCs of a wave made beside a point of a system, whether a pilot is in space in a system, and the NPCs taken out of space.</summary>
public interface IDungeonNpcSpawner
{
    /// <summary>
    /// Makes <paramref name="amount"/> NPCs rolled from <paramref name="selection"/> beside <paramref name="origin"/>. The answer lists the ships the simulation made; when fewer than
    /// <paramref name="amount"/> were made, or the simulation was not reachable, <see cref="DungeonSpawnResult.Error"/> says why.
    /// </summary>
    Task<DungeonSpawnResult> SpawnAsync(int solarSystemId, double[] origin, NpcSelection selection, int amount, CancellationToken cancellationToken);

    /// <summary>Whether the character is in space in the solar system.</summary>
    bool IsPilotInSystem(long characterId, int solarSystemId);

    /// <summary>Takes NPCs out of space. No kill is announced for them.</summary>
    void Remove(IReadOnlyList<long> shipIds);
}

public sealed record DungeonSpawnResult(IReadOnlyList<long> ShipIds, string? Error);

/// <summary>What a dungeon instance keeps of its start: the dungeon, where its NPCs come, and the waves (what each asks for).</summary>
public sealed record DungeonInstanceData(long DungeonID, int SolarSystemId, double[] Origin, IReadOnlyList<DungeonWaveData> Waves);

public sealed record DungeonWaveData(int WaveIndex, string SpawnQuery, int Amount);

/// <summary>The answer of <see cref="DungeonWaveDirector.StartAsync"/>: started (or the instance the pilot already has), or refused with the reason.</summary>
public sealed record DungeonStart(bool Started, long? InstanceId, string? Refusal);

/// <summary>
/// Runs the NPC waves of a dungeon in order, on the shared content runtime (<c>Eve.Content</c>): one definition per dungeon, one sequential objective per wave whose target is the
/// number of its NPCs. A wave's objective spawns its NPCs in a scheduler job when it becomes the one to do (at once, or after its delay, so a timer wave is a delayed job), and each
/// NPC killed (its ship id is in <c>dungeon_wave_npcs</c>, and the kill is announced on the bus as <c>npc.killed</c>) advances that wave's objective. The next wave starts when the
/// objective is done: the content runtime does the sequencing, the durable scheduler the timers and the spawns, PostgreSQL the state, so a restart resumes where it was.
/// A sweep job ends the instance when its pilot is no longer in space in its system, and takes out the NPCs of every instance that has ended (completed, failed, abandoned or expired).
/// </summary>
public sealed class DungeonWaveDirector
{
    public const string Kind = "dungeon";
    public const string DefinitionPrefix = "dungeon.";
    public const string WaveObjectiveName = "dungeon.wave";
    public const string SpawnJob = "dungeon.wave.spawn";
    public const string SweepJob = "dungeon.waves.sweep";
    public const string KillSubscriber = "dungeon.waves.kills";
    public const string NpcKilledTopic = "npc.killed";
    public const string AbandonReason = "the pilot left the system";

    /// <summary>How long a dungeon lasts when it is not finished (its content expires then, and its NPCs are taken out by the sweep).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>How often the sweep looks at the pilots and the NPCs of the dungeons.</summary>
    public static readonly TimeSpan Sweep = TimeSpan.FromSeconds(10);

    private const string MigrationText = """
        CREATE TABLE dungeon_wave_spawns (
            instance_id bigint NOT NULL,
            wave_index integer NOT NULL,
            spawned_ms bigint NOT NULL,
            npc_count integer NOT NULL,
            PRIMARY KEY (instance_id, wave_index)
        );
        CREATE TABLE dungeon_wave_npcs (
            ship_id bigint PRIMARY KEY,
            instance_id bigint NOT NULL,
            wave_index integer NOT NULL
        );
        CREATE INDEX dungeon_wave_npcs_instance ON dungeon_wave_npcs (instance_id);
        """;

    private sealed record WaveDue(long InstanceId, int WaveIndex);

    private readonly NpgsqlDataSource _source;
    private readonly ContentRuntime _content;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _bus;
    private readonly DungeonCatalog _catalog;
    private readonly WaveRules _rules;
    private readonly NpcSelector _selector;
    private readonly IDungeonNpcSpawner _spawner;
    private readonly string _schema;
    private readonly Action<string>? _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _refusalsLogged = new();

    public DungeonWaveDirector(
        NpgsqlDataSource source, ContentRuntime content, DurableScheduler scheduler, EventBus bus, DungeonCatalog catalog, WaveRules rules, NpcSelector selector,
        IDungeonNpcSpawner spawner, string schema, Action<string>? log = null)
    {
        (_source, _content, _scheduler, _bus, _catalog, _rules, _selector, _spawner) = (source, content, scheduler, bus, catalog, rules, selector, spawner);
        _log = log;
        _schema = Sql.CheckSchema(schema);
    }

    private string Q(string text) => Sql.In(_schema, text);

    public static string DefinitionOf(long dungeonID) => DefinitionPrefix + dungeonID;

    public static string ObjectiveOf(int waveIndex) => $"wave{waveIndex}";

    /// <summary>Creates the director's tables in the schema (the space schema of the host). Applied once; a second start changes nothing.</summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema, CancellationToken cancellationToken = default) =>
        Eve.Data.Migrations.ApplyAsync(source, schema, [new Eve.Data.Migration("001_dungeon_waves", MigrationText)], cancellationToken);

    /// <summary>
    /// Registers the objective type, the jobs and the subscriber, and defines the content of every dungeon the wave planner can run. Call it before the content runtime attaches
    /// (<see cref="ContentRuntime.AttachAsync"/>) and before the event bus starts.
    /// </summary>
    public async Task AttachAsync()
    {
        _content.RegisterObjectiveType(new WaveObjective());
        _scheduler.Register(SpawnJob, (context, token) => SpawnWaveAsync(context, token));
        _scheduler.Register(SweepJob, (context, token) => SweepAsync(context, token));
        foreach (Dungeon dungeon in _catalog.Dungeons)
        {
            if (WavePlanner.Plan(dungeon, _rules).Plan is { } plan)
            {
                _content.Define(Definition(plan));
            }
        }
        _bus.Subscribe(KillSubscriber, [NpcKilledTopic], (context, token) => OnKilledAsync(context, token));
        await _scheduler.EnsureRecurringAsync(SweepJob, "sweep", Sweep);
    }

    /// <summary>
    /// Starts a dungeon for a pilot who is in space in the system: its waves are checked first (a wave the planner refuses, or a spawn query that names no pool or profile of the
    /// authority, refuses the whole dungeon, and nothing is started), then the instance is made in one transaction.
    /// </summary>
    public async Task<DungeonStart> StartAsync(long characterId, int solarSystemId, double[] origin, long dungeonID, CancellationToken cancellationToken = default)
    {
        if (ValidatedPlan(dungeonID, out var plan) is { } refusal)
        {
            // a refused start is retried by every sweep of its mission: the reason is logged once per pilot and dungeon
            if (_refusalsLogged.TryAdd($"{characterId}:{dungeonID}", true))
                _log?.Invoke($"dungeon {dungeonID} of pilot {characterId} did not start: {refusal}");
            return Refused(refusal);
        }
        if (!_spawner.IsPilotInSystem(characterId, solarSystemId))
        {
            return Refused("the pilot is not in space in this system");
        }
        var data = InstanceOf(dungeonID, solarSystemId, origin, plan!);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        StartResult started = await _content.StartAsync(connection, transaction, DefinitionOf(dungeonID), characterId, data, $"dungeon:{characterId}:{dungeonID}", cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();
        return new DungeonStart(started.Created, started.Id, started.Created ? null : "the pilot already has this dungeon running");
    }


    /// <summary>The owner of a dungeon that belongs to no pilot (an incursion's staging site): the global owner, which no character has.</summary>
    public const long NoPilotOwner = 0;

    /// <summary>
    /// Starts a dungeon that belongs to no pilot (an incursion's staging site): its waves are checked as <see cref="StartAsync"/> checks them, it lasts for
    /// <paramref name="lifetime"/> unless its feature ends it, and no pilot leaving its system abandons it. It is started once per <paramref name="uniqueKey"/>: a second
    /// start answers the instance that is running (its <see cref="DungeonStart.InstanceId"/>).
    /// </summary>
    public async Task<DungeonStart> StartForSystemAsync(int solarSystemId, double[] origin, long dungeonID, string uniqueKey, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        if (ValidatedPlan(dungeonID, out var plan) is { } refusal)
        {
            return Refused(refusal);
        }
        var data = InstanceOf(dungeonID, solarSystemId, origin, plan!);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        StartResult started = await _content.StartAsync(connection, transaction, DefinitionOf(dungeonID), NoPilotOwner, data, uniqueKey, (long)lifetime.TotalMilliseconds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();
        return new DungeonStart(started.Created, started.Id, started.Created ? null : "the dungeon is already running");
    }

    /// <summary>
    /// <see cref="StartForSystemAsync(int, double[], long, string, TimeSpan, CancellationToken)"/> in the caller's transaction (a feature's job starts its sites in the same
    /// commit as its own rows, and holds no second connection while it does).
    /// </summary>
    public async Task<DungeonStart> StartForSystemAsync(int solarSystemId, double[] origin, long dungeonID, string uniqueKey, TimeSpan lifetime, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        if (ValidatedPlan(dungeonID, out var plan) is { } refusal)
        {
            return Refused(refusal);
        }
        var data = InstanceOf(dungeonID, solarSystemId, origin, plan!);
        StartResult started = await _content.StartAsync(connection, transaction, DefinitionOf(dungeonID), NoPilotOwner, data, uniqueKey, (long)lifetime.TotalMilliseconds, cancellationToken);
        return new DungeonStart(started.Created, started.Id, started.Created ? null : "the dungeon is already running");
    }

    /// <summary>The waves of a dungeon, checked against the planner and the NPC authority: the plan, or why there is none (nothing starts then).</summary>
    private string? ValidatedPlan(long dungeonID, out WavePlan? plan)
    {
        plan = null;
        if (_catalog.GetDungeon(dungeonID) is not DungeonFound found)
        {
            return $"dungeon {dungeonID} is not in the catalog";
        }
        WavePlanAnswer answer = WavePlanner.Plan(found.Dungeon, _rules);
        if (answer.Plan is not { } checkedPlan)
        {
            return answer.Refusal!;
        }
        foreach (PlannedWave wave in checkedPlan.Waves)
        {
            var (_, refusal) = WaveComposition.Resolve(_selector, wave.SpawnQuery);
            if (refusal is not null)
            {
                return $"wave {wave.WaveIndex}: {refusal}";
            }
        }
        plan = checkedPlan;
        return null;
    }

    private static DungeonInstanceData InstanceOf(long dungeonID, int solarSystemId, double[] origin, WavePlan plan) =>
        new(dungeonID, solarSystemId, origin, plan.Waves.Select(w => new DungeonWaveData(w.WaveIndex, w.SpawnQuery, w.Amount)).ToList());

    /// <summary>The content of a dungeon: one sequential objective per wave, counting its NPCs; a wave's delay is its objective's parameter.</summary>
    private static ContentDefinition Definition(WavePlan plan)
    {
        var objectives = plan.Waves
            .Select(wave => new ObjectiveDef(ObjectiveOf(wave.WaveIndex), WaveObjectiveName, new { waveIndex = wave.WaveIndex, delayMs = (long)wave.DelaySeconds * 1000 }, Target: wave.Amount))
            .ToList();
        return new ContentDefinition(Kind, DefinitionOf(plan.DungeonID), objectives) { DurationMs = (long)Lifetime.TotalMilliseconds };
    }

    private static DungeonStart Refused(string reason) => new(false, null, reason);

    /// <summary>The wave plan of a dungeon, or why it has none.</summary>
    /// <summary>
    /// Why the dungeon cannot start now (no plan, or a spawn query the NPC authority does not resolve), or null when it can start. The same check
    /// <see cref="StartAsync"/> makes before it creates the instance; an offer uses it so that an accepted mission can always start.
    /// </summary>
    public string? StartRefusalOf(long dungeonID) => ValidatedPlan(dungeonID, out _);

    public WavePlanAnswer PlanOf(long dungeonID) => _catalog.GetDungeon(dungeonID) switch
    {
        DungeonFound found => WavePlanner.Plan(found.Dungeon, _rules),
        var other => new WavePlanAnswer(null, $"dungeon {dungeonID}: {(other as DungeonNoTemplate)?.Reason ?? (other as DungeonNotInCatalog)?.Reason}"),
    };

    /// <summary>The objective of a wave: it spawns the wave's NPCs when it becomes the one to do (a delayed job for a timer wave), and counts their kills.</summary>
    public sealed class WaveObjective : Eve.Content.ObjectiveType
    {
        public override string Name => WaveObjectiveName;

        public override async Task ActivatedAsync(ActivationContext context, CancellationToken cancellationToken)
        {
            int waveIndex = context.Objective.Params.GetProperty("waveIndex").GetInt32();
            long delayMs = context.Objective.Params.TryGetProperty("delayMs", out var delay) ? delay.GetInt64() : 0;
            var due = new WaveDue(context.Objective.InstanceId, waveIndex);
            await context.Scheduler.ScheduleAsync(context.Connection, context.Transaction,
                new JobSpec(SpawnJob, context.Scheduler.Now + delayMs) { Key = $"{due.InstanceId}:{waveIndex}", Payload = due, OnDuplicate = OnDuplicate.Keep },
                cancellationToken);
        }
    }

    private async Task SpawnWaveAsync(JobContext context, CancellationToken cancellationToken)
    {
        WaveDue due = context.Payload<WaveDue>()!;
        ContentInstance? instance = await _content.GetAsync(due.InstanceId, cancellationToken);
        if (instance is not { State: "active" } || instance.Data<DungeonInstanceData>() is not { } data)
        {
            return;   // the dungeon ended before its wave came: nothing to spawn
        }
        // once per wave, in the transaction of the job: a wave that has its row is not made again (a restart, a retry)
        if (await Sql.ScalarAsync(context.Connection, context.Transaction, Q("SELECT 1 FROM {schema}.dungeon_wave_spawns WHERE instance_id = $1 AND wave_index = $2"),
                cancellationToken, due.InstanceId, due.WaveIndex) is not null)
        {
            return;
        }
        DungeonWaveData wave = data.Waves.Single(w => w.WaveIndex == due.WaveIndex);
        var (selection, refusal) = WaveComposition.Resolve(_selector, wave.SpawnQuery);
        if (selection is null)
        {
            await _content.FailAsync(context.Connection, context.Transaction, instance.Id, $"wave {due.WaveIndex}: {refusal}", cancellationToken);
            return;
        }
        DungeonSpawnResult result = await _spawner.SpawnAsync(data.SolarSystemId, data.Origin, selection, wave.Amount, cancellationToken);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, Q("INSERT INTO {schema}.dungeon_wave_spawns (instance_id, wave_index, spawned_ms, npc_count) VALUES ($1, $2, $3, $4)"),
            cancellationToken, instance.Id, due.WaveIndex, context.NowMs, result.ShipIds.Count);
        foreach (long ship in result.ShipIds)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, Q("INSERT INTO {schema}.dungeon_wave_npcs (ship_id, instance_id, wave_index) VALUES ($1, $2, $3)"),
                cancellationToken, ship, instance.Id, due.WaveIndex);
        }
        if (result.Error is not null || result.ShipIds.Count < wave.Amount)
        {
            string reason = $"{result.ShipIds.Count} of {wave.Amount} NPCs were made" + (result.Error is null ? "" : $": {result.Error}");
            await _content.FailAsync(context.Connection, context.Transaction, instance.Id, $"wave {due.WaveIndex}: {reason}", cancellationToken);
        }
    }

    /// <summary>An NPC was destroyed: when it belongs to a wave, that wave's objective counts it (once: its row goes with the count).</summary>
    private async Task OnKilledAsync(EventContext context, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(context.Event.PayloadJson);
        if (!document.RootElement.TryGetProperty("shipId", out var shipElement) || !shipElement.TryGetInt64(out long shipId))
        {
            return;
        }
        long instanceId;
        int waveIndex;
        await using (var command = Sql.Command(context.Connection, context.Transaction, Q("DELETE FROM {schema}.dungeon_wave_npcs WHERE ship_id = $1 RETURNING instance_id, wave_index"), shipId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }
            (instanceId, waveIndex) = (reader.GetInt64(0), reader.GetInt32(1));
        }
        await _content.ProgressAsync(context.Connection, context.Transaction, instanceId, ObjectiveOf(waveIndex), 1, cancellationToken);
    }

    private async Task SweepAsync(JobContext context, CancellationToken cancellationToken)
    {
        var abandoned = new List<long>();
        foreach (ContentInstance instance in await _content.ActiveByKindAsync(Kind, cancellationToken))
        {
            if (instance.Owner != NoPilotOwner && instance.Data<DungeonInstanceData>() is { } data && !_spawner.IsPilotInSystem(instance.Owner, data.SolarSystemId)
                && await _content.AbandonAsync(context.Connection, context.Transaction, instance.Id, AbandonReason, cancellationToken))
            {
                abandoned.Add(instance.Id);
                _log?.Invoke($"dungeon {instance.Definition} (instance {instance.Id}) of pilot {instance.Owner} abandoned: the pilot is not in system {data.SolarSystemId}");
            }
        }
        var withNpcs = new List<long>();
        await using (var command = Sql.Command(context.Connection, context.Transaction, Q("SELECT DISTINCT instance_id FROM {schema}.dungeon_wave_npcs")))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                withNpcs.Add(reader.GetInt64(0));
            }
        }
        var removed = new List<long>();
        foreach (long instanceId in withNpcs)
        {
            if (!abandoned.Contains(instanceId) && await _content.GetAsync(instanceId, cancellationToken) is { State: "active" })
            {
                continue;
            }
            await using var command = Sql.Command(context.Connection, context.Transaction, Q("DELETE FROM {schema}.dungeon_wave_npcs WHERE instance_id = $1 RETURNING ship_id"), instanceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                removed.Add(reader.GetInt64(0));
            }
        }
        if (removed.Count > 0)
        {
            _spawner.Remove(removed);
        }
    }
}
