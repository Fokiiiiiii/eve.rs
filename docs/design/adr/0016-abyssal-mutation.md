# ADR 0016: Abyssal mutated modules

- Status: accepted
- Date: 2026-10-08
- Decided by: the lead, on the research in the abyssal mutation spec (read-only; no build or test was run for this record)

## Context

The client's crafting window creates a mutated item through `dynamicItemService.CreateDynamicItem(mutatorItemID, sourceItemID)`,
which returns the new item id, then reads `GetMutatedAttributes(itemID)` and shows the result [X: eve-logic `craftingWindow.py:232-256`,
`dynamicItemSvc.py:33-44`]. The client does not roll anything. It needs every attribute id of the mutator, or it raises `KeyError`
[X: `dynamicItemSvc.py:33-37`].

eve.rs does not do this yet. `DynamicItemRpcService` has no create callback, so `CreateDynamicItem` answers `PyNone`
(`dotnet/src/Eve.Host/DynamicItemRpcService.cs:30-51`; wired at `Program.cs:2474`). `GetDynamicItemInfoAsync` fabricates a
record for any item that has none (`DynamicItemRpcService.cs:65-83`), and its attributes are the source type's dogma plus that record,
with no result-type layer [R]. Two tests enshrine the no-op (`CompatibilityRpcServiceTests.cs:75`, `DynamicItemRpcServiceTests.cs:13-96`).

Facts from the static data (SDE 3396210) that the design depends on [SDE]:

- Group 1964 "Mutaplasmids" holds **413** published types, one `dynamicItemAttributes` row each (not 66 as first assumed).
- The 413 map to **89** output types (metaGroup 15, "Abyssal"). There are **5,665** applicable (mutaplasmid, input) pairs over
  **1,021** distinct inputs. Every input maps to exactly one output, and no output is also an input.
- **76** distinct inputs (438 pairs, 134 mutaplasmids) are unpublished. The client filters them out (`loader.py:29-36`), so the
  server must too.
- Attribute ranges are multipliers of the **source** base value. Across 2,124 rows and 50 attribute ids, **6 rows have min > max**
  (attribute 2346 in mutaplasmids 56299-56301 and 85669-85671). A range is therefore an interval, not "min to max".
- Output types are not a reliable base. 87 of 89 outputs lack cpu (50) and power (30). Drone outputs carry placeholder values
  (`60478`: 9 = 100, where the source Acolyte I has 170). Volume differs on 186 of 1,021 pairs (12052 to 47408: 25 against 10).
