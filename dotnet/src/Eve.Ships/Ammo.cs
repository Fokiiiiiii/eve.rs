using Eve.Inventory;

namespace Eve.Ships;

/// <summary>What a change of ammunition makes the client hear, in the order it is told.</summary>
public abstract record AmmoEvent;

/// <summary>
/// A stack changed: its row after, and where it was, how many it held. The client is told the quantity (the stack's size) that differed, the place it left
/// and the flag it left; a stack that was taken whole into a module is told as leaving for <see cref="CharacterShips.JunkLocation"/>.
/// </summary>
public sealed record ItemChanged(Item After, long PreviousLocationId, int PreviousFlag, long PreviousQuantity) : AmmoEvent;

/// <summary>A module is about to hold rounds of a charge (the client shows the reload; <paramref name="ReloadMs"/> is the module's reload time).</summary>
public sealed record ChargeBeingLoaded(IReadOnlyList<long> ModuleIds, int ChargeTypeId, int ReloadMs) : AmmoEvent;

/// <summary>The rounds in a module's slot are another number: the charge is told by the ship, the slot and the type (the client has no item id for it).</summary>
public sealed record ChargeQuantityChanged(long ShipId, int Flag, int TypeId, long New, long Old) : AmmoEvent;

/// <summary>A type of charge in a slot the client has not heard of: what it is told so it can show it (the charge's attributes, with no rounds yet).</summary>
public sealed record ChargePrimed(long ShipId, int Flag, int TypeId, IReadOnlyDictionary<int, double> Attributes) : AmmoEvent;

/// <summary>The attributes of a module that the charge changed (the range and the capacitor it needs, and so on), in the order of the attributes.</summary>
public sealed record ModuleAttributesChanged(IReadOnlyList<AttributeChanged> Changes) : AmmoEvent;

/// <summary>The fitting of a docked ship is told again: one row for each module, in the order of the slots.</summary>
public sealed record FittingRefreshed(IReadOnlyList<Item> Modules) : AmmoEvent;

/// <summary>What a load or an unload did to the character's ship: what the client is told.</summary>
public sealed record AmmoChanged(long ShipId, IReadOnlyList<AmmoEvent> Events) : ShipOutcome;

/// <summary>
/// What the simulation says of a gun when the rounds in it are changed: whether the gun runs (then it keeps its rounds, and <paramref name="Before"/> and
/// <paramref name="After"/> are what they were and what they are), or not (the character's items have the rounds and nothing was changed there).
/// </summary>
public sealed record GunRounds(bool Running, long Before, long After)
{
    public static GunRounds Idle { get; } = new(false, 0, 0);
}

/// <summary>
/// A reload a pilot asked for in space (<c>LoadAmmo</c>): the module, the kind of charge, the stacks of the hold the rounds come from (all the stacks of the kind when it names
/// none) and how long the module takes. A module that takes no time (<see cref="ReloadMs"/> is 0) is loaded at once.
/// </summary>
public sealed record ReloadPlan(long ShipId, long ModuleId, int ModuleTypeId, int ModuleFlag, int ChargeTypeId, IReadOnlyList<long> SourceItemIds, int ReloadMs);

/// <summary>
/// A reload that was done: what the client is told, and the rounds the gun was given by the simulation that the hold could not give it
/// (<paramref name="Shortfall"/>: the stacks changed in the meantime), which the simulation takes back.
/// </summary>
public sealed record Reloaded(AmmoChanged Changed, long Shortfall) : ShipOutcome;
