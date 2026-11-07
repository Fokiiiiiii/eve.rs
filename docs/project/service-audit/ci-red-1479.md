# CI 1479 investigation

## Verified facts

- Baseline CI run 1479 failed the fast movement walk and `StructureCompositionTests.Registered_tower_anchor_job_publishes_eligibility_event`.
- The isolated tower test passed in Actions run 41745237074 (1 passed, 0 skipped). That did not reproduce the sharded failure.
- `EventBus.EnsureSubscriberAsync` starts a `StartAt.Beginning` cursor at zero. A Host test now publishes before subscribing, waits for delivery, then settles and asserts one receipt. The tower test also checks its count after a settle wait and an explicit pump. These checks cover replay and late duplicate delivery; they do not establish the original CI failure's cause.
- EveJS `ShipService.Handle_Undock` returns `null` on success. The V24.01 station path passes `shipsvc.Undock` to `sessionMgr.PerformSessionChange` (`eve/client/script/ui/station/base.py:497`) and does not read its result. The structure docking RPC is a separate call (`eve/client/script/ui/services/structure/structureDocking.py:35`), whose callback discards its response.
- `movement::stop` returns `Steering::Nothing` or `Steering::Actions`; the impossible `Refused` pass-through was replaced with an explicit unreachable arm because Rust's `Steering` enum also represents refusals from other movement commands. Stop preserves the speed fraction, so the movement regression scenario that expects later acceleration begins with an explicit zero fraction.

The ESS test's `None` assertion checks the bound method result after the headless client's CallDown and marshal-stream wrappers are removed. These are headless protocol checks; no graphical-client session is recorded here.

## CI records

- The isolated tower test's pass does not explain the original shard failure.
- Push run 41751642719 failed the Linux and Windows Rust checks on `scene::tests::a_ship_that_is_not_running_the_effect_or_not_in_space_has_nothing_to_stop`; the fixture expected a new speed fraction after Stop. The fixture correction was checked with the focused Rust test, format check, cargo check, and workspace Clippy.
