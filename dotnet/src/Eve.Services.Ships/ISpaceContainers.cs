using Eve.Inventory;
using Eve.Ships;

namespace Eve.Services.Ships;

/// <summary>
/// What the inventory broker and the ship service ask of the space a pilot flies in: the cans and wrecks beside the ship (what a pilot may open, from how far, and what it may
/// take), the goods that go between the ship's hold and them, and the cargo a pilot jettisons into a can. Implemented where the simulation is (<c>SpaceContainers</c>).
/// </summary>
public interface ISpaceContainers
{
    /// <summary>Whether the session has a ship flying in space.</summary>
    bool IsInSpace(ClientSession session);

    /// <summary>The solar system the session flies in.</summary>
    int? SolarSystemOf(ClientSession session);

    /// <summary>
    /// The can or wreck with this id in the pilot's solar system, for the pilot to open: its item, or null when there is no such container (anything else is no inventory).
    /// Throws the client's error when the pilot cannot open it from where it is: out of range (<c>TargetTooFar</c>), not in view (<c>FakeItemNotFound</c>).
    /// </summary>
    Task<Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves goods between the ship in space and a can or wreck (taken out of it into the hold, or put from the hold into it), and tells the pilot's client what changed, in the
    /// reference's order. Answers the id of the item that moved (a stack split off is a new one), or null when nothing was done. Throws the client's error for what is refused.
    /// </summary>
    Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken);
    Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, int? destinationFlag, CancellationToken cancellationToken) =>
        MoveAsync(session, itemId, sourceId, destinationId, quantity, cancellationToken);

    /// <summary>
    /// "Loot All": takes everything of <paramref name="itemIds"/> that is in a can or a wreck into the ship's hold in one go (all or nothing for room), and tells the pilot's client as the
    /// reference does for the batch. Answers whether something moved, or null when this is no move of the sort (the ship in space, a container to take from). Throws the client's error
    /// for what is refused (<c>SafetyActivated</c>, <c>NotEnoughCargoSpace</c>, out of range).
    /// </summary>
    Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken);
    Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, int? destinationFlag, CancellationToken cancellationToken) =>
        MoveAllAsync(session, itemIds, sourceId, destinationId, cancellationToken);

    /// <summary>Moves at most the requested units of each stack with the batch's cumulative capacity check and notifications.</summary>
    Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId,
        int? destinationFlag, long? quantity, CancellationToken cancellationToken) =>
        quantity is null
            ? MoveAllAsync(session, itemIds, sourceId, destinationId, destinationFlag, cancellationToken)
            : throw new NotSupportedException("This space container service cannot move a partial batch.");

    /// <summary>Refreshes an active ship's presentation from committed inventory after its location-name notification.</summary>
    Task RefreshShipNameAsync(ClientSession session, long itemId, CancellationToken cancellationToken) => Task.CompletedTask;


    /// <summary>Abandons loot rights for owned cans or wrecks without moving their contents.</summary>
    Task AbandonLootAsync(ClientSession session, IReadOnlyList<long> containerIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This space container service cannot abandon loot.");

    /// <summary>
    /// <c>ship.Jettison</c>: the stacks of the hold go into a new can ahead of the ship. Answers the ids that went into a can (none when nothing could be jettisoned); throws the
    /// client's error while the pilot waits for the next can.
    /// </summary>
    Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// <c>ship.LaunchFromShip</c> of an item of the hold: a mobile warp disruptor goes out into space (its id is answered); anything else is no launch of this sort
    /// (null, nothing is done). Throws the client's notice for a refusal.
    /// </summary>
    Task<long?> LaunchFromShipAsync(ClientSession session, long itemId, CancellationToken cancellationToken) => Task.FromResult<long?>(null);
}


/// <summary>The in-space drone-bay inventory boundary. Implementations must use live scene ownership and simulation commands.</summary>
public interface ISpaceDroneBays
{
    /// <summary>The active ship's in-space bay capacity and physical volume.</summary>
    Task<CharacterShips.Room?> RoomAsync(ClientSession session, long shipId, int flag, CancellationToken cancellationToken);

    /// <summary>Orders an owned, scene-controlled drone to return; the simulation's scoop event performs the database move and notifications.</summary>
    Task<long?> AddAsync(ClientSession session, long itemId, long sourceId, long destinationId, int destinationFlag, long? quantity, CancellationToken cancellationToken);
}
