using Eve.Macho;
using Eve.Ships;

namespace Eve.Services.Ships;

/// <summary>What changes in the rounds of a gun: rounds go in (up to the capacity, and no more than the hold has) or come out (all of them when the quantity is 0).</summary>
public abstract record RoundsChange
{
    private RoundsChange()
    {
    }

    public sealed record Add(long Capacity, long Available) : RoundsChange;

    public sealed record Remove(long Quantity) : RoundsChange;
}

/// <summary>
/// What the ammunition calls ask of space. A gun that runs keeps its rounds in the simulation, so a change of them is made there first and the inventory follows what it says
/// (<see cref="GunRounds"/>): in the order the simulation hears of it, after every count of rounds it spent that it told before and before every one it told after.
/// </summary>
/// <summary>A gun of a ship the session flies has spent the rounds its next cycle needs and reloads from its hold: the host loads it (FP-C8).</summary>
public sealed record SpentGun(ClientSession Session, long CharacterId, long ShipId, long ModuleId, int ChargeTypeId);

public interface ISpaceRounds
{
    /// <summary>Raised when a reloading gun runs out of the rounds its next cycle needs (the simulation holds the gun until a load answers it).</summary>
    event Action<SpentGun>? GunRanDry;

    /// <summary>
    /// Changes the rounds of a module of the ship the session flies and, when the simulation has answered what they come to, runs <paramref name="afterwards"/> with it (the inventory
    /// is changed there). A module that does not run is not in the simulation: <paramref name="afterwards"/> is run with <see cref="GunRounds.Idle"/>. Throws when the simulation
    /// cannot be reached.
    /// </summary>
    Task<T> AdjustAsync<T>(ClientSession session, long moduleId, int chargeType, RoundsChange change, Func<GunRounds, Task<T>> afterwards, CancellationToken cancellationToken);

    /// <summary>The inventory could not give a gun that runs all the rounds the simulation added to it: the simulation takes them back. Nothing waits for it.</summary>
    void GiveBack(ClientSession session, long moduleId, long rounds);
}
