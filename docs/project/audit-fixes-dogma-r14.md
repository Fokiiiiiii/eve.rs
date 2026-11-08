# Dogma audit fixes (R14)

## P2: system-category effects were omitted from attribute calculation

**Finding: confirmed and fixed.** `DogmaWorld.Runs` handled categories 0, 1, 4 and 5, then returned false for every other category. The SDE category was loaded into `EffectDef.Category`, so category 7 modifiers were silently discarded.

### Evidence

- The V24.01 client defines `dgmEffSystem = 7` and includes it in `dgmPassiveEffectCategories` (`/home/user/eve-logic/dogma/const.py:80-82`). Its type-effect loader includes system effects in the passive list (`/home/user/eve-logic/eve/common/script/dogma/baseDogmaStaticSvc.py:153-167`), and `StartPassiveEffects` routes category 7 through `StartSystemEffect` (`/home/user/eve-logic/eve/common/script/dogma/baseDogmaLocation.py:843-872`). `ModifierEffect` classifies those categories as passive and starts their modifier rows (`/home/user/eve-logic/dogma/effects/modifiereffect.py:9-10,18-21,27-29`).
- The pinned SDE 3396210 has concrete category 7 modifiers: type 3072, **Incursion Effect Assault**, carries effect 3996 (`systemArmorEmResistance`) with a 25% post-percent modifier to attribute 267 (`.cache/sde/eve-online-static-data-3396210-jsonl/types.jsonl:1751`, `typeDogma.jsonl:1459`, `dogmaEffects.jsonl:1098`). Rifter type 587 has base attribute 267 = 0.4 (`typeDogma.jsonl:200`).
- EveJS v0.12.9's `liveFittingState` uses `[0, 4]` for its fitted-module passive modifier helper (`/home/user/evejs-261028.1/server/src/services/fitting/liveFittingState.js:1118,1754-1763`). That helper is limited to fitted-module calculations and does not model the SDE system-effect item above; its omission is not evidence that category 7 should be ignored by the general Dogma engine. The V24.01 client and SDE determine this rule.

### Change and regression check

Added `EffectDef.System = 7` and made that category run regardless of `ItemState`. Category 6 (dungeon) remains outside this change and is not claimed to share the same rule.

`EngineTests.An_incursion_system_effect_applies_in_every_item_state` loads types 587 and 3072 from the SDE through PostgreSQL, verifies the Rifter baseline is 0.4, and verifies the effect changes attribute 267 to 0.5 while the effect item is offline, online, active and overloaded. Before the fix, the test failed with expected 0.5 and actual 0.4. After the fix it passed on database `eve_p3`.

### Scope and remaining verification

`Eve.Dogma` is a pure calculation library (ADR 0004); no wire shape, persistence, notification or Host registration changed. `DogmaInfo` serializes values from `ShipSnapshot.World` into `GetAllInfo` (`dotnet/src/Eve.Services.Dogma/DogmaInfo.cs:59-69,267-292`). However, `GetAllInfo` currently supplies an empty `systemWideEffectsOnShip` value (`DogmaInfo.cs:282`), and the client only scatters that event when the value is non-empty (`/home/user/eve-logic/eve/client/script/environment/godma.py:2448-2449`). This change verifies the Dogma engine when a system-effect item is present in its world; it does not verify provisioning of incursion effects through the graphical client.

The EveJS source path was inspected, but no matching executable EveJS system-effect flow was found. The graphical V24.01 client was not run. No finding in the audit report was determined false; category 6 behavior remains unverified and unchanged.
