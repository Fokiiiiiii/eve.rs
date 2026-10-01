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
