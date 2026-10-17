using Eve.Inventory;
using Eve.Planets;
using Eve.Services.Planets;
using Eve.Marshal;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private PlanetMgrService planetMgr = default!;
    private PlanetOrbitalRegistryService planetRegistry = default!;

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
            skills, now, NotifyPlanetChanged, NotifyLaunchesChanged, Env("EVE_INVENTORY_SCHEMA", PgItemStore.DefaultSchema), sim, spaceItemStore, dogma);
        await planetMgr.PlaceOfficesAsync();
        // a pilot that enters a system brings its offices back into the simulation's scene (as the cans and the structures are)
        if (spaceSessions is not null)
            spaceSessions.SystemEntered += system => _ = planetMgr.PlaceOfficesAsync(system);
        planetRegistry = new PlanetOrbitalRegistryService(planetMgr, now);
    }

    /// <summary>The inventory broker, opened with the planetary customs offices (their import and export runs in the planets' transaction).</summary>
    private Eve.Services.Inventory.InvBrokerService WithCustoms(Eve.Services.Inventory.InvBrokerService broker)
    {
        broker.Customs = planetMgr;
        return broker;
    }

    private Task NotifyPlanetChanged(long owner, PyValue? planet)
    {
        if (characterSessions.Of(owner) is { } session)
            session.SendNotification("OnMajorPlanetStateUpdate", "clientID", planet ?? PyNone.Instance, PyValue.Of(false));
        return Task.CompletedTask;
    }

    /// <summary>OnPILaunchesChange: a launch was made or ended; the client reloads its launches (no arguments on the wire).</summary>
    private Task NotifyLaunchesChanged(long owner)
    {
        if (characterSessions.Of(owner) is { } session)
            session.SendNotification("OnPILaunchesChange", "clientID");
        return Task.CompletedTask;
    }
}
