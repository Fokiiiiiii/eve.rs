using System.Text.Json.Nodes;

namespace Eve.GameStore;

public sealed partial class GameStoreCache
{
    private sealed class TableState(string name, JsonObject root, Dictionary<string, string> baseline)
    {
        public readonly object Gate = new();
        public readonly string Name = name;
        public JsonObject Root = root;

        /// <summary>Row key to the serialisation the database last acknowledged.</summary>
        public readonly Dictionary<string, string> Baseline = baseline;

        public HashSet<string> DirtyRows = [];
        public bool FullDirty;
        public bool Dirty;
        public readonly HashSet<string> Transient = new(StringComparer.Ordinal);
        public bool InFlight;
        public (PersistenceOperation Operation, Batch Batch)? Pending;
        public Timer? Timer;
    }

    private sealed record Batch(List<RowUpsert> Upserts, List<string> Deletes)
    {
        public bool IsEmpty => Upserts.Count == 0 && Deletes.Count == 0;
    }

    private static bool SetChild(JsonNode parent, string key, JsonNode? value)
    {
        switch (parent)
        {
            case JsonObject obj:
                obj[key] = value;
                return true;
            case JsonArray array when int.TryParse(key, System.Globalization.NumberStyles.None, null, out var index) && index <= array.Count:
                if (index == array.Count)
                {
                    array.Add(value);
                }
                else
                {
                    array[index] = value;
                }
                return true;
            default:
                return false;
        }
    }

    private void RequireWriter(string table, string operation, bool passiveTransient = false)
    {
        if (passiveTransient)
        {
            return;
        }
        PersistenceRoles.AssertMayMutateTable(ProcessRole, table, operation);
        _session?.AssertHealthy();
        if (_stopping)
        {
            throw new InvalidOperationException("gameStore shutdown is in progress");
        }
    }

    /// <summary>
    /// Stores a copy of <paramref name="data"/> at <paramref name="path"/>, creating
    /// missing parents. An unchanged value is a no-op (unless forced).
    /// </summary>
    public WriteResult Write(string table, string? path, JsonNode? data, WriteOptions? options = null)
    {
        options ??= new WriteOptions();
        RequireWriter(table, "write", passiveTransient: IsReader && options.Transient && !options.Force);
        var state = StateOf(table);
        if (state is null)
        {
            return new WriteResult(false, "TABLE_NOT_FOUND");
        }
        var persist = !(options.Transient && !options.Force);
        try
        {
            lock (state.Gate)
            {
                var segments = Segments(path);
                if (segments.Length == 0)
                {
                    if (data is not JsonObject newRoot)
                    {
                        return new WriteResult(false, "WRITE_ERROR");
                    }
                    var unchanged = JsonNode.DeepEquals(state.Root, newRoot);
                    if (options.Transient)
                    {
                        state.Transient.Add("/");
                    }
                    if (unchanged && !options.Force)
                    {
                        return new WriteResult(true, null);
                    }
                    state.Root = (JsonObject)newRoot.DeepClone();
                    if (persist)
                    {
                        MarkRootWriteDirty(state, options.KnownChangedPaths);
                        ScheduleFlush(state);
                    }
                    return new WriteResult(true, null);
                }

                JsonNode current = state.Root;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    if (!TryChild(current, segments[i], out var child) || child is not (JsonObject or JsonArray))
                    {
                        child = new JsonObject();
                        if (!SetChild(current, segments[i], child))
                        {
                            return new WriteResult(false, "WRITE_ERROR");
                        }
                    }
                    current = child!;
                }
                if (options.Transient)
                {
                    state.Transient.Add(NormalizePath(path));
                }
                var finalKey = segments[^1];
                if (TryChild(current, finalKey, out var existing) && JsonNode.DeepEquals(existing, data))
                {
                    return new WriteResult(true, null);
                }
                if (!SetChild(current, finalKey, data?.DeepClone()))
                {
                    return new WriteResult(false, "WRITE_ERROR");
                }
                if (persist)
                {
                    MarkRowDirty(state, segments);
                    ScheduleFlush(state);
                }
                return new WriteResult(true, null);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return new WriteResult(false, "WRITE_ERROR");
        }
    }

