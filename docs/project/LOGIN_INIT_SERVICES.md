# Login and Initial World Entry RPC Inventory

Scope: V24.01 build 3396210, from the front handshake through character selection and the first world session. This inventory combines the live client callsites, `oracle/fixtures/scenarios/login_to_character.json`, `docs/reference/spec/services.tsv`, current Host service registration, and the matching EveJS runtime path. `Implemented` means the route has a concrete server implementation; `Partial` means the route exists but a path returns a default or does not cover the full client behavior; `Missing` means no route was found. Headless verification is listed separately from graphical-client verification.

## Client entry sequence

The headless login-to-character walk (`tools/headless-client/tests/test_login_to_character.py`) validates the front handshake/session initialization, creates characters, fetches selection rows, rejects cross-account character selection, selects an owned character, and checks `machoNet.GetTime`. V24.01 callsites include `eve/client/script/ui/login/charSelection/characterSelection.py`, `eve/client/script/ui/services/ccSvc.py`, `eve/client/script/ui/login/characterCreationContainer.py`, and `eve/client/script/ui/login/characterCreationContainer_new.py`. Selection invokes `sessionMgr.PerformSessionChange(... charUnboundMgr.SelectCharacterID ...)`; the Host installs the selected character state and emits the session change through the existing session path.

## RPC inventory

