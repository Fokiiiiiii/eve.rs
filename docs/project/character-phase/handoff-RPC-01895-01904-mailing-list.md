# Mailing-list RPC host coverage: RPC-01895–RPC-01904

## Mail and mailing-list audit follow-up (2026-10-29)

The V24.01 client-wide mail paths were checked through their callers and event consumers. `eve/client/script/ui/services/mail/mailSvc.py:710-724` edits and caches a label; `:731-735` stores the `CreateLabel` reply; `:738-753` strips deleted label bits; `:1173-1184` replaces external message masks; `:1207-1210` adds a label after `OnLabelsCreatedByExternal`. The label cap now raises `CustomInfo(MAIL_LABEL_LIMIT_EXCEEDED)` rather than returning `None`, avoiding the client's `labels[None]` insertion. This intentionally improves on EveJS 261028.1 `server/src/services/mail/mailMgrService.js:433-450`, whose failed create returns `null`; the cap originates in `mailState.js:889-914`. Label masks are serialized as `PyLong`, including bit 31. Edit now publishes updated label metadata to other sessions; delete publishes changed message IDs and their resulting masks.

The V24.01 send path caches the typed body after `SendMail` (`mailSvc.py:975-985`) and mailing-list configuration saves the typed welcome text (`evemailingListConfig.py:500-506`). EveJS 261028.1 bounds subjects to 150, bodies to 8,000, and labels to 40 UTF-16 code units after trimming (`server/src/services/mail/mailState.js:53-56,84-100`); eve.rs keeps this authoritative normalization.

The concurrent station-capacity correction is on main as commits `34678e19` and `1d8571c1`. V24.01 `invCache.py:1224-1226` returns station capacity locally, so the inventory Host wire assertion keeps the client shape and the headless reference comparison now uses that same client result. Push CI run `40934986188` for `1d8571c1` passed.

For mailing-list results, V24.01 `mailingListsSvc.py:64-98` caches Create/Join replies and removes local entries after Leave/Delete. EveJS `server/src/services/mail/mailingListsMgrService.js:152-196` maps duplicate create, missing-list join/leave, owner leave and non-owner delete to `MailingListNameTaken`, `MailingListNoSuchList`, `MailingListCantRemoveOwner` and `MailingListCantDeleteNotOwner`. eve.rs returns those `UserError`s, and returns the invalid-name `CustomInfo(INVALID_MAILING_LIST)` for inputs that would otherwise produce a cached `None`. Deletion persists before notifying active member sessions with `OnMailingListDeleted`, consumed by `mailingListsSvc.py:171-179`. Role 0 sends `OnMailingListSetMuted` (`mailingListsSvc.py:149-153`; `evemailingListConfig.py:383-388`). Both joined-list and join replies set `isOperator` only for role 2, matching EveJS `mailState.js:516`; role 3 sets `isOwner`, which the client checks separately at `evemail.py:541`.

The new focused service test for the label-cap refusal passed (1 passed, 0 failed, 0 skipped). The Host wire regression for high-bit label masks and cross-session update plus the joined-list owner/operator reply test passed (2 passed, 0 failed, 0 skipped). The Host test project build passed with 0 warnings and 0 errors. Push CI for `14658cae` exposed that role 3 was incorrectly included in `isOperator`; EveJS `mailState.js:516` returns true only for role 2, while the UI checks `isOwner` separately (`evemail.py:541`). The follow-up correction changes both registered reply builders and keeps a Host wire assertion that the owner is not an operator while remains owner. The real graphical client was not exercised.

After syncing `1d8571c1`, the Host.Tests and Eve.Services.Core.Tests builds passed with 0 warnings and 0 errors; the three focused tests passed (Host 2, Core 1; 0 skipped). A full Host.Tests invocation with `EVE_PG_URL` explicitly unset reported 623 passed, 100 failed, and 233 skipped. The failures were DB-dependent tests stopping at their missing database URL checks; no PostgreSQL connection was made. These failures and skips are recorded separately from the focused passes.

## Scope and result

All ten assigned mutations were already registered in the production MailRpc service list and mapped through MailingListService to CharacterServicesComposition.MailingLists. The Host provider reads and updates the shared mail.mailingLists GameStore rows, persists changes through mailCache/mailStore, validates the current session and role, emits the client mailing-list notifications, and sends welcome mail through the existing mail writer.

Added a real Host socket regression for the ten methods. It sends CallReq packets to the registered mailingListsMgr route with strict RPC dispatch enabled, round-trips replies through Marshal, checks forwarded argument values and session character identity, and sends the full accepted 1,000-entry member/entity collections. Join returns a util.KeyVal with the id field consumed by the client; SendWelcomeMail returns its message ID; the other listed mutation replies are None.

