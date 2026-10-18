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
    /// Every minute: removes the expired shared bookmarks and tells their viewers, and tells the characters whose corporation or alliance
    /// changed what their shared folders now give them (the wiki: changes apply within five minutes). The timer re-arms itself.
    /// </summary>
    private void UpkeepSharedFolders(Eve.Services.Core.SharedFolderNotifier notifier)
    {
        foundations.LocalTimers.Set("bookmarks.upkeep", TimeSpan.FromMinutes(1), () =>
        {
            UpkeepSharedFolders(notifier);
            return notifier.UpkeepAsync();
        });
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
