using Eve.Planets;
using Eve.Services.Planets;
using Eve.Marshal;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private PlanetMgrService planetMgr = default!;

    /// <summary>
    /// The planetary interaction (planetMgr): the colony store (schema <c>planets</c>, or <c>EVE_PLANETS_SCHEMA</c>) and the static data
    /// of the planets, pins and schematics read from the SDE tables. The owner is told of each change after its commit.
    /// </summary>
    private async Task Planets()
    {
        var schema = Env("EVE_PLANETS_SCHEMA", PlanetStore.DefaultSchema);
        await PlanetStore.MigrateAsync(source, schema);
        var catalog = await PlanetCatalogLoader.LoadAsync(source);
        planetMgr = new PlanetMgrService(source, new PlanetStore(source, schema), catalog, new DeterministicResourceField(), items, economyWallets,
            skills, now, NotifyPlanetChanged);
    }

    private Task NotifyPlanetChanged(long owner, PyValue? planet)
    {
        if (characterSessions.Of(owner) is { } session)
            session.SendNotification("OnMajorPlanetStateUpdate", "clientID", planet ?? PyNone.Instance, PyValue.Of(false));
        return Task.CompletedTask;
    }
}
