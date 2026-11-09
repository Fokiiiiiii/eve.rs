# Crate simulation audit r15 revalidation

Reviewed 2026-11-09 against the code at `811f23803`; the latest `origin/main` was subsequently fast-forwarded at `704baabe6` (documentation-only). The original fix record is already present in `docs/project/audit-fixes-crate-sim.md`; this note records an independent source and test recheck.

## Confirmed findings

### P1 — A single out-of-range `CmdDock` now completes when the ship arrives

The audit described a real defect before commit `e4009bbbd`: the prior `Scene::poll` only cleared `docking_approach`, and its scene test required the client to send another `CmdDock` (`git show e4009bbbd^:crates/eve-space/src/scene/session.rs`, `:crates/eve-space/src/scene/tests.rs`).

The V24.01 client sends one `CmdDock` from `RealDock` (`/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:503-518`). A search of the client tree found no retry from the `OnDockingAccepted` consumers; they update the movement indicator and docking notification (`eve/client/script/parklife/spaceMgr.py:511-518`, `eve/client/script/ui/view/dockingHeroNotificationService.py:23-35`). EveJS 261028.1 records that out-of-range station approaches are accepted after arrival, including seven of nine observations without a second client command (`/home/user/evejs-261028.1/server/src/space/runtime/scene/pilotCommands.js:720-808`; `server/src/space/runtime/spaceRuntime/docking.js:146-217`).

The fix is present in `crates/eve-space/src/scene/session.rs:755-778`: arrival rechecks current simulation restrictions, clears the pending approach, and calls the regular docking acceptance path when allowed. A later movement command cancels the approach. The existing Rust trajectory follows to docking range; the code record documents the difference from EveJS's station-centre `GotoPoint` and tests the resulting arrival.

Regression coverage is present at `crates/eve-space/src/scene/tests.rs:1517-1597` and `crates/eve-sim/tests/link.rs:287-349`. It checks successful automatic acceptance and the following `DockingReady` / Host decision / `Docked` sequence, plus cancellation and a restriction that appears during approach. The initial `Approaching` completes the pending command-4 request in `dotnet/src/Eve.Space/SimClient.cs:382-386`; arrival therefore sends `OnDockingAccepted` and its stop update without a second command reply.

### P3 — A second sim-link `Hello` is rejected

The audit's protocol finding was also true before commit `ec92411c9`: the ready connection dispatched later `Hello` frames, and generic dispatch acknowledged them with the server version regardless of the supplied version (`git show ec92411c9^:crates/eve-sim/src/lib.rs`, `:crates/eve-space/src/sim/dispatch.rs`).

The current `serve_connection` rejects any `Hello` after the initial handshake before normal dispatch (`crates/eve-sim/src/lib.rs:71-83`). The link regression completes a valid handshake, sends a mismatched second version, and asserts connection closure without `HelloAck` (`crates/eve-sim/tests/link.rs:384-397`). This follows ADR 0007's versioned first-message handshake (`docs/design/adr/0007-space-simulation-process.md:40-47`).

## Checked and not classified as a defect

No Rust-side persistence defect was found. ADR 0007 assigns location and database ownership to the Host (`docs/design/adr/0007-space-simulation-process.md:35-42`). `SpaceSessions` routes `SimDocked` into `FinishDockingAsync` (`dotnet/src/Eve.Services.Space/SpaceSessions.cs:1118-1121`), which calls `_ships.DockAsync`, updates the client session, and sends `OnItemsChanged` followed by `OnDockingFinished` (`dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:609-660`). Host regressions cover registered `CmdDock`, the `DockingApproach` error wire, and persisted docked ship/session state (`dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:3716-3803`).

## Verification

- `cargo fmt --all -- --check` — passed.
- `cargo check --release -p eve-space -p eve-sim` — passed.
- `cargo clippy --release -p eve-space -p eve-sim --all-targets -- -D warnings` — passed.
- `cargo clippy --workspace --all-targets -- -D warnings` — passed.
- `cargo test --release -p eve-space --lib a_dock_request_from_out_of_range_is_accepted_on_arrival_without_a_second_command` — 1 passed.
- `cargo test --release -p eve-space --lib station_dock_approach` — 2 passed.
- `cargo test --release -p eve-sim --test link` — 8 passed, including the approach and repeated-Hello regressions.
- `/root/.dotnet/dotnet build dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj --configuration Release --no-restore -warnaserror` — succeeded with 0 warnings and 0 errors.
- The four relevant `SpaceServiceTests` passed with `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p6`; 0 skipped. `eve_p6` had the SDE loaded.
- Main CI run 1660 for `811f23803` completed successfully. The earlier run 1659 failure was traced to Clippy's `chunks_exact_to_as_chunks` in `eve-marshal` and `nonminimal_bool` in `eve-oracle`; `811f23803` contains the fixes, and the current workspace Clippy check passes.

## Not verified

The EveJS path was source-traced, not executed; its repository `package.json` has no test scripts. No graphical V24.01 client session was run. Rust sim-link and database-backed Host tests are headless checks and do not establish a real GUI session result.