| Service / method | V24.01 caller or phase | Status | Evidence / remaining work |
|---|---|---|---|
| Front Hello / HelloAck / ClusterConfig | TCP front connection before authentication | Implemented | `dotnet/src/Eve.Host/HostServer.cs`, `HostCodec.cs`; protocol state machine is in the Host and Rust front. Headless live walk exercises login. |
| `connection.Authenticate` / login auth | First client authentication request | Implemented | Host account directory and `FrontConnection.AuthenticateAsync`; wrong-password and account-create paths are in live-client harness. |
| `loadService` and `externalQueueMgr` connect calls | Connect and queue admission | Partial | Routes are in dispatch; no client trace establishes a real queue/admission implementation for a single local host. Audit/implement queue semantics if the V24 caller requires them. |
| `cache.GetServerVersionAndBuild` | Startup / bug-report metadata | Implemented | Concrete cache service; build/version comes from Host configuration. |
| `machoNet.GetTime` | Character selection and startup time sync | Implemented | Host MachoNet service; fixture records its result shape. |
| `machoNet.GetClusterGameStatistics` | Startup cluster display | Partial | Current Host statistics are derived from selected sessions; cluster user count is currently zero. Determine whether this call is made in login UI and source the count if required. |
| `machoNet.GetGlobalConfig` / service discovery | Startup service resolution | Implemented | Registered service list and global config exposed by Host MachoNet. |
| `sessionMgr.PerformSessionChange` | Character selection/create-to-world transition | Implemented | `SelectCharacterID` changes authenticated session and sends notification; walk compares session notification and the next world-facing requests. |
| `charUnboundMgr.GetNumCharacters` | Character screen and creation limits | Implemented | Per-account persisted character count. |
| `charUnboundMgr.GetCharactersToSelect` | Character screen | Implemented | Account-scoped character rows. |
| `charUnboundMgr.GetCharacterSelectionData` | Character screen | Implemented | Selection rows and account/subscription fields are derived from current character/account data. |
| `charUnboundMgr.GetCohortsForUser` | Character screen | Implemented | Handler present; exercised in recorded character walk. |
| `charUnboundMgr.GetCharacterLockType` | Character screen | Implemented | Handler present; exercised in recorded character walk. |
| `charUnboundMgr.GetCharOmegaDowngradeStatus` | Character screen | Implemented | Session selection data exposes current clone-grade status; exercised by focused login walk. |
| `charUnboundMgr.GetCharacterInfo` | Character selection/support dialogs | Implemented | Returns own account rows and rejects cross-account lookup. |
| `charUnboundMgr.GetValidRandomName` | Character creation | Implemented | Generated name validation uses server catalog/state; exercised by walk. |
| `charUnboundMgr.ValidateNameEx` | Character creation | Implemented | Name validation and conflict checks; exercised by walk. |
| `charUnboundMgr.CreateCharacterWithDoll` | Character creation | Implemented | Persists character, ship, starter state and initialization hooks; walk covers slot limit and starter outcomes. |
| `charUnboundMgr.SelectCharacterID` | Enter world | Implemented | Enforces account ownership and creates selected session; walk checks invalid and valid choices. |
| `charUnboundMgr` delete preparation/cancel/delete | Character management | Partial | Some deletion routes exist; expiry/purge and retry semantics are not covered by the login walk. |
| `multiLoginBlocker.Login` | SSO computer/account login gate | Implemented | Host live-session policy is tested in `docs/reference/compat/DEVIATIONS.md` (same-account replacement behavior). |
| `authentication.AccruedTime` | Account/clone UI initialization | Partial | Current service default returns an empty list when no provider is wired. Wire to persisted subscription/account state and test client consumer. |
| `authentication.AmUnderage` | Account gate | Partial | Current default is false; Host must use authoritative account age state if the deployment supports age restrictions. |
| `authentication.SetLanguageID` | Startup localization choice | Implemented | Stores the selected language in session. |
| `userSvc.GetUserToken` | Authentication/token consumers | Partial | Present in `RedeemService`; verify whether it returns a scoped live token or an empty default in Host registration. |
| `userSvc.GetUserName` / `GetCreateDate` | Account display and character support UI | Implemented | Host account/character providers. |
| `subscriptionMgr.GetCloneGrade` | Character screen and clone UI | Implemented | Character/account subscription state is read by Host provider. |
| `subscriptionMgr.GetSubscriptionTime` | Subscription UI after entry | Partial | Route exists; current provider shape/state needs direct client-flow coverage. |
| `survey.GetActiveSurveyID` | Character screen | Partial | Route is present; verify campaign configuration and result against current server data. |
| `loginCampaignManager` / `seasonalLoginCampaignManager` | Login reward surfaces | Partial | Service names are registered; campaign state and rewards depend on configured data. Verify claim mutation, ownership, and persistence before counting these as complete. |
| `milestoneMgr.ProcessCharacterLogon` / `ClaimRewards` | First logon and reward claims | Partial | State is backed by the GameStore projection; verify idempotent claim and post-restart behavior. |
| `rewardMgr.GetDelayedRewardsByGroupIDs` | Login reward UI | Partial | Route exists; verify account/character scope and persisted reward results. |
| `config.GetStationSolarSystemsByOwner` | Static/config startup | Implemented | Host reads server configuration/static catalog. |
| `localizationServer.GetAllTextChanges` | Localization startup | Partial | Route is present; configured text-change data and locale-specific coverage remain to be established. |
| `alert` / `securityMgr` initial calls | Startup gates | Partial | Concrete routes are registered; the client trace must identify the exact request and expected gate state for each deployment. |
| `chatAuthenticationService.GetAuthenticationToken` | First world entry chat initialization | Partial | Route exists; validate token ownership, expiry and client notification/connection follow-up. |
| `authentication` / `sessionMgr` notifications | Login and world transition | Implemented | Session initialization and character-change notifications are covered by headless walk. |
| `userSvc.UserLogOffCharacter` | Return to character selection | Implemented | Session remains connected without a selected character; a subsequent selection is covered in `test_login_to_character.py`. |

Inventory baseline: 38 tracked service/RPC entries (24 implemented, 14 partial, 0 missing routes). The partial count includes existing methods whose default/provider behavior still needs completion; route registration alone does not establish completion.

## Verification boundary

The fixture-backed headless walk is protocol-level verification against EveJS-recorded shapes and values; it is not a graphical-client run. The client-side files above establish callsites. Entries marked `Partial` remain open until their provider semantics and the corresponding client walk are recorded. This inventory is intentionally limited to login and initial world entry; later gameplay RPCs listed in `docs/reference/spec/services.tsv` are outside its scope.
