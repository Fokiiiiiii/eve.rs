using Eve.Characters;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>A ship that went into space or came out of it: where it is now and where it was.</summary>
public sealed record SpaceMove(Item Ship, long PreviousLocationId, int PreviousFlag, Character Character);

/// <summary>What happened to an undock or a dock: the move, or why the ship stayed where it was.</summary>
public sealed record SpaceTransition(SpaceMove? Moved, string? Refusal)
{
    public static SpaceTransition Done(SpaceMove moved) => new(moved, null);

    public static SpaceTransition Refused(string reason) => new(null, reason);
}

public sealed partial class CharacterShips
{
    /// <summary>The flag of an item that is in space, not in any inventory place.</summary>
    public const int InSpaceFlag = 0;

    /// <summary>
    /// The character's active ship leaves the hangar of the station the character is at and is in the solar system, in space. The character's
    /// station stays what it was (it is where the ship docks again if the simulation loses it).
    /// </summary>
    public async Task<SpaceTransition> UndockAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return SpaceTransition.Refused("NO_SHIP");
        }
        if (ship.LocationId != character.StationId || ship.Flag != ItemFlags.Hangar)
        {
            return SpaceTransition.Refused(ship.LocationId == character.SolarSystemId ? "ALREADY_IN_SPACE" : "NOT_DOCKED");
        }
        await items.MoveAsync(connection, transaction, ship.Id, character.SolarSystemId, InSpaceFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SpaceTransition.Done(new SpaceMove(ship with { LocationId = character.SolarSystemId, Flag = InSpaceFlag }, ship.LocationId, ship.Flag, character));
    }

    /// <summary>The character's active ship, in space, is put in the hangar of a station of the solar system it is in, and the character is there.</summary>
    public async Task<SpaceTransition> DockAsync(long characterId, long stationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return SpaceTransition.Refused("NO_SHIP");
        }
        if (ship.LocationId != character.SolarSystemId || ship.Flag != InSpaceFlag)
        {
            return SpaceTransition.Refused("NOT_IN_SPACE");
        }
        await items.MoveAsync(connection, transaction, ship.Id, stationId, ItemFlags.Hangar, cancellationToken);
        await characters.SetStationAsync(connection, transaction, characterId, stationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SpaceTransition.Done(new SpaceMove(ship with { LocationId = stationId, Flag = ItemFlags.Hangar }, ship.LocationId, ship.Flag, character with { StationId = stationId }));
    }

    /// <summary>
    /// A ship left in space (the simulation was lost, the host restarted) is put back in the hangar of the station the character last was at,
    /// so that a character never wakes up in a ship the simulation does not have. Returns whether anything was moved.
    /// </summary>
    public async Task<bool> RecoverFromSpaceAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId
            || ship.LocationId != character.SolarSystemId || ship.Flag != InSpaceFlag)
        {
            return false;
        }
        await items.MoveAsync(connection, transaction, ship.Id, character.StationId, ItemFlags.Hangar, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
