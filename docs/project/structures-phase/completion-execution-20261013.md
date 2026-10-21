# Structures phase completion execution (2026-10-13)

Canonical root: repository root, main, initial HEAD 06abc660. Every worker is explicitly gpt-6-luna and runtime-verified before GO. Preserve unrelated dirty Corporation/Space audit patches. No new branches/worktrees, descendants, guessed values or fake success. Existing AGENTS source priority and foundations override historical handoff rules.

## Required closure checklist
- [ ] ST-8a: complete controlled-structure module execution on Host and Sim, actual ammo/cap/targets/FX, controller revocation and retained hull preservation.
- [ ] ST-8b: Standup doomsdays, HOG, burst projectors and chain/lightning families, common runtime lifecycle and failures.
- [ ] ST-2: authoritative Anchor refusals/default profile/sov delay, durable unanchor hull/fittings/core/fuel/rig disposal and reinforcement-change inbox notices.
- [ ] ST-7: real beacon service placement/list/jump with ACL/range/fuel, jammer sov rule and fuel item notifications.
- [x] ST-9 core: Sov Hub ball, Entosis capture/handover, reinforcement windows, command-node outcomes and capture/reinforcement-exit scheduler recreation have PostgreSQL coverage.
- [ ] ST-9 remaining: upgrade/fuel loss drops, replacement neutral hub, real-simulation restart and graphical-client acceptance.
- [ ] ST-10/ST-11: inventory movement event, completion only after loot empty, durable removal of emptied balls.
- [ ] ST-E: remaining custom decloak/Mobile Observatory path where required by the phase.
- [ ] Existing ST-0..6/C: regressions, restart/replay and Host-to-Sim/headless acceptance; no stale handoff treated as proof.
- [ ] Native client meaningful scenarios after implementations are stable, including initialization, control/HUD/activation, docking/tether and displayed state. Record exact unavailable gates; do not invent acceptance.
- [ ] Review final scope, diff/check, docs/TODO/DEVIATIONS, commit/push and close completed workers.

## Ownership and ordering
Wave 1: Host ST-8a, Rust ST-8a, Host unanchor drop and Anchor validation have separate file scopes. Parent owns shared composition wiring, codec/messages/protocol/fixtures and shared status documents, runs all .NET checks serially and integrates current upstream. Workers report interface gaps before shared schema changes.
Wave 2: reassign only completed verified Luna workers to ST-8b, ST-7, ST-9 and loot/notification closure as dependencies permit. Never resume an agent assuming it retains its model; inspect actual turn_context metadata after delegation.

## Evidence requirements
For each row: CCP/SDE and V24/EveJS paths; actual authority; success/refusal/ownership/concurrency/retry; commit-before-notify and persistence; same meaningful reference scenario; relevant focused and broader tests. Source/unit/PG/headless/native checks remain distinct. The preceding skill filter fix (cbfd6324) is preparation and does not close module execution.

## Baseline and discovered integration contracts
- Parent recovered a real isolated CBM MCP stdio session (initialize/list_tools/index_status/search_graph), keeping source discovery on MCP despite the shared transport failure. Python output is UTF-8/JSON ASCII; cp932 failures are not source evidence.
- Parent baseline: Eve.Structures.Tests 100 PASS, no failures/skips. Headless marshal golden/handshake mock tests 1035 PASS using Python -X utf8; no server/client flow acceptance is claimed.
- Drop worker confirmed SDE3396210 attribute3101 structureRequiresDeedType points at each hull's quantum core type. StructureTimers.ApplyAsync receives the job transaction; completion callback must use that same connection/transaction. Parent reserves the main StructureComposition.cs constructor/setup call.
- Existing reinforcement inbox infrastructure is PersistenceTransactionMailbox.WriteNotificationsAsync, already used by MoonOwnerNotifications; no duplicate notification store is justified by the old handoff.
- Runtime ports 26010/26011/26100/26200/26300 are currently not listening; PG5432 is available. Stable implementations will be exercised through the documented Windows TCP launcher and matched headless/MITM scenarios before native checks.

## Source-creation MCP recovery
The codex Serena context exposes only 24 tools and filters create_text_file/read_file/replace_content despite their availability. The parent recovered a native stdio Serena session using desktop-app context with dashboards/GUI disabled, canonical cwd, and confirmed their real input schemas. Workers use that genuine MCP session for new source files; user/global configuration is unchanged. Source writing through a shell is not the fallback.
