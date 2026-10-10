using Eve.Data;
using Npgsql;

namespace Eve.Characters;

public sealed record Character(
    long Id, int AccountId, string Name, int Gender, int RaceId, int BloodlineId, int AncestryId, int SchoolId, int TypeId,
    long CorporationId, long? FactionId, long? AllianceId, long StationId, long HomeStationId,
    int SolarSystemId, int ConstellationId, int RegionId, long? ShipId,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLogoffAt, DateTimeOffset? DeletePrepareAt)
{
    /// <summary>A character queued for deletion cannot be played.</summary>
    public bool IsQueuedForDeletion => DeletePrepareAt is not null;

    public string TutorialEntryMode { get; init; } = "disabled";

    public string TutorialFirstLoginHandoff { get; init; } = "none";

    public int AirNpeState { get; init; } = 2;

    public bool AirNpeRevealOnFirstLogin { get; init; }

    public long? DirectFactionId { get; init; }

    public long DirectEnlistmentCooldownFileTime { get; init; }
}

public sealed record NewCharacter(
    int AccountId, string Name, int Gender, int RaceId, int BloodlineId, int AncestryId, int SchoolId, int TypeId,
    long CorporationId, long? FactionId, long StationId, int SolarSystemId, int ConstellationId, int RegionId,
    byte[]? DollInfo, byte[]? PortraitInfo)
{
    public string TutorialEntryMode { get; init; } = "disabled";

    public string TutorialFirstLoginHandoff { get; init; } = "none";

    public int AirNpeState { get; init; } = 2;

    public bool AirNpeRevealOnFirstLogin { get; init; }
}

/// <summary>The name is already a character's (in any spelling that folds to the same key).</summary>
public sealed class NameTakenException(string name) : Exception($"the name {name} is taken");

/// <summary>Characters in PostgreSQL, in the module's own schema (<c>characters</c> unless told otherwise).</summary>
public sealed partial class PgCharacterStore
{
    public const string DefaultSchema = "characters";

