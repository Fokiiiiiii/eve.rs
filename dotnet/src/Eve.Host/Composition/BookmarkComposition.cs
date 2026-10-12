using Eve.Bookmarks;
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
    /// The place a static bookmark points at, from the static data: a station (its type and its solar system) or a solar system (its
    /// own location). Other ids are not places the store can bookmark here (structures, celestials), so they are refused.
    /// </summary>
    private BookmarkTarget? StaticBookmarkTarget(long itemId)
    {
        if (stations.Get(itemId) is { } station)
            return new BookmarkTarget(station.Id, station.TypeId, station.SolarSystemId);
        if (itemId is > 0 and <= int.MaxValue && solarSystems.Get((int)itemId) is { } system)
            return new BookmarkTarget(system.Id, 5, system.Id);
        return null;
    }
}
