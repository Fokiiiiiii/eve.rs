using Eve.Space;
using Eve.Services.Space.Sites;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    /// <summary>The site containers (their rows are in <see cref="SiteContainerStore"/>). Null: no site container is put in the scene.</summary>
    public SiteContainerStore? SiteContainers { get; set; }

    /// <summary>Puts the hackable containers of a system back in the scene: on a pilot's entry, and after a site is filled in it.</summary>
    public async Task RestoreSiteContainersAsync(int system)
    {
        if (SiteContainers is not { } store)
        {
            return;
        }
        try
        {
            var now = _nowUnixMs();
            foreach (var container in await store.InSystemAsync(system))
            {
                if (container.ExpiresAtMs <= now)
                {
                    continue;
                }
                _sim.RestoreDeployed(new SimDeployed(
                    container.SolarSystemId, container.ContainerId, "Site container", container.TypeId, container.GroupId, container.Position, container.Radius,
                    0, 0, 0, 0, container.ExpiresAtMs - now));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the site containers of system {system} were not put back: {error.Message}");
        }
    }

    /// <summary>A destroyed container leaves the scene.</summary>
    public void RemoveSiteContainer(long containerId) => _sim.RemoveDeployed(containerId);
}
