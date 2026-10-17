using Eve.Data;
using Npgsql;

namespace Eve.FactionWarfare;

/// <summary>An occupied system: its warzone, occupier, LP pool and the victory points of the occupier's enemy.</summary>
public sealed record FwSystem(long SolarSystemId, long WarzoneId, long OccupierId, long LpPool, long AttackerPoints, long UpdatedMs);

/// <summary>A corporation's or alliance's enlistment: the faction it is in (0: none) and the change that takes effect at <see cref="EffectiveMs"/> (0: none pending).</summary>
public sealed record FwEnlistment(int OwnerKind, long OwnerId, long FactionId, long NextFactionId, long EffectiveMs);

/// <summary>
/// The factional warfare state in PostgreSQL (schema <c>factionwarfare</c>, migration 0001). Every method takes the caller's connection and
/// transaction; the rules and the transactions are in <see cref="FwService"/>.
/// </summary>
public sealed class FwStore
{
    public const string DefaultSchema = "factionwarfare";
    public const int CorporationKind = 1;
    public const int AllianceKind = 2;

    private const string SystemColumns = "solar_system_id, warzone_id, occupier_id, lp_pool, attacker_points, updated_ms";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public FwStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    public string Schema => _schema;

    public NpgsqlDataSource Source => _source;

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(FwStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>Inserts the occupied systems that have no row yet (the first start seeds them from the map and the SDE; later starts keep the state).</summary>
    public async Task SeedSystemsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<FwSystem> systems, CancellationToken cancellationToken = default)
    {
        foreach (var system in systems)
        {
            await Sql.ExecuteAsync(connection, transaction, Q(
                "INSERT INTO {schema}.fw_systems (solar_system_id, warzone_id, occupier_id, lp_pool, attacker_points, updated_ms) " +
                "VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (solar_system_id) DO NOTHING"),
                cancellationToken, system.SolarSystemId, system.WarzoneId, system.OccupierId, system.LpPool, system.AttackerPoints, system.UpdatedMs);
        }
    }

    /// <summary>Every occupied system, ordered by id.</summary>
    public async Task<IReadOnlyList<FwSystem>> SystemsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q($"SELECT {SystemColumns} FROM {{schema}}.fw_systems ORDER BY solar_system_id"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<FwSystem>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadSystem(reader));
        return rows;
    }

    /// <summary>The system's row, or null when it is not occupied. With <paramref name="forUpdate"/> the row is locked until the transaction ends.</summary>
    public async Task<FwSystem?> SystemAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long solarSystemId, bool forUpdate, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q($"SELECT {SystemColumns} FROM {{schema}}.fw_systems WHERE solar_system_id = $1{(forUpdate ? " FOR UPDATE" : "")}"), solarSystemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSystem(reader) : null;
    }

    /// <summary>Adds an LP donation to a system's pool, records it, and returns the new pool.</summary>
    public async Task<long> AddLpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, long characterId, long corporationId,
        long factionId, long amount, long donatedMs, CancellationToken cancellationToken = default)
    {
        await Sql.ExecuteAsync(connection, transaction, Q(
            "INSERT INTO {schema}.fw_lp_donations (solar_system_id, character_id, corporation_id, faction_id, amount, donated_ms) VALUES ($1, $2, $3, $4, $5, $6)"),
            cancellationToken, solarSystemId, characterId, corporationId, factionId, amount, donatedMs);
        var pool = await Sql.ScalarAsync(connection, transaction, Q(
            "UPDATE {schema}.fw_systems SET lp_pool = lp_pool + $2, updated_ms = $3 WHERE solar_system_id = $1 RETURNING lp_pool"),
            cancellationToken, solarSystemId, amount, donatedMs);
        return pool is long value ? value : throw new InvalidOperationException($"solar system {solarSystemId} is not occupied");
    }

    /// <summary>The enlistment of a corporation or an alliance, or null when it has never enlisted.</summary>
    public async Task<FwEnlistment?> EnlistmentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, int ownerKind, long ownerId, bool forUpdate, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q("SELECT owner_kind, owner_id, faction_id, next_faction_id, effective_ms FROM {schema}.fw_enlistments WHERE owner_kind = $1 AND owner_id = $2" +
              (forUpdate ? " FOR UPDATE" : "")), ownerKind, ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new FwEnlistment(reader.GetInt16(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    /// <summary>Writes an enlistment row (insert or replace).</summary>
    public Task SaveEnlistmentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, FwEnlistment enlistment, CancellationToken cancellationToken = default) =>
        Sql.ExecuteAsync(connection, transaction, Q(
            "INSERT INTO {schema}.fw_enlistments (owner_kind, owner_id, faction_id, next_faction_id, effective_ms) VALUES ($1, $2, $3, $4, $5) " +
            "ON CONFLICT (owner_kind, owner_id) DO UPDATE SET faction_id = EXCLUDED.faction_id, next_faction_id = EXCLUDED.next_faction_id, effective_ms = EXCLUDED.effective_ms"),
            cancellationToken, enlistment.OwnerKind, enlistment.OwnerId, enlistment.FactionId, enlistment.NextFactionId, enlistment.EffectiveMs);

    private static FwSystem ReadSystem(NpgsqlDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
}
