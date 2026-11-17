# POS fuel, force field, and industry arrays (2026-11-17)

## Scope and source trace

The V24.01 checkout at `/home/user/eve-logic` was searched across the client before editing. POS fuel/module state is read through the tower UI in `eve/client/script/ui/inflight/moonmining.py`; module onlining uses `eve/client/script/ui/services/menusvc.py:2493-2512` and `eve/client/script/parklife/playerOwned.py:328-332`. Force-field admission uses `eve/client/script/parklife/playerOwned.py:91-115,173-191`, and the settings are saved from `eve/client/script/ui/inflight/moonmining.py:1195-1219`. OBO targeting is initiated by `eve/client/script/parklife/playerOwnedTargetMgr.py:84-102`. The industry client calls facility discovery/detail/locations from `eve/client/script/industry/facilitySvc.py:42-72,114-120` and job installation/status from `eve/client/script/industry/industrySvc.py:92-110,139-160`.

The broad `rg` checks across `/home/user/eve-logic/eve` found the `forceFieldID` comparison only in `playerOwned.py:106-109`, the OBO calls only in `playerOwnedTargetMgr.py:87,102`, and the bunker lookup only in `factionWarfareSystemInfoContainer.py:54`. Industry tax reads/writes are consumed by `facilitySvc.py:90-94` and `facilityTaxWindow.py:30,73`. The requested `docs/project/coverage-sweep-1.md` is not present in this checkout; the five POS partial rows were cross-checked in `docs/project/service-audit/EVE_RS_COVERAGE.csv` and the supplied POS audit documents.

The comparison source is the pinned EveJS tree at `/home/user/evejs-261108.1`. Relevant behavior is in `server/src/services/ship/controlTowerRuntime.js:935-1032,1488-1554` (field create/reconcile and online transitions), `server/src/services/starbase/starbaseFuelRuntime.js:410-507` (fuel-failure shutdown), `server/src/services/starbase/starbaseAccessRuntime.js:162-170,216-263` (harmonic and admission), `server/src/services/starbase/starbaseControlRuntime.js:210-225` (OBO delegation), and `server/src/services/industry/starbaseIndustryRuntime.js:19-104` (POS industry profiles and association). EveJS is used for observable behavior; its state ownership and implementation are not copied.

## Verified changes

### Fuel failure and module state

An unpaid tower fuel period now writes the tower offline, writes associated online/onlining modules offline in the same PostgreSQL transaction, and cancels pending module-online jobs. After the transaction commits, the Host reads persisted tower/module state and updates the live simulator slim items (`posState` and `posTimestamp`). The same module shutdown is applied when a tower is explicitly taken offline. The change is in [`SpaceContainers.ControlTowers.cs`](../../dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:98).

The database-backed regression in [`ControlTowerHostTests.cs`](../../dotnet/tests/Eve.Services.Space.Tests/ControlTowerHostTests.cs:916) onboards two modules, onlines them, triggers an empty-fuel period, and checks both persisted state and simulator slim state after the scheduler transaction. This verifies Host/database/simulator state; it does not establish graphical-client rendering or the complete fuel-bay UI.

### POS assembly-array industry

The Host now resolves anchored, online POS assembly arrays and laboratories associated with a live control tower. The supported installation/activity/filter data is checked in at [`starbase-assembly-profiles.json`](../../dotnet/src/Eve.Host/Data/starbase-assembly-profiles.json) for V24 client build 3396210; the resource hashes and selection basis are recorded in the JSON. The Host registers deployed modules in facility discovery and implements detail, owner-bound facility taxes, corporation-hangar locations, and job install/read behavior. Jobs preserve the tower association internally, disappear from the public payload where EveJS omits it, and report paused while the tower/facility is offline. Material/output custody remains the existing corporation inventory authority.

The registered Host/PostgreSQL regressions are in [`IndustryFacilityHostFlowTests.cs`](../../dotnet/tests/Eve.Host.Tests/IndustryFacilityHostFlowTests.cs:72): facility listing/detail/locations and exact public fields; at line 137 tax authorization, notification and persistence across Host restart; and at line 206 a real corporation-hangar job install, material consumption, persisted tower association, offline pause and online completion. Tax changes require Station Manager access and notify `OnFacilitiesUpdated` after commit. EveJS `industryPayloads.js:113-184` omits internal `inventoryLocationID` and `starbaseTowerID`; the tests assert those fields do not leak.

Only the profiles in the checked-in data file are supported. No graphical V24 industry flow was run.

