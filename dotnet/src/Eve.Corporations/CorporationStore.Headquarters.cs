using Npgsql;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    /// <summary>
    /// Moves a corporation's headquarters to an NPC station. Answers the place the headquarters was, or null when it was already
    /// there (nothing changes then). A corporation that is an NPC corporation or is deleted cannot move.
    /// </summary>
    public Task<(long StationId, int SolarSystemId)?> MoveHeadquartersAsync(long corporationId, long stationId, int solarSystemId,
        CancellationToken token = default) =>
        WriteAsync<(long StationId, int SolarSystemId)?>(async (connection, tx) =>
        {
            (long StationId, int SolarSystemId) previous;
            await using (var read = Command(connection, tx,
                $"SELECT station_id, solar_system_id, is_npc, deleted FROM {_schema}.corporations WHERE id=$1 FOR UPDATE", corporationId))
            await using (var reader = await read.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new CorporationError("CORPORATION_NOT_FOUND");
                if (reader.GetBoolean(2) || reader.GetBoolean(3)) throw new CorporationError("CORPORATION_ACCESS_DENIED");
                previous = (reader.GetInt64(0), reader.GetInt32(1));
            }
            if (previous.StationId == stationId && previous.SolarSystemId == solarSystemId) return null;
            await using var update = Command(connection, tx,
                $"UPDATE {_schema}.corporations SET station_id=$2, solar_system_id=$3 WHERE id=$1",
                corporationId, stationId, solarSystemId);
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new CorporationError("CORPORATION_NOT_FOUND");
            return previous;
        }, token);
}
