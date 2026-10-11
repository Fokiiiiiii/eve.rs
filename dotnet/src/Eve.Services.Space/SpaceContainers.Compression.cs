using Eve.Inventory;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>A ship in space that runs a compressor: its pilot, system, radius and the SDE type lists it compresses, each with the metres it reaches.</summary>
public sealed record CompressionFacility(long CharacterId, int SolarSystemId, double Radius, IReadOnlyDictionary<int, int> TypeLists);

/// <summary>What <c>inSpaceCompressionMgr.CompressItemInSpace</c> answers: the stack that was compressed and the stack it became (the client's six values).</summary>
public sealed record InSpaceCompression(long SourceItemId, int SourceTypeId, long SourceQuantity, long OutputItemId, int OutputTypeId, long OutputQuantity);

public sealed partial class SpaceContainers
{
    public const string CompressNotInSpaceNotice = "You must be in space to compress that item.";
    public const string CompressTetheredNotice = "You cannot use in-space compression while tethered.";
      public const string CompressNoFacilityNotice = "That ship is not running a compressor.";
    public const string CompressNotInFleetNotice = "You can only compress at the compressor of a ship in your fleet.";
    public const string CompressOutOfRangeNotice = "That compressor is too far away.";
    public const string CompressWrongKindNotice = "That compressor cannot compress that item.";
    public const string CompressNotCompressibleNotice = "That item cannot be compressed.";
    public const string CompressFailedNotice = "In-space compression did not complete.";

    /// <summary>Metres a ship may be past a compressor's reach before the host refuses (the reference allows one metre, as for a command burst: positions move between the client's check and ours).</summary>
    private const double CompressReachTolerance = 1;

    /// <summary>
    /// <c>CompressItemInSpace</c>: the pilot compresses a stack of its own ship at the compression facility <paramref name="facilityShipId"/> (the ship's own ball or a fleet
    /// member's, whichever shows the compressor in the client's slim item). The rules are the wiki's (Compression): the facility's compressor must run, its pilot must be in
    /// the fleet of the one who compresses (the ship's own pilot always is), the item's type must be in the type list the compressor works, and the ship must be within the
    /// compressor's range (the surface distance, measured by the simulation). Anything else is a named <c>CustomNotify</c> refusal, never <c>None</c>. The stack is replaced by
    /// its compressed type in the same hold in one inventory transaction and the pilot is told the item changes.
    /// </summary>
    public async Task<InSpaceCompression> CompressInSpaceAsync(ClientSession session, Item item, long facilityShipId, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || !_space.IsInSpace(session) || _space.ShipIdOf(session) is not { } shipId
            || _space.SolarSystemOf(session) is not { } system)
        {
            throw CompressRefusal(CompressNotInSpaceNotice);
        }
        if (_space.IsTethered(session)) throw CompressRefusal(CompressTetheredNotice);
          await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (await _statics.CompressedTypeOfAsync(item.TypeId, cancellationToken) is not { } compressedTypeId)
        {
            throw CompressRefusal(CompressNotCompressibleNotice);
        }
        if (await _space.CompressionFacilityOfAsync(facilityShipId, cancellationToken) is not { } facility || facility.SolarSystemId != system)
        {
            throw CompressRefusal(CompressNoFacilityNotice);
        }
        var own = facilityShipId == shipId;
        if (!own && !await InOneFleetAsync(characterId, facility.CharacterId, cancellationToken))
        {
            throw CompressRefusal(CompressNotInFleetNotice);
        }
        var reach = 0;
        foreach (var (listId, range) in facility.TypeLists)
        {
            if (await _statics.TypeListNamesAsync(listId, item.TypeId, cancellationToken))
            {
                reach = Math.Max(reach, range);
            }
        }
        if (reach <= 0)
        {
            throw CompressRefusal(CompressWrongKindNotice);
        }
        if (!own)
        {
            if (await SurfaceDistanceToShipAsync(session, characterId, shipId, facility, facilityShipId, cancellationToken) is not { } distance)
            {
                throw CompressRefusal(CompressNoFacilityNotice);
            }
            if (distance > reach + CompressReachTolerance)
            {
                throw CompressRefusal(CompressOutOfRangeNotice);
            }
        }

        var commit = await _ships.CompressAsync(characterId, shipId, system, item.Id, compressedTypeId, cancellationToken);
        if (!commit.Accepted)
        {
            throw CompressRefusal(commit.Reason == "NO_SHIP" ? CompressNotInSpaceNotice : CompressFailedNotice);
        }
        if (commit.Change is { } change)
        {
            try
            {
                await _statics.EnsureTypesAsync([.. (change.Stacks ?? []).Select(stack => stack.After.TypeId).Distinct()], cancellationToken);
                Eve.Services.Ships.ShipNotifications.Send(session, characterId, change, RowOf, Py.FileTimeFromUnixMs(_nowUnixMs()));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"the compression of item {item.Id} was saved but its item notification failed: {error.Message}");
            }
        }
        return new InSpaceCompression(commit.SourceItemId, commit.SourceTypeId, commit.SourceQuantity, commit.OutputItemId, commit.OutputTypeId, commit.OutputQuantity);
    }

    /// <summary>Whether two different pilots are in one fleet (the fleet of each, equal and not none).</summary>
    private async Task<bool> InOneFleetAsync(long first, long second, CancellationToken cancellationToken) =>
        first != second && _space.FleetIdOfAsync is { } fleetOf
        && await fleetOf(first, cancellationToken) is { } firstFleet && await fleetOf(second, cancellationToken) is { } secondFleet && firstFleet == secondFleet;

    /// <summary>
    /// The metres between the surface of the pilot's ship and the surface of another ship (the client measures the same, <c>GetSurfaceDist</c>): the simulation gives
    /// the position of each ball, and the two radii come off the distance between the centres. Null when the simulation does not have either ship in the scene.
    /// </summary>
    private async Task<double?> SurfaceDistanceToShipAsync(ClientSession session, long characterId, long shipId, CompressionFacility facility, long facilityShipId, CancellationToken cancellationToken)
    {
        if (await _space.PositionOfAsync(session, shipId, cancellationToken) is not { } own
            || await _space.PositionOfAsync(session, facilityShipId, cancellationToken) is not { } other)
        {
            return null;
        }
        var centres = Math.Sqrt(Math.Pow(own.X - other.X, 2) + Math.Pow(own.Y - other.Y, 2) + Math.Pow(own.Z - other.Z, 2));
        var ownRadius = await _ships.LoadAsync(characterId, cancellationToken) is { } snapshot && snapshot.Ship.Id == shipId ? snapshot.World.Get(shipId, SimShips.Radius) : 0d;
        return Math.Max(0d, centres - Math.Max(0d, ownRadius) - Math.Max(0d, facility.Radius));
    }

    private static MachoWrappedException CompressRefusal(string notice) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(notice)));
}
