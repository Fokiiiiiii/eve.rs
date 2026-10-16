using System.Text.RegularExpressions;
using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Space.Entosis;

/// <summary>What the sovereignty records say about a system's Sovereignty Hub at a cycle: its item, its owner's alliance (null for a neutral hub), the activity
/// multiplier a capture starts with (1 for a neutral hub) and the owner's prime hour (0 for a neutral hub; the centre of the owner's daily window).</summary>
public sealed record EntosisHubReading(long HubId, int SolarSystemId, long? DefenderAllianceId, double ActivityDefenseMultiplier, int PrimeHour = 0);

/// <summary>The payload of <see cref="EntosisCaptureService.CaptureStartedTopic"/>: the owner is told that a capture of its hub has started.</summary>
public sealed record EntosisCaptureStarted(long HubId, int SolarSystemId, long DefenderAllianceId, long ModuleId, long AtMs);

/// <summary>The payload of <see cref="EntosisCaptureService.ReinforcedTopic"/>: the owned hub is reinforced until <see cref="ReinforcementExitMs"/>.</summary>
public sealed record EntosisHubReinforced(long HubId, int SolarSystemId, long DefenderAllianceId, long ReinforcementExitMs, long AtMs);

/// <summary>A captured neutral hub and the alliance and corporation that captured it: what the handover gives the system to (ST-9 d).</summary>
public sealed record EntosisCapturedHub(long HubId, int SolarSystemId, long CorporationId, long AllianceId);

/// <summary>The answer of a handover: the system is claimed by the capturer, or another alliance claimed it first.</summary>
public enum EntosisHandover : byte
{
    Claimed = 0,
    Superseded = 1,
}

/// <summary>
/// The capture of Sovereignty Hubs by entosis links (ST-9 c, d). Each cycle a link settles is a durable fact: the hub's progress is moved to the cycle's time with
/// the links that were active (<see cref="EntosisRules.Advance"/>), the link is recorded, and the completion is a durable job (<see cref="CaptureDueKind"/>) at the
/// time the capture would complete. A link's end (its module's end report) moves the progress to that time and removes the link. Every event is one PostgreSQL
/// transaction, so a restart keeps the progress as of the last event.
/// A capture of a neutral hub completes into a handover: the sovereignty records give the system to the capturer (the handover callback, through the sovereignty
/// write path), after the commit. A handover that fails is retried by a job (<see cref="HandoverDueKind"/>). An owned hub's completion is a reinforcement with its
/// timer (the wiki); the command nodes that follow are not built. A cycle is refused by name outside the owner's vulnerability window (ST-9 e), and the owner's
/// notices (capture started, reinforced) are events written in the capture's transaction (the subscriber writes them after the commit).
/// </summary>
public sealed partial class EntosisCaptureService
{
    /// <summary>The schema of the capture's tables (the migrations are applied to it).</summary>
    public const string DefaultSchema = "entosis";

    /// <summary>The durable job kind of a capture's completion; its key is the hub.</summary>
    public const string CaptureDueKind = "space.entosis.capture-due";

    /// <summary>The durable job kind of a handover's retry; its key is the hub.</summary>
    public const string HandoverDueKind = "space.entosis.handover-due";

    /// <summary>The topic of a capture's start (the owner is told; ST-9 e).</summary>
    public const string CaptureStartedTopic = "space.entosis.capture_started";

    /// <summary>The topic of an owned hub's reinforcement (the owner is told; ST-9 e).</summary>
    public const string ReinforcedTopic = "space.entosis.hub_reinforced";

    /// <summary>How long a failed handover waits before it is tried again: one minute.</summary>
    public const long HandoverRetryMs = 60_000;

