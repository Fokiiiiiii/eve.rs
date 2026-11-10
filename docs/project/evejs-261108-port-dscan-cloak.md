# EveJS 261108.1: directional scan cloak visibility

## Decision

Port the verified cloak visibility rule into the existing Host and simulation boundary. `scanMgr.ConeScan` already omitted a target when the simulation marked its ship cloaked. The simulation tracks module cloak state, but the Host owns the 60-second stargate jump cloak and does not send that state to the simulation. As a result, a jump-cloaked ship could be returned as an ordinary D-scan target. The Host now takes one cloak-state snapshot per scan and omits Host-known cloaked ships from D-scan and probe-scan candidates.

This adds no database state, sim-link message, client packet field, or notification. D-scan rows retain the existing `{id, typeID, groupID}` shape.

## Evidence and behavior trace

### V24.01 client

- A scan request starts in `eve/client/script/parklife/directionalScanSvc.py:29-49`: it obtains `scanSvc.GetScanMan()`, calls `ConeScan(scanAngle, scanRange, *direction)`, and emits `OnDirectionalScanComplete` with the returned results.
- `eve/client/script/parklife/scanSvc.py:113-116` obtains the bound object from remote `scanMgr.GetSystemScanMgr()`.
- `eve/client/script/ui/inflight/scannerFiles/directionalScannerPalette.py:576-612` stores the server result and applies the active overview preset's group filter. It has no cloak-specific result filter. A full-tree search of `/home/user/eve-logic` for `ConeScan`, `GetSystemScanMgr`, `OnDirectionalScanComplete`, and `DirectionalScan` found this request/consumer path; a cloak search in these scanner files found no cloak handling.
- `eve/client/script/ui/inflight/scannerFiles/directionalScanHandler.py:150-152` handles scan completion for the map scan effect only.
- `eve/client/script/ui/crimewatch/crimewatchTimers.py:472-476` handles the jump-cloak start/end notifications. It does not change D-scan rows.

The client delegates which objects are found to the server and renders the server's rows after its overview-group filter. There is no client-side cloak fallback to compensate for an incorrect server result.

### CCP sources and live-game rule