Capital profiles receive the V24 type-dogma security check before the Host reports them online. Profiles whose dogma requires a sovereignty level or infrastructure-hub upgrade remain listed offline: Host does not yet calculate authoritative hub-upgrade power/fuel state (the same unresolved budget recorded in `TODO.md` ST-17 c). `Capital_POS_assembly_array_obeys_its_V24_system_security_limit` covers the registered Host response for type 24656 in Jita. This is intentionally fail-closed; it is not evidence that the capital arrays can complete jobs in a qualifying system.

## Open items

- **POS force field and password admission:** the Host still does not create a force-field item/ball when a tower comes online, remove it on fuel failure/offline, publish `forceFieldID`, update its Destiny harmonic, or check corp/alliance/ship-password admission at a movement boundary. `SetTowerPassword` persistence alone is not the feature. The existing command-burst check uses the SDE shield radius and tower online state for that module's eligibility; it does not supply the force-field ball or crossing behavior.
- **Starbase OBO target calls:** `AddTargetOBO` and `RemoveTargetOBO` remain unimplemented. V24 invokes them after taking control of a battery; EveJS validates the session-local battery grant and delegates target locks to the battery's scene actor. eve.rs has neither that POS battery control grant/actor nor Rust scene targeting state for a battery. Registering these two RPC names without those owners would not implement the client flow.
- **Sentry targeting:** sentry settings persist and round-trip, but no associated battery targeting/defense controller consumes the policy.
- **Other POS inventory/use consumers:** silo `viewput`/`viewputtake` is enforced. Ship-maintenance, reprocessing/compression arrays and jump-bridge `use` behavior remain incomplete.
- **Moon/reaction production:** module onlining/resource accounting and silo-to-silo transfer are implemented, but historical moon output is not configured in the pinned EveJS legacy authority, and the SDE contains no historical moon abundance. The server does not fabricate output.
- **Control Bunker:** `GetControlBunkerIDInSystem` remains `None`. The client at `eve/client/script/ui/shared/agencyNew/ui/contentPageInfoConts/factionWarfareSystemInfoContainer.py:49-60` requires a live inventory item and ball. The pinned SDE has bunker types but no per-system live item IDs/positions, and the matching EveJS tree has no live bunker placement authority. Do not synthesize an ID or position.

These gaps remain `PARTIAL-TESTED` or open in [`EVE_RS_COVERAGE.csv`](service-audit/EVE_RS_COVERAGE.csv) and [`DEVIATIONS.md`](../reference/compat/DEVIATIONS.md). The control-bunker evidence is in [`DEEPCHECK_facWarMgr.md`](service-audit/DEEPCHECK_facWarMgr.md); POS client/EveJS row traces are in [`posmgr-partial-audit-2026-11-13.md`](posmgr-partial-audit-2026-11-13.md).

## Verification limits

Registered Host/PostgreSQL coverage and simulator slim-state assertions are headless checks. A graphical V24 client was not launched, and no complete same-scenario EveJS POS run was executed. The exact local pass counts and build results follow.

## Verification (2026-11-17)

Verified:

- `IndustryFacilityHostFlowTests`: 17 passed, 0 failed, 0 skipped on PostgreSQL `eve_p4`.
- Related Host industry classes (`IndustryJobPersistenceTests`, `IndustryJobsHostWireTests`, `IndustryHostRpcTests`, `IndustryFacilityManagerTests`, `IndustryJobNotificationWireTests`, `IndustryActivityPersistenceTests`, `IndustryCorporationPersistenceTests`): 29 passed, 0 failed, 0 skipped on `eve_p4`.
- `IndustryJobsServiceTests` and `PosMgrServiceTests`: 23 passed, 0 failed, 0 skipped.
- `StructureCompositionTests` and `StructurePosHostWireTests`: 21 passed, 0 failed, 0 skipped on `eve_p4`.
- `CommandBurstHostTests` and `SpaceContainerTests`: 51 passed, 0 failed, 0 skipped on `eve_p4`.
- `ControlTowerHostTests`: 10 passed, 0 failed, 0 skipped on `eve_p4`.
- Release builds with warnings as errors for `Eve.Host`, `Eve.Services.Core`, `Eve.Services.Space`, and their three test projects: 0 warnings, 0 errors.
- `cargo fmt --all --check`, `cargo check --workspace`, and `cargo clippy --workspace --all-targets -- -D warnings` passed. No Rust source changed.
- The profile JSON content matches the pinned EveJS profile file; the checked-in Git blob uses LF line endings while the source file uses CRLF. `git diff --check` passed.

Not checked:

- Graphical V24 acceptance, a POS-specific headless-client walk, and a complete paired EveJS/eve.rs POS run.
- Force-field ball lifecycle/admission, OBO targeting, sentry control, bunker placement, and hub-upgrade power/fuel behavior; these remain open as listed above.
- Repository-wide CI for the uncommitted worktree.
