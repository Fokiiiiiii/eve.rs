using Eve.Bookmarks;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services.Core;
using Eve.Universe;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private BookmarkStore bookmarks = default!;

    /// <summary>The bookmark folders (schema <c>bookmarks</c>, or <c>EVE_BOOKMARKS_SCHEMA</c>).</summary>
    private async Task BookmarkStores()
    {
        var schema = Env("EVE_BOOKMARKS_SCHEMA", BookmarkStore.DefaultSchema);
        await BookmarkStore.MigrateAsync(source, schema);
        bookmarks = new BookmarkStore(source, schema, now);
    }

    /// <summary>
    /// Every minute: removes the expired shared bookmarks and tells their viewers, and tells the characters whose corporation or alliance

    /// </summary>
    private void UpkeepSharedFolders(Eve.Services.Core.SharedFolderNotifier notifier)
    {
        foundations.LocalTimers.Set("bookmarks.upkeep", TimeSpan.FromMinutes(1), () =>
        {
            UpkeepSharedFolders(notifier);
            return notifier.UpkeepAsync();
        });
    }

    private ValueTask<BookmarkTarget?> StaticBookmarkTarget(long itemId, CancellationToken token) =>
        ResolveStaticBookmarkTargetAsync(stations, solarSystems, structureDirectoryReadModel.GetStructureInfoAsync, itemId, token);

    internal static async ValueTask<BookmarkTarget?> ResolveStaticBookmarkTargetAsync(
        StationCatalog stations,
        SolarSystemCatalog solarSystems,
        Func<long, CancellationToken, ValueTask<PersistedStructureInfo?>> readStructure,
        long itemId,
        CancellationToken token)
    {
        if (itemId <= 0)
            return null;
        if (stations.Get(itemId) is { } station)
            return new BookmarkTarget(station.Id, station.TypeId, station.SolarSystemId);
        if (itemId <= int.MaxValue && solarSystems.Get((int)itemId) is { } system)
            return new BookmarkTarget(system.Id, 5, system.Id);

        if (solarSystems.Systems.Any(system => system.ConstellationId == itemId))
            return new BookmarkTarget(itemId, 4, itemId);
        if (solarSystems.Systems.Any(system => system.RegionId == itemId))
            return new BookmarkTarget(itemId, 3, itemId);

        if (await readStructure(itemId, token) is not { PersistentlyDeployed: true } structure)
            return null;
        var typeId = StructureNumber(structure.Info, "typeID");
        var solarSystemId = StructureNumber(structure.Info, "solarSystemID");
        return typeId is > 0 and <= int.MaxValue && solarSystemId > 0
            ? new BookmarkTarget(itemId, (int)typeId, solarSystemId)
            : null;
    }

    private static long StructureNumber(PyValue info, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = info switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict args } => args.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => [],
        };
        foreach (var (key, value) in entries)
        {
            if (JsSemantics.StringValue(key) != name)
                continue;
            return value switch
            {
                PyInt number => number.Value,
                PyLong number => number.Value,
                _ => 0,
            };
        }
        return 0;
    }
}
