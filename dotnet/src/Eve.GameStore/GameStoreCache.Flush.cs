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
    }

    /// <summary>What gets persisted: the cache without its transient paths.</summary>
    private static JsonObject BuildSnapshot(TableState state)
    {
        if (state.Transient.Count == 0)
        {
            return state.Root;
        }
        var snapshot = (JsonObject)state.Root.DeepClone();
        foreach (var path in state.Transient)
        {
            if (path == "/")
            {
                return [];
            }
            DeletePath(snapshot, path);
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
        return new Batch(upserts, deletes);
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
    public async Task<int> FlushTableAsync(string table, CancellationToken ct = default)
    {
        var state = StateOf(table);
        if (state is null)
        {
            return 0;
        }
        RequireFlusher(table);
        lock (state.Gate)
        {
            if (state.InFlight)
            {
                state.Dirty = true;
                return 0;
            }
            state.InFlight = true;
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
            if (pending is { } earlier)
            {
                await _store.ReconcileOperationAsync(earlier.Operation.OperationId, table, fence, ct);
                lock (state.Gate)
                {
                    AdvanceBaseline(state, earlier.Batch);
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
            lock (state.Gate)
            {
                state.InFlight = false;
                again = state.Dirty;
            }
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
            await FlushAllAsync();
        }
    }
}