The pinned SDE is build 3396210. It supplies type attributes used by the scan calculations, including `dscanImmune`; it does not specify the cloak-versus-scan rule. The official CCP searches available for this check did not return a direct D-scan/cloak rule. The secondary EVE University references say cloaked ships do not appear on D-scan and cannot be scanned by probes: [Directional Scanning](https://wiki.eveuniversity.org/Directional_Scanning) and [Probe scanning results](https://wiki.eveuniversity.org/Scanning:Scan_Results). Their [Cloaking guide](https://wiki.eveuniversity.org/Cloaking) describes gate cloak as temporary protection. These are secondary references, not a graphical-client or live-Tranquility observation.

### EveJS 261028.1 versus 261108.1

- In 261028.1, `server/src/services/exploration/scanMgrService.js:512-516` checks only the item identity before continuing to type/group checks; it accepts an otherwise eligible cloaked ship.
- In 261108.1, `server/src/services/exploration/scanMgrService.js:513-520` calls `isEntityCloaked(entity)` and rejects the candidate. The handler applies that predicate at `:2660-2663`; the predicate is exported for tests at `:3153`.
- `server/src/space/runtime/shipCloak.js:281-294` treats positive `isCloaked`, `cloakMode`, or `cloaked` values as cloak state. `:477-495` applies gate-jump cloak as restore mode 2 with a 60-second expiry, so the new D-scan check also hides a ship in gate cloak.
- Probe visibility already existed in both references: `server/src/services/exploration/probes/probeScanRuntime.js:518-535,622-633` excludes cloaked probe targets. It is unchanged between 261028.1 and 261108.1.
- I executed both versions' exported candidate functions with the same synthetic gate-cloaked ship. 261028.1 returned `dscanCandidate: true`; 261108.1 returned `false`. I also executed both versions' probe target listing against that entity; both returned zero targets. This exercises the cloak predicate and target listing directly, not the complete EveJS server or its client session flow.

### eve.rs before this port

- `crates/eve-space/src/scene/directional.rs:45-51` reports each ship's simulation cloak state in `SimDirectionalTarget.Cloaked`. `dotnet/src/Eve.Services.Space/Scanning/SpaceDirectionalScans.cs:131-138` already omitted `target.Cloaked`, warping ships, and combat recon ships. This covered module cloak state.
- `crates/eve-space/src/scene/scan_targets.rs:18-31` already omitted ships whose sim `CloakState` was not `Uncloaked`; the existing test at `:157-162` covers probe visibility for a module-cloaked ship.
- `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:468-523` creates the Host pilot's jump-cloak state on gate arrival and sends `OnJumpCloakUpdated`. `dotnet/src/Eve.Services.Space/SpaceSessions.cs:26,35-75,477-478` owns the expiry and recognizes jump and module cloaks. The sim's `Traveller` arrival path has no corresponding gate-cloak state, so its scan target can be reported as uncloaked during this Host protection window.
- The Host already routes the registered method through `dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:71-85` and wires `SpaceDirectionalScans` in `dotnet/src/Eve.Host/Composition/SpaceComposition.cs:698`.
- General module cloak state remains in Rust: `crates/eve-space/src/visibility.rs:33-67` distinguishes Uncloaked, Cloaking, Cloaked, and Decloaking; `crates/eve-space/src/modules/effects.rs:183-190,985-992` enters and leaves cloak state. The scan target's cloak bit stays true until Uncloaked, matching EveJS's positive cloak-mode test.
- The Host recognizes the `cloaking`, `cloakingWarpSafe`, and `cloakingPrototype` effects in `dotnet/src/Eve.Services.Space/Effects/SmartbombEffect.cs:56-103`; the covert-ops warp-safe distinction is passed to the sim from that effect path. Existing decloak handling in `crates/eve-space/src/scene/decloak.rs:8-17,77-119` covers proximity, stabilization, and observatory decloak events. This port does not alter cloak activation, warp eligibility, decloak timing, or notifications.

## What was ported

- `SpaceSessions.CloakedShipIds()` samples Host-owned pilot cloak state once using one FILETIME value. It reads the existing pilot state; it adds no duplicate cloak store.
- `SpaceDirectionalScans` rejects `KindShip` candidates in that snapshot in addition to the simulation's existing `Cloaked` flag.
- `SpaceProbeScans` rejects Host-known cloaked `KindShip` targets in addition to the simulation's existing module-cloak filter. This closes the same gate-cloak visibility gap for combat probe results. The D-scan release change itself is the only new EveJS delta; the probe filter is supported by EveJS behavior that predates this release and by the probe rule above.
- A regression test first reproduced the leak through the actual Host socket and bound `scanMgr.ConeScan` route. It now verifies no row while jump cloak is active, the row after expiry, and the wire row's `id`, `typeID`, and `groupID`. A DB-backed Host-service test verifies both scan paths and the probe result notification.

## Already present and not changed

- Module cloak state, cloak-capable covert-ops effects, warp-safe cloak handling, target visibility, proximity decloak, Mobile Observatory decloak, the D-scan geometry/type/range rules, and the `{id, typeID, groupID}` result shape were already implemented.
- The simulation already omits module-cloaked ships from probe targets and marks them cloaked for D-scan. The new Host snapshot supplements these existing checks for the separate gate-cloak state.
- The client call signature and result consumption were already correct; no client change or scan link protocol change was needed.

## Rejected

- I did not copy the EveJS entity collection and global cloak-mode architecture. The existing sim/Host ownership model is sufficient: the sim remains authoritative for module cloak and the Host remains authoritative for the temporary gate cloak.
- I did not include unrelated changes visible in the 261108.1 tree diff: `server/src/space/runtime/shipCloak.js:581-596` adds a doomsday exception to warp-safe-effect handling, and `server/src/space/runtime/scene/cloaking.js:256-259` adds an ESS module restriction. Neither is part of the D-scan release note, and this task found no V24 client evidence to validate either behavior.
- I did not change the existing fixed 15-minute module-cloak stabilization approximation. `crates/eve-space/src/scene/decloak.rs:13` documents the missing per-module stabilization input; correcting that is a separate gameplay change requiring its own client/reference evidence and tests.

## Verification and limits

- RED then GREEN: the new D-scan test failed before the code change with the gate-cloaked ship present, then passed after the Host snapshot filter.
- RED then GREEN: the Host socket test failed before the code change with a non-empty bound `ConeScan` result, then passed after the filter. It uses the production `CharacterServicesComposition.ScanMgrRpc()` route, a real `HostServerTests` front/socket, the SDE-backed static catalog, and a simulation test double.
- Release builds passed with warnings treated as errors and zero warnings for `Eve.Services.Space`, `Eve.Services.Space.Tests`, and `Eve.Host.Tests`.
- Database-backed `ScanProbeHostTests`: 30 passed, 0 skipped. Six gate-cloak/transition tests in `SpaceServiceTests`: 6 passed, 0 skipped. The production-route Host socket regression: 1 passed, 0 skipped. `NeedlejackActivationHostTests.Host_rejects_a_cloaked_activator_and_cloaked_linked_fleet_member`: 1 passed, 0 skipped. Existing scanMgr full-state and bound-method wire tests: 2 passed, 0 skipped. All database-backed tests used `eve_p3` with SDE 3396210.
- The service test uses `eve_p3` with SDE 3396210 and checks D-scan visibility before/after the 60-second expiry plus the probe result list. It does not write new persistent cloak state; no schema or persistence behavior changed.
- `git diff --check` passed. No Rust files changed, so no Rust build or test was needed.
- A headless test is not a graphical-client verification. No V24.01 graphical client, packet capture, or live Tranquility session was run. The full EveJS server was not started; only the relevant exported EveJS D-scan predicate and probe target listing were executed. That Node process also logged that its background KillmailWorker could not open its database file; those direct pure-function results are not a full-server test.
