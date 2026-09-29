using System.Text.Json.Nodes;

namespace Eve.GameStore;

public sealed record ReadResult(bool Success, string? ErrorMsg, JsonNode? Data);

public sealed record WriteResult(bool Success, string? ErrorMsg);

/// <param name="Transient">Keep the value in memory only: it is left out of every flush until cleared.</param>
/// <param name="Force">Persist a transient write anyway, and write even when the value is unchanged.</param>
/// <param name="KnownChangedPaths">For a root write: the exact paths that changed, so only those rows are flushed.</param>
public sealed record WriteOptions(bool Transient = false, bool Force = false, IReadOnlyList<string>? KnownChangedPaths = null);

public sealed class GameStoreCacheOptions
{
    /// <summary>A table is flushed this long after its last write.</summary>
    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The process role; decides which tables it may mutate.</summary>
    public string ProcessRole { get; init; } = PersistenceRoles.Reader;

    /// <summary>Called when a background flush fails. The batch stays journaled and is retried.</summary>
    public Action<string, Exception>? OnFlushError { get; init; }

    /// <summary>Called after a batch is durably applied, with the rows it wrote and removed.</summary>
    public Action<string, IReadOnlyList<RowUpsert>, IReadOnlyList<string>>? OnFlushed { get; init; }
}

/// <summary>
/// The cache-first data layer the services use: synchronous <see cref="Read"/>,
/// <see cref="Write"/> and <see cref="Remove"/> on an in-memory copy, with the
/// database updated in the background. Tables must be loaded first
/// (<see cref="LoadAsync"/> / <see cref="PreloadAllAsync"/>), as the reference
/// server does at start-up.
///
/// A write marks only the row it touched dirty; a flush re-serialises just
/// those rows, compares them with what the database last acknowledged, and
/// journals the difference through the outbox before applying it.
///
/// Values passed in and out are copies, so callers never alias cached state.
/// </summary>
public sealed partial class GameStoreCache : IAsyncDisposable
{
    private readonly PersistenceStore _store;
    private readonly OwnerSession? _session;
    private readonly GameStoreCacheOptions _options;
    private readonly Dictionary<string, TableState> _tables = new(StringComparer.Ordinal);
    private readonly object _tablesGate = new();
    private volatile bool _stopping;
    private int _disposed;

    public GameStoreCache(PersistenceStore store, OwnerSession? session, GameStoreCacheOptions? options = null)
    {
        _store = store;
        _session = session;
        _options = options ?? new GameStoreCacheOptions();
        ProcessRole = PersistenceRoles.Normalize(_options.ProcessRole);
        if (session is null && ProcessRole != PersistenceRoles.Reader)
        {
            throw new ArgumentException("a process that mutates tables needs an owner session", nameof(session));
        }
    }

    public string ProcessRole { get; }

    private bool IsReader => ProcessRole == PersistenceRoles.Reader;

    private static string[] Segments(string? path) =>
        (path ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizePath(string? path)
    {
        var segments = Segments(path);
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private TableState? StateOf(string table)
    {
        lock (_tablesGate)
        {
            return _tables.GetValueOrDefault(table);
        }
    }

    /// <summary>
    /// Replays this owner's unfinished journal. Call once, before loading tables,
    /// so the cache starts from the state a crashed predecessor had committed to.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken ct = default)
    {
        if (_session is null)
        {
            return 0;
        }
        var fence = _session.Fence;
        var recovered = await _store.RecoverOperationsAsync(fence, ct);
        if (recovered.Count > 0)
        {
            await _store.AcknowledgeRecoveredAsync(recovered.Select(operation => operation.OperationId), fence, ct);
        }
        return recovered.Count;
    }

    public async Task LoadAsync(string table, CancellationToken ct = default)
    {
        TableNames.RequirePersistence(table);
        lock (_tablesGate)
        {
            if (_tables.ContainsKey(table))
            {
                return;
            }
        }
        var rows = await _store.LoadRowsAsync(table, createIfMissing: !IsReader, ct);
        var parsed = new List<KeyValuePair<string, JsonNode?>>(rows.Count);
        var baseline = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var node = JsonNode.Parse(row.Json);
            // The baseline is our own serialisation, not the database's text, so an
            // unchanged row compares equal after a round trip.
            baseline[row.Key] = node?.ToJsonString() ?? "null";
            parsed.Add(new KeyValuePair<string, JsonNode?>(row.Key, node));
        }
        var state = new TableState(table, RowCodec.Assemble(table, parsed), baseline);
        lock (_tablesGate)
        {
            _tables.TryAdd(table, state);
        }
    }

    public async Task PreloadAllAsync(CancellationToken ct = default)
    {
        foreach (var table in GameStoreTables.RuntimeTables)
        {
            await LoadAsync(table, ct);
        }
    }

    public bool IsLoaded(string table) => StateOf(table) is not null;

    // ── reads ───────────────────────────────────────────────────────────────

    /// <summary>A copy of the value at <paramref name="path"/> (<c>/</c> is the whole table).</summary>
    public ReadResult Read(string table, string? path = "/")
    {
        var state = StateOf(table);
        if (state is null)
        {
            return new ReadResult(false, "TABLE_NOT_FOUND", null);
        }
        lock (state.Gate)
        {
            var found = Walk(state.Root, Segments(path), out var value);
            return found ? new ReadResult(true, null, value?.DeepClone()) : new ReadResult(false, "ENTRY_NOT_FOUND", null);
        }
    }

    public bool Exists(string table, string? path) => Read(table, path).Success;

    private static bool Walk(JsonNode? root, string[] segments, out JsonNode? value)
    {
        var current = root;
        foreach (var segment in segments)
        {
            if (!TryChild(current, segment, out current))
            {
                value = null;
                return false;
            }
        }
        value = current;
        return true;
    }

    private static bool TryChild(JsonNode? parent, string segment, out JsonNode? child)
    {
        switch (parent)
        {
            case JsonObject obj:
                return obj.TryGetPropertyValue(segment, out child);
            case JsonArray array when int.TryParse(segment, System.Globalization.NumberStyles.None, null, out var index) && index < array.Count:
                child = array[index];
                return true;
            default:
                child = null;
                return false;
        }
    }
}
