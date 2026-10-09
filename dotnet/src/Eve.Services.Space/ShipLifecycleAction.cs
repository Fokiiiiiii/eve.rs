using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Events;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;
using ModuleAttributes = Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space;

/// <summary>
/// The pilots that are in space, and everything the service host does around the simulation: sending a ship out (the item, the session,
/// the notifications), forwarding what the simulation says to the client that plays the ship, and bringing the ship home when it docks, or
/// when the simulation is lost. What happens in space itself (motion, the balls, the stamps) is the simulation's (ADR 0007).
/// </summary>
public enum ShipLifecycleAction
{
    SafeLogoff,
    Eject,
    Board
}
