# ST-15: membership audit, Anchor verification, live scenarios

Base: origin/main e3645a4 (ST-14 handoff). Database `eve_p4` (live port 26103 reserved, not used). The cluster had no SDE on `eve`; `eve_p4` was
loaded from the cached pinned build 3396210 (`.cache/sde`) with `cargo run -p eve-sde -- load`, under `tools/dev/db-lock.sh`.

## 1. CorporationMember audit

- Every runtime write of `characters.corporation_id` goes through `CharacterAffiliations.SetAffiliationAsync`, whose only production caller is
  `CorporationStore.MoveAsync`. `MoveAsync` writes the `affiliation.corporation` relation through `CorporationMemberAffiliationWriter`, which
  `AllianceApplicationDueWorker.RegisterAsync` sets (Host `CorporationComposition.Alliances`, `allianceDue.RegisterAsync()`). Covered paths:
  founding (CreateAsync), application accept (RespondAsync), invitation, LeaveAsync, KickAsync (incl. the starter corporation).
- Character creation (`CharacterCreation`) inserts the character with its start corporation without `MoveAsync`: this was the gap ST-14 fixed.
  No other insert into `characters` outside `PgCharacterStore`/creation was found. ResignAsync and role/votes/share paths write `members` roles
  only, never the corporation.
- Alliance membership: `AllianceMember` relation is written by `AllianceApplicationDueWorker.WriteAffiliationAsync` (`store.AllianceAffiliationWriter`,
  set in `CorporationRpc`) and reconciled at startup. No gap found.
- Tests (eve_p4, all passed): `CreationTests` 21 (new: `A_character_made_while_the_server_runs_is_affiliated_with_its_school_corporation_through_the_relation_store`
  reads the answer through the real `RelationStore.AffiliationOfAsync`); `CorporationTests` 12 (new:
  `Accepted_application_and_leaving_move_the_corporation_membership_relation_at_runtime`, joins and leaves through the production writer wiring).
- Project references added to `Eve.Characters.Tests` (Events, Relations, Scheduler); `using Eve.Sde` added to `CorporationTests.cs`.

## 2. Anchor refusals and sovereignty delay (verified in code and tests)

- SDE refusal: `StructureRulesSource.IsAnchorRestricted` reads `disallowedAnchorCategories`/`disallowedAnchorGroups`. In the pinned build J000895
  (31002580, class 13 Shattered) and J015092 (31002505, class 1) both carry category 65, Thera (31000005) has categories [65] and groups [361].
  ST-2 notes said J015092 was a shattered system: it is not. The pinned test now names J000895 as the shattered representative.
- Sovereignty delay: `StructureAnchorAuthority.ResolveSovereigntyAnchoringMsAsync` adds `StrategicIndexLevel(ClaimedFor) * 24 h` for a null-sec
  Infrastructure Hub of another alliance, persists `anchoring_ms` (migration 0007, `StructureStore`), and the timers replay it. Missing sovereignty or
  index data fails closed before the item is consumed. Composition wiring is present (`StructureComposition.cs`, `profiles`, `sov`, `anchorWithDuration`).
- Tests passed on eve_p4: Host `StructureAnchor` filter 13 (includes `Pinned_SDE_anchor_restrictions_refuse_before_consumption`, `StructureAnchorDelayTests`
  3, `Foreign_IHub_deployment_persists_the_computed_duration_on_the_structure_row`); Structures `StructureTimerAnchoringDelayTests` 3.
- TODO row 1464 was OPEN and stale; now VERIFIED. TODO row 119 open sentence narrowed. DEVIATIONS "Refusals and sovereignty delay" already matched the code.
- Not checked: the wiki's exact null-sec ownership rule beyond the hub owner (the code uses the Infrastructure Hub's alliance, not the sovereignty claim).

## 3. Live headless scenarios: not added

- Anchor refusal by name without the role: `RequireRole` runs after the NPC-corporation check (`DropNeedsPlayerCorp`), so a new character in an
  NPC corporation gets a named refusal early, but reaching the role check needs a player corporation, which the harness does not seed.
  The harness has no Anchor wire shape (`structureDeployment` Anchor arguments not researched).
- hackingMgr no-attempt answer: not examined in this pass.
- `tools/headless-client/tests/test_structures.py` has only the two ST-14 tests; no live run was made in ST-15.

## Commands run

- `dotnet build` of Eve.Host.Tests, Eve.Structures.Tests, Eve.Characters.Tests, Eve.Corporations.Tests (0 errors after the reference fix).
- `EVE_PG_URL='Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p4' tools/dev/db-lock.sh dotnet test ... --no-build` as listed above.
- No Rust change; `cargo fmt` and clippy not exercised for this package.

## Open

- Live anchor and hacking scenarios (section 3); the graphical client; the MITM check of notices 43 and 44.
