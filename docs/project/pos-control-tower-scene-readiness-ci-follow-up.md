# Control tower unanchor scene readiness CI follow-up

## Finding

Main CI run 1672 (`a96ec73266cd`) failed in `test_a_corporation_unanchors_and_scoops_its_control_tower`: the expected online-state refusal was preceded by `CustomNotify: The control tower is not visible in the current space scene.` The test checked that `ship.Drop` had persisted a `deployed` row, changed its database state to online, and immediately called `posMgr.UnanchorStructure` (`tools/headless-client/tests/test_structures.py:693-705`). The persisted row proves placement authority completed; it does not prove the simulation has added the tower to this client's scene.

This error order matches V24 and EveJS. V24's celestial menu checker decorates `OfferUnanchorStructure` with `@needs_ballpark_item` and checks POS type, anchoring state, configuration range, ownership, and unanchor eligibility (`/home/user/eve-logic/menucheckers/celestialCheckers.py:1667-1679`). `PlayerOwned.GetSlimItem` looks up the item in Michelle's ballpark (`/home/user/eve-logic/eve/client/script/parklife/playerOwned.py:530-536`), and the menu exposes the unanchor action only through that checker (`/home/user/eve-logic/eve/client/script/ui/services/menusvc.py:1906-1909`). `menusvc.UnanchorStructure` then activates `effectAnchorLiftForStructures` (`/home/user/eve-logic/eve/client/script/ui/services/menusvc.py:2469-2487`).

EveJS validates that the tower entity exists in the current scene and can interact locally before checking whether it is offline (`server/src/services/ship/controlTowerRuntime.js:483-539,1248-1276`). `posMgr.UnanchorStructure` maps that result to `CustomNotify` (`server/src/services/planet/posMgrService.js:566-583`). eve.rs also checks scene visibility before distance and skill checks (`dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:346-377`); `UnanchorControlTowerAsync` invokes that access check before testing the persisted online state (`:557-579`).

The deployment path saves the durable record and sends a fire-and-forget `SimPlaceControlTower` request before returning (`dotnet/src/Eve.Services.Space/SpaceContainers.Disruptors.cs:279-303`; `dotnet/src/Eve.Space/SimClient.cs:1190`). The headless test must therefore wait for the client-visible tower slim after the database placement check. It now waits up to 30 seconds and retains the original assertion that an online, visible tower is refused with an offline notice (`tools/headless-client/tests/test_structures.py:693-707`). This keeps the server's V24-compatible validation order intact and tests the RPC after the scene has reached the client.

## Verification

- Main CI run 1672 reproduced the failure in the end-to-end job: [run 1672](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42749603517).
- The regression correction in `test_structures.py` is awaiting a focused `agent-test` run. Record the run and its result here before pushing.
- The graphical V24.01 client was not run. The finding is based on V24.01 client source, EveJS source, Host implementation, and CI output.