    private static readonly Regex SchemaName = new("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly DurableScheduler _scheduler;
    private readonly Func<long> _now;
    private readonly Func<int, CancellationToken, Task<EntosisHubReading?>> _readHub;
    private readonly Func<EntosisCapturedHub, CancellationToken, Task<EntosisHandover>>? _handover;
    private readonly Action<string>? _log;
    private readonly EventBus? _events;
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<int>>> _constellationSystems;
    private readonly Func<int, CancellationToken, Task<int>> _constellationOf;
    private readonly Func<EntosisDestroyedHub, CancellationToken, Task<bool>>? _destroyHub;

    /// <param name="readHub">The system's Sovereignty Hub as the records have it at this moment (null when the system has none).</param>
    /// <param name="handover">The claim of a captured neutral hub by its capturer (ST-9 d). Unset, a capture completes and waits for one.</param>
    /// <param name="events">Where the owner's notices are published (ST-9 e), in the capture's transaction. Unset, no notice is published.</param>
    public EntosisCaptureService(
        NpgsqlDataSource source, DurableScheduler scheduler, Func<long> now, Func<int, CancellationToken, Task<EntosisHubReading?>> readHub,
        Func<EntosisCapturedHub, CancellationToken, Task<EntosisHandover>>? handover = null, string schema = DefaultSchema, Action<string>? log = null,
        EventBus? events = null, Func<int, CancellationToken, Task<IReadOnlyList<int>>>? constellationSystems = null,
        Func<int, CancellationToken, Task<int>>? constellationOf = null, Func<EntosisDestroyedHub, CancellationToken, Task<bool>>? destroyHub = null)
    {
        if (!SchemaName.IsMatch(schema))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _scheduler = scheduler;
        _now = now;
        _readHub = readHub;
        _handover = handover;
        _schema = schema;
        _log = log;
        _events = events;
        _constellationSystems = constellationSystems ?? ((solarSystemId, _) => Task.FromResult<IReadOnlyList<int>>(new[] { solarSystemId }));
        _constellationOf = constellationOf ?? ((_, _) => Task.FromResult(0));
        _destroyHub = destroyHub;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(EntosisCaptureService).Assembly, "EntosisMigrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>The payload of a hub's job (completion, or handover retry).</summary>
    public sealed record HubDue(long HubId);

    /// <summary>The answer of a settled cycle: accepted, or the reason it is not (the module ends with it).</summary>
    public sealed record SettleResult(bool Accepted, string Reason);

    /// <summary>The answer of a window change: accepted with the time it takes effect (0 for an alliance default, which applies at once), or the reason it is refused.</summary>
    public sealed record WindowAnswer(bool Accepted, string Reason, long EffectiveFromMs);

    /// <summary>Registers the jobs' handlers. Call it before the scheduler starts.</summary>
    public void Register()
    {
        _scheduler.Register(CaptureDueKind, (context, token) => CaptureDueAsync(context.Payload<HubDue>()!.HubId, token));
        _scheduler.Register(HandoverDueKind, (context, token) => HandoverDueAsync(context.Payload<HubDue>()!.HubId, token));
        _scheduler.Register(CampaignStartKind, (context, token) => CampaignStartAsync(context.Payload<HubDue>()!.HubId, token));
        _scheduler.Register(NodeSpawnKind, (context, token) => NodeSpawnDueAsync(context.Payload<HubDue>()!.HubId, token));
        _scheduler.Register(NodeDueKind, (context, token) => NodeDueAsync(context.Payload<CommandNodeDue>()!.NodeId, token));
        _scheduler.Register(HubDestroyDueKind, (context, token) => HubDestroyDueAsync(context.Payload<HubDue>()!.HubId, token));
    }

    /// <summary>
    /// A link's cycle ended with its hub in reach (the simulation checked the lock and the reach). The first cycle only records the link (the warm-up); a later
    /// one moves the capture to now. Refused by name when the system's hub is not the target, or the capture is complete.
    /// </summary>
    public async Task<SettleResult> SettleCycleAsync(
        long moduleId, long characterId, long corporationId, long allianceId, long hubId, int solarSystemId, CancellationToken cancellationToken)
    {
        var reading = await _readHub(solarSystemId, cancellationToken);
        if (reading is null || reading.HubId != hubId)
        {
            // a command node of an active stage is the other target (ST-17 a); anything else is refused by name
            return await SettleNodeCycleAsync(moduleId, characterId, corporationId, allianceId, hubId, cancellationToken)
                ?? new SettleResult(false, "ENTOSIS_TARGET_NOT_SOV_HUB");
        }
        var now = _now();
        var side = reading.DefenderAllianceId is { } owner && owner == allianceId ? EntosisSide.Defender : EntosisSide.Attacker;

        bool completed;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var stored = await LoadStateAsync(connection, transaction, hubId, forUpdate: true, cancellationToken);
            var state = stored ?? new EntosisCaptureState(
                hubId, solarSystemId, 0, now, reading.ActivityDefenseMultiplier, reading.DefenderAllianceId, EntosisPhase.Idle, reading.PrimeHour);
            // an idle hub takes the current multiplier, owner and prime hour when a capture starts; a capture in progress keeps the ones it started with
            if (state.Phase == EntosisPhase.Idle)
            {
                state = state with
                {
                    ActivityDefenseMultiplier = reading.ActivityDefenseMultiplier, DefenderAllianceId = reading.DefenderAllianceId, PrimeHour = reading.PrimeHour,
                };
            }
            if (state.Phase == EntosisPhase.Captured)
            {
                return new SettleResult(false, "ENTOSIS_CAPTURE_COMPLETE");
            }
            if (state.Phase == EntosisPhase.Reinforced)
            {
                return new SettleResult(false, "SOVEREIGNTY_HUB_ALREADY_REINFORCED");
            }
            if (state.Phase == EntosisPhase.Destroyed)
            {
                return new SettleResult(false, "SOVEREIGNTY_HUB_DESTROYED");
            }
            if (await ReadInvulnerableUntilAsync(connection, transaction, hubId, cancellationToken) is { } shieldedUntil && shieldedUntil > now)
            {
                return new SettleResult(false, "SOVEREIGNTY_TARGET_NOT_VULNERABLE");
            }
            var window = await LoadWindowAsync(connection, transaction, hubId, forUpdate: true, cancellationToken);
            var locks = await LoadLocksAsync(connection, transaction, hubId, cancellationToken);
            var newLocks = new List<EntosisWindowLock>();
            var windowOpen = EntosisWindow.IsOpen(reading.PrimeHour, reading.ActivityDefenseMultiplier, now, window.At(now), locks, newLocks);
            if (!EntosisRules.IsVulnerable(reading, state, windowOpen))
            {
                return new SettleResult(false, "SOVEREIGNTY_TARGET_NOT_VULNERABLE");
            }
            // only an accepted cycle keeps what the evaluation took: a due change becomes the active setting, and the window lengths are locked
            if (window.Promotes(now))
            {
                await WriteWindowAsync(connection, transaction, hubId, window.At(now), null, null, cancellationToken);
            }
            await SaveLocksAsync(connection, transaction, hubId, newLocks, now, cancellationToken);
            var links = await LoadLinksAsync(connection, transaction, hubId, cancellationToken);
            // the first attacker cycle of a hub with no capture running starts it (the warm-up): the owner is told (EVE.js notifySovCaptureStarted)
            var defenderAlliance = reading.DefenderAllianceId ?? 0;
            var starts = side == EntosisSide.Attacker && defenderAlliance > 0 && state.Phase == EntosisPhase.Idle
                && !links.Any(link => link.ModuleId == moduleId)
                && !links.Any(link => link.Side == EntosisSide.Attacker && EntosisRules.IsActive(link, now));
            var advanced = EntosisRules.Advance(state with { DefenderAllianceId = reading.DefenderAllianceId }, links, now);
            await SaveStateAsync(connection, transaction, advanced, cancellationToken);
            var settling = new EntosisLink(moduleId, characterId, corporationId, allianceId, side, now, now);
            await UpsertLinkAsync(connection, transaction, hubId, settling, cancellationToken);
            await DeleteStaleLinksAsync(connection, transaction, hubId, now, cancellationToken);
            var after = await LoadLinksAsync(connection, transaction, hubId, cancellationToken);
            completed = await CompleteAsync(connection, transaction, state, advanced, side == EntosisSide.Attacker ? settling : null, now, cancellationToken);
            await ReconcileJobAsync(connection, transaction, advanced, after, now, cancellationToken);
            if (starts && _events is not null)
            {
                await _events.PublishAsync(connection, transaction, CaptureStartedTopic,
                    new EntosisCaptureStarted(hubId, solarSystemId, defenderAlliance, moduleId, now), hubId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        _events?.Wake();
        if (completed)
        {
            _log?.Invoke($"entosis: hub {hubId} of system {solarSystemId} is captured at {now}");
            await RunHandoverAsync(hubId, cancellationToken);
        }
        return new SettleResult(true, "");
    }

    /// <summary>A module that ran an entosis link has ended (its end report): its link stops counting, and the progress is moved to now first.</summary>
    public async Task LinkEndedAsync(long moduleId, CancellationToken cancellationToken)
    {
        long? hubId;
        bool completed = false;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            hubId = await HubOfLinkAsync(connection, transaction, moduleId, cancellationToken);
            var state = hubId is { } hub ? await LoadStateAsync(connection, transaction, hub, forUpdate: true, cancellationToken) : null;
            if (hubId is { } nodeId && state is null)
            {
                // a command node's link (ST-17 a): its progress is moved to now, and the link is removed
                await NodeLinkEndedAsync(connection, transaction, nodeId, moduleId, _now(), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (hubId is { } id && state is not null)
            {
                var now = _now();
                var links = await LoadLinksAsync(connection, transaction, id, cancellationToken);
                var advanced = EntosisRules.Advance(state, links, now);
                await SaveStateAsync(connection, transaction, advanced, cancellationToken);
                await DeleteLinkAsync(connection, transaction, moduleId, cancellationToken);
                var after = await LoadLinksAsync(connection, transaction, id, cancellationToken);
                completed = await CompleteAsync(connection, transaction, state, advanced, MostRecentAttacker(links), now, cancellationToken);
                await ReconcileJobAsync(connection, transaction, advanced, after, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        if (completed)
        {
            _log?.Invoke($"entosis: hub {hubId} is captured at the end of link {moduleId}");
            await RunHandoverAsync(hubId!.Value, cancellationToken);
        }
    }

    /// <summary>The completion job: the progress is moved to now; a capture that reached its end is captured and handed over.</summary>
    public async Task CaptureDueAsync(long hubId, CancellationToken cancellationToken)
    {
        bool completed = false;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var state = await LoadStateAsync(connection, transaction, hubId, forUpdate: true, cancellationToken);
            if (state is not null && !EntosisRules.IsDone(state.Phase))
            {
                var now = _now();
                var links = await LoadLinksAsync(connection, transaction, hubId, cancellationToken);
                var advanced = EntosisRules.Advance(state, links, now);
                await SaveStateAsync(connection, transaction, advanced, cancellationToken);
                await DeleteStaleLinksAsync(connection, transaction, hubId, now, cancellationToken);
                var after = await LoadLinksAsync(connection, transaction, hubId, cancellationToken);
                completed = await CompleteAsync(connection, transaction, state, advanced, MostRecentAttacker(links), now, cancellationToken);
                await ReconcileJobAsync(connection, transaction, advanced, after, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        if (completed)
        {
            _log?.Invoke($"entosis: hub {hubId} is captured (the capture's completion job)");
            await RunHandoverAsync(hubId, cancellationToken);
        }
    }

    /// <summary>The handover's retry job: the claim is tried again while the hub's handover is pending.</summary>
    public Task HandoverDueAsync(long hubId, CancellationToken cancellationToken) => RunHandoverAsync(hubId, cancellationToken);

    /// <summary>The capture of a hub as it is stored, or null when no link has touched it.</summary>
    public async Task<EntosisCaptureState?> ReadAsync(long hubId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await LoadStateAsync(connection, null, hubId, forUpdate: false, cancellationToken);
    }

    /// <summary>The links of a hub as they are stored (including the ones that stopped counting but are not yet removed).</summary>
    public async Task<IReadOnlyList<EntosisLink>> LinksAsync(long hubId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await LoadLinksAsync(connection, null, hubId, cancellationToken);
    }

    /// <summary>The handover of a captured hub: null until its capture completes; then pending, claimed, superseded or owned.</summary>
    public async Task<string?> HandoverAsync(long hubId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command("SELECT handover FROM {schema}.hubs WHERE hub_id = $1", connection, null, hubId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>The attacker link that moved the capture last: the one that completes it.</summary>
    private static EntosisLink? MostRecentAttacker(IReadOnlyList<EntosisLink> links) =>
        links.Where(link => link.Side == EntosisSide.Attacker).OrderByDescending(link => link.LastCycleMs).FirstOrDefault();

    /// <summary>
    /// The completion of a capture, in its transaction. A neutral hub becomes captured: it records who captured it, and its handover is pending to the capturer
    /// (or superseded when there is none). An owned hub becomes reinforced: its timer is set (the wiki: 48 hours, then the next vulnerability window) and the owner
    /// is told; the command nodes that follow are not built. Returns whether it completed.
    /// </summary>
    private async Task<bool> CompleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, EntosisCaptureState before, EntosisCaptureState after, EntosisLink? capturer, long now,
        CancellationToken cancellationToken)
    {
        if (EntosisRules.IsDone(before.Phase) || after.Phase != EntosisPhase.Captured)
        {
            return false;
        }
        if (after.DefenderAllianceId is { } owner)
        {
            var setting = (await LoadWindowAsync(connection, transaction, after.HubId, forUpdate: false, cancellationToken)).At(now);
            var exitMs = EntosisWindow.ReinforcementExit(after.PrimeHour, after.ActivityDefenseMultiplier, now, after.HubId, setting);
            await using var reinforce = Command(
                "UPDATE {schema}.hubs SET phase = 'reinforced', handover = 'owned', reinforcement_exit_ms = $2 WHERE hub_id = $1",
                connection, transaction, after.HubId, exitMs);
            await reinforce.ExecuteNonQueryAsync(cancellationToken);
            // ST-17 a: the command node stage starts when the reinforcement ends (the wiki: nodes spawn once the reinforce timer reaches 0)
            await _scheduler.ScheduleAsync(connection, transaction,
                new JobSpec(CampaignStartKind, exitMs) { Key = after.HubId.ToString(System.Globalization.CultureInfo.InvariantCulture), Payload = new HubDue(after.HubId), OnDuplicate = OnDuplicate.Replace },
                cancellationToken);
            if (_events is not null)
            {
                await _events.PublishAsync(connection, transaction, ReinforcedTopic,
                    new EntosisHubReinforced(after.HubId, after.SolarSystemId, owner, exitMs, now), after.HubId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken);
            }
            return true;
        }
        var handover = capturer is null ? "superseded" : "pending";
        await using var command = Command(
            "UPDATE {schema}.hubs SET handover = $2, captured_by_corporation_id = $3, captured_by_alliance_id = $4 WHERE hub_id = $1",
            connection, transaction, after.HubId, handover, capturer?.CorporationId, capturer?.AllianceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The handover of a captured neutral hub: the callback gives the system to the capturer (the sovereignty write path), and the answer is kept. A failure is
    /// logged and the handover is tried again by a job, a minute later.
    /// </summary>
    private async Task RunHandoverAsync(long hubId, CancellationToken cancellationToken)
    {
        if (_handover is null)
        {
            return;
        }
        string? handover;
        long? corporation, alliance;
        int solarSystemId;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var command = Command(
            "SELECT handover, captured_by_corporation_id, captured_by_alliance_id, solar_system_id FROM {schema}.hubs WHERE hub_id = $1", connection, null, hubId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }
            handover = reader.IsDBNull(0) ? null : reader.GetString(0);
            corporation = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            alliance = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            solarSystemId = reader.GetInt32(3);
        }
        if (handover != "pending" || corporation is not { } corporationId || alliance is not { } allianceId)
        {
            return;
        }
        EntosisHandover answer;
        try
        {
            answer = await _handover(new EntosisCapturedHub(hubId, solarSystemId, corporationId, allianceId), cancellationToken);
        }
        catch (Exception error)
        {
            _log?.Invoke($"entosis: the handover of hub {hubId} was not written: {error.Message}; it is tried again");
            await _scheduler.ScheduleAsync(new JobSpec(HandoverDueKind, _now() + HandoverRetryMs)
            {
                Key = hubId.ToString(System.Globalization.CultureInfo.InvariantCulture), Payload = new HubDue(hubId), OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
            return;
        }
        // the answer ends this capture: the hub is idle again (under its new owner, if it was claimed) and the links of the capture are removed
        await using var writer = await _source.OpenConnectionAsync(cancellationToken);
        await using var writerTransaction = await writer.BeginTransactionAsync(cancellationToken);
        await using var update = Command(
            "UPDATE {schema}.hubs SET handover = $2, phase = 'idle', progress = 0 WHERE hub_id = $1 AND handover = 'pending'", writer, writerTransaction,
            hubId, answer == EntosisHandover.Claimed ? "claimed" : "superseded");
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await using var clear = Command("DELETE FROM {schema}.links WHERE hub_id = $1", writer, writerTransaction, hubId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
            if (answer == EntosisHandover.Claimed)
            {
                // ST-9 f: the claimed hub takes its new owner's default window and loses the old one (the wiki: captured structures inherit it)
                await using var forget = Command("DELETE FROM {schema}.hub_windows WHERE hub_id = $1", writer, writerTransaction, hubId);
                await forget.ExecuteNonQueryAsync(cancellationToken);
                await using var inherit = Command(
                    "INSERT INTO {schema}.hub_windows (hub_id, centre_hour, length_hours) SELECT $1, centre_hour, length_hours FROM {schema}.alliance_windows WHERE alliance_id = $2",
                    writer, writerTransaction, hubId, allianceId);
                await inherit.ExecuteNonQueryAsync(cancellationToken);
                await using var locks = Command("DELETE FROM {schema}.window_locks WHERE hub_id = $1", writer, writerTransaction, hubId);
                await locks.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await writerTransaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The owner's custom window of a hub (ST-9 f). Refused by name for a hub that another or no alliance owns, and for a setting out of range. The change takes
    /// effect after the next vulnerability window (the wiki); the answer gives that time.
    /// </summary>
    public async Task<WindowAnswer> SetHubWindowAsync(long hubId, int solarSystemId, EntosisWindowSetting setting, CancellationToken cancellationToken)
    {
        var reading = await _readHub(solarSystemId, cancellationToken);
        if (reading is null || reading.HubId != hubId)
        {
            return new WindowAnswer(false, "ENTOSIS_TARGET_NOT_SOV_HUB", 0);
        }
        if (reading.DefenderAllianceId is null)
        {
            return new WindowAnswer(false, "SOVEREIGNTY_HUB_NOT_OWNED", 0);
        }
        if (!setting.IsValid)
        {
            return new WindowAnswer(false, "SOVEREIGNTY_VULNERABILITY_WINDOW_INVALID", 0);
        }
        var now = _now();
        long effective;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var window = await LoadWindowAsync(connection, transaction, hubId, forUpdate: true, cancellationToken);
            var locks = await LoadLocksAsync(connection, transaction, hubId, cancellationToken);
            var newLocks = new List<EntosisWindowLock>();
            effective = EntosisWindow.NextWindowEnd(reading.PrimeHour, reading.ActivityDefenseMultiplier, now, window.At(now), locks, newLocks);
            await WriteWindowAsync(connection, transaction, hubId, window.At(now), setting, effective, cancellationToken);
            await SaveLocksAsync(connection, transaction, hubId, newLocks, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return new WindowAnswer(true, "", effective);
    }

    /// <summary>
    /// The default window of an alliance (ST-9 f): a hub it captures inherits it (<see cref="RunHandoverAsync"/>). It applies to hubs captured after the change.
    /// </summary>
    public async Task<WindowAnswer> SetAllianceWindowAsync(long allianceId, EntosisWindowSetting setting, CancellationToken cancellationToken)
    {
        if (allianceId <= 0 || !setting.IsValid)
        {
            return new WindowAnswer(false, "SOVEREIGNTY_VULNERABILITY_WINDOW_INVALID", 0);
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(
            "INSERT INTO {schema}.alliance_windows (alliance_id, centre_hour, length_hours) VALUES ($1, $2, $3) "
            + "ON CONFLICT (alliance_id) DO UPDATE SET centre_hour = EXCLUDED.centre_hour, length_hours = EXCLUDED.length_hours",
            connection, null, allianceId, setting.CentreHour, setting.LengthHours);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new WindowAnswer(true, "", 0);
    }

    /// <summary>A hub's stored window: the active setting (null: the default window), and a pending change with the time it takes effect.</summary>
    private sealed record StoredWindow(EntosisWindowSetting? Active, EntosisWindowSetting? Pending, long? PendingFromMs)
    {
        /// <summary>The setting in force at a time: a pending change once it is due.</summary>
        public EntosisWindowSetting? At(long nowMs) => Promotes(nowMs) ? Pending : Active;

        public bool Promotes(long nowMs) => Pending is not null && PendingFromMs <= nowMs;
    }

    private async Task<StoredWindow> LoadWindowAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long hubId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT centre_hour, length_hours, pending_centre_hour, pending_length_hours, pending_from_ms FROM {schema}.hub_windows WHERE hub_id = $1"
            + (forUpdate ? " FOR UPDATE" : ""), connection, transaction, hubId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new StoredWindow(null, null, null);
        }
        return new StoredWindow(
            WindowOf(reader, 0, 1), WindowOf(reader, 2, 3), reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    private static EntosisWindowSetting? WindowOf(NpgsqlDataReader reader, int centreColumn, int lengthColumn) =>
        reader.IsDBNull(centreColumn) ? null : new EntosisWindowSetting(reader.GetInt16(centreColumn), reader.GetInt16(lengthColumn));

    private async Task WriteWindowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, EntosisWindowSetting? active, EntosisWindowSetting? pending,
        long? pendingFromMs, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "INSERT INTO {schema}.hub_windows (hub_id, centre_hour, length_hours, pending_centre_hour, pending_length_hours, pending_from_ms) "
            + "VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (hub_id) DO UPDATE SET centre_hour = EXCLUDED.centre_hour, length_hours = EXCLUDED.length_hours, "
            + "pending_centre_hour = EXCLUDED.pending_centre_hour, pending_length_hours = EXCLUDED.pending_length_hours, pending_from_ms = EXCLUDED.pending_from_ms",
            connection, transaction, hubId, active?.CentreHour, active?.LengthHours, pending?.CentreHour, pending?.LengthHours, pendingFromMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<long, long>> LoadLocksAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long hubId, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT centre_ms, duration_ms FROM {schema}.window_locks WHERE hub_id = $1", connection, transaction, hubId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var locks = new Dictionary<long, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            locks[reader.GetInt64(0)] = reader.GetInt64(1);
        }
        return locks;
    }

    private async Task SaveLocksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, IEnumerable<EntosisWindowLock> locks, long now, CancellationToken cancellationToken)
    {
        foreach (var locked in locks)
        {
            await using var command = Command(
                "INSERT INTO {schema}.window_locks (hub_id, centre_ms, duration_ms) VALUES ($1, $2, $3) ON CONFLICT (hub_id, centre_ms) DO NOTHING",
                connection, transaction, hubId, locked.CentreMs, locked.DurationMs);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        // a window that ended two days ago is not evaluated again (the window of a time is within a day of it)
        await using var prune = Command(
            "DELETE FROM {schema}.window_locks WHERE hub_id = $1 AND centre_ms < $2", connection, transaction, hubId, now - 2 * EntosisWindow.DayMs);
        await prune.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReconcileJobAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, EntosisCaptureState state, IReadOnlyList<EntosisLink> links, long now, CancellationToken cancellationToken)
    {
        var key = state.HubId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (EntosisRules.CompletionAt(state, links, now) is { } due)
        {
            await _scheduler.ScheduleAsync(connection, transaction,
                new JobSpec(CaptureDueKind, due) { Key = key, Payload = new HubDue(state.HubId), OnDuplicate = OnDuplicate.Replace }, cancellationToken);
        }
        else
        {
            await _scheduler.CancelAsync(connection, transaction, CaptureDueKind, key, cancellationToken);
        }
    }

    private NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction, params object?[] values)
    {
        var command = new NpgsqlCommand(sql.Replace("{schema}", _schema), connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value ?? DBNull.Value);
        }
        return command;
    }

    private async Task<EntosisCaptureState?> LoadStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long hubId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT hub_id, solar_system_id, progress, checkpoint_ms, activity_defense_multiplier, defender_alliance_id, phase, prime_hour FROM {schema}.hubs WHERE hub_id = $1"
            + (forUpdate ? " FOR UPDATE" : ""), connection, transaction, hubId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var phase = reader.GetString(6) switch
        {
            "idle" => EntosisPhase.Idle,
            "capturing" => EntosisPhase.Capturing,
            "captured" => EntosisPhase.Captured,
            "reinforced" => EntosisPhase.Reinforced,
            "destroyed" => EntosisPhase.Destroyed,
            var other => throw new InvalidOperationException($"unknown entosis phase {other}"),
        };
        return new EntosisCaptureState(
            reader.GetInt64(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetInt64(3), reader.GetDouble(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5), phase, reader.GetInt16(7));
    }

    private async Task SaveStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EntosisCaptureState state, CancellationToken cancellationToken)
    {
        var phase = state.Phase switch
        {
            EntosisPhase.Idle => "idle",
            EntosisPhase.Capturing => "capturing",
            EntosisPhase.Reinforced => "reinforced",
            EntosisPhase.Destroyed => "destroyed",
            _ => "captured",
        };
        await using var command = Command(
            "INSERT INTO {schema}.hubs (hub_id, solar_system_id, progress, checkpoint_ms, activity_defense_multiplier, defender_alliance_id, phase, prime_hour) "
            + "VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT (hub_id) DO UPDATE SET progress = EXCLUDED.progress, checkpoint_ms = EXCLUDED.checkpoint_ms, "
            + "defender_alliance_id = EXCLUDED.defender_alliance_id, phase = EXCLUDED.phase, prime_hour = EXCLUDED.prime_hour",
            connection, transaction, state.HubId, state.SolarSystemId, state.Progress, state.CheckpointMs, state.ActivityDefenseMultiplier, state.DefenderAllianceId, phase,
            state.PrimeHour);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<EntosisLink>> LoadLinksAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long hubId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT module_id, character_id, corporation_id, alliance_id, side, linked_ms, last_cycle_ms FROM {schema}.links WHERE hub_id = $1 ORDER BY module_id",
            connection, transaction, hubId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var links = new List<EntosisLink>();
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new EntosisLink(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetString(4) == "defender" ? EntosisSide.Defender : EntosisSide.Attacker,
                reader.GetInt64(5), reader.GetInt64(6)));
        }
        return links;
    }

    private async Task UpsertLinkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, EntosisLink link, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "INSERT INTO {schema}.links (module_id, hub_id, character_id, corporation_id, alliance_id, side, linked_ms, last_cycle_ms) "
            + "VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT (module_id) DO UPDATE SET hub_id = EXCLUDED.hub_id, character_id = EXCLUDED.character_id, "
            + "corporation_id = EXCLUDED.corporation_id, alliance_id = EXCLUDED.alliance_id, side = EXCLUDED.side, last_cycle_ms = EXCLUDED.last_cycle_ms",
            connection, transaction, link.ModuleId, hubId, link.CharacterId, link.CorporationId, link.AllianceId,
            link.Side == EntosisSide.Defender ? "defender" : "attacker", link.LinkedMs, link.LastCycleMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteStaleLinksAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, long now, CancellationToken cancellationToken)
    {
        // a link is active when its last cycle is after this (EntosisRules.IsActive): the rest stopped counting
        await using var command = Command(
            "DELETE FROM {schema}.links WHERE hub_id = $1 AND last_cycle_ms <= $2", connection, transaction, hubId, now - 2 * EntosisRules.CycleMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteLinkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long moduleId, CancellationToken cancellationToken)
    {
        await using var command = Command("DELETE FROM {schema}.links WHERE module_id = $1", connection, transaction, moduleId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long?> HubOfLinkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long moduleId, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT hub_id FROM {schema}.links WHERE module_id = $1 FOR UPDATE", connection, transaction, moduleId);
        return await command.ExecuteScalarAsync(cancellationToken) is long hubId ? hubId : null;
    }
}
