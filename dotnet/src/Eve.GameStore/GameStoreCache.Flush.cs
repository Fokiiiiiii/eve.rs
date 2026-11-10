using System.Text.Json.Nodes;

namespace Eve.GameStore;

public sealed partial class GameStoreCache
{
    private static string Serialize(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static void DeletePath(JsonNode? root, string path)
    {
        var segments = Segments(path);
        if (segments.Length == 0 || !Walk(root, segments[..^1], out var parent))
        {
            return;
        }
        if (parent is JsonObject obj)
        {
            obj.Remove(segments[^1]);
        }
        else if (parent is JsonArray array
            && int.TryParse(segments[^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            && index < array.Count)
        {
            array[index] = null;
        }
    }

    private static bool SetSnapshotChild(JsonNode parent, string key, JsonNode? value)
    {
        if (parent is JsonObject obj)
        {
            obj[key] = value;
            return true;
        }
        if (parent is JsonArray array
            && int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            while (array.Count <= index)
            {
                array.Add(null);
            }
            array[index] = value;
            return true;
        }
        return false;
    }

    private static void RestoreForcedTransientPath(JsonObject target, JsonObject source, string path, JsonNode? value)
    {
        var segments = Segments(path);
        if (segments.Length == 0)
        {
            return;
        }
        JsonNode sourceParent = source;
        JsonNode targetParent = target;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!TryChild(sourceParent, segments[i], out var sourceChild) || sourceChild is not (JsonObject or JsonArray))
            {
                return;
            }
            if (!TryChild(targetParent, segments[i], out var targetChild)
                || targetChild is not (JsonObject or JsonArray)
                || targetChild.GetType() != sourceChild.GetType())
            {
                targetChild = sourceChild is JsonArray ? new JsonArray() : new JsonObject();
                if (!SetSnapshotChild(targetParent, segments[i], targetChild))
                {
                    return;
                }
            }
            sourceParent = sourceChild;
            targetParent = targetChild;
        }
        SetSnapshotChild(targetParent, segments[^1], value?.DeepClone());
    }

    /// <summary>What gets persisted: the cache without its transient paths.</summary>
    private static JsonObject BuildSnapshot(TableState state)
    {
        if (state.Transient.Count == 0)
        {
            return state.Root;
        }
        if (state.ForcedTransient.TryGetValue("/", out var forcedRoot))
        {
            return forcedRoot.Value is JsonObject root ? (JsonObject)root.DeepClone() : [];
        }
        var snapshot = state.Transient.Contains("/") ? new JsonObject() : (JsonObject)state.Root.DeepClone();
        foreach (var path in state.Transient)
        {
            if (path == "/")
            {
                continue;
            }
            DeletePath(snapshot, path);
        }
        foreach (var (path, forced) in state.ForcedTransient)
        {
            RestoreForcedTransientPath(snapshot, state.Root, path, forced.Value);
        }
        return snapshot;
    }

    /// <summary>
    /// The rows that differ from what the database last acknowledged. Runs under
    /// the table's lock. Takes over the dirty-row set, so writes that arrive while
    /// the batch is in flight are tracked for the next flush.
    /// </summary>
    private static Batch BuildBatch(TableState state)
    {
        var snapshot = BuildSnapshot(state);
        var usePartial = state.DirtyRows.Count > 0 && !state.FullDirty && state.Transient.Count == 0;
        var upserts = new List<RowUpsert>();
        var deletes = new List<string>();
        if (usePartial)
        {
            // Re-serialise only the rows the writes touched. A row that no longer
            // exists is a delete, but only if the database has it.
            foreach (var rowKey in state.DirtyRows)
            {
                if (!RowCodec.TryGetRowValue(state.Name, snapshot, rowKey, out var value))
                {
                    if (state.Baseline.ContainsKey(rowKey))
                    {
                        deletes.Add(rowKey);
                    }
                    continue;
                }
                var json = Serialize(value);
                if (!state.Baseline.TryGetValue(rowKey, out var stored) || stored != json)
                {
                    upserts.Add(new RowUpsert(rowKey, json));
                }
            }
        }
        else
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (rowKey, value) in RowCodec.EnumerateRows(state.Name, snapshot))
            {
                present.Add(rowKey);
                var json = Serialize(value);
                if (!state.Baseline.TryGetValue(rowKey, out var stored) || stored != json)
                {
                    upserts.Add(new RowUpsert(rowKey, json));
                }
            }
            deletes.AddRange(state.Baseline.Keys.Where(key => !present.Contains(key)));
        }
        state.DirtyRows = [];
        state.FullDirty = false;
        var forcedVersions = state.ForcedTransient.ToDictionary(pair => pair.Key, pair => pair.Value.Version, StringComparer.Ordinal);
        return new Batch(upserts, deletes, forcedVersions);
    }

    private static void ClearForcedTransientWrites(TableState state, Batch batch)
    {
        foreach (var (path, version) in batch.ForcedTransientVersions)
        {
            if (state.ForcedTransient.TryGetValue(path, out var forced) && forced.Version == version)
            {
                state.ForcedTransient.Remove(path);
            }
        }
    }

    private static void AdvanceBaseline(TableState state, Batch batch)
    {
        foreach (var upsert in batch.Upserts)
        {
            state.Baseline[upsert.Key] = upsert.Json;
        }
        foreach (var key in batch.Deletes)
        {
            state.Baseline.Remove(key);
        }
    }

    /// <summary>
    /// Writes the table's pending changes to the database and returns how many
    /// rows changed. One flush per table runs at a time: a second call while one
    /// is in flight only schedules another, because diffing against a baseline
    /// that has not been acknowledged could miss a delete.
    /// </summary>
    public Task<int> FlushTableAsync(string table, CancellationToken ct = default) =>
        FlushTableCoreAsync(table, ct, duringShutdown: false);

    private async Task<int> FlushTableCoreAsync(string table, CancellationToken ct, bool duringShutdown)
    {
        var state = StateOf(table);
        if (state is null)
        {
            return 0;
        }
        if (_stopping && !duringShutdown)
        {
            throw new InvalidOperationException("gameStore shutdown is in progress");
        }
        RequireFlusher(table);
        lock (state.Gate)
        {
            if (_stopping && !duringShutdown)
            {
                throw new InvalidOperationException("gameStore shutdown is in progress");
            }
            if (state.InFlight)
            {
                state.Dirty = true;
                return 0;
            }
            state.InFlight = true;
            state.FlushCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Dirty = false;
        }
        try
        {
            var fence = _session!.Fence;

            // A batch journaled by an earlier, interrupted flush is finished first, exactly as it was.
            (PersistenceOperation Operation, Batch Batch)? pending;
            lock (state.Gate)
            {
                pending = state.Pending;
            }
            if (pending is null)
            {
                var journaled = await _store.FindOperationForTableAsync(table, fence, ct);
                if (journaled is not null)
                {
                    var recoveredBatch = new Batch([.. journaled.Upserts], [.. journaled.Deletes], new Dictionary<string, long>(StringComparer.Ordinal));
                    var recovered = (journaled, recoveredBatch);
                    lock (state.Gate)
                    {
                        state.Pending = recovered;
                    }
                    pending = recovered;
                }
            }
            if (pending is { } earlier)
            {
                await _store.ReconcileOperationAsync(earlier.Operation.OperationId, table, fence, ct);
                lock (state.Gate)
                {
                    AdvanceBaseline(state, earlier.Batch);
                    ClearForcedTransientWrites(state, earlier.Batch);
                    state.Pending = null;
                }
            }

            Batch batch;
            lock (state.Gate)
            {
                batch = BuildBatch(state);
            }
            if (batch.IsEmpty)
            {
                lock (state.Gate)
                {
                    ClearForcedTransientWrites(state, batch);
                }
                return 0;
            }

            PersistenceOperation operation;
            try
            {
                operation = await _store.EnqueueAsync(table, batch.Upserts, batch.Deletes, fence, ct);
            }
            catch
            {
                lock (state.Gate)
                {
                    state.FullDirty = true; // the next flush re-derives its diff from scratch
                    state.Dirty = true;
                }
                throw;
            }
            lock (state.Gate)
            {
                state.Pending = (operation, batch);
            }
            await _store.ReconcileOperationAsync(operation.OperationId, table, fence, ct);
            lock (state.Gate)
            {
                AdvanceBaseline(state, batch);
                ClearForcedTransientWrites(state, batch);
                state.Pending = null;
            }
            _options.OnFlushed?.Invoke(table, batch.Upserts, batch.Deletes);
            return batch.Upserts.Count + batch.Deletes.Count;
        }
        catch
        {
            lock (state.Gate)
            {
                state.Dirty = true;
            }
            throw;
        }
        finally
        {
            bool again;
            TaskCompletionSource? completion;
            lock (state.Gate)
            {
                state.InFlight = false;
                completion = state.FlushCompletion;
                state.FlushCompletion = null;
                again = state.Dirty;
            }
            completion?.TrySetResult();
            if (again && !_stopping)
            {
                ScheduleFlush(state);
            }
        }
    }

    private void RequireFlusher(string table)
    {
        PersistenceRoles.AssertMayMutateTable(ProcessRole, table, "flush");
        _session?.AssertHealthy();
        if (_session is null)
        {
            throw new InvalidOperationException("flushing requires an owner session");
        }
    }

    /// <summary>Flushes every table with pending changes. Returns the number of rows changed.</summary>
    public async Task<int> FlushAllAsync(CancellationToken ct = default)
    {
        List<TableState> candidates;
        lock (_tablesGate)
        {
            candidates = [.. _tables.Values];
        }
        var changed = 0;
        foreach (var state in candidates)
        {
            bool needed;
            lock (state.Gate)
            {
                needed = state.Dirty || state.DirtyRows.Count > 0 || state.FullDirty || state.Pending is not null;
            }
            if (needed)
            {
                changed += await FlushTableAsync(state.Name, ct);
            }
        }
        return changed;
    }

    private async Task DrainOnDisposeAsync(IReadOnlyCollection<TableState> states)
    {
        while (true)
        {
            var running = new List<Task>();
            foreach (var state in states)
            {
                lock (state.Gate)
                {
                    if (state.InFlight && state.FlushCompletion is { } completion)
                    {
                        running.Add(completion.Task);
                    }
                }
            }
            if (running.Count > 0)
            {
                await Task.WhenAll(running);
                continue;
            }

            var pending = new List<TableState>();
            foreach (var state in states)
            {
                lock (state.Gate)
                {
                    if (state.Dirty || state.DirtyRows.Count > 0 || state.FullDirty || state.Pending is not null)
                    {
                        pending.Add(state);
                    }
                }
            }
            if (pending.Count == 0)
            {
                return;
            }
            foreach (var state in pending)
            {
                await FlushTableCoreAsync(state.Name, CancellationToken.None, duringShutdown: true);
            }
        }
    }

    private void ScheduleFlush(TableState state)
    {
        if (IsReader || _stopping)
        {
            return;
        }
        state.Dirty = true;
        state.Timer ??= new Timer(_ => _ = BackgroundFlushAsync(state), null, Timeout.Infinite, Timeout.Infinite);
        state.Timer.Change(_options.FlushDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task BackgroundFlushAsync(TableState state)
    {
        try
        {
            await FlushTableAsync(state.Name);
        }
        catch (Exception ex)
        {
            _options.OnFlushError?.Invoke(state.Name, ex);
            if (!_stopping && _session is not { IsLost: true })
            {
                ScheduleFlush(state); // the batch stays journaled; try again
            }
        }
    }

    /// <summary>Stops the background flushing and writes everything that is still pending.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _stopping = true;
        List<TableState> all;
        lock (_tablesGate)
        {
            all = [.. _tables.Values];
        }
        foreach (var state in all)
        {
            state.Timer?.Dispose();
        }
        if (!IsReader)
        {
            await DrainOnDisposeAsync(all);
        }
    }
}
