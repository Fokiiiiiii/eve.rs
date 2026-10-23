# V24 Ship SKINR sequencing handoff

## Implementation

- Added PostgreSQL-backed design drafts, component licenses, sequencing jobs, custom ship licenses, and request receipts under the configured SKINR schema.
- Registered V24 draft, component-license, tier, sequencing-job, custom-license, activation, application, and unapplication handlers in the public gateway.
- Sequence initiation validates SDE layouts, skills, component licenses, station binders, and wallet PLEX in one database transaction. Retried request IDs return the same job; binder inventory notifications publish after commit.
- Durable scheduler completion persists the resulting unactivated license and job state before publishing the V24 completion and license update notices.
- Activation and application use the current cosmetic license and ship-cosmetics owner. Application emits the existing client notifications after its receipt transaction commits.
- Pricing and duration behavior, including quantity pricing without a quantity duration multiplier, is recorded in `docs/reference/compat/DEVIATIONS.md`.

## V24 flow and verification

- V24 has no separate job Claim RPC. Completion creates the license; the client reads the completed job, obtains its owned license, activates it, and applies it.
- `ShipSkinrDispatchTests` checks registered method names, tier pricing/duration rows, draft response nesting, quote behavior, and invalid request response types.
- `test_skinr_live.py` walks the public gateway using SDE-derived components and a real character/ship. It covers save/read/update/delete, invalid hull and component/license cases, skill and binder limits, PLEX rollback, concurrent retries, settlement, completion notices, restart persistence, activation, application, and ship-cosmetics notifications.
- The live runner now starts the public gateway, gives SKINR an isolated scratch schema, and restarts the Host during the walk.

## Checks and continuation

- After merging `origin/main`, the Host project and Eve.Host.Tests project builds both passed with no warnings or errors.
- Focused test `FullyQualifiedName~V24_skinr_routes_expose` passed before the merge (one test, zero skipped).
- The live headless walk and graphical-client verification still need GitHub Actions/graphical execution. No PostgreSQL database was used locally.
- After pushing, dispatch `Eve.Host.Tests` with filter `FullyQualifiedName~ShipSkinrDispatchTests`, wait for the run, and inspect the push-triggered CI failures. Fix red checks at their root, then update this note after each push.

## 2026-10-23 nested sequencing UUID assertion

- Push CI run 690 (`40016706205`) failed in the live walk after design save when `uuid.UUID(bytes=...)` received the serialized `sequencing.job.Identifier` wrapper.
- The V24 descriptors define both `GetAllActiveResponse.Job.id` and `CompletedNotice.id` as an `Identifier` message, with raw UUID bytes in its field 1. The Host encoders match that wire shape; the headless test now unwraps the nested identifier before decoding the UUID.
- Python syntax compilation and the focused live Actions check are pending. No local PostgreSQL or graphical-client check was run.
