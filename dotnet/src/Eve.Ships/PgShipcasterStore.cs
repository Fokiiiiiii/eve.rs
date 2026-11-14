using System.Data;
using System.Text.RegularExpressions;
using Npgsql;

namespace Eve.Ships;

public sealed record ShipcasterLandingPadState(
    long ItemId,
    int SolarSystemId,
    int FactionId,
    bool IsBuilt,
    long? LinkTimestamp,
    bool IsDisrupted);

public sealed record ShipcasterLauncherState(
    long ItemId,
    int SolarSystemId,
    int FactionId,
    IReadOnlyList<int> ExtraLinkedFactionIds,
    bool IsStarter,
    int? DestinationSolarSystemId = null,
    long? DestinationLandingPadId = null);

public sealed record ShipcasterScheduledTarget(
    int SolarSystemId,
    long LandingPadId,
    int FactionId,
    long StartsAtFileTime,
    long EndsAtFileTime);

/// <summary>A complete live network publication from the owner of shipcaster world state.</summary>
public sealed record ShipcasterNetworkSnapshot(
    IReadOnlyList<ShipcasterLandingPadState> LandingPads,
    IReadOnlyList<ShipcasterLauncherState> Launchers,
    IReadOnlyDictionary<long, IReadOnlyList<ShipcasterScheduledTarget>> TargetSchedules);

public sealed record ShipcasterTravelNetworkState(
    ShipcasterLauncherState Launcher,
    ShipcasterScheduledTarget? ActiveTarget,
    ShipcasterLandingPadState? LandingPad);

