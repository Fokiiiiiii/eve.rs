using Eve.Data;
using Eve.Dogma;
using Npgsql;

namespace Eve.Fitting;

/// <summary>Whether the modules fitted to ships are online and what ships and modules have taken, in PostgreSQL, in the module's own schema (<c>fitting</c> unless told otherwise).</summary>
public sealed class PgModuleStateStore
{
    public const string DefaultSchema = "fitting";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgModuleStateStore(NpgsqlDataSource source, string schema = DefaultSchema)
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
        .. Migrations.Embedded(typeof(PgModuleStateStore).Assembly, "Migrations"),
        new Migration("0003_absolute_damage", """
            ALTER TABLE ship_condition DROP CONSTRAINT ship_condition_damage_check;
            ALTER TABLE ship_condition ADD CONSTRAINT ship_condition_damage_nonnegative
                CHECK (damage >= 0 AND damage < 'Infinity'::double precision);
            ALTER TABLE ship_condition DROP CONSTRAINT ship_condition_shield_charge_check;
            ALTER TABLE ship_condition ADD CONSTRAINT ship_condition_shield_charge_finite
                CHECK (shield_charge <= 1 AND shield_charge > '-Infinity'::double precision);
            ALTER TABLE ship_condition DROP CONSTRAINT ship_condition_armor_damage_check;
            ALTER TABLE ship_condition ADD CONSTRAINT ship_condition_armor_damage_nonnegative
                CHECK (armor_damage >= 0 AND armor_damage < 'Infinity'::double precision);
            """)
    ];

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>Sets how far a module is switched on, inside the caller's transaction. Offline is the absence of a row.</summary>
    public async Task SetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long itemId, ItemState state, CancellationToken cancellationToken = default)
    {
        await using var command = state == ItemState.Offline
            ? new NpgsqlCommand($"DELETE FROM {_schema}.module_state WHERE item_id = $1", connection, transaction) { Parameters = { new() { Value = itemId } } }
            : new NpgsqlCommand(
                $"INSERT INTO {_schema}.module_state (item_id, state) VALUES ($1, $2) ON CONFLICT (item_id) DO UPDATE SET state = EXCLUDED.state", connection, transaction)
            {
                Parameters = { new() { Value = itemId }, new() { Value = (short)state } },
            };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>How far each of these modules is switched on (a module with no row is offline).</summary>
    public async Task<IReadOnlyDictionary<long, ItemState>> StatesAsync(IEnumerable<long> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        var states = ids.ToDictionary(id => id, _ => ItemState.Offline);
        if (ids.Length == 0)
        {
            return states;
        }
        await using var command = _source.CreateCommand($"SELECT item_id, state FROM {_schema}.module_state WHERE item_id = ANY($1)");
        command.Parameters.Add(new NpgsqlParameter { Value = ids });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states[reader.GetInt64(0)] = (ItemState)reader.GetInt16(1);
        }
        return states;
    }

    /// <summary>What a ship has taken (a ship with no row has taken nothing).</summary>
    public async Task<ShipCondition> ShipConditionAsync(long shipId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT damage, charge, shield_charge, armor_damage, incapacitated FROM {_schema}.ship_condition WHERE item_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = shipId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ShipCondition(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetBoolean(4))
            : ShipCondition.Full;
    }

    /// <summary>Keeps what a ship has taken. A ship that has taken nothing has no row.</summary>
    public async Task SetShipConditionAsync(long shipId, ShipCondition condition, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetShipConditionAsync(connection, transaction, shipId, condition, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }


    public async Task SetShipConditionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long shipId,
        ShipCondition condition,
        CancellationToken cancellationToken = default)
    {
        var sql = condition == ShipCondition.Full
            ? $"DELETE FROM {_schema}.ship_condition WHERE item_id = $1"
            : $"INSERT INTO {_schema}.ship_condition (item_id, damage, charge, shield_charge, armor_damage, incapacitated) VALUES ($1, $2, $3, $4, $5, $6) " +
              "ON CONFLICT (item_id) DO UPDATE SET damage = EXCLUDED.damage, charge = EXCLUDED.charge, shield_charge = EXCLUDED.shield_charge, armor_damage = EXCLUDED.armor_damage, incapacitated = EXCLUDED.incapacitated";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = shipId });
        if (condition != ShipCondition.Full)
        {
            foreach (var value in new object[]
            {
                FiniteNonNegative(condition.Damage),
                Clamp(condition.Charge),
                FiniteAtMostOne(condition.ShieldCharge),
                FiniteNonNegative(condition.ArmorDamage),
                condition.Incapacitated,
            })
            {
                command.Parameters.Add(new NpgsqlParameter { Value = value });
            }
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static double FiniteNonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static double FiniteAtMostOne(double value) => double.IsFinite(value) ? Math.Min(1, value) : 0;

    private static double Clamp(double fraction) => Math.Clamp(fraction, 0, 1);

    /// <summary>What each of these modules has taken (a module with no row was never damaged or mended).</summary>
    public async Task<IReadOnlyDictionary<long, ModuleCondition>> ModuleConditionsAsync(IEnumerable<long> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        var conditions = new Dictionary<long, ModuleCondition>();
        if (ids.Length == 0)
        {
            return conditions;
        }
        await using var command = _source.CreateCommand($"SELECT item_id, damage, armor_damage, shield_charge, incapacitated FROM {_schema}.module_condition WHERE item_id = ANY($1)");
        command.Parameters.Add(new NpgsqlParameter { Value = ids });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conditions[reader.GetInt64(0)] = new ModuleCondition(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetBoolean(4));
        }
        return conditions;
    }

    /// <summary>Keeps what modules have taken. A module that is as nobody touched it has no row.</summary>
    public async Task SetModuleConditionsAsync(IReadOnlyDictionary<long, ModuleCondition> conditions, CancellationToken cancellationToken = default)
    {
        foreach (var (itemId, condition) in conditions)
        {
            await using var command = condition == ModuleCondition.Untouched
                ? _source.CreateCommand($"DELETE FROM {_schema}.module_condition WHERE item_id = $1")
                : _source.CreateCommand(
                    $"INSERT INTO {_schema}.module_condition (item_id, damage, shield_charge, armor_damage, incapacitated) VALUES ($1, $2, $3, $4, $5) " +
                    "ON CONFLICT (item_id) DO UPDATE SET damage = EXCLUDED.damage, shield_charge = EXCLUDED.shield_charge, armor_damage = EXCLUDED.armor_damage, incapacitated = EXCLUDED.incapacitated");
            command.Parameters.Add(new NpgsqlParameter { Value = itemId });
            if (condition != ModuleCondition.Untouched)
            {
                foreach (var value in new object[] { Clamp(condition.Damage), Clamp(condition.ShieldCharge), Clamp(condition.ArmorDamage), condition.Incapacitated })
                {
                    command.Parameters.Add(new NpgsqlParameter { Value = value });
                }
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
