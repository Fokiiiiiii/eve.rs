using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Eve.Ships;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private MapTelemetry? mapTelemetry;
    private readonly object mapHistoryStaticDataGate = new();
    private Task<MapHistoryStaticData>? mapHistoryStaticData;

    private Task<MapHistoryStaticData> LoadMapHistoryStaticDataOnceAsync()
    {
        lock (mapHistoryStaticDataGate)
            return mapHistoryStaticData ??= LoadMapHistoryStaticDataAsync();
    }

    private sealed record MapHistoryStaticData(
        IReadOnlyDictionary<long, int> TypeCategories,
        IReadOnlySet<long> CapsuleTypeIds,
        IReadOnlySet<long> FactionIds);

    private async ValueTask<PyValue?> ReadMapTelemetryRpcDataAsync(ServiceCall call)
    {
        switch (call.Method)
        {
            case "GetSolarSystemVisits":
                return MapTelemetryState.SolarSystemVisits(
                    mapTelemetry?.ReadState() ?? MapTelemetryState.Empty(),
                    call.Session.CharacterId ?? 0);
            case "GetHistory":
            {
                var statId = PositiveMapRpcId(call.Arg(0));
                var hours = PositiveMapRpcId(call.Arg(1));
                var telemetry = mapTelemetry?.ReadState() ?? MapTelemetryState.Empty();
                var killmails = new JsonObject();
                MapHistoryStaticData statics = new(new Dictionary<long, int>(), new HashSet<long>(), new HashSet<long>());
                if (statId is 3 or 5)
                {
                    killmails = await ReadGameStoreRawAsync("killmails", call.Cancellation);
                    statics = await LoadMapHistoryStaticDataOnceAsync().WaitAsync(call.Cancellation);
                }
                return MapTelemetryState.History(
                    telemetry, killmails, statId, hours, now(),
                    statics.TypeCategories, statics.CapsuleTypeIds, statics.FactionIds);
            }
            case "GetBeaconCount":
                return await ReadMapBeaconCountsAsync(call.Cancellation);
            default:
                return await ReadMapRpcDataAsync(call);
        }
    }

    private async ValueTask<PyValue> ReadMapBeaconCountsAsync(CancellationToken cancellationToken)
    {
        var deployed = new PgDeployedStore(source, spaceSchema);
        var fields = (await deployed.ByKindAsync("cyno_field", cancellationToken))
            .Concat(await deployed.ByKindAsync("mobile_cyno_beacon", cancellationToken))
            .ToArray();
        var fleetOwners = new HashSet<long>();
        foreach (var ownerId in fields.Select(field => field.OwnerId).Distinct())
            if (await fleetStore.FleetIdForCharacterAsync(ownerId, cancellationToken) is not null)
                fleetOwners.Add(ownerId);

        return new PyDict(MapTelemetryState.ActiveBeaconCounts(fields, fleetOwners, now())
            .Select(entry => new KeyValuePair<PyValue, PyValue>(
                new PyInt(entry.Key), new PyInt(entry.Value)))
            .ToArray());
    }

    private async Task<MapHistoryStaticData> LoadMapHistoryStaticDataAsync()
    {
        static long Number(JsonElement doc, string field) =>
            doc.TryGetProperty(field, out var value) && value.TryGetInt64(out var number) && number > 0
                ? number
                : 0;

        var groups = await sde.ReadAsync("groups", cancellationToken: CancellationToken.None);
        var groupCategories = groups.ToDictionary(row => row.Key, row => (int)Number(row.Doc, "categoryID"));
        var capsuleGroups = groups
            .Where(row => row.Doc.TryGetProperty("name", out var name) && SdeReader.English(name) == "Capsule")
            .Select(row => row.Key)
            .ToHashSet();
        var types = await sde.ReadAsync("types", cancellationToken: CancellationToken.None);
        var categories = types.ToDictionary(
            row => row.Key,
            row => groupCategories.GetValueOrDefault(Number(row.Doc, "groupID")));
        var capsules = types
            .Where(row => capsuleGroups.Contains(Number(row.Doc, "groupID")))
            .Select(row => row.Key)
            .ToHashSet();
        var factions = (await sde.ReadAsync("factions", cancellationToken: CancellationToken.None))
            .Where(row => Number(row.Doc, "militiaCorporationID") > 0)
            .Select(row => row.Key)
            .ToHashSet();
        return new MapHistoryStaticData(categories, capsules, factions);
    }
}

internal sealed class MapAccessGuardService(IService inner, Func<ClientSession, bool> isBlocked) : IService
{
    public string Name => inner.Name;

    public bool HasMethod(string method) => inner.HasMethod(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (Name == "map" && isBlocked(call.Session))
            throw MachoErrors.ThrowUserError(
                "CustomInfo", ("info", new PyStr("The map does not work inside Abyssal Deadspace.")));
        return await inner.CallAsync(call);
    }
}
