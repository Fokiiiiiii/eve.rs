using System.Globalization;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Space.Entosis;

public sealed record CommandNodeSpawned(long HubId, long NodeId, int SolarSystemId, long AtMs);

public sealed record CommandNodeCaptured(long HubId, long NodeId, int SolarSystemId, bool ByAttackers, long AtMs);

public sealed record CommandStageEnded(long HubId, int SolarSystemId, CommandNodeOutcome Outcome, IReadOnlyList<long> RemovedNodeIds, long AtMs);

/// <summary>The stage has started in the constellation (the notice of the defender: EVE.js notifySovCommandNodeEventStarted, V24.01 161).</summary>
public sealed record CommandStageStarted(long HubId, int ConstellationId, int SolarSystemId, long DefenderAllianceId, long AtMs);

/// <summary>The hub was destroyed and the sovereignty records have lost it (the notice of the defender: V24.01 162).</summary>
public sealed record HubDestroyed(long HubId, int SolarSystemId, long DefenderAllianceId, long AtMs);

/// <summary>The hub a destruction is written for: the sovereignty records lose it from its system and its defender.</summary>
public sealed record EntosisDestroyedHub(long HubId, int SolarSystemId, long DefenderAllianceId);

/// <summary>The payload of a node's completion job.</summary>
public sealed record CommandNodeDue(long NodeId);

/// <summary>
/// in the constellation (a further node about every 30 minutes, up to the caps). A node is a target of the entosis links like the hub: an attacker's link moves
/// it toward capture, a defender's back, and the node regenerates when no link is on it. A captured node moves the stage's bar by 7% and a new one spawns; nine
/// attacker captures end the stage with the hub destroyed, six defender captures with the hub defended (invulnerable until the next window). The node is the
/// target: its links are the links table's, keyed by the node id; its progress is the signed <see cref="CommandNodeRules"/> progress.
/// </summary>
public sealed partial class EntosisCaptureService
{
    /// <summary>The durable job kinds and topics of the stage (the Host puts the nodes in space from the topics).</summary>
    public const string CampaignStartKind = "space.entosis.campaign-start";
    public const string NodeSpawnKind = "space.entosis.node-spawn";
    public const string NodeDueKind = "space.entosis.node-due";
    public const string NodeSpawnedTopic = "space.entosis.node_spawned";
    public const string NodeCapturedTopic = "space.entosis.node_captured";
    public const string StageEndedTopic = "space.entosis.stage_ended";
    public const string StageStartedTopic = "space.entosis.stage_started";
    public const string HubDestroyedTopic = "space.entosis.hub_destroyed";

    /// <summary>The job that writes a destroyed hub's loss into the sovereignty records, retried until it is written.</summary>
    public const string HubDestroyDueKind = "space.entosis.hub-destroyed-due";

    /// <summary>
    /// A node's id is the hub's id times this, plus the node's spawn number (shortcut: a derived id, not the item store's; a stage spawns fewer nodes than this;
    /// upgrade: allocate the nodes' ids from the item store).
    /// </summary>
    private const int MaxSpawnsPerCampaign = 1000;

    private sealed record NodeRow(long NodeId, long HubId, int SolarSystemId, double Progress, long CheckpointMs);

    private sealed record CampaignRow(int SolarSystemId, long DefenderAllianceId, double ActivityDefenseMultiplier, int AttackerCaptures, int DefenderCaptures, string Phase);

    private sealed record HubForCampaign(int SolarSystemId, long DefenderAllianceId, double ActivityDefenseMultiplier, long? ExitMs);

    private static long NodeIdOf(long hubId, int spawn) => hubId * MaxSpawnsPerCampaign + spawn;

