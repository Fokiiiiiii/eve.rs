# CI Fix Handoff

## Current state

- The original failures on main were the New Eden Store catalog availability assertion and the live fitting walk. The Store test now provides the settlement callback required to report offers as purchasable; its purchase path remains untouched by the catalog read. The focused Release test passed on `eve_p4` with `EVE_MARKET_SEED=skip`.
- The fitting failure was `SessionRpcBudgetExceeded` during the inventory `List` after a module move. Tracing counted 132 weighted units for setup plus six inventory/Dogma re-reads against the old 120-unit default. The default is now 160, and `DispatchGoldenTests.The_fitting_inventory_rereads_fit_the_default_session_budget` protects that sequence.
- On `eve_p4`, the skip-free live fitting regression passed both race variants. The full fast live suite passed; its eight slow cases and one skill-training-speed case were skipped by suite conditions. The focused dispatcher suite passed 147/147. After merging, `Eve.Services.Core.Tests` passed 862/862 and `Eve.Services.Tests` passed 208/208.
- Runs 519, 520, and 522 also exposed an alliance RPC budget assertion using `allianceRegistry` for a route served by `corpRegistry`; commit `f2384b0f` corrected the test target. The pre-merge Release suite passed 205/205, and the post-merge count is 208/208 above.
- The pinned SDE build 3396210 is loaded in `eve_p4`; the local JSONL copy is `.cache/sde/eve-online-static-data-3396210-jsonl`.

## CI status at this handoff

- Run 529 passed all jobs, including the required CI gate, on `37b521d0`.
- Run 531 passed all jobs, including the required CI gate and Windows job, on `e3053512`.
- Run 533 for `7db4c9ed` had passed E2E, Rust, build, and all .NET shards; its Windows job was still running at the last status check.
- Run 539 for `d5b946b4` was active at the last status check. The checked-out `origin/main` tip is now `38decf14`; no workflow for that tip was listed in the latest `ci-status.py` output.

## Continue

Check the newest `origin/main` workflow with `python3 tools/dev/ci-status.py status`. If any job turns red, inspect its step tail and retrieve the full job log through GitHub REST without forwarding the Authorization header across the log redirect. Reproduce only on `eve_p4`, through `tools/dev/db-lock.sh`, and add a focused regression for each root fix.

## Commands

Use lane `eve_p4` only and run database work through `tools/dev/db-lock.sh`. The live port is 26103. For .NET, set `MSBUILDDISABLENODEREUSE=1` and pass `-p:UseSharedCompilation=false`.


## 2026-10-23 dependency-bump gate investigation

- CI run 608 on `c2ff6220` reported RustSec `RUSTSEC-2026-0385` for locked `ctutils 0.4.2`. The advisory was issued on 2026-10-23 and its patched range is `>=0.4.4`; the locked dependency is in the RustCrypto digest chain, not Tokio.
- The Python client job runs on Python 3.12.15. Its failure was `test_selection_routes_missing_appearance_through_avatar_save_before_game_entry`: the new live scenario called `connect()` without the `EVE_LIVE_HOST` guard used by the other live tests, so the standalone job attempted `127.0.0.1:26000` and received `ConnectionRefusedError`. The Windows log also showed successful installation of grpcio 1.84.1, protobuf 7.36.2, and pycryptodome 4.1.0 before pytest began.
- Added the same `EVE_LIVE_HOST` skip condition to the paperdoll scenario and added the file to the explicit live-test list in `tools/dev/run-eve-live.sh`, so the scenario runs only with the server stack.
- Run 608 also showed independent live and .NET failures; check the newest main gate after the dependency and Python-test fixes to determine which remain.


## Push 9f8612b0

- Pushed the paperdoll live-test guard and live-harness test-list update on main as `9f8612b0`.
- Push-triggered CI run 611 (`39963051154`) was in progress at this handoff update. Run 610 on the previous head `ab356e0f` had failed its Python client and Windows Python jobs; run 611 checks the guard and server-backed scenario.


## 2026-10-23 selected-agent SHA checkout

- Runs 60 and 61 failed in `actions/checkout` when the selected-agent dispatch supplied abbreviated commit IDs. The workflow passed that prefix directly to checkout, which tried to resolve it as a remote branch or tag.
- The workflow now fetches full history from the dispatch branch, resolves a requested commit prefix or branch locally, and checks out the resolved commit in detached mode.
- Focused regression `AgentTestRefResolutionTests.test_short_commit_and_branch_refs_check_out_as_detached_commits` passed locally.
- Push-triggered CI is still running; this entry will be updated with the run result after Actions completes.

## 2026-10-23 Python and Rust dependency fixes

- Run 611 on `9f8612b0` completed with the standalone Python client job successful after the live-only paperdoll test received its `EVE_LIVE_HOST` guard. Windows package installation had already shown grpcio 1.84.1, protobuf 7.36.2, and pycryptodome 4.1.0 install on Python 3.12.15; no generated-code or API incompatibility appeared.
- Run 608 identified active RustSec advisory `RUSTSEC-2026-0385` in `ctutils 0.4.2`, a transitive RustCrypto digest dependency. The official advisory fixes it in `ctutils >=0.4.4`; `Cargo.lock` now selects 0.4.4 with the official registry checksum.
- Local `cargo +1.94.1 fmt --all -- --check` and `CARGO_TARGET_DIR=target-cx CARGO_BUILD_JOBS=2 CARGO_INCREMENTAL=0 cargo +1.94.1 check --workspace --locked` passed. The required workspace Clippy check is in progress.
- Runs 611 and 614 also failed independent CI paths on their older heads: run 611 reported the cargo-capacity and paperdoll-state .NET assertions; run 614 failed Host startup because `CharacterServicesComposition.CharacterServiceList` found no entry. Current main includes follow-up changes in those areas. Check the newest main push gate before treating those paths as resolved.
- The newest observed push gate was run 614 on `690991ea`; its Rust advisory job passed before the fixed lockfile was pushed, while E2E failed at Host startup. A fresh push-triggered gate is required to verify the lockfile and current main together.
