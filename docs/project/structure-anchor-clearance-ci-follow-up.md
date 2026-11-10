# Structure anchor clearance CI follow-up

## Finding

The `CantDeployBlocked` response in CI run 1667 (`fdcbad9854cc`) was correct. The old live scenario warped 2,000,000 m from the legal-system station, but that point was only 1,724,888 m from moon `40057788` (type 14). With the pinned SDE build 3396210, the moon radius is 160,000 m and the Astrahus radius is 45,000 m. V24 requires 4,000,000 m plus both radii, a minimum of 4,205,000 m. The old test point was therefore inside the client-defined exclusion area.

V24.01's `structures/deployment.py:51,73-104` defines the 4,000,000 m moon clearance and rejects a candidate when its distance is less than that clearance plus the deployed type radius and blocker radius. `eve/client/script/ui/structure/deployment/deploymentEntity.py:50-69,174-179` builds the preview candidate from the ship position, checks the ballpark objects, and limits the offset. `eve/client/script/ui/services/structure/structureDeployment.py:82-97,166-175` follows the preview flow and sends `structureDeployment.Anchor` after confirmation.

The EveJS reference has the same rule. `server/src/services/structure/structureDeploymentService.js:20-43` forwards the Anchor arguments to `deployStructureFromInventoryItem`. `server/src/services/sovereignty/sovPlayerDeployment.js:275-319` validates against the live scene and maps a placement conflict to `CantDeployBlocked` with the blocking type ID. `server/src/services/structure/structureDeploymentPlacement.js:109,326-414,425-502` applies the 4,000,000 m clearance plus both radii.

eve.rs uses the pinned SDE radii and the same clearance in `dotnet/src/Eve.Host/Persistence/Structures/StructureAnchorAuthority.cs:247-294`. The test position, rather than that rule, needed correction. Commit `15c45bd0c` moved the warp destination to 5,000,000 m from the station; the candidate is 4,725,338 m from the nearest moon, outside the 4,205,000 m minimum. The test still requires the same successful Anchor RPC, persisted structure, and client-visible notifications (`tools/headless-client/tests/test_structures.py:49-56,482-534`).

## Verification

- CI run 1668, after commit `15c45bd0c`, completed successfully; its end-to-end server and headless-client job passed: [run 1668](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42732054573).
- CI run 1671 for `b3b7ea2e2f34` completed successfully: [run 1671](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42746067198).
- `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -c Release` passed with 0 warnings and 0 errors.
- On PostgreSQL database `eve_p3`, after loading pinned SDE build 3396210 with `cargo run -p eve-sde -- ensure`, `Structure_anchor_rpc_rejects_a_location_inside_the_client_moon_clearance_and_preserves_the_item` passed 1/1.
- A focused `agent-test` run for `test_structures.py` at the exact `cx3` tip SHA `b3b7ea2e2f344cbe9b51ac8aafc57691297aab01` completed successfully: [run 1386](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42746780689). The initial dispatch by branch name `cx3` could not resolve because that branch is not on the remote, so the workflow did not start its tests; no test result was counted from that run.

The graphical V24.01 client was not run. Verification is from source inspection, the Host wire test, and headless client/server runs.
