using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>An item that is somewhere else than it was.</summary>
public sealed record ItemMoved(Item After, long PreviousLocationId, int PreviousFlag);

/// <summary>Something dogma tells the client happened to a ship or a module.</summary>
public abstract record DogmaEvent;

/// <summary>An attribute of an item that has a new value (<paramref name="Old"/> is null when the item had none).</summary>
public sealed record AttributeChanged(long ItemId, int AttributeId, double? Old, double New) : DogmaEvent;

/// <summary>An effect that started or stopped on a module.</summary>
public sealed record EffectChanged(long ItemId, int EffectId, bool Started) : DogmaEvent;

public enum ShipRefusal
{
    /// <summary>The module is not fitted to the ship (or the ship is not the character's).</summary>
    NotFitted,

    /// <summary>The processor would be overloaded.</summary>
    NotEnoughCpu,

    /// <summary>The power grid would be overloaded.</summary>
    NotEnoughPower,

    /// <summary>The module needs a skill the character has not trained.</summary>
    SkillRequired,

    /// <summary>The module cannot go where it was put (see <see cref="ShipRefused.Fit"/>).</summary>
    CannotFit,

    /// <summary>No free slot suits the module.</summary>
    NoSuitableSlot,

    /// <summary>What was moved does not fit in the hold.</summary>
    NotEnoughCargoSpace,

    /// <summary>What was moved does not fit in the can or wreck it was put in (the same room figures).</summary>
    NoSpaceForThat,

    /// <summary>What was moved does not fit in the drone bay (the same room figures: <see cref="ShipRefused.Available"/> and <see cref="ShipRefused.Volume"/>).</summary>
    NotEnoughDroneBaySpace,

    /// <summary>What was put in the drone bay is not a drone.</summary>
    NotADrone,

    /// <summary>The item type is not accepted by the selected ship hold.</summary>
    NotAllowedInHold,
}

public abstract record ShipOutcome;

/// <summary>Nothing was done, and the client is told nothing (the reference answers None): an item that is not there or not the character's.</summary>
public sealed record NothingHappened : ShipOutcome;

/// <summary>What was asked cannot be done, and why.</summary>
public sealed record ShipRefused(
    ShipRefusal Reason, FitFailure? Fit = null, int ModuleTypeId = 0, int ShipTypeId = 0, string? Detail = null, double Available = 0, double Volume = 0) : ShipOutcome;

/// <summary>
/// What was done: the items that moved, and what dogma reports of it, in the order the client is told. A move that is not a fitting one (goods into the hold, out of it,
/// stacks split and stacked) is told as the items it changed, each with what it was before (<paramref name="Stacks"/>).
/// </summary>
public sealed record ShipChanged(long ItemId, long ShipId, IReadOnlyList<ItemMoved> Items, IReadOnlyList<DogmaEvent> Events, IReadOnlyList<ItemChanged>? Stacks = null) : ShipOutcome;
