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
| `subscriptionMgr.GetSubscriptionTime` | Subscription UI after entry | Implemented | `SubscriptionMgrService` derives expiry and remaining days from the persisted account omega expiry. |
| `survey.GetActiveSurveyID` | Character screen | Partial | Route is present; verify campaign configuration and result against current server data. |
| `loginCampaignManager` / `seasonalLoginCampaignManager` | Login reward surfaces | Partial | Service names are registered; campaign state and rewards depend on configured data. Verify claim mutation, ownership, and persistence before counting these as complete. |
| `milestoneMgr.ProcessCharacterLogon` / `ClaimRewards` | First logon and reward claims | Implemented | The Host uses `LoginMilestoneStore`; the milestone live test covers the logon timer and claim path. |
| `rewardMgr.GetDelayedRewardsByGroupIDs` | Login reward UI | Implemented | Host reads pending loyalty points from the live incursion director; groups without pending rewards are omitted. |
| `config.GetStationSolarSystemsByOwner` | Static/config startup | Implemented | Host reads server configuration/static catalog. |
| `localizationServer.GetAllTextChanges` | Localization startup | Partial | Route is present; configured text-change data and locale-specific coverage remain to be established. |
| `alert` / `securityMgr` initial calls | Startup gates | Partial | Concrete routes are registered; the client trace must identify the exact request and expected gate state for each deployment. |
| `chatAuthenticationService.GetAuthenticationToken` | First world entry chat initialization | Partial | Route exists; validate token ownership, expiry and client notification/connection follow-up. |
| `authentication` / `sessionMgr` notifications | Login and world transition | Implemented | Session initialization and character-change notifications are covered by headless walk. |
| `userSvc.UserLogOffCharacter` | Return to character selection | Implemented | Session remains connected without a selected character; a subsequent selection is covered in `test_login_to_character.py`. |

## Post-selection and first-screen service detail

The original table above covers connection, authentication, and character entry. These additional methods cover the requested character, inventory, wallet, settings, mail, calendar, organization, map, store, and first-screen state. Some are called when a panel opens rather than automatically during login. The live headless login walk does not exercise the complete graphical interface; partial rows stay open until their data provider and client-visible walk are covered.

