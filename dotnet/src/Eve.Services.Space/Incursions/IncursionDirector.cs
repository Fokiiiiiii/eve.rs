using System.Text.Json.Nodes;
using Eve.Content;
using Eve.Data;
using Eve.Dungeons.Incursions;
using Eve.Scheduler;
using Eve.Services.Space.Waves;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Space.Incursions;

/// <summary>
/// The lifecycle of the Sansha incursions (B-L1) and their staging sites (B-S1). Each band keeps its capacity of incursions: when one ends, the band's next one is
/// scheduled 12 to 36 hours later. An incursion is mobilized for 48 hours, established for a day and withdrawing for a day, then it is gone; each phase change is a durable
/// scheduler job that checks the phase it leaves before it moves on, so a repeated or late job changes nothing. The state is in PostgreSQL (<c>incursions</c>), so a
/// restart resumes the phases where they were. Its staging sites are dungeons of the wave runtime (<see cref="DungeonWaveDirector"/>), owned by no pilot: they live as long as
/// their incursion and are ended with it, and their NPCs are taken out by the director's sweep.
/// </summary>
public sealed class IncursionDirector
{
    public const string SpawnJob = "incursion.spawn";
    public const string SitesJob = "incursion.sites";
    public const string PhaseJob = "incursion.phase";

    /// <summary>When a band has no free constellation to spawn in, it tries again after this long.</summary>
    public static readonly TimeSpan NoConstellationRetry = TimeSpan.FromHours(1);

    /// <summary>The site origins are drawn within this distance of the system's origin, in metres.</summary>
    private const double SiteSpread = 50_000;

    private const string MigrationText = """
        CREATE TABLE incursions (
            id bigserial PRIMARY KEY,
            band text NOT NULL,
            constellation_id integer NOT NULL,
            staging_system_id integer NOT NULL,
            systems integer[] NOT NULL,
            phase text NOT NULL,
            phase_since_ms bigint NOT NULL,
            phase_due_ms bigint NOT NULL,
            influence double precision NOT NULL,
            reward_group_id bigint NOT NULL,
            pending_lp bigint NOT NULL DEFAULT 0,
            started_ms bigint NOT NULL,
            ended_ms bigint,
            end_reason text
        );
        CREATE INDEX incursions_open ON incursions (band) WHERE ended_ms IS NULL;
        CREATE TABLE incursion_sites (
            incursion_id bigint NOT NULL REFERENCES incursions (id),
            site_index integer NOT NULL,
            dungeon_id bigint NOT NULL,
            origin_x double precision NOT NULL,
            origin_y double precision NOT NULL,
            origin_z double precision NOT NULL,
            instance_id bigint NOT NULL,
            PRIMARY KEY (incursion_id, site_index)
        );
        """;

    private sealed record SpawnDue(string Band);

    private sealed record PhaseDue(long IncursionId, string From);

    private sealed record IncursionRef(long IncursionId);

    private readonly NpgsqlDataSource _source;
    private readonly ContentRuntime _content;
    private readonly DurableScheduler _scheduler;
    private readonly DungeonWaveDirector _sites;
    private readonly string _schema;
    private readonly Func<double> _random;

    /// <summary>The constellations of each band (the ones with a system of that band), each with its systems, in id order.</summary>
    private readonly Dictionary<IncursionBand, List<int>> _constellationsOfBand = [];
    private readonly Dictionary<int, List<SolarSystemInfo>> _systemsOfConstellation = [];

    public IncursionDirector(
        NpgsqlDataSource source, ContentRuntime content, DurableScheduler scheduler, DungeonWaveDirector sites, SolarSystemCatalog systems, string schema,
        Func<double>? random = null)
    {
        ArgumentNullException.ThrowIfNull(systems);
        (_source, _content, _scheduler, _sites) = (source, content, scheduler, sites);
        _schema = Sql.CheckSchema(schema);
        _random = random ?? Random.Shared.NextDouble;
        foreach (SolarSystemInfo system in systems.Systems.OrderBy(s => s.Id))
        {
            if (system.ConstellationId <= 0)
            {
                continue;
            }
            if (!_systemsOfConstellation.TryGetValue(system.ConstellationId, out var list))
            {
                _systemsOfConstellation[system.ConstellationId] = list = [];
            }
            list.Add(system);
        }
        foreach (var (constellation, members) in _systemsOfConstellation)
        {
            foreach (IncursionBand band in Enum.GetValues<IncursionBand>())
            {
                if (members.Any(s => IncursionRules.BandOf(s.Security) == band))
                {
                    if (!_constellationsOfBand.TryGetValue(band, out var ids))
                    {
                        _constellationsOfBand[band] = ids = [];
                    }
                    ids.Add(constellation);
                }
            }
        }
    }

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>Creates the director's tables in the schema (the space schema of the host). Applied once; a second start changes nothing.</summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema, CancellationToken cancellationToken = default) =>
        Eve.Data.Migrations.ApplyAsync(source, schema, [new Eve.Data.Migration("001_incursions", MigrationText)], cancellationToken);

