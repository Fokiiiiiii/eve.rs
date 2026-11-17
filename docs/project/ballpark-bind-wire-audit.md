# Ballpark bind wire audit

Date: 2026-11-16

## Finding

The first complete ballpark state after station undock is a `SetState` Destiny action by itself. The undocked ship's initial movement state is already part of the `SetState` payload, so separate `SetBallAgility`, `SetBallMass`, and `SetMaxSpeed` primes are unnecessary in this bootstrap. Login and gate-jump arrivals still include dynamic-ship primes after `SetState`.

`Scene::bind` captures `fresh_undock` before `take_in_view` mutates `pilot.bound` (`crates/eve-space/src/scene/session.rs:299-311`, `crates/eve-space/src/crowd/mod.rs:35-48`). It conditionally appends dynamic ship numbers only for the other bootstrap paths (`session.rs:350-365`). This distinction avoids inferring the arrival path from state after binding has already changed it.

## Client and EveJS evidence

- V24.01 `Michelle.DoDestinyUpdate` returns without applying Destiny updates while its ballpark is unset (`/home/user/eve-logic/eve/client/script/remote/michelle.py:482-511`). Therefore pre-bind updates do not establish visible ball state.
- V24.01 `Michelle.SetState` clears and reconstructs the ballpark from the supplied state buffer and slim items (`michelle.py:968-1005`). `FlushState` groups actions by timestamp and queues them for simulation (`michelle.py:1098-1162`).
- EveJS returns the undock bootstrap as `SetState` alone, with its source comment recording the observed TQ behavior (`/home/user/evejs-261108.1/server/src/space/runtime/scene/presentation.js:944-956`).

This supports treating updates sent before the first `SetState` as invisible to the client and comparing the undock bootstrap from the first `SetState` onward. It does not justify dropping later Destiny actions or changing their payloads.

## Regression coverage

- `crates/eve-space/src/scene/tests.rs` verifies the undock bind action list and preserves the post-SetState primes for progressed movement, login, and jump states.
- `tools/headless-client/tests/test_undock.py` adds a wire-level helper that ignores only owner Destiny updates before the first `SetState`, following the V24 ballpark guard.
- Jump, multi-ship visibility, and target live comparisons use the same bootstrap boundary. Their subsequent client-visible action shapes, payloads, state transitions, and notifications remain compared.

The afterburner walk also compares dogma notifications after applying the V24 client-side `OnMultiEvent` dispatch rules. V24 `godma.OnMultiEvent_` dispatches event families in a fixed order, filters repeated effects by owner/item, and groups attribute changes by owner/item/attribute before emitting them (`/home/user/eve-logic/eve/client/script/environment/godma.py:212-288`). `clientDogmaLocation.OnModuleAttributeChanges` applies the incoming attribute rows to loaded dogma items, including heat attributes (`/home/user/eve-logic/eve/client/script/dogma/clientDogmaLocation.py:310-342`). EveJS queues these rows into `OnMultiEvent` (`/home/user/evejs-261108.1/server/src/space/runtime/dogmaEvents.js:86-103,133-135`); the headless comparison normalizes that wrapper before comparing the module walk.

The first direct EveJS replay against the historical `module.json` capture differed in event order/timing and capacitor cadence. The current capture was then recorded from EveJS and replayed against a fresh EveJS process; after normalizing V24 `OnMultiEvent` dispatch and matching capacitor rows by the actor ship's mapped `itemID`, that self-comparison passed. The same actor-only capacitor comparison passed against eve.rs. The historical replay mismatch was therefore a stale/timing-sensitive fixture comparison, not an EveJS or eve.rs behavior failure. V24 groups attribute rows by owner/item/attribute (`godma.py:246-252`) and applies each row to its own item (`clientDogmaLocation.py:310-342`); comparing rows from different ships by position was invalid. The outcomes and commands are in `docs/project/live-skipped-scenarios.md`.

Focused Rust test results and final live scenario results are recorded in `docs/project/live-skipped-scenarios.md`.

## SetState stamp

The undock bootstrap uses the current whole-second Destiny stamp. `Scene::bind` calls `next_stamp(now)` once for the state before constructing the `SetState` payload (`crates/eve-space/src/scene/session.rs:312-340`); with no earlier Destiny action in this bind, `next_stamp` returns `stamp_at(now)` (`crates/eve-space/src/scene/mod.rs:936-938,1175-1185`). The regression test asserts that exact value. An older `+1` assertion reflected the former multi-update bootstrap, not the current SetState-first flow. V24 stores the SetState stamp as its latest state time and discards later updates older than it (`/home/user/eve-logic/eve/client/script/remote/michelle.py:1103-1112`), then applies the SetState buffer (`:1147-1152`). EveJS emits this undock `SetState` alone (`presentation.js:944-947`).

## Host-level undock expectation

The database-backed `Space_login_binds_a_ballpark_that_contains_the_ego_ship` test initially failed after its opt-in simulator environment was enabled: the observed undock Destiny action list started with `SetState`, while the assertion expected `AddBalls2` first (`dotnet/tests/Eve.Host.Tests/SpaceBallparkBootstrapHostTests.cs:102-118`). V24 ignores `DoDestinyUpdate` while no ballpark is set (`/home/user/eve-logic/eve/client/script/remote/michelle.py:482-511`); `SetState` clears and builds the ballpark from its buffer and slims (`:968-1005`). EveJS 261108.1 returns an undock `SetState` as the only bootstrap update (`/home/user/evejs-261108.1/server/src/space/runtime/scene/presentation.js:944-947`). The assertion now checks the exact action list `['SetState']`, preserving a strict wire-shape check and the existing ego-state assertions.

## Database-backed verification

Tests used `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4` and the pinned SDE. Release `eve-sim` and `EVE_ABYSSAL_SIM_SDE` were supplied for `RealAbyssalSimPgFact` tests. The filtered .NET results were: Host (`CharacterHostTests`, `SkillHandlerRpcHostTests`, `SkillsHostTests`) 93 passed on the initial run; its four opt-in simulator cases were skipped because those two simulator variables were initially absent, then rerun with the environment set. The four reruns first produced three passes and the stale SetState assertion failure described above; after the assertion fix, all four passed with zero skips. Other filtered classes passed with zero skips: Dogma 26, Services.Skills 16, Skills 24, Services.Space 173, and Ships `ShipTests` 31. The Release solution build completed with 0 warnings and 0 errors. These are focused class runs, not the full .NET test suite.