| Service / method | V24.01 caller or phase | Status | Evidence / remaining work |
|---|---|---|---|
| `config.GetBlackListedPlanets` | Post-selection bootstrap (`ConfigService.cs`; login walk checks shape) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `ProjectDiscovery.is_enabled` | Post-selection bootstrap (`ProjectDiscoveryService.cs`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.MachoResolveObject` | Global assets prime for container 10002 (`CharMgrService.cs`) | Partial | Host wrapper regression remains open (TODO global-assets entry). |
| `jumpCloneSvc.MachoResolveObject` | Jump-clone initialization (`JumpCloneService.cs`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `ProjectDiscovery.initialize_tutorial_status` | Tutorial startup (`ProjectDiscoveryService.cs`; TODO CH-4) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `account.GetCashBalance` | Wallet display, account service | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `account.GetTransactions` | Personal/corporate wallet history (`wallet` client panels) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `accessGroupBookmarkMgr.GetMyActiveBookmarks` | Bookmark prime (`bookmarkSvc.py:41,163`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `agentMgr.GetAgents` | Agent and mission entry (`agents.py:92`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `agentMgr.GetCompletedCareerAgentIDs` | Career-agent state (`agents.py:219`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `agentMgr.GetDisabledMissions` | Agent mission availability (`agents.py:359`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `agentMgr.GetMyJournalDetails` | Mission journal (`journal.py:312,331`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `agentMgr.GetMyEpicArcStatus` | Epic arc entry (`epicArcSvc.py:32`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `agentMgr.GetPlausibleMissionIDs` | Mission candidate query (`evemissions/client/qa_tools.py:96`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `contractProxy.GetLoginInfo` | Contract window startup (`contracts.py:191`; TODO contracts) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `contractProxy.GetMyContractEscrow` | Contract progression notifier (`contracts.py:83,770-774`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `structureHangarViewMgr.GetMyHangarViewState` | Hangar view initialization; persisted callback wired | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `map.GetCurrentSovData` | Sovereignty map overlay | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `map.GetMyExtraMapInfo` | Map overlays for the logged-in character | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `vaultManager.get_account_balance` | PLEX/vault balance (`vgsClient.py:30`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `storeManager.get_offers` | In-game store catalog (`vgsClient.py:36`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `storeManager.get_categories` | In-game store navigation (`vgsClient.py:45`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `storeManager.get_products` | In-game store products (`vgsClient.py:54`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `achievementTrackerMgr.HasEverWarped` | Space camera startup (`enterSpaceCamera.py:40`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `achievementTrackerMgr.GetCompletedAchievementsAndClientEventCount` | Achievements state (`achievementSvc.py:129`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `achievementTrackerMgr.UpdateClientAchievmentsAndCounters` | Persists client achievement state (`achievementSvc.py:229`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `skillMgr2.GetMySkillHandler` | Resolves character skill handler (`skillsvc.py:128-131`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `skillHandler.GetSkills` | Character skill cache (`skillsvc.py:133-137`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `skillHandler.GetSkillQueueAndFreePoints` | Training queue and free points (`skillQueueSvc.py:75-78,116-117`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `skillHandler.GetAttributes` | Character attributes after implant/booster reads (`skillsvc.py:220-224`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `skillHandler.GetImplants` | Active character implants (`skillsvc.py:220-224`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `jumpCloneSvc.GetCloneState` | Jump clones and clone implants (`clonejumpsvc.py:52-79`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `jumpCloneSvc.GetStationCloneState` | Station clone list (`clonejumpsvc.py:90-94`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `jumpCloneSvc.GetShipCloneState` | Active ship clone list (`clonejumpsvc.py:82-88`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.GetPublicInfo` | Character profile and agent entry (`characterOverviewPanel.py:47`; `agents.py:282,520`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.GetPaperdollState` | Station character appearance (`station/base.py:197`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.GetSettingsInfo` | Client settings signature (`carbonui/services/settingsSvc.py:148`); signing key remains open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.GetCharacterSettings` | Character settings load (`characterSettingsSvc.py:31-33`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.SaveCharacterSetting` | Character settings write (`characterSettingsSvc.py:49`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.DeleteCharacterSetting` | Character settings delete (`characterSettingsSvc.py:54-56`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `sharedSettingsMgr.GetStoredSharedSetting` | Shared setting read (`sharedSettings/util.py:13`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `sharedSettingsMgr.StoreSettingLinkAndGetID` | Shared inventory preset (`filterSvc.py:449-450`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.GetContactList` | Personal address book (`addressbookService.py:207-217`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.GetOwnerNoteLabels` | Owner-note list (`notepad.py`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.GetOwnerNote` | Saved owner note (`notepad.py:247,518`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `charMgr.GetNote` | Character note (`panelNotes.py:35`; `notepad.py:509,557`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `invbroker.GetInventoryFromId` | Character inventory prime (`invCache.py:GetInventoryFromId`) | Implemented | Host bound-object regression in `HostServerTests`; character inventory scenarios in `test_login_to_character.py`. |
| `standingMgr.GetStandingTransactions` | Standings history (`standingsvc.py:174-178`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `standingMgr.GetStandingCompositions` | Standings composition (`standingsvc.py:282-283`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `LPSvc.GetAllMyCharacterWalletLPBalances` | Personal LP balances (`loyaltyPointsWalletSvc.py:31`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `LPSvc.GetAllMyCorporationWalletLPBalances` | Corporation LP balances (`loyaltyPointsWalletSvc.py:37`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `LPStoreMgr.GetAvailableOffersFromCorp` | LP store offers (`lpStoreSvc.py:98`); offer provider is data-dependent | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `LPStoreMgr.TakeOfferForCharacter` | LP purchase (`lpStoreSvc.py:249`); full client flow remains open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.GetResponsesForCharacter` | Calendar response prime (`eveCalendarsvc.py:250-257`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.GetResponsesToEvent` | Calendar event response read (`eveCalendarsvc.py`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.CreatePersonalEvent` | Personal calendar event (`eveCalendarsvc.py:172-185`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.CreateCorporationEvent` | Corporation calendar event (`eveCalendarsvc.py:191-193`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.CreateAllianceEvent` | Alliance calendar event (`eveCalendarsvc.py:191-193`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.EditPersonalEvent` | Personal event edit (`eveCalendarsvc.py:205-217`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.EditCorporationEvent` | Corporation event edit (`eveCalendarsvc.py:205-217`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.EditAllianceEvent` | Alliance event edit (`eveCalendarsvc.py:205-217`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.DeleteEvent` | Calendar event delete (`eveCalendarsvc.py`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.UpdateEventParticipants` | Calendar invitations (`eveCalendarsvc.py:196-202`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarMgr.SendEventResponse` | Calendar response mutation (`eveCalendarsvc.py`); notification producer open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarProxy.GetEventList` | Calendar month view (`eveCalendarsvc.py:235-247`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `calendarProxy.GetEventDetails` | Calendar event details (`eveCalendarsvc.py:259-262`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `mailMgr.SyncMail` | Initial mailbox sync (`mailSvc.py:124,157`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `mailMgr.GetMailHeaders` | Mailbox header hydration (`mailSvc.py:192-204`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `mailMgr.GetBody` | Message body (`mailSvc.py:512`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `mailMgr.GetLabels` | Mailbox labels (`mailSvc.py:678`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `notificationMgr.GetAllNotifications` | Notification list (`notificationSvc.py:93`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `notificationMgr.GetByGroupID` | Notification group view (`notificationSvc.py:60,139`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `notificationMgr.GetUnprocessed` | Unread notification count (`notificationSvc.py:107`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `corpmgr.GetPublicInfo` | Corporation profile (`corporationInfoWindow.py:49,184`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `corpmgr.GetCorporationIDForCharacter` | Character affiliation (`characterInfoWindow.py:111`; `infoContentBase.py:472`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `corporationSvc.GetCorpInfo` | Corporation information panel (`infosvc.py:1166`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `allianceRegistry.GetAlliancePublicInfo` | Alliance profile (`all_cso.py:136`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `allianceRegistry.GetAlliance` | Alliance detail (`all_cso_alliance.py:53`) | Implemented | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `fwCharacterEnlistmentMgr.GetMyEnlistment` | Logged-in character faction-war state (`fwEnlistmentSvc.py:80`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `fleetMgr.AddToWatchlist` | Fleet watchlist changes (`parklife/fleetSvc.py:668`); full session list flow open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `fleetMgr.RemoveFromWatchlist` | Fleet watchlist changes (`parklife/fleetSvc.py:719`); full session list flow open | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |
| `fleetMgr.RegisterForDamageUpdates` | Fleet watchlist damage updates (`parklife/fleetSvc.py:795`) | Partial | Evidence and remaining provider work are linked in the caller path and current feature entries in `docs/project/TODO.md`. |

Inventory baseline: 120 tracked rows: 68 implemented, 52 partial, and 0 missing routes. This combines 38 connection/character-entry entries (27 implemented and 11 partial) with 82 post-selection/first-screen entries (41 implemented and 41 partial).

## Verification boundary

The fixture-backed headless walk is protocol-level verification against EveJS-recorded shapes and values; it is not a graphical-client run. The client-side files above establish callsites. Entries marked `Partial` remain open until their provider semantics and the corresponding client walk are recorded. Still to enumerate from V24.01 callers and the complete client sequence: fitting and ship setup calls, remaining fleet/watchlist reads and bound methods, chat channel list and membership calls, full store purchase methods, and any additional sovereignty/map requests made before the UI is usable. The broad route manifest is not itself a caller inventory.
