# Main CI red gate: ownerless structure placements

## Finding

Main CI run 1691 at `7ebebf19d896` failed in two live structure flows:

- `test_a_director_anchors_an_astrahus_from_its_ship_in_a_legal_system_and_the_structure_is_placed_and_told`
- `test_a_corporation_unanchors_and_scoops_its_control_tower`

Both flows lost the simulation link while re-entering space, then surfaced the generic “Space is not available” error. Local reproduction showed the sim link rejecting a `PlaceStructure` frame for the Encounter Surveillance System (ESS): structure `8440000030000903`, system `30000903`, type `55914`, group `4079`, radius `1000`, owner `0`.

The Host intentionally serializes an ESS with no assigned owner as `state.OwnerId ?? 0` in [EssComposition.cs](/home/user/wt-cx4/dotnet/src/Eve.Host/Composition/EssComposition.cs:238). Another Host caller sends owner `0` for faction warfare command nodes in [SpaceSovHubs.cs](/home/user/wt-cx4/dotnet/src/Eve.Services.Space/SpaceSovHubs.cs:114). The sim-link decoder rejected every owner ID `<= 0` in `decode_message.rs`, so it rejected this valid Host frame before placing the structure.

## Compatibility check and decision

Pinned SDE build 3396210 identifies type 55914 as the published Encounter Surveillance System in group 4079 (`types.jsonl:35796`, `groups.jsonl:1402`). This confirms the frame's type/group identity; it does not assign an owner to this generated ESS.

EveJS 261108.1 sets generated ESS `ownerID` to `1` in `server/src/space/encounterSurveillanceSystem.js:126-147`. That is a different fallback from the eve.rs Host's persisted nullable owner. The fix preserves the Host's authoritative `0` sentinel instead of inventing an owner. The V24.01 client maps generic sentry and space-object groups in `eve/client/script/environment/spaceObject/repository.py:90-97`; `targetMgr.py:1172-1173` also skips owner-cache insertion for a falsey owner ID. A whole-tree search found no ESS-specific owner rule. This is source inspection, not verification with the graphical client.

## Change and regression coverage

The decoder now permits owner ID `0` and continues to reject negative IDs at `crates/eve-space/src/link/decode_message.rs:1575-1585`. New regression tests cover both an ownerless ESS round trip and rejection of a negative owner in `crates/eve-space/src/link/structure_tests.rs:5-40`.

Before the decoder change, the new ownerless-ESS regression failed with `BadValue("structure")`. After the change it passed. The two CI live tests above also passed together against `eve_p4`, exercising the previously broken re-entry transition after structure placement and unanchor/scoop.

## Verification

Verified locally on 2026-11-10:

- `cargo +1.94.1 test -p eve-space --lib link::structure_tests` — 2 passed, 0 failed, 0 ignored.
- `cargo +1.94.1 test -p eve-space --test simlink_fixtures every_valid_frame_encodes_to_its_bytes_and_decodes_back` — 1 passed.
- `cargo +1.94.1 test -p eve-space --test simlink_fixtures every_invalid_frame_is_refused_for_the_stated_reason` — 1 passed.
- Focused live tests for the two CI failures, via `tools/dev/run-eve-live.sh` with `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4` — 2 passed, 0 skipped.
- `cargo +1.94.1 check -p eve-space` — passed.
- `cargo +1.94.1 fmt --all -- --check` — passed after formatting.
- `cargo +1.94.1 clippy -p eve-space --all-targets -- -D warnings` — passed.
- `cargo +1.94.1 build --release -p eve-space -p eve-sim` — passed with no warnings.
- `git diff --check` — passed.

The graphical V24.01 client was not run. Full EveJS server behavior was not run for this fix; the relevant EveJS implementation was inspected. No claim is made here about ESS ownership semantics beyond preserving the Host's nullable value on the sim link.
