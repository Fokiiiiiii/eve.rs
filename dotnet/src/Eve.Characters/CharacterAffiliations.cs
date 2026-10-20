using Npgsql;

namespace Eve.Characters;

public sealed partial class PgCharacterStore
{
    /// <summary>The authoritative character row, held until the caller's transaction ends.</summary>
    public async Task<Character?> LockAndGetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM {_schema}.characters WHERE id = $1 FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = id } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task SetAffiliationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        long corporationId, long? allianceId, long? factionId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"UPDATE {_schema}.characters SET corporation_id = $2, alliance_id = $3, faction_id = $4 WHERE id = $1", connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = corporationId },
                new() { Value = (object?)allianceId ?? DBNull.Value }, new() { Value = (object?)factionId ?? DBNull.Value } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetCorporationAllianceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long corporationId,
        long? allianceId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"UPDATE {_schema}.characters SET alliance_id = $2 WHERE corporation_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = corporationId }, new() { Value = (object?)allianceId ?? DBNull.Value } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Character>> ForCorporationAsync(long corporationId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.characters WHERE corporation_id = $1 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = corporationId });
        var characters = new List<Character>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            characters.Add(Read(reader));
        }
        return characters;
    }

    public const int CorporationAffiliationPageSize = 256;

    public async Task<IReadOnlyList<(long CharacterId, long CorporationId)>> AffiliationsAfterAsync(
        long afterCharacterId, int limit = CorporationAffiliationPageSize, CancellationToken cancellationToken = default)
    {
        if (afterCharacterId < 0)
            throw new ArgumentOutOfRangeException(nameof(afterCharacterId));
        if (limit is < 1 or > CorporationAffiliationPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be between 1 and {CorporationAffiliationPageSize}");

        await using var command = _source.CreateCommand(
            $"SELECT id, corporation_id FROM {_schema}.characters WHERE id > $1 ORDER BY id LIMIT $2");
        command.Parameters.Add(new NpgsqlParameter { Value = afterCharacterId });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        var affiliations = new List<(long CharacterId, long CorporationId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            affiliations.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return affiliations;
    }
}
