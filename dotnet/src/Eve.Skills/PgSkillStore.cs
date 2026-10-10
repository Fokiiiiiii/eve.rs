using Eve.Data;
using Npgsql;

namespace Eve.Skills;

/// <summary>A skill a character has: its level and the points in it.</summary>
public sealed record CharacterSkill(int TypeId, int Level, long SkillPoints);

public sealed record CharacterNeuralRemap(
    int Charisma, int Intelligence, int Memory, int Perception, int Willpower,
    int FreeRespecs, long? LastRespecFileTime, long? NextTimedRespecFileTime)
{
    public const int MinimumAttributeValue = 17;
    public const int MaximumAttributeValue = 27;
    public const int TotalRemapPoints = 99;
    public const long TimedRemapIntervalFileTime = 365L * 24 * 60 * 60 * 10_000_000;

    public static CharacterNeuralRemap Default { get; } = new(20, 20, 20, 20, 20, 3, null, null);

    public double Attribute(int id) => id switch
    {
        164 => Charisma,
        165 => Intelligence,
        166 => Memory,
        167 => Perception,
        168 => Willpower,
        _ => 20,
    };
}

public sealed class NeuralRemapCooldownException(long nextAvailableFileTime) : Exception
{
    public long NextAvailableFileTime { get; } = nextAvailableFileTime;
}

/// <summary>An active booster persisted with its expiry and selected side-effect IDs.</summary>
public sealed record StoredSkillBooster(long CharacterId, long ItemId, int BoosterTypeId, long ExpiryFileTime, IReadOnlyList<int> SideEffectIds);

/// <summary>Persists character skills, unallocated free skill points, and active booster state.</summary>
public sealed class PgSkillStore
{
    public const string DefaultSchema = "skills";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, CharacterNeuralRemap> _neuralRemaps = new();

    public PgSkillStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() =>
        [
            .. Migrations.Embedded(typeof(PgSkillStore).Assembly, "Migrations"),
            new Migration("0003_unallocated_skill_points", """
                CREATE TABLE IF NOT EXISTS character_free_skill_points (
                    character_id bigint PRIMARY KEY,
                    points bigint NOT NULL DEFAULT 0 CHECK (points >= 0)
                );
                """),
            new Migration("0004_active_skill_boosters", """
                CREATE TABLE IF NOT EXISTS active_skill_boosters (
                    item_id bigint PRIMARY KEY CHECK (item_id > 0),
                    character_id bigint NOT NULL CHECK (character_id > 0),
                    booster_type_id integer NOT NULL CHECK (booster_type_id > 0),
                    expiry_filetime bigint NOT NULL CHECK (expiry_filetime > 0),
                    side_effect_ids integer[] NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS active_skill_boosters_character_expiry
                    ON active_skill_boosters (character_id, expiry_filetime);
                """),
            new Migration("0005_daily_alpha_skill_injectors", """
                CREATE TABLE IF NOT EXISTS daily_alpha_skill_injector_uses (
                    character_id bigint NOT NULL CHECK (character_id > 0),
                    item_type_id integer NOT NULL CHECK (item_type_id > 0),
                    used_at_unix_ms bigint NOT NULL CHECK (used_at_unix_ms > 0),
                    PRIMARY KEY (character_id, item_type_id)
                );
                """),
            new Migration("0006_recent_skill_point_changes", """
                CREATE TABLE IF NOT EXISTS recent_skill_point_changes (
                    character_id bigint NOT NULL CHECK (character_id > 0),
                    type_id integer NOT NULL CHECK (type_id > 0),
                    point_change bigint NOT NULL CHECK (point_change > 0),
                    PRIMARY KEY (character_id, type_id)
                );
                CREATE TABLE IF NOT EXISTS skill_isis_fetch_budget (
                    character_id bigint PRIMARY KEY CHECK (character_id > 0),
                    fetch_budget smallint NOT NULL DEFAULT 0 CHECK (fetch_budget BETWEEN 0 AND 4)
                );
                """),
            new Migration("0007_character_neural_remaps", """
                CREATE TABLE IF NOT EXISTS character_neural_remaps (
                    character_id bigint PRIMARY KEY CHECK (character_id > 0),
                    charisma smallint NOT NULL CHECK (charisma BETWEEN 17 AND 27),
                    intelligence smallint NOT NULL CHECK (intelligence BETWEEN 17 AND 27),
                    memory smallint NOT NULL CHECK (memory BETWEEN 17 AND 27),
                    perception smallint NOT NULL CHECK (perception BETWEEN 17 AND 27),
                    willpower smallint NOT NULL CHECK (willpower BETWEEN 17 AND 27),
                    free_respecs integer NOT NULL DEFAULT 3 CHECK (free_respecs >= 0),
                    last_respec_filetime bigint CHECK (last_respec_filetime > 0),
                    next_timed_respec_filetime bigint CHECK (next_timed_respec_filetime > 0)
                );
                """),
        ];

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>Runs related item and booster-state writes in one caller-defined transaction.</summary>
    public async Task<T> InTransactionAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await action(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>Gives a character skills inside the caller's transaction (a skill it already has is replaced).</summary>
    public async Task GrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        IEnumerable<CharacterSkill> skills,
        CancellationToken cancellationToken = default,
        bool trackRecentChanges = false)
    {
        foreach (var skill in skills)
        {
            var previous = 0L;
            if (trackRecentChanges)
            {
                await using var previousCommand = new NpgsqlCommand(
                    $"SELECT skill_points FROM {_schema}.character_skills WHERE character_id = $1 AND type_id = $2",
                    connection, transaction)
                {
                    Parameters = { new() { Value = characterId }, new() { Value = skill.TypeId } },
                };
                previous = await previousCommand.ExecuteScalarAsync(cancellationToken) is long points ? points : 0L;
            }

            await using var command = new NpgsqlCommand(
                $"INSERT INTO {_schema}.character_skills (character_id, type_id, level, skill_points) VALUES ($1, $2, $3, $4) " +
                "ON CONFLICT (character_id, type_id) DO UPDATE SET level = EXCLUDED.level, skill_points = EXCLUDED.skill_points",
                connection, transaction)
            {
                Parameters = { new() { Value = characterId }, new() { Value = skill.TypeId }, new() { Value = (short)skill.Level }, new() { Value = skill.SkillPoints } },
            };
            await command.ExecuteNonQueryAsync(cancellationToken);

            var gained = skill.SkillPoints - previous;
            if (trackRecentChanges && gained > 0)
                await RecordRecentSkillPointChangeAsync(connection, transaction, characterId, skill.TypeId, gained, cancellationToken);
        }
    }

