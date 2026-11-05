using Eve.Services.Space.Sites;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    /// <summary>
    /// The resources of a gas or ore site, made when the site starts: the cloud of a reservoir (one of the candidates of its template, picked by the
    /// site's id), and the rocks of a deposit at their offsets from the site's origin. Each is an item launched by the site, in the scene of its system,
    /// as a belt's rock is. shortcut: the resources are not removed when the site ends (a belt keeps its rocks the same way until they are mined).
    /// </summary>
    public async Task PlaceSiteResourcesAsync(int solarSystemId, long siteId, SiteTemplate template, double[] origin)
    {
        try
        {
            if (template.Clouds is { Count: > 0 } clouds)
            {
                var cloud = clouds[(int)(Math.Abs(siteId) % clouds.Count)];
                await PlaceSiteResourceAsync(solarSystemId, siteId, cloud, 1_000, origin);
            }
            foreach (var rock in template.Rocks ?? [])
            {
                var position = new[] { origin[0] + rock.Offset[0], origin[1] + rock.Offset[1], origin[2] + rock.Offset[2] };
                await PlaceSiteResourceAsync(solarSystemId, siteId, rock.TypeId, rock.Quantity, position);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the resources of site {siteId} could not be placed: {error.Message}");
        }
    }

    /// <summary>
    /// The resources of the live sites of a system are put back in its scene when a pilot comes to it (the scene is made for the pilot, and the
    /// simulation drops it when nobody is left in it). A placement that is already in the scene is replaced, as the simulation does.
    /// </summary>
    public async Task RestoreSiteResourcesAsync(int solarSystemId, IReadOnlyCollection<long> siteIds)
    {
        if (siteIds.Count == 0)
        {
            return;
        }
        try
        {
            foreach (var spaceItem in await _spaceItems.InSystemAsync(solarSystemId))
            {
                if (!siteIds.Contains(spaceItem.LauncherId) || await _items.GetAsync(spaceItem.ItemId) is not { } stored || stored.Quantity <= 0)
                {
                    continue;
                }
                await _statics.EnsureTypesAsync([stored.TypeId]);
                if (_statics.Type(stored.TypeId) is not { } type)
                {
                    continue;
                }
                _placed.TryAdd(stored.Id, 0);
                _sim.PlaceAsteroid(new SimPlaceAsteroid(
                    solarSystemId, stored.Id, stored.TypeId, type.GroupId, type.CategoryId, stored.CustomName ?? type.Name ?? "Asteroid", type.GraphicId,
                    spaceItem.Position, spaceItem.Direction, spaceItem.Radius, stored.Quantity));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the resources of system {solarSystemId} were not put back: {error.Message}");
        }
    }

    /// <summary>
    /// The resources of a site that ends leave the world: each leaves the scene (as a depleted rock does) and the store (its item and its place), in the
    /// transaction of the end. A site's resources that the store does not have (none was placed) are no change.
    /// </summary>
    public async Task RemoveSiteResourcesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, long siteId)
    {
        foreach (var spaceItem in await _spaceItems.InSystemAsync(solarSystemId))
        {
            if (spaceItem.LauncherId != siteId)
            {
                continue;
            }
            _placed.TryRemove(spaceItem.ItemId, out _);
            try
            {
                _sim.RemoveWreck(spaceItem.ItemId, 2);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"resource {spaceItem.ItemId} of site {siteId} could not be taken out of the scene: {error.Message}");
            }
            await _spaceItems.RemoveAsync(connection, transaction, spaceItem.ItemId);
            await _items.DeleteAsync(connection, transaction, spaceItem.ItemId);
        }
    }

    private async Task PlaceSiteResourceAsync(int solarSystemId, long siteId, int typeId, long quantity, double[] position)
    {
        await _statics.EnsureTypesAsync([typeId]);
        if (_statics.Type(typeId) is not { } type)
        {
            _log?.Invoke($"site {siteId}: the resource type {typeId} has no static data and is not placed");
            return;
        }
        var radiusId = _statics.AttributeId("radius");
        var radius = radiusId is { } id ? type.Attributes.GetValueOrDefault(id, 25d) : 25d;
        if (!double.IsFinite(radius) || radius <= 0)
        {
            radius = 25d;
        }
        var created = await _ships.CreateAsteroidAsync(type.TypeId, solarSystemId, siteId, quantity, position, [1d, 0d, 0d], radius, _spaceItems);
        if (_placed.TryAdd(created.Item.Id, 0))
        {
            _sim.PlaceAsteroid(new SimPlaceAsteroid(
                solarSystemId, created.Item.Id, type.TypeId, type.GroupId, type.CategoryId, type.Name ?? "Asteroid", type.GraphicId,
                created.SpaceItem.Position, created.SpaceItem.Direction, created.SpaceItem.Radius, created.Item.Quantity));
        }
    }
}