    /// <summary>
    /// Registers the jobs, and fills each band that has never had an incursion to its capacity. Call it before the scheduler starts.
    /// </summary>
    public async Task AttachAsync()
    {
        _scheduler.Register(SpawnJob, (context, token) => SpawnAsync(context, token));
        _scheduler.Register(PhaseJob, (context, token) => PhaseAsync(context, token));
        _scheduler.Register(SitesJob, (context, token) => SitesAsync(context, token));
        await using var connection = await _source.OpenConnectionAsync();
        foreach (IncursionBand band in Enum.GetValues<IncursionBand>())
        {
            // a band that has had an incursion keeps its respawn timers across a restart: only a band never used is filled
            if ((long)(await Sql.ScalarAsync(connection, null, Q("SELECT count(*) FROM {schema}.incursions WHERE band = $1"), CancellationToken.None, IncursionRules.NameOf(band)))! > 0)
            {
                continue;
            }
            for (var slot = 0; slot < IncursionRules.Capacity(band); slot++)
            {
                await _scheduler.ScheduleAsync(new JobSpec(SpawnJob, _scheduler.Now)
                {
                    Key = $"fill:{IncursionRules.NameOf(band)}:{slot}",
                    Payload = new SpawnDue(IncursionRules.NameOf(band)),
                    OnDuplicate = OnDuplicate.Keep,
                });
            }
        }
    }

