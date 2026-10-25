# Petitioner RPC Host coverage: RPC-02249–RPC-02274

## Client and EveJS trace

The whole V24.01 `/home/user/eve-logic` tree was searched for every method in this batch. Besides the calls listed below, the search found no further petition-service caller or result consumer. The separate planet helper `planetUtil.IsSerenity` is local and is not a petitioner RPC.

| Row | V24.01 call and consumer (`/home/user/eve-logic`) | EveJS v0.12.9 |
|---|---|---|
| RPC-02249 | `eve/client/script/ui/shared/neocom/petition.py:574`; the window iterates `queueName` and `queueID` to create tabs at :220-221. | `server/src/services/support/petitionerService.js:156-159` returns an empty list. |
| RPC-02250 | Same `GetQueues` call at `petition.py:574`; the returned collection is cached and rendered by :220-221. | Same empty-list handler, :156-159. |
| RPC-02251 | `petition.py:120`; the caller checks list length and reads `petitionID`, `text`, and `messageID` at :121-124. | `Handle_GetUnreadMessages`, :127-130, returns an empty list. |
| RPC-02252 | Same caller and array consumer, `petition.py:120-124`. | Same empty-list handler, :127-130. |
| RPC-02253 | `eve/client/script/ui/shared/neocom/petitionWindow.py:158`; country is part of the `billingCategories` lookup key at :159-161. | `Handle_GetUserCatalogCountry`, `petitionerService.js:190-193`, returns `US`. |
| RPC-02254 | Same country lookup, `petitionWindow.py:158-161`. | Same `US` response, :190-193. |
| RPC-02255 | `eve/client/script/ui/shared/neocom/help.py:117`; the returned string is passed to `webbrowser.open_new`. | `Handle_GetZendeskJwtLink`, `petitionerService.js:121-125`, delegates to `buildZendeskLink` (:80-86). |
| RPC-02256 | `eve/client/script/ui/shared/neocom/petitionWindow.py:106`; returned string is opened in the browser. | Same link handler and helper, :121-125 and :80-86. |
| RPC-02257 | Same `petitionWindow.py:106` caller listed for this row. Whole-tree search also found `notifications/client/notificationCenter.py:370`, which passes `notification.data['text']` as `ticketID` and opens the returned string. | The same helper reads `kwargs.ticketID` and creates a ticket URL, :80-86. |
| RPC-02259 | `eve/client/script/ui/shared/neocom/help.py:83`; truthy result controls the old-open-petitions label at :83-84. | `Handle_HasOpenTickets`, `petitionerService.js:116-119`, returns false. |
| RPC-02260 | `eve/client/script/ui/shared/neocom/petition.py:212`; truthy result allows the petition button on Serenity. | `Handle_IsSerenity`, `petitionerService.js:101-104`, returns false. |
| RPC-02261 | `eve/client/script/ui/shared/neocom/help.py:89`; gates the English help-center button. | `Handle_IsZendeskEnabled`, `petitionerService.js:106-109`, returns true. |
| RPC-02262 | `eve/client/script/ui/shared/neocom/petitionWindow.py:125`; gates category 29's help-center replacement for English. | Same true response, :106-109. |
| RPC-02263 | Same category/language gate, `petitionWindow.py:125`. | Same true response, :106-109. |
| RPC-02264 | `eve/client/script/ui/shared/neocom/help.py:58`; selects the Help Center option in the bug-report dialog. | `Handle_IsZendeskSwapEnabled`, `petitionerService.js:111-114`, returns true. |
| RPC-02265 | `help.py:81`; selects Help Center versus the legacy petition action. | Same true response, :111-114. |
| RPC-02266 | `eve/client/script/ui/shared/neocom/petition.py:212`; combined with `IsSerenity`, controls the create-petition button. | Same true response, :111-114. |
| RPC-02267 | `petition.py:132`; caller records the ID in a local read list before the RPC; the return value is ignored. | `Handle_MarkAsRead`, `petitionerService.js:132-137`, returns null. |
| RPC-02268 | Same local de-duplication and ignored return, `petition.py:128-133`. | Same null acknowledgement, :132-137. |
| RPC-02269 | `eve/client/script/ui/shared/neocom/petitionWindow.py:179`; negative result codes -1 through -4 show distinct refusal messages; nonnegative results continue to petition creation (:179-201). | `Handle_MayPetition`, `petitionerService.js:198-204`, returns `MAY_PETITION_DISABLED=-4` (:17). |
| RPC-02270 | Same input gate and result consumer, `petitionWindow.py:179-201`. | Same -4 refusal, :198-204 and :17. |
| RPC-02271 | `eve/client/script/ui/shared/neocom/petition.py:921`; sends petition ID, message and the petitionee comment checkbox. The caller handles the `PetitionClosed` user error; it ignores a success payload. | `Handle_PetitioneeChat`, `petitionerService.js:237-243`, returns null and records an audit event. |
| RPC-02272 | Same three-argument caller and error path, `petition.py:921-929`. | Same null acknowledgement, :237-243. |
| RPC-02273 | `petition.py:978`; claimed-petition chat sends only petition ID and message. The caller handles `PetitionClosed` and ignores a success payload (:978-984). | Same handler accepts the two supplied arguments and returns null, :237-243. |
| RPC-02274 | Same two-argument claimed-petition caller, `petition.py:978-984`. | Same null acknowledgement, :237-243. |

## eve.rs behavior and regression

`dotnet/src/Eve.Services.Core/PetitionerService.cs` registers the service methods, with client-compatible replies in `DefaultResponse` (list replies, country, URL, feature booleans, -4 refusal, and None acknowledgements). These defaults match the EveJS behavior above. There is no ticket or petition state provider in either reference path, so this batch does not add synthetic persistence or notifications.

The notification caller supplies the ticket ID as text. EveJS's shared `normalizeNumber` accepts numeric strings (`server/src/services/_shared/serviceHelpers.js:423-464`). The Host's Marshal decoder can present that wire value as a byte or string value; the previous link code used `JsSemantics.NumberValue`, which accepts only numeric Marshal types and sent the generic URL. `ZendeskLink` now parses text with invariant floating-point rules, preserves numeric integer/long/real inputs, truncates positive fractional values like EveJS, and keeps the generic URL for invalid or nonpositive values.

`dotnet/tests/Eve.Host.Tests/PetitionerHostWireTests.cs` registers the actual `CoreServices.Create` services in `ServiceRegistry` and sends encoded petitioner requests through `PacketDispatcher`. It covers every distinct method in the rows, no-ticket and text-ticket URL variants, invalid ticket fallback, the two observed `PetitioneeChat` argument shapes, and a transport `machoVersion` keyword. It asserts each decoded CallRsp and client-consumed response shape.

## Verification

- Pre-merge `Eve.Services.Core` build: passed, 0 warnings and 0 errors.
- Post-merge `Eve.Host.Tests` build: passed, 0 errors; one transient MSB3026 DLL copy lock from a concurrent build retried successfully.
- Focused `Petitioner_client_methods_answer_through_registered_host_wire`: passed locally, 1 test, 0 skipped.
- GitHub Actions agent-test run 40207478662 for commit de4f8401: passed; 1 test job, no failed tests or jobs.
- Push-triggered `ci.yml` run 40207469674 for de4f8401: in progress at last check.
- Graphical-client verification: not performed.
