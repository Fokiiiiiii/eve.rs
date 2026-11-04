# ADR 0005: Fitting rules are a library, a ship change is one transaction, and what follows a reply is sent after it

- Status: accepted
- Date: 2026-09-29
- Decided by: the repository owner's direction (ADR 0001); the details are the implementer's.

## Context

Fitting a module, taking it out and switching it on or off change three things that different modules own:
where the item is (`Eve.Inventory`), whether the module is on (nothing owned it yet) and what the ship's
numbers are (`Eve.Dogma`, ADR 0004). The client is told about each change by two notifications whose order
and content the reference fixes: `OnMultiEvent` (what dogma reports) and `OnItemsChanged` (the item that
moved). It also expects some notifications to come after the answer to the call that caused them (the three
that follow `GetAllInfo`).

The reference does all of this in one handler that reads and writes its one store. That cannot be copied:
modules own their data (ADR 0003) and no module knows the protocol.

## Decision

1. **`Eve.Fitting` holds the rules and the module state.** `FittingRules.Check` says whether a module can be
   fitted to a ship in a slot and, if not, which rule it broke and the numbers a message needs (the slot is
   taken, a skill is missing, too many of a group, rig size, calibration, hard points...). It works on a
   `DogmaWorld`, does no input or output and knows no protocol. `Slots` says which flags are slots and how
   many a ship has of each kind. `PgModuleStateStore` keeps whether a module is online: a row per online
   module in its own schema (an offline module has no row), written in a transaction the caller owns.
2. **`Eve.Ships` is the one place that changes a ship.** `CharacterShips` takes the character's lock, loads the
   ship (`ShipSnapshot`: the character, the ship, what is in it, the skills, the module states, as a dogma
   world), decides, and moves the item and sets the module state in one transaction. It answers with an
   outcome (`NothingHappened`, `ShipRefused`, `ShipChanged`) that carries the item moves and the dogma events
   (an attribute changed from this to that, an effect started or stopped), not messages. Dogma decides which
   attributes changed by working out the ship before and after.
3. **The adapters say it in the protocol.** `Eve.Services.Ships` turns an outcome into the notifications the
   reference sends, in its order, and a refusal into the user error it shows (`CustomNotify`,
   `ModuleFitFailed`, `CantFitTooManyByGroup`, `CantFitTooManyByType`, `DeniedShipChanged`). `dogmaIM` and the
   inventory broker are thin over it.
4. **A notification that belongs after the answer runs after the answer.** A service handler can queue an
   action on the session (`ClientSession.AfterResponse`); the dispatcher runs the queue once the response has
   been sent, and drops it when the call ended in an error. This keeps the order the client sees without the
   handler owning the sending order.
5. **The state of a module belongs to the module, not to the ship's numbers.** Whether a module is online is
   dogma's `isOnline` attribute, worked out from the state the module has; a character's starter modules are
   made online with the ship, in the creation transaction.

## Consequences

- Fitting rules are unit-tested without a database (a hand-made world), and against the real ships and modules
  of the static data (`RealShipTests`); the ship changes are tested in PostgreSQL, and the notifications by
  decoding what the session was sent.
- A change that also touches something else (skill training changing a ship's numbers, space) takes the
  character's lock and one transaction the same way, and its events are the same `DogmaEvent`s.
- The rules are known only as far as the reference's validator lists them and the client shows them. A rule
  the reference does not check (for example, cargo capacity) is not there until it is measured against the
  real game and recorded in `docs/reference/compat/DEVIATIONS.md`.
- Only whole stacks move, and only the active ship is handled; partial stacks, charges and other ships come
  with the slices that need them.