- Worked example, mutaplasmid 47297 on 12052 (the spec's section 1 table): the mutated attributes come from the source's base
  values, and skill requirements from the output 47408.

Rules [C, W]: the mutaplasmid and the input module are both consumed and a new Abyssal module is created; each mutaplasmid works on
one module group; the process is irreversible; Abyssal modules are not sellable on the market; the tooltip shows the mutaplasmid and
the input used (CCP patch notes 1.0 and 1.5). The wiki (secondary) adds that skill requirements are those of the T2 module and that
each stat is rolled flat over its band. CCP is silent on the distribution and on the exact server gates.

EveJS v0.12.9 is the working functional reference. It takes the base from the source type (`dynamicItemService.js:328, 335`),
takes skill requirements from the source (`liveFittingState.js:1620-1643`), consumes and grants in separate, non-atomic steps
(`dynamicItemService.js:456-531`), fabricates metadata for result-type items (`dynamicItemService.js:248-267`), and keeps its data in
`customInfo` (`dynamicItemService.js:187-246`). `AGENTS.md` asks us to keep what is verified and not to inherit those flaws.

## Decision

**D1. Effective attributes.** A mutated item's effective attribute map is the **source** type's attributes, overlaid by the
**output** type's attributes **only for skill requirements** (`requiredSkill1-5` and their levels: 182-184, 277-279, 1285-1289).
Each mutated attribute's value is the **source base value times the roll**: `base(source) x (min + roll x (max - min))`. Output
values for any other attribute are not used. Output-only identity values (techLevel 422, metaLevelOld 633, metaGroupID 1692,
canFitShip 1298-1299, drone bonus 1255) are not taken from the output in this decision. Their treatment is still open (see
Consequences).

**D2. Skill requirements come from the output type.** This is the T2 layer. The wiki says a mutated module needs the skills of
the T2 module regardless of the base module [W]. The SDE agrees (the output carries the skill rows that the source lacks on 352
pairs). Fitting already reads the module's type (`FittingRules.cs:69-73`, `CharacterShips.cs:521`), so this is the output type.
EveJS, which takes the requirement from the source, is a deliberate difference (deviation 121).

**D3. Volume.** A singleton result takes the **output type's** SDE volume, expressed through `ItemVolumes.Unit`
(`Eve.Inventory/ItemVolumes.cs`). For 12052 to 47408 this is 10 instead of the source's 25. This is an inference: CCP is silent, and
no source confirms it [U].

**D4. Consumption.** One unit of the mutaplasmid stack is consumed (the stack is reduced by one, or deleted at quantity one), and the
source module is consumed. The source must be a singleton. This follows the existing consume rule in `SkillHandlerService.cs:894-905`.
EveJS consumes one unit too (`dynamicItemService.js:38, 456-463`).

**D5. Source eligibility.** A **fitted** source is refused. A source must also be in a hangar or the cargo hold of the character
(the flags the client's gate and station inventory use). A **damaged** source is allowed: neither the client gate nor EveJS checks
damage, and no recorded rule does. Owner, distinct ids, quantity and the published mapping are checked as in the refusals below.

**D6. Stored roll.** The roll is stored as an exact double in `inventory.item_mutations.rolls`, and the computed `values` are stored
too. The mutation layer applies them **without extra rounding**. The existing `DogmaWorld` rounding of attributes 30 and 50 to two
decimal places stays where it is (`DogmaWorld.cs:385-387`). A stored item is **never re-rolled and never recomputed on load**. An SDE
change cannot move an existing item. EveJS rounds to six decimal places (`dynamicItemService.js:355-357`); we do not adopt that as a
rule. The wire rounding is not specified by CCP [U].

**D7. Result location and flag.** The result takes the **source's** location and flag. The client's shown location is therefore the
one the source had. The source is consumed and the result created in the same transaction. CCP is silent [U].

**D8. Roll distribution.** The roll is **uniform within [min, max] taken as an interval**. This matches the wiki [W]; CCP is
silent. Where min > max (6 rows) the result is still the interval, so the endpoints are r = 0 giving `min`-side values and r = 1
giving the other end. The roll is an **injectable input**: production draws it once per attribute (crypto-uniform [0,1), clamped),
and tests pass explicit rolls. A roll that is missing is an error, not a default.

**D9. Provenance.** The item record stores the mutator type, the mutator item id, the source type and the source item id, with the
result. CCP shows the mutaplasmid and the input in the tooltip (patch 1.5 [C]), so this is player-visible. It lives in the
`inventory.item_mutations` table (`0005_item_mutation`; `0004` is already the item `custom_info` migration), not in `custom_info`.

**Other decisions recorded with D1-D9.**

- *Placement.* The authority `ItemMutationService` lives in the domain project `Eve.Inventory` (`Mutation/`), not in
  `Eve.Services.Inventory`: `dotnet/src/AGENTS.md` reserves `Eve.Services*` for RPC handlers, wire conversion and notifications.
  `Eve.Inventory` references `Eve.Dogma` (pure, ADR 0004) only: the architecture test (ADR 0003) keeps domain modules off the
  Event Bus. The authority publishes through `IItemMutationEvents`, which the Host implements over the bus (`BusItemMutationEvents`).
- *Transaction.* Consume of the mutaplasmid and the source, creation of the result and the store of its record happen in **one
  PostgreSQL transaction**. Both inputs are locked in ascending id order (`LockAndGetAsync`). Any failure before commit leaves both
  inputs, no record and no event.
- *Event.* The event `item.mutated` is written with the shared Event Bus helper, `EventBus.PublishAsync(connection, transaction, topic,
  payload, key)`, inside the same transaction, so the outbox row exists exactly when the change does (FOUNDATIONS rule 1). The
  event is **delivered** after the commit, because the pump does not deliver past an open publisher (FOUNDATIONS rule 4); `bus.Wake()`
  follows the commit. No new C# event is added. Payload: item id, result type, mutator type, source type, character id; key `char:<id>`.
- *Refusals.* Refusals are proper `UserError`s (`CustomNotify` with a `notify` string, text parity with `dynamicItemService.js:393-416`),
  never `PyNone`. `PyNone` is answered only by `GetDynamicItemInfo` for an unknown item (an info query, not a fabricated record).
- *Explicit refusals that stay.* Reprocess of mutated items (out of scope; the wiki says they reprocess to T1 minerals). Market listing
  of mutated items (not sellable [C]). Re-mutation (an output is never an input, so the mapping refuses it). Unpublished inputs.
  Fitted sources (D5). Any mutated attribute that the source lacks (EveJS silently drops it, which later gives the client a `KeyError`).
- *Wire.* **No sim wire change.** The simulation receives numbers that the C# handlers package (`cycle_ms`, `capacitor_need`,
  attribute values). The sim wire epoch stays at 20 (`SimCodec.cs:30`, `crates/eve-space/src/link.rs:17`). `GetDynamicItemInfo` keeps
  its keys (itemID, typeID, sourceTypeID, sourceItemID, mutatorTypeID, mutatorItemID, characterID, createdAtMs, attributes,
  modifiers, baseAttributes). `attributes` is the full effective map that the server's dogma uses, so the client and the server agree.
  `modifiers` carries the mutator's attribute ids (the client reads only its keys, `tooltip/dynamicItem.py:47`).

## Web evidence summary

Sources were read directly. CCP and the EVE University wiki are used as named in the spec's section 2.

| Source | Status | Used for |
|---|---|---|
| CCP patch notes for Into the Abyss (`eveonline.com/news/view/patch-notes-for-eve-online-into-the-abyss`, CCP Falcon, 2018-05-25) | 200 | Consume both inputs; irreversible; tiers; one group per mutaplasmid; not sellable on market, tradable in contracts |
| CCP forum, "Mutaplasmids are now live on Singularity" (`forums.eveonline.com/t/.../72282`, CCP_Claymore, 2018-04-26) | 200 | Test-server launch; variants; mutaplasmids drop in Abyssal Deadspace |
| EVE University wiki, "Abyssal modules" (`wiki.eveuniversity.org/Mutaplasmids`) | 200, undated | Flat distribution; T2 skill requirements; reprocess to T1 minerals; contract tooltips |
| CCP support article on mutaplasmids (linked from `docs/project/README.md`) | HTTP 403 | Not used; not worked around |
| CCP developer static-data guide | HTTP 404 on the guessed path | Not used; the schema comes from the SDE rows |

Statements the sources do not settle are marked [U] in the spec and in this ADR: the output volume (D3), the source gates
(D5), result location and flag (D7), the roll distribution (D8, wiki only), the displayed rounding (D6) and the server's
handling of damage.

## Consequences

* Mutated items become real: the crafting window creates an item, the record persists, and `GetDynamicItemInfo` answers from
  stored values, never from a fabricated record. The fabricated metadata in `DynamicItemRpcService.cs:65-83` and the customInfo
  read path (`:59-83`) are removed.
* One factory builds the effective dogma of a module or a drone from its row, used by all six creation sites. `DogmaWorld` gains a
  base type (the source) and an output layer for skill requirements. `Eve.Dogma` stays a pure library (ADR 0004).
* The shared foundations are used as FOUNDATIONS prescribes: one transaction, the bus for the event, no new timer, no new event list.
  A publishing transaction stays short.
* **Open (not decided here):** the output-only identity values (D1 excludes them): techLevel 422, metaLevelOld 633, metaGroupID 1692,
  canFitShip 1298-1303, drone bonus 1255 and 2335-2338. Each needs a decision and a test before the mutated module is called done.
  Drone bases come from the source (Acolyte I 9 = 170, 263 = 40, 265 = 190), never from the output placeholders.
* **Unverified:** the crafting-window flow and animation, the tooltip, the client's reaction to `OnItemChange`, contract tooltips,
  the displayed rounding, the damaged-source and fitted-source gates as CCP runs them, and the output volume. The live harness
  (`tests/test_mutation.py`) is unverified until it is run with the graphical client. Skipped PostgreSQL tests are not passed tests.
* Reprocess, market listing and re-mutation stay refused. Adding any of them later needs a new ADR.
