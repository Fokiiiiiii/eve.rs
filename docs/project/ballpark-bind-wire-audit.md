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
