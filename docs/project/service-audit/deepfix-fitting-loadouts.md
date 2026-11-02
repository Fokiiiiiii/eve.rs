# Fitting / loadout deep correctness handoff

## Scope checked

- V24.01 fitting manager path: `fittingSvc.py:268-313` persists one fitting; `:322-354` sends bulk saves and maps temporary IDs; `:356-386` validates ship and each (typeID, flag, quantity); `:388-430` reads the character/corp/community/alliance sets; `:433-448` deletes; `:554-629` loads onto the active ship and reports missing inventory; `:1053-1109` consumes add/update/delete notifications. Callers include `fittingMgmtWindow.py:300`, `fittingBrowser.py:513`, `hullAndHardwareTypeBrowser.py:433-441`, and import/export paths in `export.py:503,1049`.
- Ammo client path: V24.01 `dogma/ammoloadingUtils.py:32-35` computes charge rounds as `int(capacity / volume + 1e-07)`; `clientDogmaLocation.py:1010-1060` applies the result while loading weapons and skips nonpositive counts. The wider callers include normal and ghost fitting ammo operations and the drop handler.
- EveJS 261028.1 uses the same epsilon rule at `server/src/services/fitting/chargeCompatibilityPolicy.js:30-58` and applies it in compatibility checks at `:88-164`.
- EVE University Weapons guide describes ammo as weapon-consumed and relevant to weapon effectiveness: https://wiki.eveuniversity.org/Weapons . Pinned SDE in `eve_p6` is build 3396210; the loaded SDE includes an affected compatible torpedo launcher/charge pair (type IDs 13924 / 2811, capacity 2.3 / volume 0.05).

## Defect fixed

Binary floating point can make a whole-number capacity ratio slightly smaller (for example 0.6 / 0.05 is represented below 12). `AmmoRules.Capacity` used floor, so it gave 11 while V24.01 and EveJS give 12. Changed it to truncate after the client's epsilon. The regression first failed (11 instead of 12), then passed. A Space Host/sim test exercises real ammo loading and firing, with only the statics values adjusted to make the boundary deterministic.

Code: `dotnet/src/Eve.Fitting/AmmoRules.cs:46-49`; unit regression `dotnet/tests/Eve.Fitting.Tests/RulesTests.cs:72-80`; Host/sim regression `dotnet/tests/Eve.Services.Space.Tests/AmmoHostTests.cs:60-88`.

## Other fitting service checks

Host fitting manager is `dotnet/src/Eve.Services.Core/FittingMgrServices.cs`: list/save/update/bulk/delete/rename are implemented through the PostgreSQL saved-fitting store; corporation invalidation precedes its change notification. Character/corporation/alliance limits match V24.01 constants (500/600/600). The wire-facing notification payloads align with the client handlers in `fittingSvc.py:1053-1109`. Existing crate persistence test covers saved-fitting serialization through the crate flow (`dotnet/tests/Eve.Host.Tests/CrateServicePersistenceTests.cs:93-208`).

EveJS normalizes incoming short fitData rows to zero-filled triples and rejects invalid type IDs (`server/src/_secondary/fitting/fittingStore.js:101-118,676-755`). Host `FittingMgrCalls.CheckedDraftAsync` currently drops rows shorter than three values in `FittingMgrServices.cs:250-290`. The normal client validates/unpacks three values at `fittingSvc.py:356-386`, so the malformed row is outside the ordinary UI path; direct hostile RPC parity remains a follow-up candidate and was not changed in this patch.

## Verification

Passed locally on `eve_p6`: focused fitting rules regression 1/1; focused ammo Host/sim regression 1/1; `AmmoHostTests|MissileHostTests` 20/20; `AmmoTests|AmmoRaceTests|ReloadTests` 17/17; Dogma `ReloadServiceTests` 7/7. Changed test projects built with zero warnings/errors. Tests ran through `tools/dev/db-lock.sh`; no skips.

The SDE `ensure` script was not usable against this preloaded foreign-table SDE because index creation is unsupported for foreign tables; the pinned data itself was present and used by the passing host tests. No graphical V24.01 client session was run. Full save/update/delete/bulk/load wire flows have been traced but were not replayed end-to-end in this audit.
