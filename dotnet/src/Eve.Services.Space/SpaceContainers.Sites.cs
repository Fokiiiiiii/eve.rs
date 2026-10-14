using Eve.Inventory;
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

    /// <summary>Only a matching site row in the looted state makes a site container inventory-openable.</summary>
    private async Task<bool> IsLootedSiteContainerAsync(long containerId, int typeId, int groupId, CancellationToken cancellationToken)
    {
        if (SiteContainers is not { } store ||
            await store.GetAsync(containerId, cancellationToken) is not { State: "looted" } record)
        {
            return false;
        }
        return record.TypeId == typeId && record.GroupId == groupId;
    }

    /// <summary>The owner-0 shell stays public; a pilot may move only its own selected cargo from a looted site container.</summary>
    private async Task<bool> CanTakeFromSiteContainerAsync(long characterId, Item container, IReadOnlyList<long>? itemIds, CancellationToken cancellationToken)
    {
        if (itemIds is not { Count: > 0 } ||
            _statics.Type(container.TypeId) is not { } type ||
            !await IsLootedSiteContainerAsync(container.Id, container.TypeId, type.GroupId, cancellationToken))
        {
            return false;
        }
        foreach (var id in itemIds.Distinct())
        {
            if (await _items.GetAsync(id, cancellationToken) is not { } item ||
                item.LocationId != container.Id || item.OwnerId != characterId)
            {
                return false;
            }
        }
        return true;
    }
}