/// <summary>
/// Durable state published by the shipcaster world owner.  The client-visible queries and travel RPCs read
/// the same rows, so a deployment, link, disruption, or target schedule cannot be split across caches.
/// </summary>
public sealed class PgShipcasterStore
{
    public const string DefaultSchema = PgSpaceItemStore.DefaultSchema;

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgShipcasterStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _schema = schema;
    }

    public string Schema => _schema;

    public static Task<IReadOnlyList<string>> MigrateAsync(
        NpgsqlDataSource source,
        string schema = DefaultSchema,
        CancellationToken cancellationToken = default) =>
        PgSpaceItemStore.MigrateAsync(source, schema, cancellationToken);

    public async Task<bool> NetworkPublishedAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            SELECT published_at_ms IS NOT NULL
            FROM {_schema}.shipcaster_network_state
            WHERE singleton = true
            """);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    /// <summary>
    /// Replaces the published network atomically. Callers must supply live item IDs, component state and target
    /// windows from the shipcaster world owner; this method does not infer deployments from type IDs.
    /// </summary>
    public async Task PublishSnapshotAsync(
        ShipcasterNetworkSnapshot snapshot,
        long publishedAtMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (publishedAtMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(publishedAtMs));
        ArgumentNullException.ThrowIfNull(snapshot.LandingPads);
        ArgumentNullException.ThrowIfNull(snapshot.Launchers);
        ArgumentNullException.ThrowIfNull(snapshot.TargetSchedules);

        var pads = snapshot.LandingPads.ToDictionary(pad => pad.ItemId);
        var launchers = snapshot.Launchers.ToDictionary(launcher => launcher.ItemId);
        foreach (var pad in pads.Values)
            Validate(pad);
        foreach (var launcher in launchers.Values)
            Validate(launcher);
        foreach (var pair in snapshot.TargetSchedules)
        {
            if (!launchers.TryGetValue(pair.Key, out var launcher) || launcher.IsStarter)
                throw new ArgumentException("A target schedule must belong to a published non-starter launcher.", nameof(snapshot));
            if (pair.Value is null || pair.Value.Count > 3)
                throw new ArgumentException("A shipcaster can have at most three scheduled linked destinations.", nameof(snapshot));
            var ordered = pair.Value.OrderBy(target => target.StartsAtFileTime).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var target = ordered[index];
                Validate(target);
                if (index > 0 && ordered[index - 1].EndsAtFileTime > target.StartsAtFileTime)
                    throw new ArgumentException("A launcher cannot publish overlapping destination windows.", nameof(snapshot));
                if (!pads.TryGetValue(target.LandingPadId, out var pad) || !pad.IsBuilt || pad.IsDisrupted ||
                    pad.LinkTimestamp is null || pad.SolarSystemId != target.SolarSystemId || pad.FactionId != target.FactionId)
                    throw new ArgumentException("A scheduled target must identify a built, linked, undisrupted published landing pad.", nameof(snapshot));
            }
        }
        foreach (var launcher in launchers.Values.Where(launcher => launcher.IsStarter))
        {
            if (!pads.TryGetValue(launcher.DestinationLandingPadId!.Value, out var pad) || !pad.IsBuilt || pad.IsDisrupted ||
                pad.SolarSystemId != launcher.DestinationSolarSystemId)
                throw new ArgumentException("A starter destination must identify a built, undisrupted published landing pad.", nameof(snapshot));
        }

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteSchedule = new NpgsqlCommand(
            $"DELETE FROM {_schema}.shipcaster_target_schedule", connection, transaction))
            await deleteSchedule.ExecuteNonQueryAsync(cancellationToken);
        await using (var deleteLaunchers = new NpgsqlCommand(
            $"DELETE FROM {_schema}.shipcaster_launchers", connection, transaction))
            await deleteLaunchers.ExecuteNonQueryAsync(cancellationToken);
        await using (var deletePads = new NpgsqlCommand(
            $"DELETE FROM {_schema}.shipcaster_landing_pads", connection, transaction))
            await deletePads.ExecuteNonQueryAsync(cancellationToken);

        foreach (var pad in pads.Values)
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schema}.shipcaster_landing_pads
                    (item_id, solar_system_id, faction_id, is_built, link_timestamp, is_disrupted)
                VALUES ($1, $2, $3, $4, $5, $6)
                """, connection, transaction);
            command.Parameters.AddWithValue(pad.ItemId);
            command.Parameters.AddWithValue(pad.SolarSystemId);
            command.Parameters.AddWithValue(pad.FactionId);
            command.Parameters.AddWithValue(pad.IsBuilt);
            command.Parameters.AddWithValue((object?)pad.LinkTimestamp ?? DBNull.Value);
            command.Parameters.AddWithValue(pad.IsDisrupted);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var launcher in launchers.Values)
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schema}.shipcaster_launchers
                    (item_id, solar_system_id, faction_id, extra_linked_faction_ids, is_starter,
                     destination_solar_system_id, destination_landing_pad_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """, connection, transaction);
            command.Parameters.AddWithValue(launcher.ItemId);
            command.Parameters.AddWithValue(launcher.SolarSystemId);
            command.Parameters.AddWithValue(launcher.FactionId);
            command.Parameters.AddWithValue(launcher.ExtraLinkedFactionIds.ToArray());
            command.Parameters.AddWithValue(launcher.IsStarter);
            command.Parameters.AddWithValue((object?)launcher.DestinationSolarSystemId ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)launcher.DestinationLandingPadId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var (launcherId, targets) in snapshot.TargetSchedules)
        {
            var ordered = targets.OrderBy(target => target.StartsAtFileTime).ToArray();
            for (var slot = 0; slot < ordered.Length; slot++)
            {
                var target = ordered[slot];
                await using var command = new NpgsqlCommand($"""
                    INSERT INTO {_schema}.shipcaster_target_schedule
                        (launcher_id, target_slot, solar_system_id, landing_pad_id, faction_id,
                         starts_at_filetime, ends_at_filetime)
                    VALUES ($1, $2, $3, $4, $5, $6, $7)
                    """, connection, transaction);
                command.Parameters.AddWithValue(launcherId);
                command.Parameters.AddWithValue((short)slot);
                command.Parameters.AddWithValue(target.SolarSystemId);
                command.Parameters.AddWithValue(target.LandingPadId);
                command.Parameters.AddWithValue(target.FactionId);
                command.Parameters.AddWithValue(target.StartsAtFileTime);
                command.Parameters.AddWithValue(target.EndsAtFileTime);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (var publish = new NpgsqlCommand($"""
            UPDATE {_schema}.shipcaster_network_state SET published_at_ms = $1 WHERE singleton = true
            """, connection, transaction))
        {
            publish.Parameters.AddWithValue(publishedAtMs);
            if (await publish.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The shipcaster network-state migration has no singleton row.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ShipcasterLandingPadState>> LandingPadsAsync(
        int? factionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            SELECT item_id, solar_system_id, faction_id, is_built, link_timestamp, is_disrupted
            FROM {_schema}.shipcaster_landing_pads
            WHERE $1::integer IS NULL OR faction_id = $1
            ORDER BY faction_id, solar_system_id, item_id
            """);
        command.Parameters.AddWithValue((object?)factionId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pads = new List<ShipcasterLandingPadState>();
        while (await reader.ReadAsync(cancellationToken))
            pads.Add(new ShipcasterLandingPadState(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetBoolean(5)));
        return pads;
    }

    public async Task<IReadOnlyList<int>> FactionsWithShipcasterAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            SELECT DISTINCT faction_id
            FROM {_schema}.shipcaster_launchers
            WHERE NOT is_starter
            ORDER BY faction_id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var factions = new List<int>();
        while (await reader.ReadAsync(cancellationToken))
            factions.Add(reader.GetInt32(0));
        return factions;
    }

    /// <summary>Reads a launcher, its active scheduled target and destination pad from one published DB snapshot.</summary>
    public async Task<ShipcasterTravelNetworkState?> TravelNetworkStateAsync(
        long launcherId,
        long? nowFileTime,
        CancellationToken cancellationToken = default)
    {
        if (launcherId <= 0)
            return null;

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var published = new NpgsqlCommand($"""
            SELECT published_at_ms IS NOT NULL
            FROM {_schema}.shipcaster_network_state
            WHERE singleton = true
            """, connection, transaction))
        {
            if ((bool)(await published.ExecuteScalarAsync(cancellationToken) ?? false) is false)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }

        ShipcasterLauncherState? launcher;
        await using (var command = new NpgsqlCommand($"""
            SELECT item_id, solar_system_id, faction_id, extra_linked_faction_ids, is_starter,
                   destination_solar_system_id, destination_landing_pad_id
            FROM {_schema}.shipcaster_launchers
            WHERE item_id = $1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(launcherId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            launcher = await reader.ReadAsync(cancellationToken)
                ? new ShipcasterLauncherState(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetFieldValue<int[]>(3), reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6))
                : null;
        }
        if (launcher is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        ShipcasterScheduledTarget? target = null;
        if (!launcher.IsStarter && nowFileTime is { } now)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT solar_system_id, landing_pad_id, faction_id, starts_at_filetime, ends_at_filetime
                FROM {_schema}.shipcaster_target_schedule
                WHERE launcher_id = $1 AND starts_at_filetime < $2 AND ends_at_filetime > $2
                ORDER BY target_slot
                LIMIT 1
                """, connection, transaction);
            command.Parameters.AddWithValue(launcherId);
            command.Parameters.AddWithValue(now);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            target = await reader.ReadAsync(cancellationToken)
                ? new ShipcasterScheduledTarget(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2),
                    reader.GetInt64(3), reader.GetInt64(4))
                : null;
        }

        var destinationPadId = launcher.IsStarter
            ? launcher.DestinationLandingPadId
            : target?.LandingPadId;
        ShipcasterLandingPadState? pad = null;
        if (destinationPadId is { } itemId)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT item_id, solar_system_id, faction_id, is_built, link_timestamp, is_disrupted
                FROM {_schema}.shipcaster_landing_pads WHERE item_id = $1
                """, connection, transaction);
            command.Parameters.AddWithValue(itemId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            pad = await reader.ReadAsync(cancellationToken)
                ? new ShipcasterLandingPadState(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetBoolean(5))
                : null;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ShipcasterTravelNetworkState(launcher, target, pad);
    }

    public async Task<bool> HasUnusedStarterAtAsync(long characterId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || solarSystemId <= 0)
            return false;
        await using var command = _source.CreateCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_schema}.shipcaster_launchers l
                WHERE l.is_starter AND l.solar_system_id = $2
                  AND NOT EXISTS (
                      SELECT 1 FROM {_schema}.shipcaster_starter_jumps j WHERE j.character_id = $1
                  )
            )
            """);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(solarSystemId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> ClaimStarterJumpAsync(
        long characterId,
        long launcherId,
        int destinationSolarSystemId,
        long destinationLandingPadId,
        long claimedAtMs,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || launcherId <= 0 || destinationSolarSystemId <= 0 || destinationLandingPadId <= 0)
            return false;
        await using var command = _source.CreateCommand($"""
            INSERT INTO {_schema}.shipcaster_starter_jumps
                (character_id, launcher_item_id, destination_solar_system_id, destination_landing_pad_id,
                 state, claimed_at_ms, used_at_ms)
            VALUES ($1, $2, $3, $4, 0, $5, NULL)
            ON CONFLICT (character_id) DO NOTHING
            RETURNING character_id
            """);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(launcherId);
        command.Parameters.AddWithValue(destinationSolarSystemId);
        command.Parameters.AddWithValue(destinationLandingPadId);
        command.Parameters.AddWithValue(claimedAtMs);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> CompleteStarterJumpAsync(long characterId, long usedAtMs, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            UPDATE {_schema}.shipcaster_starter_jumps
            SET state = 1, used_at_ms = $2
            WHERE character_id = $1 AND state = 0
            """);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(usedAtMs);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReleaseStarterJumpAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            DELETE FROM {_schema}.shipcaster_starter_jumps
            WHERE character_id = $1 AND state = 0
            """);
        command.Parameters.AddWithValue(characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> StarterUsedAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            SELECT EXISTS (SELECT 1 FROM {_schema}.shipcaster_starter_jumps WHERE character_id = $1)
            """);
        command.Parameters.AddWithValue(characterId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static void Validate(ShipcasterLandingPadState pad)
    {
        if (pad.ItemId <= 0 || pad.SolarSystemId <= 0 || pad.FactionId <= 0 || pad.LinkTimestamp is <= 0)
            throw new ArgumentOutOfRangeException(nameof(pad), "A shipcaster pad needs positive item, system and faction IDs and an optional positive link timestamp.");
    }

    private static void Validate(ShipcasterLauncherState launcher)
    {
        if (launcher.ItemId <= 0 || launcher.SolarSystemId <= 0 || launcher.FactionId <= 0 ||
            launcher.ExtraLinkedFactionIds is null || launcher.ExtraLinkedFactionIds.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(nameof(launcher), "A shipcaster launcher needs positive item, system and faction IDs.");
        if (launcher.IsStarter && (launcher.DestinationSolarSystemId is not > 0 || launcher.DestinationLandingPadId is not > 0))
            throw new ArgumentException("A starter shipcaster needs an authoritative destination system and landing-pad item.", nameof(launcher));
        if (!launcher.IsStarter && (launcher.DestinationSolarSystemId is not null || launcher.DestinationLandingPadId is not null))
            throw new ArgumentException("A regular shipcaster destination comes from its current schedule.", nameof(launcher));
    }

    private static void Validate(ShipcasterScheduledTarget target)
    {
        if (target.SolarSystemId <= 0 || target.LandingPadId <= 0 || target.FactionId <= 0 ||
            target.StartsAtFileTime <= 0 || target.EndsAtFileTime <= target.StartsAtFileTime)
            throw new ArgumentOutOfRangeException(nameof(target), "A shipcaster target needs valid destination IDs and an increasing client FILETIME interval.");
    }
}
