using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

public sealed class PersistenceSkyhookActivationWriter(
    Func<string, JsonObject> readCacheTable,
    Func<string, string?, JsonNode?, Eve.GameStore.WriteResult> write,
    Func<string, CancellationToken, Task<int>> flushTableAsync,
    Func<long, CancellationToken, ValueTask<Eve.Services.Core.SkyhookResourceSnapshot?>> readSkyhookAsync,
    Func<Eve.Services.ServiceCall, Eve.Services.Core.SkyhookResourceSnapshot, Eve.Services.Core.SkyhookResourcePermission, CancellationToken, ValueTask<bool>> authorizeAsync,
    SemaphoreSlim? sharedWriteGate = null)
{
    private readonly SemaphoreSlim _gate = sharedWriteGate ?? new(1, 1);

    public async ValueTask<Eve.Services.Core.SkyhookResourceSnapshot?> PersistAsync(
        Eve.Services.ServiceCall call,
        Eve.Services.Core.SkyhookResourceSnapshot expected,
        bool active,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var permission = active
                ? Eve.Services.Core.SkyhookResourcePermission.Activate
                : Eve.Services.Core.SkyhookResourcePermission.Deactivate;
            if (!await authorizeAsync(call, expected, permission, cancellationToken))
                return null;

            var current = await readSkyhookAsync(expected.SkyhookId, cancellationToken);
            if (current is null ||
                current.CorporationId != expected.CorporationId ||
                current.ProductionActive != expected.ProductionActive ||
                !string.Equals(current.ResourceVersion, expected.ResourceVersion, StringComparison.Ordinal))
                return null;

            var key = expected.SkyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var path = $"/skyhooks/{key}";
            if (readCacheTable("sovereignty")["skyhooks"] is not JsonObject rows ||
                rows[key] is not JsonObject row ||
                !string.Equals(row["resourceVersion"]?.ToString() ?? string.Empty,
                    expected.ResourceVersion, StringComparison.Ordinal))
                return null;

            var updatedRow = (JsonObject)row.DeepClone();
            updatedRow["productionActive"] = active;
            var version = Guid.NewGuid().ToString("N");
            updatedRow["resourceVersion"] = version;
            var result = write("sovereignty", path, updatedRow);
            if (!result.Success)
                throw new InvalidOperationException($"Could not persist Skyhook activation for {expected.SkyhookId}: {result.ErrorMsg}");

            // Finish the one-row commit after the cache mutation has been staged.
            await flushTableAsync("sovereignty", CancellationToken.None);
            var persisted = await readSkyhookAsync(expected.SkyhookId, CancellationToken.None);
            return persisted is { } readback &&
                readback.CorporationId == expected.CorporationId &&
                readback.ProductionActive == active &&
                string.Equals(readback.ResourceVersion, version, StringComparison.Ordinal)
                    ? readback
                    : null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
