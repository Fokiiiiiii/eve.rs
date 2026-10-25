# Host coverage handoff: AIR NPE, authentication, subscription, and userSvc RPCs

## Completed coverage

- Added `LoginCompatibilityRpcHostWireTests.Air_and_login_support_rpcs_return_client_shapes_through_the_host`. It sends AIR NPE, authentication, language, ping, clone-grade, and subscription-time calls through the TCP Host and registered RPC services. Each call includes the V24 transport keyword `machoVersion=1`. It checks the AIR state and skip notification, the four language session attributes, authentication replies, Ping's None reply, and an active subscription's clone grade and FILETIME.
- Existing `TransportMetadataHostWireTests.Client_transport_macho_version_is_not_an_application_keyword_for_core_read_calls` covers `is_air_npe_enabled`, `Ping`, and `GetCloneGrade` with the transport keyword and confirms an unrelated keyword remains rejected.
- Existing `UserSvcRpcHostWireTests.User_service_activation_and_feedback_rpcs_are_dispatched_through_host_wire` covers resculpt activation, both CastDownvote forms, item and soulbound multi-training activation, the returned slot/time list, and the subsequent training-list read through the TCP Host.
- The duplicated inventory entries for the same client methods are covered by these method-level wire checks.

## Whole-tree V24.01 client trace and consumers

The full `/home/user/eve-logic` tree was searched with Serena pattern search and CBM graph queries. These are the caller and consumer paths found:

- AIR NPE: `storylines/client/airnpe.py:58-61,71-105` caches the enabled flag and state, treats COMPLETED and SKIPPED as terminal, and calls skip only from the active state. Its notification handler updates the cache and reveals the tutorial UI at lines 40-51. Character creation branches on enabled state in `eve/client/script/ui/login/characterCreationContainer_new.py:741-742` and `characterCreationContainer.py:893-894`. Other skip callers found across the tree include `nodegraph/client/actions/tutorial.py:13,37,87,123`, `eve/client/script/ui/shared/systemMenu/systemmenu.py:429`, `infoPanels/infoPanelOperations.py:190`, and `storylines/client/objectives/trackers/airnpetracker.py:23`.
- Anti-addiction: `eve/client/script/ui/login/antiaddiction.py:17,205-212` only checks underage status for the optic region and preloads each accrued session returned by the RPC at line 118.
- Ping: the only remote caller found is `carbon/client/script/remote/connectionService.py:178-191`; it ignores the result in its idle ping loop.
- Clone grade: `omega/client/clone_grade_svc.py:112-130` caches the RPC result and compares it to the Omega grade. The whole-tree callers of the shared accessor are `agentinteraction/rewardcont.py:245`, `loginrewards/client/seasonalLoginCampaignService.py:212`, `eve/common/script/util/industryCommon.py:488-491`, `mercDenUiController.py:81`, `crimewatchSvc.py:130`, `skillQueueSvc.py:82`, and `agents.py:558`; the result gates reward, industry, crimewatch, skill-queue, planetary, and agent behavior in the accessor consumers at `clone_grade_svc.py:130-170`.
- Subscription time: `eve/client/script/ui/shared/neocom/charsheet/plexPanel.py:57,145-158` formats a returned expiry as the Omega time or switches to the Alpha label for None; subscription update notifications also refresh the same display.
- Language: `eve/client/script/parklife/gameui.py:768-774` sends the selected language while logged in, then updates the local session, preferences, and OS language.
- User service: `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:387-391` sends resculpt after confirmation and ignores its boolean reply. `activateMultiTraining.py:132-198,221-236` reads current slots, activates either an item or soulbound tokens with a requested training slot, and reloads the slots. Both downvote callers are in `customersatisficationcont.py:41-50,85-91`; they ignore the RPC reply and update the local vote/tooltip state.

## EveJS v0.12.9 comparison

- `airNpeService.js:25-51` returns the tutorial enabled flag and current state; skip marks state and sends `OnAirNpeStateChanged` only after a successful change. eve.rs follows the same response and notification contract and uses its existing persisted character-state provider in production.
- `authenticationService.js:236-266` returns configured accrued-session rows and underage status; language change updates the same four session keys and returns null. eve.rs uses those client shapes and its Host test checks the transport keyword.
- `pingService.js:18-21` returns null, matching the client ignoring the reply.
- `subscriptionMgrService.js:135-149` returns the expiry FILETIME or null and the session clone grade. eve.rs derives both from the shared Omega license state.
- `userService.js:770-816,1097-1102,1109-1113` returns the active MCT slot rows, consumes valid soulbound MCT certificates and notifies training changes, and returns true for resculpt/downvote. The existing Host test checks the client-used slot tuple/list shape and both downvote inputs.

## Checks and remaining limits

- Local Host project build: passed after correcting a test-only type assertion.
- Focused Host wire test `Air_and_login_support_rpcs_return_client_shapes_through_the_host`: passed, 1 passed and 0 skipped.
- The first focused attempts exposed incorrect test expectations for the actual CoreServices authentication reply, default Omega session, and wire-encoded notification name; those expectations were corrected before the passing run.
- No PostgreSQL database or graphical client was used. The new AIR callback fixture verifies Host ordering and notification wire encoding; production persistence remains covered by the existing provider path rather than this in-memory wire fixture.

## Inventory note

The task's RPC IDs for GetCloneGrade/GetSubscriptionTime and the later userSvc methods differ from those currently attached to those service/method names in `docs/project/service-audit/EVE_RS_COVERAGE.csv` (for example, the CSV currently maps 02897-02900 to tourneyMgr and 02956-02965 to voteManager). This handoff follows the task's explicit service, method, and V24.01 client callsites.
