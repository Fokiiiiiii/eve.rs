using Eve.Inventory;

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

    /// <summary>
    /// <c>ship.Jettison</c>: the stacks of the hold go into a new can ahead of the ship. Answers the ids that went into a can (none when nothing could be jettisoned); throws the
    /// client's error while the pilot waits for the next can.
    /// </summary>
    Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken);
}
