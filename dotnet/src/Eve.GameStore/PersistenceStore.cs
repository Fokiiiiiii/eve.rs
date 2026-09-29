using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Eve.GameStore;

/// <summary>
/// The durable side of the game store on PostgreSQL. One table per logical
/// table, <c>"&lt;schema&gt;"."&lt;table&gt;"(key text primary key, doc jsonb, rev bigint)</c>, plus:
/// <list type="bullet">
/// <item><c>_persistence_owners</c>: role leases with an epoch (the fence).</item>
/// <item><c>_persistence_outbox</c>: at most one journaled batch per table, so
/// an interrupted flush is replayed exactly, never re-derived.</item>
/// <item><c>_migrations</c>: tables already imported from a legacy source.</item>
/// </list>
/// Every mutation validates its fence in its own transaction (row share lock on
/// the owner row), so a successor that took over cannot be raced.
/// </summary>
public sealed partial class PersistenceStore : IAsyncDisposable
{
    public const long MinLeaseMs = 1_000;
    public const long DefaultLeaseMs = 30_000;
    private const int MaxJsonDepth = 256;

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = MaxJsonDepth };

    private readonly NpgsqlDataSource _source;
    private readonly bool _ownsSource;
    private readonly Func<long>? _clockMs;
    private readonly ConcurrentDictionary<string, byte> _ensuredTables = new(StringComparer.Ordinal);

    /// <param name="connectionString">A PostgreSQL connection string.</param>
    /// <param name="schema">The schema that holds every store table.</param>
    /// <param name="clockMs">Test hook: milliseconds since the Unix epoch. Defaults to the database clock.</param>
    public PersistenceStore(string connectionString, string schema = "gamestore", Func<long>? clockMs = null)
        : this(NpgsqlDataSource.Create(connectionString), schema, ownsSource: true, clockMs)
    {
    }

    public PersistenceStore(NpgsqlDataSource source, string schema = "gamestore", bool ownsSource = false, Func<long>? clockMs = null)
    {
        Schema = TableNames.RequireSafe(schema);
        _source = source;
        _ownsSource = ownsSource;
        _clockMs = clockMs;
    }

    public string Schema { get; }

    public ValueTask DisposeAsync() => _ownsSource ? _source.DisposeAsync() : ValueTask.CompletedTask;

    private string Qualified(string table) => $"\"{Schema}\".\"{table}\"";

    /// <summary>Creates the schema and the three internal tables. Safe to call from several processes at once.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, "schema", ct);
        await ExecuteAsync(connection, tx, $"""
            CREATE SCHEMA IF NOT EXISTS "{Schema}";

            CREATE TABLE IF NOT EXISTS {Qualified(TableNames.Owners)} (
              owner_role       text PRIMARY KEY,
              epoch            bigint NOT NULL CHECK (epoch > 0),
              instance_id      text NOT NULL,
              active           boolean NOT NULL,
              exclusive        boolean NOT NULL DEFAULT false,
              lease_expires_at bigint NOT NULL,
              acquired_at      timestamptz NOT NULL,
              released_at      timestamptz
            );

            CREATE TABLE IF NOT EXISTS {Qualified(TableNames.Outbox)} (
              operation_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
              table_name        text NOT NULL UNIQUE,
              owner_role        text NOT NULL,
              owner_epoch       bigint NOT NULL CHECK (owner_epoch > 0),
              owner_instance_id text NOT NULL,
              upserts           jsonb NOT NULL,
              deletes           jsonb NOT NULL,
              state             text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'applied')),
              created_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
              applied_at        timestamptz,
              CHECK ((state = 'pending' AND applied_at IS NULL) OR (state = 'applied' AND applied_at IS NOT NULL))
            );

            CREATE TABLE IF NOT EXISTS {Qualified(TableNames.Migrations)} (
              table_name  text PRIMARY KEY,
              migrated_at timestamptz NOT NULL
            );
            """, ct);
        await tx.CommitAsync(ct);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Serialises work that must not interleave, without holding a row lock.</summary>
    private Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string name, CancellationToken ct) =>
        ExecuteAsync(
            connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@name, 0))", ct,
            ("name", NpgsqlDbType.Text, $"eve.gamestore:{Schema}:{name}"));

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, params (string Name, NpgsqlDbType Type, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, type, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        }
        return command;
    }

    private static Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string Name, NpgsqlDbType Type, object? Value)[] parameters) =>
        RunAsync(async () =>
        {
            await using var command = Command(connection, tx, sql, parameters);
            return await command.ExecuteNonQueryAsync(ct);
        });

    /// <summary>Maps the one database error the JSON payload can cause to a store error.</summary>
    private static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UntranslatableCharacter)
        {
            throw new PersistenceException(
                "a JSON document contains \\u0000, which PostgreSQL jsonb cannot store",
                PersistenceCodes.UnsupportedJson,
                inner: ex);
        }
    }

    /// <summary>Milliseconds since the Unix epoch on the database clock, so hosts with skewed clocks agree.</summary>
    private async Task<long> NowAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        if (_clockMs is not null)
        {
            return _clockMs();
        }
        await using var command = Command(connection, tx, "SELECT (extract(epoch from clock_timestamp()) * 1000)::bigint");
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static string SerializeUpserts(IReadOnlyList<RowUpsert> upserts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var upsert in upserts)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(upsert.Key);
                writer.WriteRawValue(upsert.Json, skipInputValidation: true);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static string SerializeDeletes(IReadOnlyList<string> deletes) => JsonSerializer.Serialize(deletes);

    private static List<RowUpsert> ParseUpserts(string json)
    {
        using var document = JsonDocument.Parse(json, JsonOptions);
        var result = new List<RowUpsert>(document.RootElement.GetArrayLength());
        foreach (var pair in document.RootElement.EnumerateArray())
        {
            result.Add(new RowUpsert(pair[0].GetString()!, pair[1].GetRawText()));
        }
        return result;
    }

    private static List<string> ParseDeletes(string json)
    {
        using var document = JsonDocument.Parse(json, JsonOptions);
        return document.RootElement.EnumerateArray().Select(element => element.GetString()!).ToList();
    }

    /// <summary>Rejects anything that is not a (key, valid JSON text) pair before it can reach a journal.</summary>
    private static List<RowUpsert> ValidateUpserts(IEnumerable<RowUpsert>? upserts, string label = "persistence upserts")
    {
        var result = new List<RowUpsert>();
        var index = 0;
        foreach (var upsert in upserts ?? [])
        {
            if (upsert.Key is null)
            {
                throw new ArgumentException($"{label}[{index}] must have a string key");
            }
            if (upsert.Json is null)
            {
                throw new ArgumentException($"{label}[{index}] must have a JSON string");
            }
            try
            {
                using var _ = JsonDocument.Parse(upsert.Json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"{label}[{index}] is not valid JSON: {ex.Message}", ex);
            }
            result.Add(upsert);
            index += 1;
        }
        return result;
    }

    private static List<string> ValidateDeletes(IEnumerable<string>? deletes, string label = "persistence deletes")
    {
        var result = new List<string>();
        foreach (var key in deletes ?? [])
        {
            result.Add(key ?? throw new ArgumentException($"{label} must contain only string keys"));
        }
        return result;
    }
}
