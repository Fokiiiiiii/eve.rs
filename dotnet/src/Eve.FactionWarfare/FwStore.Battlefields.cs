using Eve.Data;
using Npgsql;

namespace Eve.FactionWarfare;

/// <summary>The battlefield tables of the factional warfare store (migration 0005): the open battlefields, their points and their pilots.</summary>
public sealed partial class FwStore
{
    /// <summary>The open battlefields, each with its three points (a battlefield is open until one side wins it).</summary>
    public async Task<IReadOnlyList<FwBattlefield>> OpenBattlefieldsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "SELECT b.battlefield_id, b.solar_system_id, b.occupier_id, b.score, b.updated_ms, b.instance_id, p.point_index, p.hold_ms, p.holder_id " +
            "FROM {schema}.fw_battlefields b JOIN {schema}.fw_battlefield_points p ON p.battlefield_id = b.battlefield_id " +
            "WHERE b.closed_ms IS NULL ORDER BY b.battlefield_id, p.point_index"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(long Id, long System, long Occupier, long Score, long Updated, long Instance, int Index, long Hold, long Holder)>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt16(6), reader.GetInt64(7), reader.GetInt64(8)));
        return rows.GroupBy(row => row.Id).Select(group =>
        {
            var first = group.First();
            return new FwBattlefield(group.Key, first.System, first.Occupier, first.Score, first.Updated, first.Instance,
                group.Select(row => new FwBattlefieldPoint(row.Index, row.Hold, row.Holder)).ToList());
        }).ToList();
    }

    /// <summary>The closing time of the last completed battlefield (null when none has completed).</summary>
    public async Task<long?> LastBattlefieldClosedMsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken = default) =>
        await Sql.ScalarAsync(connection, transaction, Q("SELECT MAX(closed_ms) FROM {schema}.fw_battlefields"), cancellationToken) is long closedMs ? closedMs : null;

    /// <summary>Opens a battlefield in a system with its three points (no hold yet) and returns its id.</summary>
    public async Task<long> InsertBattlefieldAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, long occupierId, long nowMs,
        CancellationToken cancellationToken = default)
    {
        long id = (long)(await Sql.ScalarAsync(connection, transaction, Q(
            "INSERT INTO {schema}.fw_battlefields (solar_system_id, occupier_id, score, spawned_ms, updated_ms) VALUES ($1, $2, 0, $3, $3) RETURNING battlefield_id"),
            cancellationToken, solarSystemId, occupierId, nowMs))!;
        for (int index = 0; index < FwBattlefields.PointCount; index++)
        {
            await Sql.ExecuteAsync(connection, transaction, Q(
                "INSERT INTO {schema}.fw_battlefield_points (battlefield_id, point_index) VALUES ($1, $2)"), cancellationToken, id, (short)index);
        }
        return id;
    }

    /// <summary>Records the dungeon instance of a battlefield's site (its gate's room).</summary>
    public Task SetBattlefieldInstanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long battlefieldId, long instanceId,
        CancellationToken cancellationToken = default) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.fw_battlefields SET instance_id = $2 WHERE battlefield_id = $1"),
            cancellationToken, battlefieldId, instanceId);

    /// <summary>Stores the score of an open battlefield, and the time it was counted to.</summary>
    public Task SaveBattlefieldAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long battlefieldId, long score, long nowMs,
        CancellationToken cancellationToken = default) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.fw_battlefields SET score = $2, updated_ms = $3 WHERE battlefield_id = $1 AND closed_ms IS NULL"),
            cancellationToken, battlefieldId, score, nowMs);

    /// <summary>Stores a point's hold timer and the side that holds it.</summary>
    public Task SaveBattlefieldPointAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long battlefieldId, int index, long holdMs, long holderId,
        CancellationToken cancellationToken = default) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.fw_battlefield_points SET hold_ms = $3, holder_id = $4 WHERE battlefield_id = $1 AND point_index = $2"),
            cancellationToken, battlefieldId, (short)index, holdMs, holderId);

    /// <summary>Closes a battlefield with its winner and its final score.</summary>
    public Task CloseBattlefieldAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long battlefieldId, long winnerId, long score, long nowMs,
        CancellationToken cancellationToken = default) =>
        Sql.ExecuteAsync(connection, transaction, Q(
            "UPDATE {schema}.fw_battlefields SET closed_ms = $2, winner_id = $3, score = $4, updated_ms = $2 WHERE battlefield_id = $1 AND closed_ms IS NULL"),
            cancellationToken, battlefieldId, nowMs, winnerId, score);

    /// <summary>Records a pilot that was within a point's range of a battlefield (its war faction at the time).</summary>
    public Task AddBattlefieldPilotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long battlefieldId, long characterId, long factionId,
        CancellationToken cancellationToken = default) =>
        Sql.ExecuteAsync(connection, transaction, Q(
            "INSERT INTO {schema}.fw_battlefield_pilots (battlefield_id, character_id, faction_id) VALUES ($1, $2, $3) " +
            "ON CONFLICT (battlefield_id, character_id) DO UPDATE SET faction_id = EXCLUDED.faction_id"),
            cancellationToken, battlefieldId, characterId, factionId);

    /// <summary>The pilots of a battlefield that were on a faction's side, by character id.</summary>
    public async Task<IReadOnlyList<long>> BattlefieldPilotsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long battlefieldId, long factionId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "SELECT character_id FROM {schema}.fw_battlefield_pilots WHERE battlefield_id = $1 AND faction_id = $2 ORDER BY character_id"), battlefieldId, factionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pilots = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
            pilots.Add(reader.GetInt64(0));
        return pilots;
    }
}