    private static string KeyOf(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a target is a live command node of an active stage in that system (the Host's target check).</summary>
    public async Task<bool> IsCommandNodeAsync(long nodeId, int solarSystemId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(
            "SELECT 1 FROM {schema}.nodes n JOIN {schema}.campaigns c ON c.hub_id = n.hub_id WHERE n.node_id = $1 AND n.solar_system_id = $2 AND c.phase = 'active'",
            connection, null, nodeId, solarSystemId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>The live command nodes of a system: the Host puts them in space when a pilot comes into the system.</summary>
    public async Task<IReadOnlyList<CommandNodeSpawned>> LiveNodesAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(
            "SELECT n.node_id, n.hub_id FROM {schema}.nodes n JOIN {schema}.campaigns c ON c.hub_id = n.hub_id "
            + "WHERE n.solar_system_id = $1 AND c.phase = 'active' ORDER BY n.node_id",
            connection, null, solarSystemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = new List<CommandNodeSpawned>();
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new CommandNodeSpawned(reader.GetInt64(1), reader.GetInt64(0), solarSystemId, 0));
        }
        return nodes;
    }

    /// <summary>The stage's start, at the reinforcement's exit (the job <see cref="CampaignStartKind"/>). A stage that exists, or a hub that is not reinforced, is left alone.</summary>
    public async Task CampaignStartAsync(long hubId, CancellationToken cancellationToken)
    {
        var now = _now();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var hub = await ReadHubForCampaignAsync(connection, transaction, hubId, cancellationToken);
        if (hub is null || hub.ExitMs is null || hub.ExitMs > now || await CampaignExistsAsync(connection, transaction, hubId, cancellationToken))
        {
            return;
        }
        var systems = await SystemsOfAsync(hub.SolarSystemId, cancellationToken);
        var nextSpawn = now + SpawnIntervalMs(hubId, now);
        await using (var insert = Command(
            "INSERT INTO {schema}.campaigns (hub_id, solar_system_id, defender_alliance_id, activity_defense_multiplier, phase, started_ms, next_spawn_ms, spawned, "
            + "attacker_captures, defender_captures) VALUES ($1, $2, $3, $4, 'active', $5, $6, 0, 0, 0)",
            connection, transaction, hubId, hub.SolarSystemId, hub.DefenderAllianceId, hub.ActivityDefenseMultiplier, now, nextSpawn))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var index = 0; index < CommandNodeRules.InitialNodes; index++)
        {
            await SpawnNodeAsync(connection, transaction, hubId, systems, now, cancellationToken);
        }
        await _scheduler.ScheduleAsync(connection, transaction,
            new JobSpec(NodeSpawnKind, nextSpawn) { Key = KeyOf(hubId), Payload = new HubDue(hubId), OnDuplicate = OnDuplicate.Replace }, cancellationToken);
        if (_events is not null)
        {
            var constellation = await _constellationOf(hub.SolarSystemId, cancellationToken);
            await _events.PublishAsync(connection, transaction, StageStartedTopic,
                new CommandStageStarted(hubId, constellation, hub.SolarSystemId, hub.DefenderAllianceId, now), KeyOf(hubId), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        _events?.Wake();
    }

    /// <summary>The extra spawn (the job <see cref="NodeSpawnKind"/>): one node if the caps allow, then the next spawn about 30 minutes later (with jitter).</summary>
    public async Task NodeSpawnDueAsync(long hubId, CancellationToken cancellationToken)
    {
        var now = _now();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var campaign = await ReadCampaignAsync(connection, transaction, hubId, cancellationToken);
        if (campaign is { Phase: "active" })
        {
            await SpawnNodeAsync(connection, transaction, hubId, await SystemsOfAsync(campaign.SolarSystemId, cancellationToken), now, cancellationToken);
            var next = now + SpawnIntervalMs(hubId, now);
            await using (var update = Command("UPDATE {schema}.campaigns SET next_spawn_ms = $2 WHERE hub_id = $1", connection, transaction, hubId, next))
            {
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await _scheduler.ScheduleAsync(connection, transaction,
                new JobSpec(NodeSpawnKind, next) { Key = KeyOf(hubId), Payload = new HubDue(hubId), OnDuplicate = OnDuplicate.Replace }, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        _events?.Wake();
    }

    /// <summary>A node's completion job: its progress is moved to now, and a node that reached either end is captured.</summary>
    public async Task NodeDueAsync(long nodeId, CancellationToken cancellationToken)
    {
        var now = _now();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var node = await ReadNodeAsync(connection, transaction, nodeId, cancellationToken);
            var campaign = node is null ? null : await ReadCampaignAsync(connection, transaction, node.HubId, cancellationToken);
            if (node is not null && campaign is { Phase: "active" })
            {
                var links = await LoadLinksAsync(connection, transaction, nodeId, cancellationToken);
                var progress = CommandNodeRules.Advance(node.Progress, node.CheckpointMs, now, links, campaign.ActivityDefenseMultiplier);
                if (Math.Abs(progress) >= 1)
                {
                    await CaptureNodeAsync(connection, transaction, node, campaign, progress > 0, now, cancellationToken);
                }
                else
                {
                    await SaveNodeAsync(connection, transaction, nodeId, progress, now, cancellationToken);
                    await ReconcileNodeJobAsync(connection, transaction, nodeId, progress, links, campaign.ActivityDefenseMultiplier, now, cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        _events?.Wake();
    }

    /// <summary>A link on a command node settled a cycle (the simulation checked the lock and the reach). Refused by name when the stage is not active.</summary>
    private async Task<SettleResult?> SettleNodeCycleAsync(
        long moduleId, long characterId, long corporationId, long allianceId, long nodeId, CancellationToken cancellationToken)
    {
        var now = _now();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var node = await ReadNodeAsync(connection, transaction, nodeId, cancellationToken);
            if (node is null)
            {
                return null;
            }
            var campaign = await ReadCampaignAsync(connection, transaction, node.HubId, cancellationToken);
            if (campaign is null)
            {
                return null;
            }
            if (campaign.Phase != "active")
            {
                return new SettleResult(false, "ENTOSIS_CAMPAIGN_ENDED");
            }
            var side = allianceId == campaign.DefenderAllianceId ? EntosisSide.Defender : EntosisSide.Attacker;
            var links = await LoadLinksAsync(connection, transaction, nodeId, cancellationToken);
            var progress = CommandNodeRules.Advance(node.Progress, node.CheckpointMs, now, links, campaign.ActivityDefenseMultiplier);
            await UpsertLinkAsync(connection, transaction, nodeId, new EntosisLink(moduleId, characterId, corporationId, allianceId, side, now, now), cancellationToken);
            await DeleteStaleLinksAsync(connection, transaction, nodeId, now, cancellationToken);
            if (Math.Abs(progress) >= 1)
            {
                await CaptureNodeAsync(connection, transaction, node, campaign, progress > 0, now, cancellationToken);
            }
            else
            {
                await SaveNodeAsync(connection, transaction, nodeId, progress, now, cancellationToken);
                await ReconcileNodeJobAsync(connection, transaction, nodeId, progress, await LoadLinksAsync(connection, transaction, nodeId, cancellationToken),
                    campaign.ActivityDefenseMultiplier, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        _events?.Wake();
        return new SettleResult(true, "");
    }

    /// <summary>A node's link ended (its module's end report): the progress is moved to now and the link is removed.</summary>
    private async Task NodeLinkEndedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long nodeId, long moduleId, long now, CancellationToken cancellationToken)
    {
        var node = await ReadNodeAsync(connection, transaction, nodeId, cancellationToken);
        var campaign = node is null ? null : await ReadCampaignAsync(connection, transaction, node.HubId, cancellationToken);
        if (node is null || campaign is null)
        {
            return;
        }
        var links = await LoadLinksAsync(connection, transaction, nodeId, cancellationToken);
        var progress = CommandNodeRules.Advance(node.Progress, node.CheckpointMs, now, links, campaign.ActivityDefenseMultiplier);
        await DeleteLinkAsync(connection, transaction, moduleId, cancellationToken);
        if (Math.Abs(progress) >= 1 && campaign.Phase == "active")
        {
            await CaptureNodeAsync(connection, transaction, node, campaign, progress > 0, now, cancellationToken);
            return;
        }
        await SaveNodeAsync(connection, transaction, nodeId, progress, now, cancellationToken);
        await ReconcileNodeJobAsync(connection, transaction, nodeId, progress, await LoadLinksAsync(connection, transaction, nodeId, cancellationToken),
            campaign.ActivityDefenseMultiplier, now, cancellationToken);
    }

    /// <summary>A node is captured: it leaves the stage, the tally moves, and then either a new node spawns or the stage ends (its outcome).</summary>
    private async Task CaptureNodeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NodeRow node, CampaignRow campaign, bool byAttackers, long now,
        CancellationToken cancellationToken)
    {
        await _scheduler.CancelAsync(connection, transaction, NodeDueKind, KeyOf(node.NodeId), cancellationToken);
        await using (var links = Command("DELETE FROM {schema}.links WHERE hub_id = $1", connection, transaction, node.NodeId))
        {
            await links.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var remove = Command("DELETE FROM {schema}.nodes WHERE node_id = $1", connection, transaction, node.NodeId))
        {
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }
        var attackers = campaign.AttackerCaptures + (byAttackers ? 1 : 0);
        var defenders = campaign.DefenderCaptures + (byAttackers ? 0 : 1);
        await using (var count = Command(
            "UPDATE {schema}.campaigns SET attacker_captures = $2, defender_captures = $3 WHERE hub_id = $1", connection, transaction, node.HubId, attackers, defenders))
        {
            await count.ExecuteNonQueryAsync(cancellationToken);
        }
        if (_events is not null)
        {
            await _events.PublishAsync(connection, transaction, NodeCapturedTopic,
                new CommandNodeCaptured(node.HubId, node.NodeId, node.SolarSystemId, byAttackers, now), KeyOf(node.NodeId), cancellationToken);
        }
        var outcome = CommandNodeRules.Outcome(attackers, defenders);
        if (outcome == CommandNodeOutcome.Open)
        {

            await SpawnNodeAsync(connection, transaction, node.HubId, await SystemsOfAsync(campaign.SolarSystemId, cancellationToken), now, cancellationToken);
            return;
        }
        await EndStageAsync(connection, transaction, node.HubId, campaign.SolarSystemId, outcome, now, cancellationToken);
    }

    /// <summary>
    /// </summary>
    private async Task EndStageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, int solarSystemId, CommandNodeOutcome outcome, long now,
        CancellationToken cancellationToken)
    {
        var removed = new List<long>();
        await using (var list = Command("SELECT node_id FROM {schema}.nodes WHERE hub_id = $1", connection, transaction, hubId))
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                removed.Add(reader.GetInt64(0));
            }
        }
        foreach (var nodeId in removed)
        {
            await _scheduler.CancelAsync(connection, transaction, NodeDueKind, KeyOf(nodeId), cancellationToken);
            await using var links = Command("DELETE FROM {schema}.links WHERE hub_id = $1", connection, transaction, nodeId);
            await links.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var nodes = Command("DELETE FROM {schema}.nodes WHERE hub_id = $1", connection, transaction, hubId))
        {
            await nodes.ExecuteNonQueryAsync(cancellationToken);
        }
        await _scheduler.CancelAsync(connection, transaction, NodeSpawnKind, KeyOf(hubId), cancellationToken);
        var phase = outcome == CommandNodeOutcome.DefendersWon ? "defended" : "destroyed";
        await using (var end = Command("UPDATE {schema}.campaigns SET phase = $2, ended_ms = $3 WHERE hub_id = $1", connection, transaction, hubId, phase, now))
        {
            await end.ExecuteNonQueryAsync(cancellationToken);
        }
        if (outcome == CommandNodeOutcome.DefendersWon)
        {
            int primeHour;
            double multiplier;
            await using (var read = Command("SELECT prime_hour, activity_defense_multiplier FROM {schema}.hubs WHERE hub_id = $1", connection, transaction, hubId))
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                primeHour = reader.GetInt16(0);
                multiplier = reader.GetDouble(1);
            }
            var setting = (await LoadWindowAsync(connection, transaction, hubId, forUpdate: false, cancellationToken)).At(now);
            var until = EntosisWindow.NextWindowStart(primeHour, multiplier, now, setting);
            await using var shield = Command(
                "UPDATE {schema}.hubs SET phase = 'idle', progress = 0, reinforcement_exit_ms = NULL, invulnerable_until_ms = $2 WHERE hub_id = $1",
                connection, transaction, hubId, until);
            await shield.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            // the sovereignty records lose the hub in a job (HubDestroyDueKind), so a crash after this commit is answered by the job
            await using var destroy = Command(
                "UPDATE {schema}.hubs SET phase = 'destroyed', progress = 0, handover = 'destroy-pending' WHERE hub_id = $1", connection, transaction, hubId);
            await destroy.ExecuteNonQueryAsync(cancellationToken);
            await _scheduler.ScheduleAsync(connection, transaction,
                new JobSpec(HubDestroyDueKind, now) { Key = KeyOf(hubId), Payload = new HubDue(hubId), OnDuplicate = OnDuplicate.Replace }, cancellationToken);
        }
        if (_events is not null)
        {
            await _events.PublishAsync(connection, transaction, StageEndedTopic,
                new CommandStageEnded(hubId, solarSystemId, outcome, removed, now), KeyOf(hubId), cancellationToken);
        }
    }

    /// <summary>Spawns one node in the least-filled system from the spawn number, within the caps; false when the caps refuse it.</summary>
    private async Task<bool> SpawnNodeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, IReadOnlyList<int> systems, long now, CancellationToken cancellationToken)
    {
        int spawned;
        await using (var counter = Command("SELECT spawned FROM {schema}.campaigns WHERE hub_id = $1 FOR UPDATE", connection, transaction, hubId))
        {
            spawned = await counter.ExecuteScalarAsync(cancellationToken) is int count ? count : -1;
        }
        if (spawned < 0 || spawned >= MaxSpawnsPerCampaign || systems.Count == 0)
        {
            return false;
        }
        var liveBySystem = new Dictionary<int, int>();
        await using (var live = Command("SELECT solar_system_id, COUNT(*) FROM {schema}.nodes WHERE hub_id = $1 GROUP BY solar_system_id", connection, transaction, hubId))
        await using (var reader = await live.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                liveBySystem[reader.GetInt32(0)] = (int)reader.GetInt64(1);
            }
        }
        if (liveBySystem.Values.Sum() >= CommandNodeRules.MaxNodesPerConstellation)
        {
            return false;
        }
        int? chosen = null;
        for (var step = 0; step < systems.Count && chosen is null; step++)
        {
            var candidate = systems[(spawned + step) % systems.Count];
            if (liveBySystem.GetValueOrDefault(candidate) < CommandNodeRules.MaxNodesPerSystem)
            {
                chosen = candidate;
            }
        }
        if (chosen is not { } system)
        {
            return false;
        }
        var nodeId = NodeIdOf(hubId, spawned);
        await using (var insert = Command(
            "INSERT INTO {schema}.nodes (node_id, hub_id, solar_system_id, progress, checkpoint_ms) VALUES ($1, $2, $3, 0, $4)",
            connection, transaction, nodeId, hubId, system, now))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var bump = Command("UPDATE {schema}.campaigns SET spawned = spawned + 1 WHERE hub_id = $1", connection, transaction, hubId))
        {
            await bump.ExecuteNonQueryAsync(cancellationToken);
        }
        if (_events is not null)
        {
            await _events.PublishAsync(connection, transaction, NodeSpawnedTopic,
                new CommandNodeSpawned(hubId, nodeId, system, now), KeyOf(nodeId), cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// The destruction of a hub (the job <see cref="HubDestroyDueKind"/>): the sovereignty records lose it (the callback), then the hub is 'destroyed' and the
    /// </summary>
    public async Task HubDestroyDueAsync(long hubId, CancellationToken cancellationToken)
    {
        EntosisDestroyedHub? hub = null;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var command = Command(
            "SELECT solar_system_id, defender_alliance_id FROM {schema}.hubs WHERE hub_id = $1 AND handover = 'destroy-pending'", connection, null, hubId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(1))
            {
                hub = new EntosisDestroyedHub(hubId, reader.GetInt32(0), reader.GetInt64(1));
            }
        }
        if (hub is null)
        {
            return;
        }
        var written = true;
        if (_destroyHub is not null)
        {
            try
            {
                written = await _destroyHub(hub, cancellationToken);
            }
            catch (Exception error)
            {
                _log?.Invoke($"entosis: the loss of hub {hubId} was not written: {error.Message}; it is tried again");
                written = false;
            }
        }
        if (!written)
        {
            await _scheduler.ScheduleAsync(new JobSpec(HubDestroyDueKind, _now() + HandoverRetryMs)
            {
                Key = KeyOf(hubId), Payload = new HubDue(hubId), OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
            return;
        }
        var now = _now();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await using (var update = Command(
                "UPDATE {schema}.hubs SET handover = 'destroyed' WHERE hub_id = $1 AND handover = 'destroy-pending'", connection, transaction, hubId))
            {
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 1 && _events is not null)
                {
                    await _events.PublishAsync(connection, transaction, HubDestroyedTopic,
                        new HubDestroyed(hubId, hub.SolarSystemId, hub.DefenderAllianceId, now), KeyOf(hubId), cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        _events?.Wake();
    }

    private async Task ReconcileNodeJobAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long nodeId, double progress, IReadOnlyList<EntosisLink> links,
        double activityDefenseMultiplier, long now, CancellationToken cancellationToken)
    {
        if (CommandNodeRules.CompletionAt(progress, now, links, activityDefenseMultiplier) is { } due)
        {
            await _scheduler.ScheduleAsync(connection, transaction,
                new JobSpec(NodeDueKind, due) { Key = KeyOf(nodeId), Payload = new CommandNodeDue(nodeId), OnDuplicate = OnDuplicate.Replace }, cancellationToken);
        }
        else
        {
            await _scheduler.CancelAsync(connection, transaction, NodeDueKind, KeyOf(nodeId), cancellationToken);
        }
    }

    private static long SpawnIntervalMs(long hubId, long nowMs) =>
        (long)(CommandNodeRules.ExtraSpawnIntervalMs * (0.5 + EntosisWindow.Draw(hubId, nowMs) / 18446744073709551616.0));

    private async Task<IReadOnlyList<int>> SystemsOfAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        var systems = (await _constellationSystems(solarSystemId, cancellationToken)).Distinct().OrderBy(system => system).ToList();
        if (systems.Count == 0)
        {
            return new[] { solarSystemId };
        }
        return systems;
    }

    private async Task<HubForCampaign?> ReadHubForCampaignAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT solar_system_id, defender_alliance_id, activity_defense_multiplier, reinforcement_exit_ms FROM {schema}.hubs "
            + "WHERE hub_id = $1 AND phase = 'reinforced' AND defender_alliance_id IS NOT NULL FOR UPDATE",
            connection, transaction, hubId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new HubForCampaign(reader.GetInt32(0), reader.GetInt64(1), reader.GetDouble(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private async Task<bool> CampaignExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT 1 FROM {schema}.campaigns WHERE hub_id = $1", connection, transaction, hubId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<CampaignRow?> ReadCampaignAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT solar_system_id, defender_alliance_id, activity_defense_multiplier, attacker_captures, defender_captures, phase "
            + "FROM {schema}.campaigns WHERE hub_id = $1 FOR UPDATE",
            connection, transaction, hubId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new CampaignRow(reader.GetInt32(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5));
    }

    private async Task<NodeRow?> ReadNodeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long nodeId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT hub_id, solar_system_id, progress, checkpoint_ms FROM {schema}.nodes WHERE node_id = $1 FOR UPDATE",
            connection, transaction, nodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new NodeRow(nodeId, reader.GetInt64(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetInt64(3));
    }

    private async Task SaveNodeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long nodeId, double progress, long now, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "UPDATE {schema}.nodes SET progress = $2, checkpoint_ms = $3 WHERE node_id = $1", connection, transaction, nodeId, progress, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long?> ReadInvulnerableUntilAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long hubId, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT invulnerable_until_ms FROM {schema}.hubs WHERE hub_id = $1", connection, transaction, hubId);
        return await command.ExecuteScalarAsync(cancellationToken) is long until ? until : null;
    }
}