    private async Task RecordRecentSkillPointChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        int typeId,
        long pointChange,
        CancellationToken cancellationToken)
    {
        await using (var change = new NpgsqlCommand(
            $"INSERT INTO {_schema}.recent_skill_point_changes AS stored (character_id, type_id, point_change) VALUES ($1, $2, $3) " +
            "ON CONFLICT (character_id, type_id) DO UPDATE SET point_change = stored.point_change + EXCLUDED.point_change",
            connection, transaction))
        {
            change.Parameters.Add(new NpgsqlParameter { Value = characterId });
            change.Parameters.Add(new NpgsqlParameter { Value = typeId });
            change.Parameters.Add(new NpgsqlParameter { Value = pointChange });
            await change.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var budget = new NpgsqlCommand(
            $"INSERT INTO {_schema}.skill_isis_fetch_budget (character_id, fetch_budget) VALUES ($1, 4) " +
            "ON CONFLICT (character_id) DO UPDATE SET fetch_budget = 4",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        };
        await budget.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(int TypeId, long PointChange)>> ConsumeRecentSkillPointChangesAsync(
        long characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) return Array.Empty<(int, long)>();

        return await InTransactionAsync(async (connection, transaction, token) =>
        {
            await using (var characterLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
            {
                characterLock.Parameters.Add(new NpgsqlParameter { Value = $"skill-isis:{characterId}" });
                await characterLock.ExecuteScalarAsync(token);
            }

            short fetchBudget;
            await using (var readBudget = new NpgsqlCommand(
                $"SELECT fetch_budget FROM {_schema}.skill_isis_fetch_budget WHERE character_id = $1 FOR UPDATE",
                connection, transaction))
            {
                readBudget.Parameters.Add(new NpgsqlParameter { Value = characterId });
                fetchBudget = await readBudget.ExecuteScalarAsync(token) is short value ? value : (short)0;
            }

            var changes = new List<(int TypeId, long PointChange)>();
            await using (var readChanges = new NpgsqlCommand(
                $"SELECT type_id, point_change FROM {_schema}.recent_skill_point_changes WHERE character_id = $1 ORDER BY type_id",
                connection, transaction))
            {
                readChanges.Parameters.Add(new NpgsqlParameter { Value = characterId });
                await using var reader = await readChanges.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    changes.Add((reader.GetInt32(0), reader.GetInt64(1)));
            }
            if (changes.Count == 0) return (IReadOnlyList<(int TypeId, long PointChange)>)changes;

            if (fetchBudget <= 1)
            {
                await using (var clear = new NpgsqlCommand(
                    $"DELETE FROM {_schema}.recent_skill_point_changes WHERE character_id = $1",
                    connection, transaction))
                {
                    clear.Parameters.Add(new NpgsqlParameter { Value = characterId });
                    await clear.ExecuteNonQueryAsync(token);
                }
                await using var reset = new NpgsqlCommand(
                    $"UPDATE {_schema}.skill_isis_fetch_budget SET fetch_budget = 0 WHERE character_id = $1",
                    connection, transaction)
                {
                    Parameters = { new() { Value = characterId } },
                };
                await reset.ExecuteNonQueryAsync(token);
            }
            else
            {
                await using var decrement = new NpgsqlCommand(
                    $"UPDATE {_schema}.skill_isis_fetch_budget SET fetch_budget = $2 WHERE character_id = $1",
                    connection, transaction)
                {
                    Parameters = { new() { Value = characterId }, new() { Value = (short)(fetchBudget - 1) } },
                };
                await decrement.ExecuteNonQueryAsync(token);
            }
            return (IReadOnlyList<(int TypeId, long PointChange)>)changes;
        }, cancellationToken);
    }

    /// <summary>The character's skills, by type.</summary>
    public async Task<IReadOnlyList<CharacterSkill>> ForCharacterAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT type_id, level, skill_points FROM {_schema}.character_skills WHERE character_id = $1 ORDER BY type_id");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        var list = new List<CharacterSkill>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new CharacterSkill(reader.GetInt32(0), reader.GetInt16(1), reader.GetInt64(2)));
        }
        return list;
    }

    /// <summary>Read eligibility inside the caller's transaction, without a second snapshot.</summary>
    public async Task<int> LevelAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,long characterId,int typeId,CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"SELECT level FROM {_schema}.character_skills WHERE character_id=$1 AND type_id=$2",connection,transaction)
        {
            Parameters = { new() { Value=characterId },new() { Value=typeId } },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is short level ? level : 0;
    }

    /// <summary>The skill map in a caller-owned snapshot, used for fees and eligibility without eleven separate reads.</summary>
    public async Task<IReadOnlyList<CharacterSkill>> ForCharacterAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,long characterId,CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"SELECT type_id,level,skill_points FROM {_schema}.character_skills WHERE character_id=$1 ORDER BY type_id",connection,transaction)
        {
            Parameters = { new() { Value=characterId } },
        };
        var skills = new List<CharacterSkill>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) skills.Add(new(reader.GetInt32(0),reader.GetInt16(1),reader.GetInt64(2)));
        return skills;
    }

    /// <summary>The total skill points of each of the characters (0 for one with no skills).</summary>
    public async Task<IReadOnlyDictionary<long, long>> TotalPointsAsync(IEnumerable<long> characterIds, CancellationToken cancellationToken = default)
    {
        var ids = characterIds.ToArray();
        var totals = ids.ToDictionary(id => id, _ => 0L);
        if (ids.Length == 0)
        {
            return totals;
        }
        await using var command = _source.CreateCommand(
            $"SELECT character_id, sum(skill_points)::bigint FROM {_schema}.character_skills WHERE character_id = ANY($1) GROUP BY character_id");
        command.Parameters.Add(new NpgsqlParameter { Value = ids });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            totals[reader.GetInt64(0)] = reader.GetInt64(1);
        }
        return totals;
    }

    /// <summary>The character's unallocated skill points (zero until an authoritative grant is recorded).</summary>
    public async Task<long> FreeSkillPointsAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT points FROM {_schema}.character_free_skill_points WHERE character_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return await command.ExecuteScalarAsync(cancellationToken) is long points ? points : 0L;
    }


    public async Task<long> FreeSkillPointsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT points FROM {_schema}.character_free_skill_points WHERE character_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        };
        return (long?)await command.ExecuteScalarAsync(cancellationToken) ?? 0;
    }

    public async Task<long> TotalPointsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT COALESCE(sum(skill_points), 0)::bigint FROM {_schema}.character_skills WHERE character_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    /// <summary>Credits unallocated points inside the caller's transaction, for reward and injector providers.</summary>
    public async Task<long> GrantFreeSkillPointsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long points,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (points <= 0) throw new ArgumentOutOfRangeException(nameof(points));

        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.character_free_skill_points AS balance (character_id, points) VALUES ($1, $2) " +
            "ON CONFLICT (character_id) DO UPDATE SET points = balance.points + EXCLUDED.points RETURNING points",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = points } },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("free skill point grant did not return a balance"));
    }


    public async Task<bool> TryUseDailyAlphaSkillInjectorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        int itemTypeId,
        long nowUnixMs,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.daily_alpha_skill_injector_uses AS usage
                (character_id, item_type_id, used_at_unix_ms)
            VALUES ($1, $2, $3)
            ON CONFLICT (character_id, item_type_id) DO UPDATE
                SET used_at_unix_ms = EXCLUDED.used_at_unix_ms
                WHERE usage.used_at_unix_ms <= EXCLUDED.used_at_unix_ms - 86400000
            RETURNING 1
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = characterId },
                new() { Value = itemTypeId },
                new() { Value = nowUnixMs },
            },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Reads and locks the balance row, creating its zero row inside the caller's transaction if absent.</summary>
    internal async Task<long> LockFreeSkillPointsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CancellationToken cancellationToken = default)
    {
        await using (var create = new NpgsqlCommand(
            $"INSERT INTO {_schema}.character_free_skill_points (character_id, points) VALUES ($1, 0) ON CONFLICT (character_id) DO NOTHING",
            connection, transaction))
        {
            create.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = new NpgsqlCommand(
            $"SELECT points FROM {_schema}.character_free_skill_points WHERE character_id = $1 FOR UPDATE",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        };
        return (long)(await read.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("free skill point balance row disappeared"));
    }

    /// <summary>
    /// Applies a validated plan only if the skill rows still match its snapshot and the locked balance can pay for it.
    /// Skill updates and debit remain part of the caller's transaction.
    /// </summary>
    internal async Task<long?> ApplyFreeSkillPointsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        IReadOnlyDictionary<int, CharacterSkill> before,
        IReadOnlyList<CharacterSkill> after,
        long pointsSpent,
        CancellationToken cancellationToken = default)
    {
        if (pointsSpent <= 0 || after.Count == 0 || after.Select(skill => skill.TypeId).Distinct().Count() != after.Count)
        {
            return null;
        }

        var ids = after.Select(skill => skill.TypeId).ToArray();
        var current = new Dictionary<int, CharacterSkill>();
        await using (var query = new NpgsqlCommand(
            $"SELECT type_id, level, skill_points FROM {_schema}.character_skills WHERE character_id = $1 AND type_id = ANY($2) ORDER BY type_id FOR UPDATE",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = ids } },
        })
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var skill = new CharacterSkill(reader.GetInt32(0), reader.GetInt16(1), reader.GetInt64(2));
                current.Add(skill.TypeId, skill);
            }
        }

        long calculatedSpend = 0;
        foreach (var updated in after)
        {
            if (!before.TryGetValue(updated.TypeId, out var expected) ||
                !current.TryGetValue(updated.TypeId, out var persisted) ||
                persisted != expected ||
                updated.SkillPoints <= expected.SkillPoints ||
                updated.Level is < 0 or > 5)
            {
                return null;
            }
            calculatedSpend = checked(calculatedSpend + updated.SkillPoints - expected.SkillPoints);
        }
        if (calculatedSpend != pointsSpent)
        {
            return null;
        }

        var balance = await LockFreeSkillPointsAsync(connection, transaction, characterId, cancellationToken);
        if (balance < pointsSpent)
        {
            return null;
        }

        await GrantAsync(connection, transaction, characterId, after, cancellationToken, trackRecentChanges: true);
        await using var debit = new NpgsqlCommand(
            $"UPDATE {_schema}.character_free_skill_points SET points = points - $2 WHERE character_id = $1 AND points >= $2 RETURNING points",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = pointsSpent } },
        };
        return await debit.ExecuteScalarAsync(cancellationToken) is long remaining
            ? remaining
            : throw new InvalidOperationException("locked free skill point balance could not be debited");
    }

    /// <summary>Persists an activation in the caller's item transaction. The item id makes retries no-ops.</summary>
    public async Task<bool> TryPersistActiveBoosterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long itemId,
        int boosterTypeId,
        long expiryFileTime,
        IReadOnlyList<int> sideEffectIds,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        if (boosterTypeId <= 0) throw new ArgumentOutOfRangeException(nameof(boosterTypeId));
        if (expiryFileTime <= 0) throw new ArgumentOutOfRangeException(nameof(expiryFileTime));
        ArgumentNullException.ThrowIfNull(sideEffectIds);

        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.active_skill_boosters (item_id, character_id, booster_type_id, expiry_filetime, side_effect_ids) " +
            "VALUES ($1, $2, $3, $4, $5) ON CONFLICT (item_id) DO NOTHING",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = itemId },
                new() { Value = characterId },
                new() { Value = boosterTypeId },
                new() { Value = expiryFileTime },
                new() { Value = sideEffectIds.ToArray() },
            },
        };
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Reads boosters whose client FILETIME expiry is strictly after now.</summary>
    public async Task<IReadOnlyList<StoredSkillBooster>> ActiveSkillBoostersAsync(
        long characterId,
        long nowFileTime,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (nowFileTime < 0) throw new ArgumentOutOfRangeException(nameof(nowFileTime));

        await using var command = _source.CreateCommand(
            $"SELECT character_id, item_id, booster_type_id, expiry_filetime, side_effect_ids FROM {_schema}.active_skill_boosters " +
            "WHERE character_id = $1 AND expiry_filetime > $2 ORDER BY item_id");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowFileTime });
        return await ReadSkillBoostersAsync(command, cancellationToken);
    }

    /// <summary>Serializes activations for one character so two requests cannot occupy the same booster slot.</summary>
    public async Task LockActiveSkillBoostersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('active_skill_boosters:' || $1::text, 0))",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        };
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>Reads active booster rows using the same transaction as the character's inventory lock.</summary>
    public async Task<IReadOnlyList<StoredSkillBooster>> ActiveSkillBoostersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long nowFileTime,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (nowFileTime < 0) throw new ArgumentOutOfRangeException(nameof(nowFileTime));

        await using var command = new NpgsqlCommand(
            $"SELECT character_id, item_id, booster_type_id, expiry_filetime, side_effect_ids FROM {_schema}.active_skill_boosters " +
            "WHERE character_id = $1 AND expiry_filetime > $2 ORDER BY item_id",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = nowFileTime } },
        };
        return await ReadSkillBoostersAsync(command, cancellationToken);
    }

    /// <summary>Reads expired booster rows using the same transaction as item removal.</summary>
    public async Task<IReadOnlyList<StoredSkillBooster>> ExpiredSkillBoostersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long nowFileTime,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (nowFileTime < 0) throw new ArgumentOutOfRangeException(nameof(nowFileTime));

        await using var command = new NpgsqlCommand(
            $"SELECT character_id, item_id, booster_type_id, expiry_filetime, side_effect_ids FROM {_schema}.active_skill_boosters " +
            "WHERE character_id = $1 AND expiry_filetime <= $2 ORDER BY item_id",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = nowFileTime } },
        };
        return await ReadSkillBoostersAsync(command, cancellationToken);
    }

    /// <summary>Reads one live booster by its owning character and item id.</summary>
    public async Task<StoredSkillBooster?> ActiveSkillBoosterAsync(
        long characterId,
        long itemId,
        long nowFileTime,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        if (nowFileTime < 0) throw new ArgumentOutOfRangeException(nameof(nowFileTime));

        await using var command = _source.CreateCommand(
            $"SELECT character_id, item_id, booster_type_id, expiry_filetime, side_effect_ids FROM {_schema}.active_skill_boosters " +
            "WHERE character_id = $1 AND item_id = $2 AND expiry_filetime > $3");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = itemId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowFileTime });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredSkillBooster(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetFieldValue<int[]>(4))
            : null;
    }

    /// <summary>Reads expired rows without changing state; repeated reads are idempotent.</summary>
    public async Task<IReadOnlyList<StoredSkillBooster>> ExpiredSkillBoostersAsync(
        long characterId,
        long nowFileTime,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (nowFileTime < 0) throw new ArgumentOutOfRangeException(nameof(nowFileTime));

        await using var command = _source.CreateCommand(
            $"SELECT character_id, item_id, booster_type_id, expiry_filetime, side_effect_ids FROM {_schema}.active_skill_boosters " +
            "WHERE character_id = $1 AND expiry_filetime <= $2 ORDER BY item_id");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowFileTime });
        return await ReadSkillBoostersAsync(command, cancellationToken);
    }

    /// <summary>Deletes expired state inside the caller's transaction; repeated calls are no-ops.</summary>
    public async Task<int> DeleteExpiredSkillBoostersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long nowFileTime,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (nowFileTime < 0) throw new ArgumentOutOfRangeException(nameof(nowFileTime));

        await using var command = new NpgsqlCommand(
            $"DELETE FROM {_schema}.active_skill_boosters WHERE character_id = $1 AND expiry_filetime <= $2",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = nowFileTime } },
        };
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<StoredSkillBooster>> ReadSkillBoostersAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var boosters = new List<StoredSkillBooster>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            boosters.Add(new StoredSkillBooster(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetFieldValue<int[]>(4)));
        }
        return boosters;
    }

    public double LearningAttribute(long characterId, int attributeId) =>
        _neuralRemaps.TryGetValue(characterId, out var remap) ? remap.Attribute(attributeId) : 20;

    internal void CacheNeuralRemap(long characterId, CharacterNeuralRemap remap) => _neuralRemaps[characterId] = remap;

    public async Task<CharacterNeuralRemap> NeuralRemapAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var remap = await ReadNeuralRemapAsync(connection, null, characterId, forUpdate: false, cancellationToken);
        CacheNeuralRemap(characterId, remap);
        return remap;
    }

    internal async Task<CharacterNeuralRemap> ReadNeuralRemapAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId,
        bool forUpdate, CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT charisma, intelligence, memory, perception, willpower, free_respecs, last_respec_filetime, next_timed_respec_filetime FROM {_schema}.character_neural_remaps WHERE character_id = $1" +
            (forUpdate ? " FOR UPDATE" : "");
        await using var command = new NpgsqlCommand(sql, connection, transaction) { Parameters = { new() { Value = characterId } } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return CharacterNeuralRemap.Default;
        return new CharacterNeuralRemap(
            reader.GetInt16(0), reader.GetInt16(1), reader.GetInt16(2), reader.GetInt16(3), reader.GetInt16(4),
            reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7));
    }

    internal async Task<CharacterNeuralRemap> UpdateNeuralRemapAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        IReadOnlyDictionary<int, int> attributes, long nowFileTime, CancellationToken cancellationToken = default)
    {
        var current = await ReadNeuralRemapAsync(connection, transaction, characterId, forUpdate: true, cancellationToken);
        var freeRespecs = current.FreeRespecs;
        var lastRespecFileTime = nowFileTime;
        var nextTimedRespecFileTime = current.NextTimedRespecFileTime;
        if (nextTimedRespecFileTime is null || nextTimedRespecFileTime <= nowFileTime)
        {
            nextTimedRespecFileTime = nowFileTime + CharacterNeuralRemap.TimedRemapIntervalFileTime;
        }
        else if (freeRespecs > 0)
        {
            freeRespecs--;
        }
        else
        {
            throw new NeuralRemapCooldownException(nextTimedRespecFileTime.Value);
        }

        var updated = current with
        {
            Charisma = attributes[164],
            Intelligence = attributes[165],
            Memory = attributes[166],
            Perception = attributes[167],
            Willpower = attributes[168],
            FreeRespecs = freeRespecs,
            LastRespecFileTime = lastRespecFileTime,
            NextTimedRespecFileTime = nextTimedRespecFileTime,
        };
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.character_neural_remaps (character_id, charisma, intelligence, memory, perception, willpower, free_respecs, last_respec_filetime, next_timed_respec_filetime) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) " +
            "ON CONFLICT (character_id) DO UPDATE SET charisma = EXCLUDED.charisma, intelligence = EXCLUDED.intelligence, memory = EXCLUDED.memory, perception = EXCLUDED.perception, willpower = EXCLUDED.willpower, free_respecs = EXCLUDED.free_respecs, last_respec_filetime = EXCLUDED.last_respec_filetime, next_timed_respec_filetime = EXCLUDED.next_timed_respec_filetime",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = characterId }, new() { Value = (short)updated.Charisma }, new() { Value = (short)updated.Intelligence },
                new() { Value = (short)updated.Memory }, new() { Value = (short)updated.Perception }, new() { Value = (short)updated.Willpower },
                new() { Value = updated.FreeRespecs }, new() { Value = updated.LastRespecFileTime is { } last ? last : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint },
                new() { Value = updated.NextTimedRespecFileTime is { } next ? next : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
        return updated;
    }
}
