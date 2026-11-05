using System.Text.Json.Nodes;
using Eve.Corporations;
using Eve.Services.Core;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    // the SDE category "Blueprint" (categories key 9): blueprint items have a group of their own per kind, so the group does not identify them
    private const int BlueprintCategoryId = 9;
    private const int IndustryJobCompleted = 100;
    private const int MaxLocationDepth = 8;

    /// <summary>The blueprint item with that id, or null when it is not a blueprint.</summary>
    private async ValueTask<BlueprintRecord?> ReadBlueprintAsync(long itemId, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(itemId, cancellationToken);
        if (item is null) return null;
        var type = await itemTypes.GetAsync(item.TypeId, cancellationToken);
        if (type?.CategoryId != BlueprintCategoryId) return null;
        return await BlueprintRecordAsync(item, cancellationToken);
    }


    private async ValueTask<IReadOnlyList<BlueprintRecord>> ReadOwnedBlueprintsAsync(long ownerId, CancellationToken cancellationToken)
    {
        var records = new List<BlueprintRecord>();
        foreach (var item in await items.OwnedByAsync(ownerId, cancellationToken))
        {
            var type = await itemTypes.GetAsync(item.TypeId, cancellationToken);
            if (type?.CategoryId == BlueprintCategoryId)
                records.Add(await BlueprintRecordAsync(item, cancellationToken));
        }
        return records;
    }


    private async ValueTask<IReadOnlyDictionary<long, long>> ReadActiveJobsAsync(long ownerId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, long>();
        var table = await industryJobOperations.ReadJobsAsync(cancellationToken);
        if (table?["jobs"] is not JsonObject jobs) return result;
        foreach (var (key, node) in jobs)
        {
            if (node is not JsonObject job || !long.TryParse(key, out var jobId)) continue;
            if (!long.TryParse(job["ownerID"]?.ToString(), out var jobOwner) || jobOwner != ownerId) continue;
            if (!long.TryParse(job["status"]?.ToString(), out var status) || status >= IndustryJobCompleted) continue;
            if (long.TryParse(job["blueprintID"]?.ToString(), out var blueprintId) && blueprintId > 0)
                result[blueprintId] = jobId;
        }
        return result;
    }

    private async ValueTask<BlueprintRecord> BlueprintRecordAsync(Eve.Inventory.Item item, CancellationToken cancellationToken)
    {
        var locationType = stations.Get(item.LocationId) is { } station ? station.TypeId
            : (await items.GetAsync(item.LocationId, cancellationToken))?.TypeId ?? 0;
        return new BlueprintRecord(item.Id, item.TypeId, item.OwnerId, item.LocationId, locationType, item.Flag, item.BlueprintCopy,
            item.BlueprintRuns, item.MaterialEfficiency, item.TimeEfficiency, await SolarSystemOfAsync(item.LocationId, cancellationToken),
            item.Flag == CorporationOfficeStore.ImpoundedFlag);
    }

    /// <summary>The solar system a location is in: a station's, or that of the item the location is in (up to a few levels).</summary>
    private async ValueTask<long> SolarSystemOfAsync(long locationId, CancellationToken cancellationToken)
    {
        var current = locationId;
        for (var depth = 0; depth < MaxLocationDepth; depth++)
        {
            if (stations.Get(current) is { } station) return station.SolarSystemId;
            var container = await items.GetAsync(current, cancellationToken);
            if (container is null) return 0;
            current = container.LocationId;
        }
        return 0;
    }
}