    /// <summary>The incursions that are not gone, as the map reads them (the report row of the client, see <c>BuildIncursionReport</c>).</summary>
    public async Task<IReadOnlyList<JsonObject>> ReadActiveAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<JsonObject>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Q("""
            SELECT id, staging_system_id, systems, phase, influence, reward_group_id, started_ms FROM {schema}.incursions
            WHERE ended_ms IS NULL ORDER BY id
            """), connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var staging = reader.GetInt32(1);
            var systems = reader.GetFieldValue<int[]>(2);
            var phase = IncursionRules.PhaseNamed(reader.GetString(3)) ?? IncursionPhase.Mobilized;
            var startedMs = reader.GetInt64(6);
            rows.Add(new JsonObject
            {
                ["taleID"] = id,
                ["templateClassID"] = IncursionRules.TemplateClassId,
                ["templateNameID"] = 0L,
                ["stagingSolarSystemID"] = (long)staging,
                ["aggressorFactionID"] = IncursionRules.AggressorFactionId,
                ["state"] = (long)IncursionRules.StateOf(phase),
                ["influence"] = reader.GetDouble(4),
                // FILETIME of the influence's last change (the client's wallclock): the influence has not changed since the incursion began
                ["lastUpdated"] = (startedMs + 11_644_473_600_000L) * 10_000L,
                ["decayRate"] = 0d,
                ["graceTime"] = 0d,
                ["hasFinalEncounter"] = false,
                ["effects"] = new JsonArray(),
                ["rewardGroupID"] = reader.GetInt64(5),
                ["incursedSystems"] = new JsonArray(systems.Select(system => (JsonNode)JsonValue.Create((long)system)!).ToArray()),
                // only the staging system is a row of the systems in incursions: the other systems have no site yet to show
                ["systems"] = new JsonArray(new JsonObject
                {
                    ["locationID"] = (long)staging,
                    ["sceneType"] = (long)IncursionRules.StagingSceneType,
                    ["templateNameID"] = 0L,
                }),
                ["hasChat"] = false,
                ["chatAnnouncementMessageId"] = 0L,
            });
        }
        return rows;
    }

    /// <summary>The loyalty points pending on each of the reward groups that a live incursion uses (<c>rewardMgr.GetDelayedRewardsByGroupIDs</c>).</summary>
    public async Task<IReadOnlyDictionary<long, long>> PendingLoyaltyPointsAsync(IReadOnlyList<long> rewardGroupIds, CancellationToken cancellationToken = default)
    {
        var pending = new Dictionary<long, long>();
        if (rewardGroupIds.Count == 0)
        {
            return pending;
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Q("""
            SELECT reward_group_id, sum(pending_lp)::bigint FROM {schema}.incursions
            WHERE ended_ms IS NULL AND reward_group_id = ANY($1) GROUP BY reward_group_id
            """), connection);
        command.Parameters.AddWithValue(rewardGroupIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending[reader.GetInt64(0)] = reader.GetInt64(1);
        }
        return pending;
    }

    private async Task SpawnAsync(JobContext context, CancellationToken cancellationToken)
    {
        SpawnDue due = context.Payload<SpawnDue>()!;
        if (IncursionRules.BandNamed(due.Band) is not { } band)
        {
            return;
        }
        // the spawn decisions are taken one at a time: two bands' jobs must not pick the same constellation, or overfill a band
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtext('eve.incursion.spawn'))", cancellationToken);
        long open = (long)(await Sql.ScalarAsync(context.Connection, context.Transaction,
            Q("SELECT count(*) FROM {schema}.incursions WHERE band = $1 AND ended_ms IS NULL"), cancellationToken, due.Band))!;
        if (open >= IncursionRules.Capacity(band))
        {
            return;
        }
        var busy = new HashSet<int>();
        await using (var command = Sql.Command(context.Connection, context.Transaction, Q("SELECT constellation_id FROM {schema}.incursions WHERE ended_ms IS NULL")))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                busy.Add(reader.GetInt32(0));
            }
        }
        var free = (_constellationsOfBand.TryGetValue(band, out var candidates) ? candidates : new List<int>()).Where(c => !busy.Contains(c)).ToList();
        if (free.Count == 0)
        {
            await context.ScheduleAsync(new JobSpec(SpawnJob, context.NowMs + (long)NoConstellationRetry.TotalMilliseconds)
            {
                Key = $"retry:{due.Band}:{context.NowMs}",
                Payload = due,
            }, cancellationToken);
            return;
        }
        int constellation = free[Pick(free.Count)];
        var members = _systemsOfConstellation[constellation];
        var inBand = members.Where(s => IncursionRules.BandOf(s.Security) == band).ToList();
        int staging = inBand[Pick(inBand.Count)].Id;
        int[] systems = members.Select(s => s.Id).ToArray();
        long nowMs = context.NowMs;
        long id = (long)(await Sql.ScalarAsync(context.Connection, context.Transaction, Q("""
            INSERT INTO {schema}.incursions (band, constellation_id, staging_system_id, systems, phase, phase_since_ms, phase_due_ms, influence, reward_group_id, started_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $6) RETURNING id
            """), cancellationToken, due.Band, constellation, staging, systems, IncursionRules.NameOf(IncursionPhase.Mobilized), nowMs,
            nowMs + (long)IncursionRules.MobilizedLength.TotalMilliseconds, IncursionRules.FullInfluence, IncursionRules.StagingRewardGroupId))!;
        await context.ScheduleAsync(PhaseSpec(id, IncursionPhase.Mobilized, nowMs + (long)IncursionRules.MobilizedLength.TotalMilliseconds), cancellationToken);
        await context.ScheduleAsync(new JobSpec(SitesJob, nowMs) { Key = $"sites:{id}", Payload = new IncursionRef(id) }, cancellationToken);
    }

    private async Task PhaseAsync(JobContext context, CancellationToken cancellationToken)
    {
        PhaseDue due = context.Payload<PhaseDue>()!;
        if (IncursionRules.PhaseNamed(due.From) is not { } from || IncursionRules.LengthOf(from) is null)
        {
            return;
        }
        await using var command = Sql.Command(context.Connection, context.Transaction,
            Q("SELECT phase, band FROM {schema}.incursions WHERE id = $1 FOR UPDATE"), due.IncursionId);
        string? phase = null;
        string band = "";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                (phase, band) = (reader.GetString(0), reader.GetString(1));
            }
        }
        // a phase is left once: a job for a phase the incursion is no longer in changes nothing
        if (phase != IncursionRules.NameOf(from))
        {
            return;
        }
        // the time the phase was due (not the time the job ran): a late run does not stretch the phases
        long nowMs = context.ScheduledForMs;
        if (from == IncursionPhase.Withdrawing)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction,
                Q("UPDATE {schema}.incursions SET phase = $2, phase_since_ms = $3, phase_due_ms = $3, ended_ms = $3, end_reason = $4 WHERE id = $1"),
                cancellationToken, due.IncursionId, IncursionRules.NameOf(IncursionPhase.Ended), nowMs, "withdrawn");
            var sites = new List<long>();
            await using (var sitesCommand = Sql.Command(context.Connection, context.Transaction,
                Q("SELECT instance_id FROM {schema}.incursion_sites WHERE incursion_id = $1"), due.IncursionId))
            await using (var sitesReader = await sitesCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await sitesReader.ReadAsync(cancellationToken))
                {
                    sites.Add(sitesReader.GetInt64(0));
                }
            }
            foreach (long instance in sites)
            {
                await _content.AbandonAsync(context.Connection, context.Transaction, instance, $"incursion {due.IncursionId} ended", cancellationToken);
            }
            await context.ScheduleAsync(new JobSpec(SpawnJob, nowMs + (long)IncursionRules.RespawnDelay(_random()).TotalMilliseconds)
            {
                Key = $"respawn:{due.IncursionId}",
                Payload = new SpawnDue(band),
            }, cancellationToken);
            return;
        }
        var next = from == IncursionPhase.Mobilized ? IncursionPhase.Established : IncursionPhase.Withdrawing;
        long dueMs = nowMs + (long)IncursionRules.LengthOf(next)!.Value.TotalMilliseconds;
        await Sql.ExecuteAsync(context.Connection, context.Transaction,
            Q("UPDATE {schema}.incursions SET phase = $2, phase_since_ms = $3, phase_due_ms = $4 WHERE id = $1"),
            cancellationToken, due.IncursionId, IncursionRules.NameOf(next), nowMs, dueMs);
        await context.ScheduleAsync(PhaseSpec(due.IncursionId, next, dueMs), cancellationToken);
    }

    /// <summary>
    /// The staging sites of an incursion: <see cref="IncursionRules.StagingSiteCount"/> dungeons of the staging dungeon list, each started once (its unique key is its
    /// incursion and its index), so a repeated job finds the sites it started. A site that is refused stops the job, which the scheduler retries.
    /// </summary>
    private async Task SitesAsync(JobContext context, CancellationToken cancellationToken)
    {
        IncursionRef due = context.Payload<IncursionRef>()!;
        await using var command = Sql.Command(context.Connection, context.Transaction,
            Q("SELECT staging_system_id, phase FROM {schema}.incursions WHERE id = $1 FOR SHARE"), due.IncursionId);
        int staging;
        string phase;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }
            (staging, phase) = (reader.GetInt32(0), reader.GetString(1));
        }
        if (phase == IncursionRules.NameOf(IncursionPhase.Ended))
        {
            return;
        }
        var made = new Dictionary<int, (long Dungeon, double[] Origin)>();
        await using (var existing = Sql.Command(context.Connection, context.Transaction,
            Q("SELECT site_index, dungeon_id, origin_x, origin_y, origin_z FROM {schema}.incursion_sites WHERE incursion_id = $1"), due.IncursionId))
        await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                made[reader.GetInt32(0)] = (reader.GetInt64(1), new[] { reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4) });
            }
        }
        for (var index = 0; index < IncursionRules.StagingSiteCount; index++)
        {
            var (dungeon, origin) = made.TryGetValue(index, out var site)
                ? site
                : (IncursionRules.StagingDungeonIds[Pick(IncursionRules.StagingDungeonIds.Count)], RandomOrigin());
            var start = await _sites.StartForSystemAsync(staging, origin, dungeon, $"incursion:{due.IncursionId}:site:{index}", IncursionRules.MaximumLifetime, cancellationToken);
            if (start.InstanceId is not { } instance)
            {
                throw new InvalidOperationException($"incursion {due.IncursionId} staging site {index} (dungeon {dungeon}): {start.Refusal}");
            }
            await Sql.ExecuteAsync(context.Connection, context.Transaction,
                Q("""
                INSERT INTO {schema}.incursion_sites (incursion_id, site_index, dungeon_id, origin_x, origin_y, origin_z, instance_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (incursion_id, site_index) DO NOTHING
                """),
                cancellationToken, due.IncursionId, index, dungeon, origin[0], origin[1], origin[2], instance);
        }
    }

    private JobSpec PhaseSpec(long incursionId, IncursionPhase from, long runAtMs) =>
        new(PhaseJob, runAtMs) { Key = $"phase:{incursionId}:{IncursionRules.NameOf(from)}", Payload = new PhaseDue(incursionId, IncursionRules.NameOf(from)) };

    private int Pick(int count) => Math.Min(count - 1, (int)Math.Floor(_random() * count));

    private double[] RandomOrigin() => [Offset(), Offset(), Offset()];

    private double Offset() => (_random() * 2 - 1) * SiteSpread;
}
