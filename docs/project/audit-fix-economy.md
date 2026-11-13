# Economy audit fixes (2026-11-13)

This record covers the external audit findings C-01, M-01, and H-05. Each finding was reproduced with a Host-level regression against the unmodified implementation before applying its fix. The regressions use PostgreSQL-backed Host wiring and assert wire results, persisted state, and notifications where applicable.

## C-01 — request identity for bounty and industry operations

**Verdict: confirmed and fixed.** The old persisted keys were derived from character and Macho call ID only. The V24.01 client initializes MachoNet's call counter to `1L` and increments it per blocking call (`carbon/common/script/net/machoNet.py:455,3598-3600`), so the counter can repeat after a client restart. The bounty client passes target and amount to `bountyProxy.AddToBounty` (`eve/client/script/ui/shared/bountySvc.py:82-84`); industry passes the serialized job to `InstallJob` and job/system arguments to completion or cancellation (`eve/client/script/industry/industrySvc.py:139-160`).

The baseline Host tests failed as expected: a new bounty placement in another session returned the first placement's `1,000,000` pool instead of `1,500,000`; a second industry install after reconnect returned `IndustryInvalidJob` because the old operation key replayed the first job. In the fixed implementation, bounty keys include placer, session ID, call ID, target, and an invariant-culture amount, with an advisory transaction lock around lookup and placement. Industry keys include session ID, character, method, call ID, and a SHA-256 fingerprint of the encoded method arguments. Retries with the same session and request retain the same key; a new session or changed request gets a different key. Existing PostgreSQL uniqueness and transaction handling continue to protect industry persistence.

The executable reference forwards these same client arguments to bounty and industry runtimes (`server/src/services/bounty/bountyProxyService.js:379-435`, `server/src/services/industry/industryManagerService.js:91-125` in EveJS 261108.1). Those adapters do not define the persisted request-key behavior; the new key scope preserves their client-visible debit/job behavior while preventing a stale replay.

## M-01 — corporation contract wallet division

**Verdict: confirmed and fixed.** V24.01's `createContractWnd.py` sets `startStationDivision` from the selected inventory item's `flagID` and includes it in `ContractStruct` (`eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:150-155,878-882,1605-1627`). The wallet division is held separately in session `corpAccountKey` (`eve/common/script/sys/eveSessions.py:395`); the wallet UI selects it with `SetAccountKey` and checks access to that active division (`eve/client/script/ui/shared/neocom/wallet/walletUtil.py:55-56,105-116`).

The baseline Host regression failed with `ErrorResponse` when the selected wallet division was funded but the hangar division was not. EveJS 261108.1 builds the contract wallet reference from `session.corpAccountKey` (`server/src/services/contracts/contractRuntimeState.js:300-307,665-675`) and checks persisted take permission for that wallet before charging (`:1193-1202,1946-1978`).

`ContractProxyService` now charges the selected session wallet key, keeps `startStationDivision` for item custody, and asks Host's corporation store to validate current membership and take permission for that exact division. The registered-Host regression checks the contract wire response, persisted wallet key, debit and notification; it then retries with a division lacking take permission and verifies an error with no new contract, debit, or item mutation.

## H-05 — courier delivery location and atomic turn-in

**Verdict: confirmed and fixed.** The old docking path checked the mission destination station but counted the character's item quantity across all locations. The client mission record exposes the objective destination and required cargo (`missions/client/active_agent_mission.py:94-105`); the agent UI reads the courier `hasCargo` value (`eve/client/script/ui/station/agents/agents.py:755-763`) and sends agent actions through `agentDialogueWindow.py:427-431`. EveJS 261108.1 allows courier completion at the drop-off station and rejects a completion action at another location (`server/src/services/agent/agentMissionRuntime.js:6119-6142,8884-8892`).

The baseline registered-Host regression failed because cargo at a different station completed the courier mission. The fix counts only this mission's tagged cargo in the destination station hangar or in the character's active ship when that ship is docked there. Completion then locks and rechecks those inventory rows and commits cargo consumption, mission state, reward settlement, loyalty points, and standings in one transaction. The Host regression covers a wrong-station refusal, destination hangar delivery, a ship docked at the destination, concurrent docking callbacks, persistence after restart, and no duplicate reward/notification on replay.

The existing eve.rs docking hook still initiates turn-in on docking. EveJS's inspected path uses the client's explicit agent `Complete` action. This fix intentionally leaves that pre-existing trigger unchanged because H-05 concerns location validation and atomic settlement; the trigger difference is recorded as a remaining parity gap and was not checked with the graphical client.

## Verification record

- Baseline tests were run first in an isolated worktree at `5f59a150e` and failed on the three reported stale behaviors (C-01 has separate bounty and industry regressions). The core test slice initially found a stale Arc29 fake that did not record the new transaction-batched reward argument; the fake now models that behavior and the original assertion passes unchanged.
- On `eve_p5`, the selected Host slice passed **23/23, 0 skipped**. The Core service slice passed **66/66, 0 skipped** after the Arc29 fake correction.
- Release builds with warnings treated as errors passed for `Eve.Services.Crimewatch`, `Eve.Services.Core`, `Eve.Host`, `Eve.Host.Tests`, and `Eve.Services.Core.Tests`, each with **0 warnings and 0 errors**.
- One earlier broad Host filter accidentally selected the `HostServerTests` partial class across unrelated files. That run was cancelled and is not counted; it emitted one skip (`CharacterHostTests.A_player_ship_death_creates_a_persistent_victim_kill_report_notification`) before cancellation. The completed focused Host slice reported no skips.
- Before this change, main's latest required CI gate was green in [run 1767](https://github.com/Fokiiiiiii/eve.rs/actions/runs/43423204713). Its advisory latest-stable Rust clippy job failed, while the required gate and all other listed jobs succeeded.
- The V24.01 client source and EveJS 261108.1 implementation were inspected. EveJS was not executed; its checked-out server package does not define a test script for these flows.
- A native V24.01 graphical-client run was not performed. Headless Host tests do not establish graphical-client acceptance.
- Post-push CI is monitored separately; this record makes no claim about that run.
