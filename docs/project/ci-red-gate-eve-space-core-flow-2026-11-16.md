# CI red gate: EVE space core-flow comparisons (2026-11-16)

## 確認済みの原因と判定

CI runs 1825/1826 (head `5c16947a1`, run 1826 ID `43927990123`) failed in `crates/eve-space/tests/others_match_reference.rs` for two different comparison-input issues. The production Destiny messages were consistent with the current SDE and EveJS behavior; the regression tests had not kept up with the reference fixture shape and the current sentry population.

`others.json` is a recorded session from EveJS commit `b637d2a55469ee23d24411ed5a7646391de314d9`, recorded on 2026-09-29. Its provenance describes the two player characters and their client-visible steps (`oracle/PROVENANCE.md:42-45,74-76`; fixture metadata in `oracle/fixtures/scenarios/others.json`). Current EveJS `261108.1` enables default Empire sentry scenery unless explicitly disabled (`server/src/config/schema/world.js:269-277`) and adds sentries for system stargates and NPC stations (`server/src/space/defaultEmpireScenery.js:431-438`). eve.rs populates the static sentries before a first bind (`crates/eve-space/src/scene/session.rs:277-285`; population and type selection in `crates/eve-space/src/pochven_population.rs:527-599`). V24.01 applies every `extraBallData` entry in `AddBalls2` to a `SlimItem` and passes new items through `ProcessBallAdd` (`/home/user/eve-logic/eve/client/script/remote/michelle.py:1256-1277`). The eight sentries between the two player ships are therefore valid current SDE-backed scene entities absent from the older recording; the fixture does not describe a system with no static sentries.

The bind comparison now selects both player rows by their recorded ship IDs and compares them, in order, exactly against the fixture. It separately checks the eight sentry type IDs and requires that the AddBalls2 list contain only those sentries plus the two player ships (`crates/eve-space/tests/others_match_reference.rs:192-233`). The SDE-derived NPC values are unchanged.

The `SetBallAgility` difference (`4.018499851226807` versus `4.0185`) came from reconstructing the recorded ship from its packed ball while failing to select that ship's recorded Destiny number action. In `others.json`, ship IDs are nested under `watcher` and `actor`; the old helper looked only for a top-level `shipID`, so it fell back to the ball's f32 inertia. The corrected helper takes the ID from the recorded ball and matches `SetBallAgility` by ID (`crates/eve-space/tests/support/mod.rs:175-183,209-220,260-264`). V24 applies agility through `ballpark.SetBallAgility` (`/home/user/eve-logic/eve/client/script/ui/services/flightControls.py:209-215`), and EveJS marshals the reference number with `buildMarshalReal` (`server/src/space/destiny/stream/actions.js:165-166`). The comparison now preserves the fixture's exact `4.0185` value; it does not use a tolerance or alter the emitted value.

## CI job review

- Runs 1825/1826 reported the same two Rust test failures. In run 1826's Windows combined job, `Load SDE and test Rust workspace` stopped on those same failures; the later .NET build, Python tests, PowerShell headless entry point, and Windows client steps were skipped, not passed. The Linux .NET build, Python client tests, and end-to-end headless flow passed in that run.
- The fix was committed to main as `d9d77c051` (`Fix EveJS core flow reference comparisons`). On follow-up main run 1827 / `43937372450`, Rust tests, Rust format and clippy, Windows Rust/.NET/Python/live-client job, .NET build, Python tests, fixture generation, and the reported .NET test shards were successful at the time of this note. The end-to-end job was still in progress; its final result is not claimed here.

## Verification performed locally

All commands ran in `/tmp/wt-cx5-red-main-fix-20261116` at `d9d77c051`, with the pinned SDE build 3396210 available. `cargo +1.99.0 fmt --all -- --check`, `cargo +1.99.0 test -p eve-space`, `cargo +1.99.0 build --release -p eve-space`, `cargo +1.99.0 check --release -p eve-space`, and `cargo +1.99.0 clippy --release -p eve-space --all-targets -- -D warnings` passed. The focused Release `others_match_reference` target passed 5/5. The full `eve-space` suite passed all targets with zero failures and zero skips.

For the requested database lane, PostgreSQL database `eve_p5` was created, pinned SDE 3396210 was loaded into schema `sde`, and the database-backed `SpaceEventsTests` class passed 17/17 with zero skips using `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p5`.

## Not checked

The graphical V24.01 client was not launched locally. Local wire checks used the recorded headless-client fixtures; main CI's Windows live-client step completed successfully, but that is not a graphical V24 desktop session. Main CI run 1827's end-to-end job is still pending as of this note.
