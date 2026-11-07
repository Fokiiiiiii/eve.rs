# Needlejack, shipcaster, and corporate wallet review handoff

## Finding 3 — starter shipcaster eligibility

Verified: production registered `starterShipcasterTravelMgr` without an eligibility policy, while `ShipcasterRpcService.CallAsync` deliberately raises `MissingBackend` when the policy is absent. The V24.01 client returns this RPC result directly from `eve/client/script/parklife/shipcasterSvc.py:32-33`. The local launcher uses a separate local eligibility check at `eve/client/script/shipcaster/client/components/shipcasterLauncher.py:238-246`; a whole-client search found no caller of `CanJumpStarterShipcaster` in the snapshot. EveJS wires the same RPC to its runtime eligibility provider at `src/server/src/services/shipcaster/starterShipcasterTravelMgrService.js:23-29`.

Change: production now registers through `CharacterServicesComposition.CreateStarterShipcasterService`, which returns false and emits one warning on its first use until an eligibility provider is supplied. The service-level missing-policy exception remains a guard. The Host wire test uses this same production factory, checks repeated false results and one warning; `ShipcasterRpcTests` asserts true and false policy results.

Checks passed:
- `dotnet build dotnet/src/Eve.Services.Space -p:UseSharedCompilation=false`
- `dotnet build dotnet/src/Eve.Host -p:UseSharedCompilation=false`
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Services.Space.Tests --filter 'FullyQualifiedName~ShipcasterRpcTests' -p:UseSharedCompilation=false` (16 passed, 0 skipped)
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests --filter 'FullyQualifiedName~Movement_rpcs_use_registered_host_routes_with_client_wire_shapes' -p:UseSharedCompilation=false` (1 passed, 0 skipped)

## Finding 3 — Actions confirmation

Commit `b71fcafcaacb` was pushed as `Fix starter shipcaster eligibility wiring`. Push-triggered CI run `42289361399` and selected agent-test run `42289403240` both completed successfully.

## Finding 1 — Needlejack cloak validation

Verified against the V24.01 client: `randomJump/client/clientRandomJumpValidator.py:99-102` treats either the ship ball's `isCloaked` state or an in-progress cloak as cloaked, and `:166-171` adds `SHIP_IS_CLOAKED` with the character ID. `randomJump/error.py:22-23` assigns code 15. `filamentSpoolup.py:25-36` calls the same validator for trace-gate activation; the random-jump service/controller are the only activation RPC path found in the client graph. The validator is local to each client, so checking each linked pilot server-side prevents moving a participating pilot whose own client would reject activation.

The Host can read cloak state from its authoritative pilot state: active cloak module effects are retained in `RunningModules`, and jump-cloak state is tracked separately. The Rust scene decloak state is not queried by this Host path. `SpaceSessions.IsCloaked` now checks these Host-owned states and the effect names handled by the shared cloaking effect. Needlejack validation returns code 15 with the affected character ID for a cloaked activator or in-range linked fleet pilot. The Host test activates a real cloaking module for both cases and checks the error IDs, unchanged filament, and absent activation row.

Local checks passed: Host and Space project builds; all `NeedlejackActivationHostTests` (9 passed, 0 skipped); four cloak-related Space tests (4 passed, 0 skipped). Commit `e2229ecb` was pushed to main. Selected agent-test run `42293894129` and push-triggered CI run `42293854018` both completed successfully with no failed tests or jobs.

## Finding 2 — post-commit relocation recovery

Verified: the filament decrement, persisted ship and character locations, and activation row commit together. The V24.01 controller only marks activation finished after the RPC returns; it maps an unexpected RPC exception to error 22 (`randomJump/client/randomJumpController.py:141-167`), and the random-jump service calls the activation RPC directly (`randomJump/client/randomJumpSvc.py:17-19`). A relocation error after commit therefore reported failure for an activation that had already consumed the filament and moved persisted state.

Change: after commit, `CompleteCommittedAsync` now schedules a background retry with the same deterministic move operation ID when the first simulation relocation fails. Simulation `public_move` treats an identical operation ID and payload as a successful replay (`crates/eve-space/src/sim/spawn.rs:792-802`; replay regression `crates/eve-space/src/sim/tests.rs:835-864`). The Host test drops the first move reply, asserts the activation RPC succeeds and persisted state remains committed, drains background work, and checks the retry uses the same operation ID and reaches the destination.

Local checks passed on `eve_p4`: Host and Space project builds; `NeedlejackActivationHostTests` (9 passed, 0 skipped); `SpaceServiceTests` (168 passed, 0 skipped). Commit `04cb3c38` was pushed to main. Selected Agent Test run `42305570939` and push-triggered CI run `42305547087` both completed successfully with no failed jobs.

## Finding 4 — corporate wallet notification roles

Verified on the server: `AccountService.GetCashBalance` allows the existing Accountant and Junior Accountant read rule through `WalletAccountAsync(..., requireTake:false)` (`dotnet/src/Eve.Services.Wallet/AccountService.cs:157-181`; `dotnet/src/Eve.Corporations/CorporationStore.cs:483-513`), while the notification gate previously excluded them.

Commit `d3baea09` implements finding 4: `WalletAnnouncer` uses `CorporationRoles.CanReadWalletDivision`, the same rule as wallet-division reads. Connected `rolesAtAll` values are recomputed after direct and bulk member-role updates, title-mask changes or deletion, CEO succession, and member removal; the login hook remains the source of the refreshed attributes.

The Haiku client rationale was incomplete: `WalletSvc.OnAccountChange` reaches `_OnCorpAccountChange`, which checks the selected division's read role before updating its cache (`eve/client/script/ui/shared/neocom/wallet/walletSvc.py:80-115`), then scatters the local notification; the market balance widget listens to that event (`eve/client/script/ui/shared/market/wallet_balance.py:135-140`). The RPC and notification audiences are aligned in the worktree change, while the V24 UI's selected-division check remains in effect. EveJS 261028.1 broadcasts corporate changes broadly (`src/server/src/services/corporation/corpWalletState.js:241-256`); the role-filtered server behavior is recorded in `docs/reference/compat/DEVIATIONS.md`.

The initial related Host run exposed a Director notification gap: `WalletAccountAsync(..., requireTake:false)` permits Directors, but the shared `CanReadWalletDivision` predicate omitted them. The predicate and its store comment now include Directors alongside accountants, junior accountants, and the selected division's Account Take bit. Post-merge builds passed for `Eve.Host` and `Eve.Services.Market`. DB-locked checks on `eve_p4` passed with no skips: `AnnouncementsTests` (2), the related `Eve.Corporations.Tests` classes (120), the related `Eve.Host.Tests` classes (415), and the `Eve.Market.Tests` classes (30). Commit `d3baea09` was pushed. Selected Agent Test run `42311011908` completed successfully. Push-triggered CI run `42310976718` failed in `.NET (other tests 3/3)` on `CorporationOfficeTests.Bill_manager_lists_saves_settings_and_pays_office_rent_in_the_wallet_transaction`: the accountant now receives the intended wallet notification, but the old test expected only the RPC reply. The test was updated to assert the exact notification wire shape followed by `CallRsp`; the focused DB test passes on `eve_p4`. The same CI shard reported one skipped test in the full `Eve.Market.Tests` run (149 passed, 1 skipped); the related local Market classes passed 30 tests with no skips.
