# Crate Destiny audit r15 fixes

## Verified findings

### P2: owned fighters were marked interactive

Confirmed. `Ship::ball()` maps a fighter companion to `Kind::Fighter` and marks its controller as positive (`crates/eve-space/src/ship.rs:263-323`). The old Destiny branch used the drone owner/controller rule for both drones and fighters. The current EveJS encoder explicitly returns false for fighters while keeping the identity rule for drones (`/home/user/evejs-261028.1/server/src/space/destiny/stream/ballEncoding.js:196-209`). V24.01 reads `ball.isInteractive` when it decides whether Assist and Guard may target a fleet ship (`/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/droneFunctions.py:101-141`); other whole-client references include `svc_gridutils.py:165`, `svc_destroyableitems.py:385,435`, `insider.py:195`, and `menucheckers/celestialCheckers.py:1863`.

The encoder now keeps the force/configured-flag overrides, then returns non-interactive for fighters and applies owner/controller identity only to drones (`crates/eve-destiny/src/ball.rs:549-574`). The end-to-end regression launches a fighter through `Scene`, finds its `DoDestinyUpdate(AddBalls2)` record, and asserts that the encoded fighter flag is clear (`crates/eve-space/src/fighters/tests.rs:47-83`). Reverting the old combined branch made that test fail with `IS_INTERACTIVE` set. The oracle regression also checks that drones remain interactive and explicit force/configured overrides remain effective (`crates/eve-oracle/tests/destiny_golden.rs:98-135`).

### P2: Destiny ball fixtures captured an older EveJS encoder

Confirmed. The original provenance named source commit `70ea5d807a328fa6d00bf07c2fb6a24ddea5e816`, while the supplied reference archive is version `261028.1`. Running its exported `encodeEntityBall` over all 1,827 `ball.encode` inputs found 108 stale expected outputs/errors. The examples in the audit were reproduced: the fighter flag changed, ORBIT now prefers `orbitDistance`, and WARP records use root fields (`targetPoint`, `effectStamp`, `warpDistance`, `warpMinRange`, `warpFactor`). Current source evidence is `/home/user/evejs-261028.1/server/src/space/destiny/stream/ballEncoding.js:196-209,396-405,533-547`.

The fixture refresh is reproducible with `node tools/dev/refresh-destiny-fixtures.js <evejs-root> [output-path]`. It updates only the `ball.encode` group. `destiny_adapter` now maps ORBIT range and WARP fields using the same mode-specific/root-field rules (`crates/eve-oracle/src/destiny_adapter.rs:178-192,365-402`). All 4,758 Destiny fixture cases pass after the refresh. `oracle/PROVENANCE.md` records the old source for the unchanged groups, the archive package version, and source hashes. The archive has no `.git` metadata, so a commit hash for version `261028.1` could not be established.

V24.01 passes the range supplied to Orbit through `CmdOrbit` and displays the ball's `followRange` for ORBIT (`/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:236-263`, `parklife/spaceMgr.py:692-710`). Warp UI reads the ball's `effectStamp` for preparation progress and active-warp transitions (`parklife/spaceMgr.py:627-650`, `environment/effects/Warp.py:455-465`). The headless fixture tests validate the serialized record; no graphical client was launched.

### P3: oversized mini-geometry arrays wrote inconsistent counts and payloads

Confirmed. A `usize as u16` count could wrap to zero while the encoder still wrote all elements. EveJS truncates each mini-geometry array to `0xffff` before writing its count and elements (`/home/user/evejs-261028.1/server/src/space/destiny/stream/staticBallTail.js:49-63,171-198`). Rust now caps each slice before writing both count and payload (`crates/eve-destiny/src/ball.rs:450-452,840-872`). The regression encodes mini-balls, mini-capsules, and mini-boxes at 65,535 and 65,536 entries and checks each count and record length (`crates/eve-oracle/tests/destiny_golden.rs:176-198`). The V24.01 behavior tree reads mini-ball records when finding the closest surface (`/home/user/eve-logic/behaviors/actions/ballparks.py:712-724`), so a wrapped count would leave the client parsing subsequent geometry at the wrong offset.

## Not changed

The audit's alignment note is still unverified as a client-visible defect. Production `Ship::ball()` sends the alignment presentation with `IS_MASSIVE` (`crates/eve-destiny/src/ball.rs:590-626`); EveJS currently has `SHIPS_MASSIVE_ON_THE_WIRE = false` while its comment ties that interim choice to server collision handling (`/home/user/evejs-261028.1/server/src/space/destiny/stream/ballEncoding.js:58-63`). eve.rs does resolve swept and remaining collisions in the simulation (`crates/eve-space/src/scene/collision.rs:12-70`). No V24.01 graphical-client comparison established which flag is correct in this state, so the code was left unchanged and this is not recorded as a confirmed bug.

No database-backed test applies to these pure Rust encoder/oracle changes. `eve-destiny` and `eve-oracle` have no database dependency. The real GUI client and the full live headless suite were not run. The codebase-memory/Serena MCP tools were unavailable in this environment; source tracing used local file searches and line reads instead.

## Verification

- Before the fixes, the fighter flag, ORBIT range, root WARP fields, and 65,536-entry geometry tests each failed with the reported mismatches.
- The scene-to-AddBalls2 fighter regression also failed under the old combined fighter/drone branch (`IS_INTERACTIVE` byte set), then passed with the corrected branch.
- Current EveJS `ball.encode` fixture refresh: 1,827 cases; 108 outputs/errors changed from the historical fixture.
- `cargo test -p eve-oracle --test destiny_golden destiny_golden -- --exact`: all 4,758 cases passed after fixture refresh.
- `cargo fmt --all` and `cargo fmt --all -- --check`: passed.
- `cargo check -p eve-destiny -p eve-oracle -p eve-space --release`: passed with no warnings.
- `cargo build -p eve-destiny -p eve-oracle -p eve-space --release`: passed with no warnings. The first attempt ran out of disk; after `cargo clean -p eve-space` cleared only generated package artifacts, the build completed.
- `cargo clippy -p eve-destiny -p eve-oracle -p eve-space --all-targets -- -D warnings`: passed.
- Focused and affected checks passed: Destiny golden oracle (5 tests, including all 4,758 cases), fighter tests (18 tests, plus the exact AddBalls2 regression after the final lint edit), NPC tests (15), `eve-destiny` (4), and `payloads_match_reference` (2), `others_match_reference` (5), `scene_matches_reference` (7), and `jump_matches_reference` (4).
- `node --check tools/dev/refresh-destiny-fixtures.js`: passed.
- No test was skipped. No database test applies to these pure Rust encoder/oracle changes. Main Actions CI and the graphical client have not been checked yet.