    public WriteResult Remove(string table, string? path)
    {
        RequireWriter(table, "remove from");
        var state = StateOf(table);
        if (state is null)
        {
            return new WriteResult(false, "TABLE_NOT_FOUND");
        }
        lock (state.Gate)
        {
            var segments = Segments(path);
            if (segments.Length == 0)
            {
                return new WriteResult(false, "INVALID_PATH");
            }
            if (!Walk(state.Root, segments[..^1], out var parent) || !TryChild(parent, segments[^1], out _))
            {
                return new WriteResult(false, "ENTRY_NOT_FOUND");
            }
            switch (parent)
            {
                case JsonObject obj:
                    obj.Remove(segments[^1]);
                    break;
                case JsonArray array:
                    array[int.Parse(segments[^1], System.Globalization.CultureInfo.InvariantCulture)] = null; // a hole, as `delete` leaves
                    break;
            }
            ClearTransientPrefixes(state, [NormalizePath(path)]);
            MarkRowDirty(state, segments);
            ScheduleFlush(state);
            return new WriteResult(true, null);
        }
    }

    // ── transient paths: kept in memory, never flushed ──────────────────────

    /// <summary>Marks (or clears) paths as memory-only. Clearing a path clears everything beneath it.</summary>
    public void SetTransientPaths(string table, IEnumerable<string> paths, bool enabled = true)
    {
        RequireWriter(table, enabled ? "mark transient paths for" : "clear transient paths for", passiveTransient: IsReader && enabled);
        var state = StateOf(table) ?? throw new KeyNotFoundException($"table is not loaded: {table}");
        var normalized = paths.Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        lock (state.Gate)
        {
            if (enabled)
            {
                state.Transient.UnionWith(normalized);
            }
            else
            {
                ClearTransientPrefixes(state, normalized);
            }
        }
    }

    public bool IsTransient(string table, string path)
    {
        var state = StateOf(table);
        if (state is null)
        {
            return false;
        }
        var normalized = NormalizePath(path);
        lock (state.Gate)
        {
            return state.Transient.Any(marker => marker == "/" || normalized == marker || normalized.StartsWith(marker + "/", StringComparison.Ordinal));
        }
    }

    private static void ClearTransientPrefixes(TableState state, IReadOnlyCollection<string> prefixes)
    {
        if (state.Transient.Count == 0 || prefixes.Count == 0)
        {
            return;
        }
        if (prefixes.Contains("/"))
        {
            state.Transient.Clear();
            return;
        }
        state.Transient.RemoveWhere(marker =>
        {
            // A marker goes when it, or any ancestor of it, is being cleared.
            var ancestor = marker;
            while (ancestor.Length > 0 && ancestor != "/")
            {
                if (prefixes.Contains(ancestor))
                {
                    return true;
                }
                var slash = ancestor.LastIndexOf('/');
                ancestor = slash > 0 ? ancestor[..slash] : "/";
            }
            return false;
        });
    }

    // ── dirty tracking ──────────────────────────────────────────────────────

    private static void MarkRowDirty(TableState state, string[] segments)
    {
        if (state.FullDirty)
        {
            return;
        }
        if (segments.Length == 0)
        {
            state.FullDirty = true; // a root write: any row may have changed
            return;
        }
        var groups = GameStoreTables.RowGroupsFor(state.Name);
        var first = segments[0];
        if (groups is not null && groups.Contains(first))
        {
            if (segments.Length == 1)
            {
                state.FullDirty = true; // wrote or removed the whole group
                return;
            }
            state.DirtyRows.Add($"{first}{GameStoreTables.RowKeySeparator}{segments[1]}");
            state.DirtyRows.Add(first); // the group's skeleton row, to match the exploded rows
            return;
        }
        state.DirtyRows.Add(first);
    }

    /// <summary>
    /// A root write cannot say which rows changed, so it falls back to a full diff,
    /// unless the caller supplies the exact changed paths. An empty or malformed
    /// hint is never trusted.
    /// </summary>
    private static void MarkRootWriteDirty(TableState state, IReadOnlyList<string>? knownChangedPaths)
    {
        if (knownChangedPaths is null or { Count: 0 })
        {
            MarkRowDirty(state, []);
            return;
        }
        foreach (var path in knownChangedPaths)
        {
            var segments = Segments(path);
            if (!path.StartsWith('/') || segments.Length == 0)
            {
                MarkRowDirty(state, []);
                return;
            }
            MarkRowDirty(state, segments);
            if (state.FullDirty)
            {
                return;
            }
        }
    }
}
