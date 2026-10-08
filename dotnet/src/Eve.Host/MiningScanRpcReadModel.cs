using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Ships;

namespace Eve.Host;

/// <summary>Builds the survey-scan view from persisted asteroid state and the live space simulation.</summary>
public sealed class MiningScanRpcReadModel(
    SpaceSessions space,
    PgItemStore items,
    PgSpaceItemStore spaceItems,
    DogmaStatic statics)
{
    private const double ScanDistanceMeters = 250_000;

    public async ValueTask<IReadOnlyList<MiningScanResult>> ReadAsync(
        ClientSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var systemId = space.SolarSystemOf(session);
        var shipId = space.ShipIdOf(session);
        if (systemId is not > 0 || shipId is not > 0)
            return [];

        var ship = await items.GetAsync(shipId.Value, cancellationToken).ConfigureAwait(false);
        if (ship is null)
            return [];
        var shipPosition = await space.PositionOfAsync(session, shipId.Value, cancellationToken).ConfigureAwait(false);
        if (shipPosition is not { } origin || origin.SolarSystemId != systemId)
            return [];

        await statics.EnsureTypesAsync([ship.TypeId], cancellationToken).ConfigureAwait(false);
        var radiusAttribute = statics.AttributeId("radius");
        var shipRadius = radiusAttribute is { } radiusId && statics.Type(ship.TypeId) is { } shipType
            ? Nonnegative(shipType.Attributes.GetValueOrDefault(radiusId))
            : 0;

        var storedItems = await items.InLocationAsync(systemId.Value, ownerId: 0, cancellationToken).ConfigureAwait(false);
        var byId = storedItems.ToDictionary(item => item.Id);
        var inSpace = await spaceItems.InSystemAsync(systemId.Value, cancellationToken).ConfigureAwait(false);
        var candidates = inSpace
            .Where(spaceItem => byId.TryGetValue(spaceItem.ItemId, out var item) && item.Quantity > 0)
            .Select(spaceItem => (Space: spaceItem, Item: byId[spaceItem.ItemId]))
            .ToArray();
        if (candidates.Length == 0)
            return [];

        await statics.EnsureTypesAsync(candidates.Select(candidate => candidate.Item.TypeId).Distinct().ToArray(), cancellationToken)
            .ConfigureAwait(false);
        var results = new List<MiningScanResult>();
        foreach (var candidate in candidates)
        {
            if (statics.Type(candidate.Item.TypeId) is not { } type ||
                statics.CategoryName(type.CategoryId) != "Asteroid" ||
                SurfaceDistance(origin, shipRadius, candidate.Space.Position, candidate.Space.Radius) > ScanDistanceMeters)
                continue;

            var position = await space.PositionOfAsync(session, candidate.Space.ItemId, cancellationToken).ConfigureAwait(false);
            if (position is not { } current || current.SolarSystemId != systemId)
                continue;
            var distance = SurfaceDistance(origin, shipRadius, [current.X, current.Y, current.Z], candidate.Space.Radius);
            if (distance <= ScanDistanceMeters)
                results.Add(new(candidate.Item.Id, candidate.Item.TypeId, candidate.Item.Quantity, distance));
        }
        return results;
    }

    private static double SurfaceDistance(
        WarpCoordinates left,
        double leftRadius,
        IReadOnlyList<double> right,
        double rightRadius)
    {
        if (right.Count < 3 || right.Take(3).Any(value => !double.IsFinite(value)))
            return double.PositiveInfinity;
        var x = left.X - right[0];
        var y = left.Y - right[1];
        var z = left.Z - right[2];
        return Math.Max(0, Math.Sqrt(x * x + y * y + z * z) - Nonnegative(leftRadius) - Nonnegative(rightRadius));
    }

    private static double Nonnegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
