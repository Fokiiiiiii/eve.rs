using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    /// <summary>
    /// A belt holds a harvestable gas cloud beside its rock: the arrival that seeds the rock seeds the cloud too (the belt generation, reused). When no live cloud
    /// is left in the belt, a pilot's arrival makes one, of the published cloud type the belt's id picks (a belt keeps its kind). shortcut: CCP keeps gas clouds in
    /// gas sites, not in belts; the site model is not built, so the belts carry the clouds here (docs/reference/compat/DEVIATIONS.md).
    /// </summary>
    private async Task SeedGasCloudAsync(SimArrived arrived)
    {
        try
        {
            await SeedGasCloudCoreAsync(arrived);
        }
        catch (Exception error)
        {
            _log?.Invoke($"gas cloud for belt {arrived.BeltId} could not be seeded: {error.Message}");
        }
    }

    private async Task SeedGasCloudCoreAsync(SimArrived arrived)
    {
        if (arrived.BeltId <= 0 || arrived.Position.Count != 3 || arrived.Position.Any(position => !double.IsFinite(position)))
        {
            return;
        }
        var session = _space.ClientSessionOf(arrived.SessionId);
        if (session is null || !_space.IsInSpace(session) || _space.SolarSystemOf(session) is not { } system || system <= 0)
        {
            return;
        }
        var belt = (system, arrived.BeltId);
        if (!_seedingClouds.TryAdd(belt, 0))
        {
            return;
        }
        try
        {
            foreach (var spaceItem in await _spaceItems.InSystemAsync(system))
            {
                if (spaceItem.LauncherId != arrived.BeltId || await _items.GetAsync(spaceItem.ItemId) is not { } stored)
                {
                    continue;
                }
                await _statics.EnsureTypesAsync([stored.TypeId]);
                if (_statics.Type(stored.TypeId) is { } storedType && MiningTargets.IsGasCloud(_statics, storedType) && stored.Quantity > 0)
                {
                    return;
                }
            }

            // the clouds that have a gas to yield (MiningTargets); the belt's id picks one of them
            var clouds = new List<TypeDef>();
            foreach (var cloud in await _statics.TypesInGroupsAsync([MiningTargets.GasCloudGroup]))
            {
                if (await _statics.GasYieldTypeAsync(cloud.TypeId) is not null)
                {
                    clouds.Add(cloud);
                }
            }
            if (clouds.Count == 0)
            {
                _log?.Invoke($"no published gas cloud type with a gas to yield is available for belt {arrived.BeltId}");
                return;
            }
            var type = clouds[(int)(arrived.BeltId % clouds.Count)];
            var radiusId = _statics.AttributeId("radius");
            var radius = radiusId is { } id ? type.Attributes.GetValueOrDefault(id, 25d) : 25d;
            if (!double.IsFinite(radius) || radius <= 0)
            {
                radius = 25d;
            }
            var position = arrived.Position.ToArray();
            position[2] += Math.Max(1_000d, radius * 2d);

            // shortcut: a cloud holds 1,000 units, as a belt rock does; the stock of a cloud is not in the SDE (docs/reference/compat/DEVIATIONS.md)
            const long initialUnits = 1_000;
            var created = await _ships.CreateAsteroidAsync(
                type.TypeId, system, arrived.BeltId, initialUnits, position, [0d, 0d, 1d], radius, _spaceItems);
            if (_placed.TryAdd(created.Item.Id, 0))
            {
                try
                {
                    _sim.PlaceAsteroid(new SimPlaceAsteroid(
                        system, created.Item.Id, type.TypeId, type.GroupId, type.CategoryId, type.Name ?? MiningTargets.GasCloudGroup, type.GraphicId,
                        created.SpaceItem.Position, created.SpaceItem.Direction, created.SpaceItem.Radius, created.Item.Quantity));
                }
                catch (Exception error)
                {
                    _placed.TryRemove(created.Item.Id, out _);
                    _log?.Invoke($"gas cloud {created.Item.Id} in belt {arrived.BeltId} could not be placed: {error.Message}");
                }
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"gas cloud for belt {arrived.BeltId} could not be seeded: {error.Message}");
        }
        finally
        {
            _seedingClouds.TryRemove(belt, out _);
        }
    }

    private async Task SeedAsteroidBeltAsync(SimArrived arrived)
    {
        if (arrived.BeltId <= 0 || arrived.Position.Count != 3 || arrived.Position.Any(position => !double.IsFinite(position)))
        {
            return;
        }
        var session = _space.ClientSessionOf(arrived.SessionId);
        if (session is null || !_space.IsInSpace(session))
        {
            return;
        }
        if (_space.SolarSystemOf(session) is not { } system || system <= 0)
        {
            return;
        }

        var belt = (system, arrived.BeltId);
        // one arrival seeds a belt at a time; a second that comes meanwhile finds the rocks the first one made
        if (!_seedingBelts.TryAdd(belt, 0))
        {
            return;
        }
        try
        {
            var live = 0;
            foreach (var spaceItem in await _spaceItems.InSystemAsync(system))
            {
                if (spaceItem.LauncherId != arrived.BeltId
                    || await _items.GetAsync(spaceItem.ItemId) is not { } stored)
                {
                    continue;
                }
                await _statics.EnsureTypesAsync([stored.TypeId]);
                var storedType = _statics.Type(stored.TypeId);
                if (storedType is null || _statics.CategoryName(storedType.CategoryId) != "Asteroid")
                {
                    continue;
                }

                if (stored.Quantity <= 0)
                {
                    // a depleted rock is not the belt's any more: it does not stop the belt being seeded again
                    _placed.TryAdd(stored.Id, 0);
                    continue;
                }
                live++;
                if (_placed.TryAdd(stored.Id, 0))
                {
                    try
                    {
                        _sim.PlaceAsteroid(new SimPlaceAsteroid(
                            system, stored.Id, stored.TypeId, storedType.GroupId, storedType.CategoryId,
                            stored.CustomName ?? storedType.Name ?? "Asteroid", storedType.GraphicId,
                            spaceItem.Position, spaceItem.Direction, spaceItem.Radius, stored.Quantity));
                    }
                    catch (Exception error)
                    {
                        _placed.TryRemove(stored.Id, out _);
                        _log?.Invoke($"asteroid {stored.Id} in belt {arrived.BeltId} could not be placed: {error.Message}");
                    }
                }
            }
            if (live > 0)
            {
                return;
            }

            // no rock is left in the belt: it is seeded with the ores its system's security holds (BeltOres, from the EVE University belt rows)
            // shortcut: a store error part-way through leaves the belt short of ores until the rocks it has deplete and it is seeded again
            var types = await _statics.TypesInCategoryAsync("Asteroid");
            var radiusId = _statics.AttributeId("radius");
            var volumeId = _statics.AttributeId("volume");
            var index = 0;
            foreach (var name in BeltOres.For(_space.SecurityOfSystem(system) ?? 0d))
            {
                var type = types.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var unitVolume = type is not null && volumeId is { } volumeAttribute ? type.Attributes.GetValueOrDefault(volumeAttribute) : 0d;
                if (type is null || !(unitVolume > 0))
                {
                    _log?.Invoke($"ore {name} for belt {arrived.BeltId} has no published asteroid type with a volume");
                    continue;
                }

                var radius = radiusId is { } id ? type.Attributes.GetValueOrDefault(id, 25d) : 25d;
                if (!double.IsFinite(radius) || radius <= 0)
                {
                    radius = 25d;
                }
                var position = arrived.Position.ToArray();
                position[0] += (++index) * Math.Max(1_000d, radius * 2d);
                var created = await _ships.CreateAsteroidAsync(
                    type.TypeId, system, arrived.BeltId, BeltOres.UnitsFor(unitVolume), position, [1d, 0d, 0d], radius, _spaceItems);
                if (_placed.TryAdd(created.Item.Id, 0))
                {
                    try
                    {
                        _sim.PlaceAsteroid(new SimPlaceAsteroid(
                            system, created.Item.Id, type.TypeId, type.GroupId, type.CategoryId, type.Name, type.GraphicId,
                            created.SpaceItem.Position, created.SpaceItem.Direction, created.SpaceItem.Radius, created.Item.Quantity));
                    }
                    catch (Exception error)
                    {
                        _placed.TryRemove(created.Item.Id, out _);
                        _log?.Invoke($"seed asteroid {created.Item.Id} was saved but could not be placed: {error.Message}");
                    }
                }
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"asteroids for belt {arrived.BeltId} could not be seeded: {error.Message}");
        }
        finally
        {
            _seedingBelts.TryRemove(belt, out _);
        }
    }
}
