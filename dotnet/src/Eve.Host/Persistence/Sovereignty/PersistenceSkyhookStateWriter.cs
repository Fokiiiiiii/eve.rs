using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Services.Slash;

namespace Eve.Host;

/// <summary>
/// <c>/skyhook state</c>: sets the state of an orbital skyhook's row in the sovereignty table. The row is staged, flushed and read back, as a skyhook's
/// activation is (<see cref="PersistenceSkyhookActivationWriter"/>), and the write shares its gate.
/// </summary>
public sealed class PersistenceSkyhookStateWriter(
    Func<string, JsonObject> readCacheTable,
    Func<string, string?, JsonNode?, Eve.GameStore.WriteResult> write,
    Func<string, CancellationToken, Task<int>> flushTableAsync,
    Func<SemaphoreSlim> writeGate)
{
    public async Task<SkyhookStateOutcome> SetStateAsync(long skyhookId, int state, CancellationToken cancellationToken)
    {
        var gate = writeGate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            var key = skyhookId.ToString(CultureInfo.InvariantCulture);
            if (readCacheTable("sovereignty")["skyhooks"] is not JsonObject rows || rows[key] is not JsonObject row)
                return SkyhookStateOutcome.NotFound;
            if (StateOf(row) == state)
                return SkyhookStateOutcome.Unchanged;

            var updated = (JsonObject)row.DeepClone();
            updated["state"] = state;
            var result = write("sovereignty", $"/skyhooks/{key}", updated);
            if (!result.Success)
                throw new InvalidOperationException($"Could not persist the state of skyhook {skyhookId}: {result.ErrorMsg}");
            await flushTableAsync("sovereignty", CancellationToken.None);

            var persisted = readCacheTable("sovereignty")["skyhooks"] is JsonObject back && back[key] is JsonObject readback ? StateOf(readback) : null;
            return persisted == state
                ? SkyhookStateOutcome.Changed
                : throw new InvalidOperationException($"The state of skyhook {skyhookId} did not read back as {state}.");
        }
        finally
        {
            gate.Release();
        }
    }

    private static int? StateOf(JsonObject row) =>
        int.TryParse(row["state"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var state) ? state : null;
}