    private const string Columns =
        "id, account_id, name, gender, race_id, bloodline_id, ancestry_id, school_id, type_id, corporation_id, faction_id, alliance_id, " +
        "station_id, home_station_id, solar_system_id, constellation_id, region_id, ship_id, created_at, last_logoff_at, delete_prepare_at, " +
        "tutorial_entry_mode, tutorial_first_login_handoff, air_npe_state, air_npe_reveal_on_first_login, direct_faction_id, direct_enlistment_cooldown_filetime";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgCharacterStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgCharacterStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private static Character Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetInt16(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8),
        r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11), r.GetInt64(12), r.GetInt64(13),
        r.GetInt32(14), r.GetInt32(15), r.GetInt32(16), r.IsDBNull(17) ? null : r.GetInt64(17),
        r.GetFieldValue<DateTimeOffset>(18), r.IsDBNull(19) ? null : r.GetFieldValue<DateTimeOffset>(19), r.IsDBNull(20) ? null : r.GetFieldValue<DateTimeOffset>(20))
    {
        TutorialEntryMode = r.GetString(21),
        TutorialFirstLoginHandoff = r.GetString(22),
        AirNpeState = r.GetInt16(23),
        AirNpeRevealOnFirstLogin = r.GetBoolean(24),
        DirectFactionId = r.IsDBNull(25) ? null : r.GetInt64(25),
        DirectEnlistmentCooldownFileTime = r.GetInt64(26),
    };

    /// <summary>The account's characters, oldest first (the order the selection screen shows them).</summary>
    public async Task<IReadOnlyList<Character>> ForAccountAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.characters WHERE account_id = $1 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        var list = new List<Character>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<Character?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.characters WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>The characters with these ids, by id (an id that is not a character is left out).</summary>
    public async Task<IReadOnlyList<Character>> GetManyAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        var wanted = ids.ToArray();
        var list = new List<Character>();
        if (wanted.Length == 0)
        {
            return list;
        }
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.characters WHERE id = ANY($1) ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = wanted });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<IReadOnlyList<Character>> AllAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.characters ORDER BY id");
        var list = new List<Character>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(Read(reader));
        return list;
    }

    public async Task<bool> SetGenderAsync(long characterId, int gender, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"UPDATE {_schema}.characters SET gender = $1 WHERE id = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = (short)gender });
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> SetBloodlineAsync(long characterId, int bloodlineId, int raceId, int typeId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"UPDATE {_schema}.characters SET bloodline_id = $1, race_id = $2, type_id = $3 WHERE id = $4");
        command.Parameters.Add(new NpgsqlParameter { Value = bloodlineId });
        command.Parameters.Add(new NpgsqlParameter { Value = raceId });
        command.Parameters.Add(new NpgsqlParameter { Value = typeId });
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }


    public async Task<bool> SetFactionWarfareStateAsync(
        long characterId,
        long? directFactionId,
        long cooldownFileTime,
        CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"UPDATE {_schema}.characters SET direct_faction_id = $1, direct_enlistment_cooldown_filetime = $2 WHERE id = $3");
        command.Parameters.Add(new NpgsqlParameter { Value = directFactionId is > 0 ? (object)directFactionId.Value : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = Math.Max(0, cooldownFileTime) });
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ClearDeletePrepareAsync(long characterId, int accountId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"UPDATE {_schema}.characters SET delete_prepare_at = NULL WHERE id = $1 AND account_id = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// The shared persistence transition used by character selection and <c>air_npe.skip_air_npe</c>.
    /// Returns the new state only when this call changed it.
    /// </summary>
    public async Task<int?> MarkTutorialSkippedAsync(long characterId, bool airNpeEnabled, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"UPDATE {_schema}.characters SET air_npe_state = 3, tutorial_first_login_handoff = 'none', air_npe_reveal_on_first_login = FALSE " +
            "WHERE id = $1 AND (($2 AND tutorial_entry_mode = 'tutorial' AND air_npe_state = 1) OR " +
            "(tutorial_entry_mode = 'cinematic_intro_overlay' AND tutorial_first_login_handoff <> 'none' AND air_npe_state = 2))");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = airNpeEnabled });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0 ? 3 : null;
    }

    public async Task<bool> ConsumeTutorialRevealOnFirstLoginAsync(long characterId, bool airNpeEnabled, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"UPDATE {_schema}.characters SET air_npe_reveal_on_first_login = FALSE WHERE id = $1 AND air_npe_state = 2 " +
            "AND air_npe_reveal_on_first_login = TRUE AND (($2 AND tutorial_entry_mode = 'tutorial') OR tutorial_entry_mode = 'cinematic_intro_overlay')");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = airNpeEnabled });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> CountForAccountAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT count(*) FROM {_schema}.characters WHERE account_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Serialises the creations of one account (until the transaction ends), so that two at once cannot both take the
    /// last free slot.
    /// </summary>
    public static async Task LockAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int accountId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1, $2)", connection, transaction)
        {
            Parameters = { new() { Value = AccountLockClass }, new() { Value = accountId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const int AccountLockClass = 0x43484152; // "CHAR"

    public async Task<int> CountForAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int accountId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_schema}.characters WHERE account_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = accountId } },
        };
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Whether a character has this name (in any spelling that folds to the same key).</summary>
    public async Task<bool> NameTakenAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT EXISTS (SELECT 1 FROM {_schema}.characters WHERE name_key = $1)");
        command.Parameters.Add(new NpgsqlParameter { Value = CharacterNames.Key(name) });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Adds a character inside the caller's transaction. Throws <see cref="NameTakenException"/> for a taken name.</summary>
    public async Task<long> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewCharacter c, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.characters (account_id, name, name_key, gender, race_id, bloodline_id, ancestry_id, school_id, type_id, " +
            "corporation_id, faction_id, station_id, home_station_id, solar_system_id, constellation_id, region_id, doll_info, portrait_info, " +
            "tutorial_entry_mode, tutorial_first_login_handoff, air_npe_state, air_npe_reveal_on_first_login) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21) RETURNING id",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = c.AccountId },
                new() { Value = c.Name },
                new() { Value = CharacterNames.Key(c.Name) },
                new() { Value = (short)c.Gender },
                new() { Value = c.RaceId },
                new() { Value = c.BloodlineId },
                new() { Value = c.AncestryId },
                new() { Value = c.SchoolId },
                new() { Value = c.TypeId },
                new() { Value = c.CorporationId },
                new() { Value = (object?)c.FactionId ?? DBNull.Value },
                new() { Value = c.StationId },
                new() { Value = c.SolarSystemId },
                new() { Value = c.ConstellationId },
                new() { Value = c.RegionId },
                new() { Value = (object?)c.DollInfo ?? DBNull.Value },
                new() { Value = (object?)c.PortraitInfo ?? DBNull.Value },
                new() { Value = c.TutorialEntryMode },
                new() { Value = c.TutorialFirstLoginHandoff },
                new() { Value = (short)c.AirNpeState },
                new() { Value = c.AirNpeRevealOnFirstLogin },
            },
        };
        try
        {
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation && error.ConstraintName?.Contains("name_key", StringComparison.Ordinal) == true)
        {
            throw new NameTakenException(c.Name);
        }
    }

    public async Task SetShipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long shipId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"UPDATE {_schema}.characters SET ship_id = $1 WHERE id = $2", connection, transaction)
        {
            Parameters = { new() { Value = shipId }, new() { Value = characterId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records the station a character is docked at (or last was), inside the caller's transaction.</summary>
    public async Task SetStationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long stationId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"UPDATE {_schema}.characters SET station_id = $1 WHERE id = $2", connection, transaction)
        {
            Parameters = { new() { Value = stationId }, new() { Value = characterId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Moves a character to a solar system (with its constellation and region) in its own transaction; the station is not changed.</summary>
    public async Task SetSolarSystemAsync(long characterId, int solarSystemId, int constellationId, int regionId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"UPDATE {_schema}.characters SET solar_system_id = $1, constellation_id = $2, region_id = $3 WHERE id = $4");
        command.Parameters.Add(new() { Value = solarSystemId });
        command.Parameters.Add(new() { Value = constellationId });
        command.Parameters.Add(new() { Value = regionId });
        command.Parameters.Add(new() { Value = characterId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records the solar system a character is in (with its constellation and region), inside the caller's transaction.</summary>
    public async Task SetLocationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, int solarSystemId, int constellationId, int regionId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"UPDATE {_schema}.characters SET solar_system_id = $1, constellation_id = $2, region_id = $3 WHERE id = $4", connection, transaction)
        {
            Parameters = { new() { Value = solarSystemId }, new() { Value = constellationId }, new() { Value = regionId }, new() { Value = characterId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>What the client sent for the paper doll and the portrait, as it was sent.</summary>
    public async Task<(byte[]? Doll, byte[]? Portrait)> AppearanceAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT doll_info, portrait_info FROM {_schema}.characters WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.IsDBNull(0) ? null : (byte[])reader[0], reader.IsDBNull(1) ? null : (byte[])reader[1])
            : (null, null);
    }


    /// <summary>The paper doll and portrait payloads for a batch of character ids.</summary>
    public async Task<IReadOnlyDictionary<long, (byte[]? Doll, byte[]? Portrait)>> AppearanceManyAsync(
        IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        var wanted = ids.Distinct().ToArray();
        var result = new Dictionary<long, (byte[]? Doll, byte[]? Portrait)>();
        if (wanted.Length == 0) return result;
        await using var command = _source.CreateCommand(
            $"SELECT id, doll_info, portrait_info FROM {_schema}.characters WHERE id = ANY($1)");
        command.Parameters.Add(new NpgsqlParameter { Value = wanted });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetInt64(0)] = (
                reader.IsDBNull(1) ? null : (byte[])reader[1],
                reader.IsDBNull(2) ? null : (byte[])reader[2]);
        return result;
    }

    /// <summary>Updates only appearance fields the client supplied; a deleted doll clears both.</summary>
    public async Task<bool> SetAppearanceAsync(long id, byte[]? doll, bool updateDoll,
        byte[]? portrait, bool updatePortrait, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"UPDATE {_schema}.characters SET doll_info = CASE WHEN $2 THEN $3 ELSE doll_info END, " +
            "portrait_info = CASE WHEN $4 THEN $5 ELSE portrait_info END WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = updateDoll });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)doll ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = updatePortrait });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)portrait ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
