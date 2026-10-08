using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using System.Text.Json;

namespace Eve.Host;

public sealed record DevelopmentIndexRecord(
    long SolarSystemId,
    long MilitaryPoints,
    long IndustrialPoints,
    long ClaimedFor);

public sealed record SpaceCompressionResult(
    long SourceItemId,
    int SourceTypeId,
    long SourceQuantity,
    long OutputItemId,
    int OutputTypeId,
    long OutputQuantity);

[Service("devIndexManager")]
public sealed partial class DevIndexManagerRpcService(
    Func<CancellationToken, ValueTask<IReadOnlyList<DevelopmentIndexRecord>>> readAll)
{
    [Method("GetAllDevelopmentIndices")]
    public async ValueTask<PyValue> GetAllDevelopmentIndices(ServiceCall call)
    {
        var records = await readAll(call.Cancellation);
        var rows = new List<PyValue>(records.Count);
        foreach (var record in records.OrderBy(record => record.SolarSystemId))
        {
            rows.Add(Py.KeyVal(
                ("solarSystemID", new PyInt(record.SolarSystemId)),
                ("militaryPoints", new PyInt(Math.Max(0, record.MilitaryPoints))),
                ("industrialPoints", new PyInt(Math.Max(0, record.IndustrialPoints))),
                ("claimedFor", new PyInt(Math.Max(0, record.ClaimedFor)))));
        }
        return new PyList(rows);
    }
}

[Service("eject")]
public sealed partial class EjectRpcService(
    Func<ServiceCall, long, long, CancellationToken, ValueTask>? launch = null)
{
    [Method("LaunchShipOrContainerFromWreckOrContainer")]
    public async ValueTask<PyValue> LaunchShipOrContainerFromWreckOrContainer(ServiceCall call)
    {
        var sourceId = OperationalRpcIds.Of(call.Arg(0));
        var itemId = OperationalRpcIds.Of(call.Arg(1));
        if (launch is not null &&
            call.Session.GetInt("charid") is > 0 &&
            sourceId is > 0 &&
            itemId is > 0)
        {
            await launch(call, sourceId.Value, itemId.Value, call.Cancellation);
        }
        return PyNone.Instance;
    }
}

[Service("encodedItems")]
public sealed partial class EncodedItemsRpcService(
    Func<long, CancellationToken, ValueTask<Item?>> getItem,
    Func<ServiceCall, Item, CancellationToken, ValueTask<bool>>? isDecodableType = null)
{
    private static readonly string[] ResourceUrlKeys = ["evejsEncodedItemUrl", "encodedItemUrl"];

    [Method("DecodeItem")]
    public async ValueTask<PyValue> DecodeItem(ServiceCall call)
    {
        var itemId = OperationalRpcIds.Of(call.Arg(0));
        if (itemId is not > 0 or > 9_000_000_000_000_000_000L)
        {
            return PyNone.Instance;
        }

        var item = await getItem(itemId.Value, call.Cancellation);
        if (item is null || item.Id != itemId || item.Quantity < 1)
        {
            return PyNone.Instance;
        }

        var characterId = call.Session.GetInt("charid") ?? 0;
        var corporationId = call.Session.GetInt("corpid") ?? 0;
        if (item.OwnerId > 0 &&
            (characterId > 0 || corporationId > 0) &&
            item.OwnerId != characterId &&
            item.OwnerId != corporationId)
        {
            return PyNone.Instance;
        }

        // A missing type-list provider fails closed.
        if (isDecodableType is null || !await isDecodableType(call, item, call.Cancellation))
        {
            return PyNone.Instance;
        }

        var url = ResourceUrl(item.CustomInfo);
        return url is not null && (url.EndsWith(".png", StringComparison.Ordinal) ||
                                   url.EndsWith(".webm", StringComparison.Ordinal))
            ? new PyStr(url)
            : PyNone.Instance;
    }

    internal static bool IsInTypeList(JsonElement list, ItemType type)
    {
        static bool Contains(JsonElement row, string property, int id)
        {
            if (id <= 0 || !row.TryGetProperty(property, out var values) ||
                values.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var value in values.EnumerateArray())
                if (value.TryGetInt32(out var candidate) && candidate == id)
                    return true;
            return false;
        }

        var included = Contains(list, "includedTypeIDs", type.TypeId) ||
                       Contains(list, "includedGroupIDs", type.GroupId) ||
                       Contains(list, "includedCategoryIDs", type.CategoryId);
        var excluded = Contains(list, "excludedTypeIDs", type.TypeId) ||
                       Contains(list, "excludedGroupIDs", type.GroupId) ||
                       Contains(list, "excludedCategoryIDs", type.CategoryId);
        return included && !excluded;
    }

    private static string? ResourceUrl(string? customInfo)
    {
        if (string.IsNullOrWhiteSpace(customInfo))
            return null;
        try
        {
            using var document = JsonDocument.Parse(customInfo);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var key in ResourceUrlKeys)
                if (document.RootElement.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    return value.GetString()?.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }
}

[Service("inSpaceCompressionMgr")]
public sealed partial class InSpaceCompressionMgrRpcService(
    Func<long, CancellationToken, ValueTask<Item?>> getItem,
    Func<ServiceCall, Item, long, CancellationToken, ValueTask<SpaceCompressionResult?>>? compress = null)
{
    [Method("CompressItemInSpace")]
    public async ValueTask<PyValue> CompressItemInSpace(ServiceCall call)
    {
        var itemId = OperationalRpcIds.Of(call.Arg(0));
        var facilityId = OperationalRpcIds.Of(call.Arg(1));
        var characterId = call.Session.GetInt("charid") ?? 0;
        var shipId = call.Session.GetInt("shipid") ?? 0;
        var systemId = call.Session.GetInt("solarsystemid2") ??
                       call.Session.GetInt("solarsystemid") ?? 0;
        if (itemId is not > 0 || facilityId is not > 0 ||
            characterId <= 0 || shipId <= 0 || systemId <= 0)
        {
            return PyNone.Instance;
        }

        var item = await getItem(itemId.Value, call.Cancellation);
        if (item is null || item.Id != itemId || item.OwnerId != characterId ||
            item.LocationId != shipId || item.Quantity < 1)
        {
            return PyNone.Instance;
        }
        if (compress is null)
            return PyNone.Instance;

        var result = await compress(call, item, facilityId.Value, call.Cancellation);
        if (result is null ||
            result.SourceItemId != item.Id ||
            result.SourceTypeId != item.TypeId ||
            result.SourceQuantity != item.Quantity ||
            result.SourceTypeId <= 0 ||
            result.SourceQuantity <= 0 ||
            result.OutputItemId <= 0 ||
            result.OutputTypeId <= 0 ||
            result.OutputQuantity <= 0)
        {
            return PyNone.Instance;
        }

        return Py.Tuple(
            new PyLong(result.SourceItemId),
            new PyInt(result.SourceTypeId),
            new PyLong(result.SourceQuantity),
            new PyLong(result.OutputItemId),
            new PyInt(result.OutputTypeId),
            new PyLong(result.OutputQuantity));
    }
}

internal static class OperationalRpcIds
{
    public static long? Of(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };
}
