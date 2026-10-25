# Selected RPC Host coverage handoff

## Change

The production route owners already register this set: `CoreServices.Create` provides ping, authentication, and subscription services; `ProgressRpc` provides AIR NPE using the character store. `HostComposition` injects authentication callbacks backed by `PgAccruedTimeStore` and `PgAccountDirectory`. Do not register another default authentication instance from `CharacterServicesComposition.CoreRpc()`: CI showed it overrides the configured callbacks when the character services are present.

Authentication string arguments now use `JsSemantics.StringValue`, which accepts the client wire's `PyBytes` as well as Unicode/token values. The existing nonempty token, length, argument-count, and language validation remains in place.

`LoginOnboardingSubscriptionRpcHostWireTests` covers Air NPE get/skip/get, authentication accrued time and underage replies, language session updates, subscription time and clone grade, and ping through the real TCP `HostServer` and `PacketDispatcher`. Calls include transport `machoVersion`; the existing `TransportMetadataHostWireTests` also covers it for `air_npe.is_air_npe_enabled`, `pingService.Ping`, and `subscriptionMgr.GetCloneGrade`.

`UserSvcRpcHostWireTests` registers the production `RedeemService` in the same real Host path and exercises resculpt, item and soulbound training activation, downvote with and without feedback, and a subsequent training-slot read. The test uses in-memory callback implementations to avoid PostgreSQL; persistence and restart are not established by this wire test.

## Whole-tree V24.01 client trace and EveJS comparison

| Rows | Caller and reply consumer in `/home/user/eve-logic` | EveJS v0.12.9 |
| --- | --- | --- |
| 00866, 00869; 00867, 00868 | `storylines/client/airnpe.py:57-60, 70-105` calls all three methods. The enabled flag gates character creation in `eve/client/script/ui/login/characterCreationContainer_new.py:741` and `characterCreationContainer.py:893`. State is cached and drives tutorial state checks; `OnAirNpeStateChanged` updates that cache and completed/skipped UI in `storylines/client/airnpe.py:63-69`. | `server/src/services/npe/airNpeService.js:30-34` reads the character tutorial snapshot; `get_air_npe_state` returns its `airNpeState`. |
| 00899, 00900; 00901, 00902 | `eve/client/script/ui/login/antiaddiction.py:207-212` calls `AmUnderage(jwtToken)` and `AccruedTime()`; accrued rows feed `Session.PreLoad` at `:118`. | `server/src/services/login/authenticationService.js:236-250` returns the configured age flag and accrued-session rows. |
| 02291 | `carbon/client/script/remote/connectionService.py:188-189` calls `pingService.Ping()` during idle transport checks and ignores the result. | `server/src/services/ping/pingService.js:18-21` answers null. |
| 02897, 02898 | `omega/client/clone_grade_svc.py:124-125` caches `GetCloneGrade()`; Omega logic compares the result with the Omega grade at `:129-131`. | `server/src/services/subscription/subscriptionMgrService.js:145-149` returns the current clone grade. |
| 02899, 02900 | `eve/client/script/ui/shared/neocom/charsheet/plexPanel.py:145-151` displays a truthy expiry time as the Omega expiration date. | `server/src/services/subscription/subscriptionMgrService.js:135-143` returns FILETIME or null. |
| 00907, 00908 | `eve/client/script/parklife/gameui.py:770-771` sends the selected language, then updates local session and preferences; no RPC result is consumed. | `server/src/services/login/authenticationService.js:252-266` normalizes the language and updates session attributes. |
| 02956, 02957 | `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:387-391` calls after confirmation and ignores the reply. | `server/src/services/user/userService.js:1097-1102` returns true. |
| 02958-02961 | `eve/client/script/ui/shared/activateMultiTraining.py:227-230` calls item or soulbound activation, then reloads the slot state; the RPC reply is not used. | `server/src/services/user/userService.js:770-816` returns the updated slot list, consumes matching redeem certificates, and announces training changes. |
| 02962-02965 | `eve/client/script/ui/shared/systemMenu/customersatisficationcont.py:41-45, 89-90` calls downvote with or without feedback; it ignores the reply and updates the local vote/tooltip. | `server/src/services/user/userService.js:1109-1113` returns true without storing a vote. |

Whole-tree search of `/home/user/eve-logic` found no additional remote callers for these service methods. Duplicate row IDs name repeated observations of the same method contract.

## Verification and remaining work

Local verification: `dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~Login_and_onboarding_rpcs_keep_client_wire_contracts_with_transport_kwargs|FullyQualifiedName~User_service_activation_and_feedback_rpcs_are_dispatched_through_host_wire' -p:UseSharedCompilation=false -m:1` passed both focused Host wire tests with zero skips. The auth test also confirms that an empty JWT receives an error response. No PostgreSQL database was used. The wire checks do not use a graphical client.

Selected Actions test run `40231173241` passed for commit `5bc385bb6004460c20b5272e321ba3a32a7853c9`, with no failed tests or jobs. Push-triggered CI run `40231095991` failed: duplicate default authentication registration overrode the configured persistence callbacks and increased the RPC inventory by six. The duplicate `CoreRpc` registrations were removed in the follow-up fix. Persistence and restart were source-reviewed in the existing production store callbacks; the database-backed test lane was outside this batch.
