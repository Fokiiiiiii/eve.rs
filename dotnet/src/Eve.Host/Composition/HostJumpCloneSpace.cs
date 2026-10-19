using Eve.Dogma;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Ships;

namespace Eve.Host;

/// <summary>
/// The space side of the jump clone installs: the pilot's active ship and its Clone Vat Bay (the fittings and the hull's dogma), the
/// distance of two ships in the scene, the online sessions, and the structures' clone bays.
/// </summary>
internal sealed class HostJumpCloneSpace(SpaceSessions? space, CharacterShips ships, DogmaStatic dogma, CharacterSessions sessions,
    Func<long, long, CancellationToken, ValueTask<StructureCloneBay?>> structureBay) : IJumpCloneSpace
{

    private const int CloneVatBayI = 23735;

    /// <summary>The SDE attributes of a hull: maxJumpClones (979), canReceiveCloneJumps (982) and maxOperationalDistance (715).</summary>
    private const int MaxJumpClonesAttribute = 979, CanReceiveCloneJumpsAttribute = 982, MaxOperationalDistanceAttribute = 715;

    private static readonly IReadOnlyDictionary<int, double> NoAttributes = new Dictionary<int, double>();

    public async ValueTask<ShipCloneBay?> ActiveShipAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var shipId = (long?)session.GetInt("shipid") ?? 0;
        if (session.CharacterId is not { } characterId || shipId <= 0)
            return null;
        if (await ships.ShipTypeOfAsync(characterId, shipId, cancellationToken) is not { } typeId)
            return null;
        await dogma.EnsureTypesAsync([typeId], cancellationToken);
        var attributes = dogma.Type(typeId)?.Attributes ?? NoAttributes;
        // shortcut: a fitted Clone Vat Bay counts whether or not it is online (EVE.js isEffectivelyOnlineModule); the fittings keep no
        // online state outside a space scene. Upgrade: read the module state from DogmaIM when it is needed.
        var hasBay = (await ships.FittedModulesAsync(characterId, shipId, cancellationToken)).Any(module => module.TypeId == CloneVatBayI);
        return new ShipCloneBay(
            shipId,
            attributes.GetValueOrDefault(CanReceiveCloneJumpsAttribute) > 0,
            hasBay,
            (int)Math.Max(0, attributes.GetValueOrDefault(MaxJumpClonesAttribute)),
            attributes.GetValueOrDefault(MaxOperationalDistanceAttribute));
    }

    /// <summary>
    /// The centre-to-centre distance of two ships in one system. shortcut: the hulls' radii are not taken off (EVE.js
    /// <c>calculateSurfaceDistanceMeters</c> does), so the range is measured centre to centre; upgrade with the ship radii of the scene.
    /// </summary>
    public async ValueTask<double?> DistanceAsync(ClientSession first, long firstShipId, ClientSession second, long secondShipId, CancellationToken cancellationToken)
    {
        // no space scene on this server: no ship is in space
        if (space is null || await space.PositionOfAsync(first, firstShipId, cancellationToken) is not { } at)
            return null;
        if (await space.PositionOfAsync(second, secondShipId, cancellationToken) is not { } other || at.SolarSystemId != other.SolarSystemId)
            return null;
        var (dx, dy, dz) = (at.X - other.X, at.Y - other.Y, at.Z - other.Z);
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    public ClientSession? SessionOf(long characterId) => sessions.Of(characterId);

    public ValueTask<StructureCloneBay?> StructureBayAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
        structureBay(characterId, structureId, cancellationToken);
}
