using System.Text.RegularExpressions;
using Eve.Data;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Space.Entosis;

/// <summary>What the sovereignty records say about a system's Sovereignty Hub at a cycle: its item, its owner's alliance (null for a neutral hub), and the activity
/// multiplier a capture starts with (1 for a neutral hub).</summary>
public sealed record EntosisHubReading(long HubId, int SolarSystemId, long? DefenderAllianceId, double ActivityDefenseMultiplier);

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
/// write path), after the commit. A handover that fails is retried by a job (<see cref="HandoverDueKind"/>); an owned hub's completion records the capture and
/// hands nothing over (its reinforcement and the command-node event are not built).
/// </summary>
public sealed class EntosisCaptureService
{
    /// <summary>The schema of the capture's tables (the migrations are applied to it).</summary>
    public const string DefaultSchema = "entosis";

    /// <summary>The durable job kind of a capture's completion; its key is the hub.</summary>
    public const string CaptureDueKind = "space.entosis.capture-due";

    /// <summary>The durable job kind of a handover's retry; its key is the hub.</summary>
    public const string HandoverDueKind = "space.entosis.handover-due";

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

    /// <param name="readHub">The system's Sovereignty Hub as the records have it at this moment (null when the system has none).</param>
    /// <param name="handover">The claim of a captured neutral hub by its capturer (ST-9 d). Unset, a capture completes and waits for one.</param>
    public EntosisCaptureService(
        NpgsqlDataSource source, DurableScheduler scheduler, Func<long> now, Func<int, CancellationToken, Task<EntosisHubReading?>> readHub,
        Func<EntosisCapturedHub, CancellationToken, Task<EntosisHandover>>? handover = null, string schema = DefaultSchema, Action<string>? log = null)
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
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(EntosisCaptureService).Assembly, "EntosisMigrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>The payload of a hub's job (completion, or handover retry).</summary>
    public sealed record HubDue(long HubId);

    /// <summary>The answer of a settled cycle: accepted, or the reason it is not (the module ends with it).</summary>
    public sealed record SettleResult(bool Accepted, string Reason);

    /// <summary>Registers the jobs' handlers. Call it before the scheduler starts.</summary>
    public void Register()
    {
        _scheduler.Register(CaptureDueKind, (context, token) => CaptureDueAsync(context.Payload<HubDue>()!.HubId, token));
        _scheduler.Register(HandoverDueKind, (context, token) => HandoverDueAsync(context.Payload<HubDue>()!.HubId, token));
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
            return new SettleResult(false, "ENTOSIS_TARGET_NOT_SOV_HUB");
        }
        var now = _now();
        var side = reading.DefenderAllianceId is { } owner && owner == allianceId ? EntosisSide.Defender : EntosisSide.Attacker;

        bool completed;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var stored = await LoadStateAsync(connection, transaction, hubId, forUpdate: true, cancellationToken);
            var state = stored ?? new EntosisCaptureState(hubId, solarSystemId, 0, now, reading.ActivityDefenseMultiplier, reading.DefenderAllianceId, EntosisPhase.Idle);
            if (state.Phase == EntosisPhase.Captured)
            {
                return new SettleResult(false, "ENTOSIS_CAPTURE_COMPLETE");
            }
            var links = await LoadLinksAsync(connection, transaction, hubId, cancellationToken);
            var advanced = EntosisRules.Advance(state with { DefenderAllianceId = reading.DefenderAllianceId }, links, now);
            await SaveStateAsync(connection, transaction, advanced, cancellationToken);
            var settling = new EntosisLink(moduleId, characterId, corporationId, allianceId, side, now, now);
            await UpsertLinkAsync(connection, transaction, hubId, settling, cancellationToken);
            await DeleteStaleLinksAsync(connection, transaction, hubId, now, cancellationToken);
            var after = await LoadLinksAsync(connection, transaction, hubId, cancellationToken);
            completed = await CompleteAsync(connection, transaction, state, advanced, side == EntosisSide.Attacker ? settling : null, cancellationToken);
            await ReconcileJobAsync(connection, transaction, advanced, after, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
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
            if (hubId is { } id && state is not null)
            {
                var now = _now();
                var links = await LoadLinksAsync(connection, transaction, id, cancellationToken);
                var advanced = EntosisRules.Advance(state, links, now);
                await SaveStateAsync(connection, transaction, advanced, cancellationToken);
                await DeleteLinkAsync(connection, transaction, moduleId, cancellationToken);
                var after = await LoadLinksAsync(connection, transaction, id, cancellationToken);
                completed = await CompleteAsync(connection, transaction, state, advanced, MostRecentAttacker(links), cancellationToken);
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
            if (state is not null && state.Phase != EntosisPhase.Captured)
            {
                var now = _now();
                var links = await LoadLinksAsync(connection, transaction, hubId, cancellationToken);
                var advanced = EntosisRules.Advance(state, links, now);
                await SaveStateAsync(connection, transaction, advanced, cancellationToken);
                await DeleteStaleLinksAsync(connection, transaction, hubId, now, cancellationToken);
                var after = await LoadLinksAsync(connection, transaction, hubId, cancellationToken);
                completed = await CompleteAsync(connection, transaction, state, advanced, MostRecentAttacker(links), cancellationToken);
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
    /// The completion of a capture, in its transaction: when the hub becomes captured now, it records who captured it and what the handover is (pending to the
    /// capturer, or owned: an owned hub changes hands in no way that is built). Returns whether it completed.
    /// </summary>
    private async Task<bool> CompleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, EntosisCaptureState before, EntosisCaptureState after, EntosisLink? capturer,
        CancellationToken cancellationToken)
    {
        if (before.Phase == EntosisPhase.Captured || after.Phase != EntosisPhase.Captured)
        {
            return false;
        }
        var handover = after.DefenderAllianceId is not null ? "owned" : capturer is null ? "superseded" : "pending";
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
        await using var writer = await _source.OpenConnectionAsync(cancellationToken);
        await using var update = Command(
            "UPDATE {schema}.hubs SET handover = $2 WHERE hub_id = $1 AND handover = 'pending'", writer, null,
            hubId, answer == EntosisHandover.Claimed ? "claimed" : "superseded");
        await update.ExecuteNonQueryAsync(cancellationToken);
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
            "SELECT hub_id, solar_system_id, progress, checkpoint_ms, activity_defense_multiplier, defender_alliance_id, phase FROM {schema}.hubs WHERE hub_id = $1"
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
            var other => throw new InvalidOperationException($"unknown entosis phase {other}"),
        };
        return new EntosisCaptureState(
            reader.GetInt64(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetInt64(3), reader.GetDouble(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5), phase);
    }

    private async Task SaveStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EntosisCaptureState state, CancellationToken cancellationToken)
    {
        var phase = state.Phase switch
        {
            EntosisPhase.Idle => "idle",
            EntosisPhase.Capturing => "capturing",
            _ => "captured",
        };
        await using var command = Command(
            "INSERT INTO {schema}.hubs (hub_id, solar_system_id, progress, checkpoint_ms, activity_defense_multiplier, defender_alliance_id, phase) "
            + "VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (hub_id) DO UPDATE SET progress = EXCLUDED.progress, checkpoint_ms = EXCLUDED.checkpoint_ms, "
            + "defender_alliance_id = EXCLUDED.defender_alliance_id, phase = EXCLUDED.phase",
            connection, transaction, state.HubId, state.SolarSystemId, state.Progress, state.CheckpointMs, state.ActivityDefenseMultiplier, state.DefenderAllianceId, phase);
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
