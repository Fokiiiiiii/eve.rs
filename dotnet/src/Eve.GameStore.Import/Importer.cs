using Eve.GameStore;

namespace Eve.GameStore.Import;

public sealed class ImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="SqlitePath">A gamestore.sqlite written by the reference server.</param>
/// <param name="Tables">Only these tables; null means every key/json table.</param>
/// <param name="Replace">Overwrite target tables that already hold rows; otherwise a non-empty target table is an error.</param>
/// <param name="ApplyOutbox">
/// Apply the source's journaled-but-unacknowledged batches on top of its rows. Without it, a source that
/// still has journaled batches is refused: importing only the rows would silently drop those changes.
/// </param>
/// <param name="Verify">Read every imported table back and compare it with the source.</param>
public sealed record ImportOptions(
    string SqlitePath,
    IReadOnlyList<string>? Tables = null,
    bool Replace = false,
    bool ApplyOutbox = false,
    bool IgnoreLease = false,
    bool Verify = true);

public sealed record TableImport(string Table, int Rows, int JournalBatchesApplied, bool Verified);

public sealed record ImportReport(IReadOnlyList<TableImport> Tables, IReadOnlyList<(string Table, string Reason)> Skipped, IReadOnlyList<string> Migrated);

/// <summary>Copies a reference-server SQLite game store into PostgreSQL.</summary>
public static class Importer
{
    public static async Task<ImportReport> RunAsync(PersistenceStore target, ImportOptions options, Action<string>? log = null, CancellationToken ct = default)
    {
        using var source = new SqliteSource(options.SqlitePath);
        var (found, skipped) = source.ListTables();
        var tables = options.Tables is null ? found : options.Tables.ToList();
        var unknown = tables.Where(t => !found.Contains(t)).ToList();
        if (unknown.Count > 0)
        {
            throw new ImportException($"not a key/json table in the source: {string.Join(", ", unknown)}");
        }

        RequireQuiescent(source, options);
        var journal = source.Journal();
        if (journal.Count > 0 && !options.ApplyOutbox)
        {
            throw new ImportException(
                $"the source has {journal.Count} journaled batch(es) that were never acknowledged ({string.Join(", ", journal.Select(o => o.Table).Distinct())}). " +
                "Importing only the rows would drop them; start the old server once so it recovers them, or pass ApplyOutbox.");
        }

        await target.EnsureSchemaAsync(ct);
        // The maintenance role is exclusive: it fails while any live server holds a role on the target.
        await using var session = await OwnerSession.StartAsync(
            target, PersistenceRoles.Maintenance, $"import:{Environment.ProcessId}", new AcquireOptions(LeaseMs: 120_000), ct: ct);
        var fence = session.Fence;

        var results = new List<TableImport>();
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ImportTableAsync(source, target, fence, table, journal, options, log, ct));
        }

        var migrated = new List<string>();
        foreach (var table in source.Migrated())
        {
            if (tables.Contains(table))
            {
                await target.MarkMigratedAsync(table, fence, ct);
                migrated.Add(table);
            }
        }
        return new ImportReport(results, skipped, migrated);
    }

    private static void RequireQuiescent(SqliteSource source, ImportOptions options)
    {
        if (options.IgnoreLease)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var live = source.Owners().Where(owner => owner.Active && owner.LeaseExpiresAt > now).ToList();
        if (live.Count > 0)
        {
            throw new ImportException(
                $"the source database is in use: live owner lease held by {string.Join(", ", live.Select(o => $"{o.Role} ({o.InstanceId})"))}. Stop the old server first, or pass IgnoreLease.");
        }
    }

    private static async Task<TableImport> ImportTableAsync(
        SqliteSource source, PersistenceStore target, OwnerFence fence, string table,
        IReadOnlyList<SourceOperation> journal, ImportOptions options, Action<string>? log, CancellationToken ct)
    {
        if (!options.Replace && await target.TableExistsAsync(table, ct) && await target.RowCountAsync(table, ct) > 0)
        {
            throw new ImportException($"target table {table} already has rows; pass Replace to overwrite it");
        }

        var operations = journal.Where(o => o.Table == table).ToList();
        var effective = new EffectiveRows(operations);
        var started = System.Diagnostics.Stopwatch.StartNew();
        int written;
        try
        {
            written = await target.ReplaceRowsAsync(table, effective.Apply(source.Rows(table)), fence, ct);
        }
        catch (Exception ex) when (ex is PersistenceException or Npgsql.PostgresException)
        {
            throw new ImportException($"importing {table} failed: {ex.Message}", ex);
        }
        log?.Invoke($"{table}: {written} rows in {started.ElapsedMilliseconds} ms" + (operations.Count > 0 ? $" ({operations.Count} journaled batch(es) applied)" : ""));

        var verified = false;
        if (options.Verify)
        {
            await VerifyAsync(source, target, table, effective, ct);
            verified = true;
        }
        return new TableImport(table, written, operations.Count, verified);
    }

    private static async Task VerifyAsync(SqliteSource source, PersistenceStore target, string table, EffectiveRows effective, CancellationToken ct)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in effective.Apply(source.Rows(table)))
        {
            expected[row.Key] = JsonCanonical.ToText(row.Json);
        }
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in await target.LoadRowsAsync(table, createIfMissing: false, ct))
        {
            actual[row.Key] = JsonCanonical.ToText(row.Json);
        }
        var missing = expected.Keys.Except(actual.Keys).Take(3).ToList();
        var extra = actual.Keys.Except(expected.Keys).Take(3).ToList();
        var different = expected.Where(pair => actual.TryGetValue(pair.Key, out var value) && value != pair.Value).Select(pair => pair.Key).Take(3).ToList();
        if (missing.Count + extra.Count + different.Count > 0)
        {
            throw new ImportException(
                $"verification of {table} failed: missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", extra)}], different [{string.Join(", ", different)}]");
        }
    }

    /// <summary>The source rows with its journaled batches applied on top, in journal order.</summary>
    private sealed class EffectiveRows
    {
        private readonly Dictionary<string, string?> _overrides = new(StringComparer.Ordinal);

        public EffectiveRows(IEnumerable<SourceOperation> operations)
        {
            foreach (var operation in operations)
            {
                foreach (var upsert in operation.Upserts)
                {
                    _overrides[upsert.Key] = upsert.Json;
                }
                foreach (var key in operation.Deletes)
                {
                    _overrides[key] = null; // a delete wins over the earlier upsert, as in the reference
                }
            }
        }

        public IEnumerable<StoredRow> Apply(IEnumerable<StoredRow> rows)
        {
            if (_overrides.Count == 0)
            {
                return rows;
            }
            return Overlay(rows);
        }

        private IEnumerable<StoredRow> Overlay(IEnumerable<StoredRow> rows)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (_overrides.TryGetValue(row.Key, out var replacement))
                {
                    seen.Add(row.Key);
                    if (replacement is not null)
                    {
                        yield return new StoredRow(row.Key, replacement);
                    }
                    continue;
                }
                yield return row;
            }
            foreach (var (key, json) in _overrides)
            {
                if (json is not null && !seen.Contains(key))
                {
                    yield return new StoredRow(key, json);
                }
            }
        }
    }
}
