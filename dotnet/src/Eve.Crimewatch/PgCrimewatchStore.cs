using Eve.Data;
using Npgsql;

namespace Eve.Crimewatch;

/// <summary>What crimewatch keeps in PostgreSQL: the ship safety level of each character (later: the timers and flags that must outlast a session).</summary>
public sealed class PgCrimewatchStore
{
    public const string DefaultSchema = "crimewatch";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgCrimewatchStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgCrimewatchStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>The character's safety level; full when it has never set one.</summary>
    public async Task<int> SafetyOfAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var level = await Sql.ScalarAsync(connection, null, Q("SELECT level FROM {schema}.safety WHERE character_id = $1"), cancellationToken, characterId);
        return level is short stored ? stored : SafetyLevel.Full;
    }

    /// <summary>Sets the level (brought to 0, 1 or 2) and returns what is kept.</summary>
    public async Task<int> SetSafetyAsync(long characterId, double? requested, long nowMs, CancellationToken cancellationToken = default)
    {
        var level = SafetyLevel.Normalize(requested);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await Sql.ExecuteAsync(connection, null, Q("""
            INSERT INTO {schema}.safety (character_id, level, updated_ms) VALUES ($1, $2, $3)
            ON CONFLICT (character_id) DO UPDATE SET level = EXCLUDED.level, updated_ms = EXCLUDED.updated_ms
            """), cancellationToken, characterId, (short)level, nowMs);
        return level;
    }
}
