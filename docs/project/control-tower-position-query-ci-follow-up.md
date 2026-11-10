# Control tower position query CI follow-up

## Finding

Main CI run 1672 (`a96ec73266cd`) failed in `test_a_corporation_unanchors_and_scoops_its_control_tower`. The expected online-state refusal was preceded by `CustomNotify: The control tower is not visible in the current space scene.` The Host produced that notice because `PositionOfAsync` returned no position for the tower (`dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:368-370`). The unanchor handler correctly performs this access check before refusing a tower that is online (`:557-576`).

This was a simulation query gap, not a failure of the POS state rule or evidence that the test needed to wait for a ballpark event. Before the fix, `Scene::position_of` resolved solar-system bodies and pilot ships, then returned `TARGET_NOT_FOUND` for a control tower even though `PlaceControlTower` had created its deployable ball in the scene (`crates/eve-space/src/scene/movement.rs:118-131` before the fix; `crates/eve-space/src/sim/dispatch.rs:1883-1888`). Commit `921459046` adds position and radius lookup for ball-backed deployables while continuing to refuse entities without a ball (`crates/eve-space/src/scene/movement.rs:118-141`). The control-tower regression now asserts that `PositionOf` returns the tower's coordinates and radius (`crates/eve-space/src/deployable/tests.rs:367-402`).

That server check matches EveJS. `validateControlTowerAnchorAccess` resolves the tower entity in the current scene, requires a local interaction, and checks distance before the state transition tests whether it is offline (`/home/user/evejs-261108.1/server/src/services/ship/controlTowerRuntime.js:517-545,1248-1276`). `posMgr.UnanchorStructure` maps the refusal to the client-facing `CustomNotify` (`server/src/services/planet/posMgrService.js:566-583`). V24's `OfferUnanchorStructure` checker requires a ballpark item and validates its type, anchoring state, range, ownership, and unanchor eligibility (`/home/user/eve-logic/menucheckers/celestialCheckers.py:1667-1679`); the inflight menu routes the visible structure to `menusvc.UnanchorStructure` (`/home/user/eve-logic/eve/client/script/ui/services/menusvc.py:1906-1909,2469-2487`).

The first local hypothesis was a race between persistence and scene delivery. The source comparison and the later simulation regression disproved that hypothesis: the test already reached a scene containing a tower, but `PositionOf` did not include deployables. No change to the live test's refusal assertion is needed. The upstream fix is already on main and merged into this worktree.

## Verification

- CI run 1672 reproduced the failure in the end-to-end job: [run 1672](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42749603517).
- CI run 1673 is exercising commit `921459046` and includes the full live end-to-end job: [run 1673](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42752793932). Record the final result here when it completes.
- Focused `agent-test` run 1387 was dispatched for local commit `7cf3f28f9` but could not check out that commit because it had not been pushed to any remote ref; its tests were skipped, so it is not a test pass: [run 1387](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42753121137).
- The graphical V24.01 client was not run. Findings use the V24.01 source, EveJS source, Host behavior, simulation regression, and CI output.