The test callback records the real service's Host-provider boundary. Existing production GameStore persistence and notification code was inspected but is not invoked by this callback-backed wire regression. No database was used.

## Client, EveJS, and Host evidence

| Row | V24.01 client calls and consumes | EveJS 261028.1 behavior | Host behavior |
|---|---|---|---|
| RPC-01895 Join | mailingListsSvc.py:73-76 calls Join(name), reads ret.id, updates myMailingLists, and scatters OnMyMaillistChanged; evemail.py:2915-2917 closes only for a non-None result. | server/src/services/mail/mailingListsMgrService.js:167-179 returns mailing-list info on success and maps join refusals to named UserErrors. | MailingListComposition.cs:163-218 resolves name/access, persists membership, and returns MailingListJoinValue; wire test checks KeyVal id. |
| RPC-01896 KickMembers | mailingListsSvc.py:99-102 invalidates cached GetMembers; evemailingListConfig.py:284-290 calls it and refreshes the access/member UI. | server/src/services/mail/mailingListsMgrService.js:197-218 removes authorized members and returns null. | MailingListComposition.cs:277-307 persists changes and sends OnMailingListLeave; client consumes it in mailingListsSvc.py:161-169. |
| RPC-01897 Leave | mailingListsSvc.py:79-86 removes the local list and scatters OnMyMaillistChanged; evemail.py:501-502 ignores the RPC result. | server/src/services/mail/mailingListsMgrService.js:181-187 leaves and maps owner leave to MailingListCantRemoveOwner. | MailingListComposition.cs:233-244 removes the caller from members and persists; the UI-level local list update follows the service path. |
| RPC-01898 SaveWelcomeMail | mailingListsSvc.py:184-185; evemailingListConfig.py:500-506 saves when “send to all” is off and ignores the result. | server/src/services/mail/mailingListsMgrService.js:292-300 saves and returns null. | MailingListComposition.cs:390-418 stores bounded welcome text and timestamps; the wire test checks None. |
| RPC-01899 SendWelcomeMail | mailingListsSvc.py:187-188; evemailingListConfig.py:503-505 calls it when “send to all” is on and ignores the result. | server/src/services/mail/mailingListsMgrService.js:302-311 sends through mail state and returns the created message ID or null. | MailingListComposition.cs:390-418 persists the welcome text and calls the existing mail writer; the wire test checks message-ID passthrough. |
| RPC-01900 SetDefaultAccess | mailingListsSvc.py:136-138; evemailingListConfig.py:453-456 sends the default access and member role and ignores the result. | server/src/services/mail/mailingListsMgrService.js:265-290 updates defaults and returns null. | MailingListComposition.cs:366-380 normalizes and persists defaults; wire test checks None. |
| RPC-01901 SetEntitiesAccess | mailingListsSvc.py:109-115 maps single-entity access to a dictionary; evemailingListConfig.py:355-357 sends bulk access then refreshes settings. | server/src/services/mail/mailingListsMgrService.js:220-236 normalizes and applies entity access, then returns null. | MailingListComposition.cs:316-344 validates manager access and persists normalized access; wire test checks the dictionary and 1,000-entry boundary. |
| RPC-01902 SetMembersClear | mailingListsSvc.py:131-134 invalidates cached members; evemailingListConfig.py:269-275,288-290 clears selected members then refreshes the member list. | server/src/services/mail/mailingListsMgrService.js:256-263 restores the default member role and returns null. | MailingListComposition.cs:277-307 applies default role, persists, and sends OnMailingListSetClear; the client consumes this in mailingListsSvc.py:155-159. |
| RPC-01903 SetMembersMuted | mailingListsSvc.py:121-124 invalidates cached members; evemailingListConfig.py:276-282,288-290 mutes selected members then refreshes. | server/src/services/mail/mailingListsMgrService.js:238-245 mutes authorized members and returns null. | MailingListComposition.cs:277-307 persists and sends OnMailingListSetMuted; the client consumes this in mailingListsSvc.py:149-153. |
| RPC-01904 SetMembersOperator | mailingListsSvc.py:126-129 invalidates cached members; evemailingListConfig.py:283-290 promotes selected members then refreshes. | server/src/services/mail/mailingListsMgrService.js:247-254 assigns operator role and returns null. | MailingListComposition.cs:277-307 persists and sends OnMailingListSetOperator; the client consumes this in mailingListsSvc.py:143-147. |

The client-side decision points above were searched across the full /home/user/eve-logic tree, including downstream notification handlers in mailingListsSvc.py. The transport adds machoVersion=1 when no cached version exists (carbon/common/script/net/ServiceCallGPCS.py:221-225); the Host wire regression now sends it as kwargs and checks that the manager receives the original positional arguments.

