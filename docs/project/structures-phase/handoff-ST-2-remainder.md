# ST-2 remainder handoff (worktree wt-st2b)

Scope: the three open items after upstream work (anchor refusals and sovereignty delay, the reinforcement-change notice, the structureProfiles write gap).

## Done

- structureProfiles write gap (root fix): `PersistenceStructureProfilesRpcBackend.WriteTableAsync` loads the table into the game store cache before writing. The cache refuses a write to an unloaded table (TABLE_NOT_FOUND), so a corporation with no default profile could never get one. Regression: `StructureCompositionTests.A_fresh_store_creates_the_corporations_default_profile_on_the_first_profile_change_and_keeps_it_after_a_restart` (red before, green after; StructureCompositionTests 12/12).

## Verified in code on main (tests in the pinned run list below)

- Anchor refusals: pinned SDE restrictions (Thera, shattered J015092, starter/career Kisogo/Amsen/Manifest, trade hubs), ADM 4.0 rule, extra-large high-sec, 1000 km, corporation age.
- Sovereignty anchoring delay: `ResolveSovereigntyAnchoringMsAsync`, persisted as `anchoring_ms` (migration 0007), restored on timer replay.
- Reinforcement-change notice: `ReinforcementChangedNotifications` writes through `PersistenceTransactionMailbox.WriteNotificationsAsync`, keyed by the outbox event id.

## Open (next in this pass)

- Replay of the reinforcement notice through the real bus (rewind the subscriber cursor, restart, check one record per box).
- Pinned test runs for StructureAnchorTests, StructureAnchorDelayTests, StructureTimerAnchoringDelayTests, StructureUnanchorDropTests.
- Unanchor drop (its code is on main; DEVIATIONS still says "not done"; Rust group 306 classification to check).
- DEVIATIONS and TODO rows for each item.

## Gotchas

- Use `EVE_PG_URL='Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p3'` (key=value) for dotnet; wrap heavy runs in `tools/dev/db-lock.sh`.
- Keep `-p:UseSharedCompilation=false`.