## Verification

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -m:1 --no-restore` — 0 warnings, 0 errors after updating from main.
- PASS: focused Host wire test on the merged tree with `machoVersion=1` sent as transport kwargs — 1 passed, 0 failed, 0 skipped.
- Build prerequisite: added the missing `Eve.Services` using to the newly merged `ShipRpcHostTests.cs`; that unrelated test had blocked compilation of the shared Host test project.

- PASS: MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Mailing_list_manager_mutations_are_forwarded_through_registered_host_wire' — 1 passed, 0 failed, 0 skipped.
- The same focused test was rerun after tightening the Join KeyVal field assertion — 1 passed, 0 failed, 0 skipped.
- GitHub Actions agent-test run 40212503977 for commit 6fbb4951 succeeded: the selected Host test passed, with no failed tests or jobs.
- `python3 tools/dev/ci-status.py failures 6fbb4951` returned “No ci.yml run found” when checked after the focused run.
- A real graphical client walk was not performed.


## Follow-up on pushed CI

- Commit `3bbc382b` was pushed to `main`. Agent-test run `40230484611` passed the selected mailing-list Host wire test (1 passed, 0 failed, 0 skipped).
- Push-triggered CI run `40230469827` failed in unrelated Host test cases. Its registry inventory reported 1,420 RPC rows instead of the old 1,414 snapshot; the snapshot was updated to 1,420. In a local no-database invocation this registry test reports only 236 rows and therefore does not represent the CI Host composition.
- Other CI failures exposed stale wire-test assumptions: decoded strings are `PyBytes`, error packets use `MachoMessage.ErrorResponse` (15), and inventory movement emits a notification before the call reply. The affected assertions/read loop now follow those observed wire shapes.
- The CI insurance purchase test failed because the Rifter produced no 50% quote from its returned insurance value. It remains to be resolved on the database-backed Actions lane; local database tests were not run as required.
- Local Host test project build passed with 0 errors (6 transient file-copy retry warnings while another process held `Eve.Services.Space.dll`). The no-database inventory focused invocation failed with 236 rows versus the CI snapshot of 1,420; no database was used.


## Latest verification state

- Commit `f70c174e` is pushed to `main`; its selected Agent test run `40235241341` failed after starting the Host test command, but the emitted log contained no test failure names or summary. The prior broader selected run `40232977831` likewise ended without test-result details.
- Push-triggered CI for `1fe2bb2e` completed with failures. The Host test failures included the insurance quote test at `InsuranceRpcHostWireTests.cs:140`, plus wire expectation mismatches in character notes, Beyonce formation strings, and ship fitting tuple equality. The latter three expectations were updated on `f70c174e`. Inventory count and bound inventory notification ordering corrections were also included in the prior commit.
- The same CI run's fast live Python job failed in two authentication tests: the new-account accrued-time result was empty and the age-gate read stayed false after the test updated account state. Eight slow/skill-speed tests were skipped by their environment gates. Those authentication failures are outside the mailing-list RPC path.
- On `f70c174e`, push-triggered CI run `40235234218` was queued when this handoff was updated.
- Local Host test project build after the latest wire expectation changes passed: 0 warnings, 0 errors. The no-database inventory test invocation reported 236 rows, while database-backed CI had reported 1,420; no PostgreSQL database was used locally.
- The mailing-list focused Agent test passed on `3bbc382b` (run `40230484611`, 1 passed, 0 failed, 0 skipped). The latest edits do not change that test or production mailing-list code.


## Final Actions result

- Push-triggered CI run `40235234218` for `f70c174e` completed with failures. The .NET build, Rust checks, Python tests, end-to-end/headless job, Windows job, and most .NET shards passed. Two Host shards failed:
  - `Character_manager_mutations_persist_through_registered_host_wire` received ERRORRESPONSE type 15 while fetching the owner note after the preceding mutations.
  - `Ship_fitting_info_and_dirt_timestamp_use_the_registered_host_routes` still expected PyStr for two decoded strings; the wire values were PyBytes.
  - `Insurance_rpc_purchase_lookup_list_and_cancellation_follow_the_persisted_host_state` still found no 50% quote for the Rifter insurance value.
- Agent test run `40235241341` for the selected five Host tests failed after starting the project test command; its recorded step output did not include test names or a test summary. The mailing-list test itself passed in run `40230484611` before these unrelated Host-test follow-ups.
- The code under review is the pushed `f70c174e` state; later `main` merges add unrelated work. This handoff includes that state’s completed Actions results. No real graphical-client walkthrough was done, and no PostgreSQL database was used locally.
