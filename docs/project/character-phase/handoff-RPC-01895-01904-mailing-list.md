# Mailing-list RPC host coverage: RPC-01895–RPC-01904

## Scope and result

All ten assigned mutations were already registered in the production MailRpc service list and mapped through MailingListService to CharacterServicesComposition.MailingLists. The Host provider reads and updates the shared mail.mailingLists GameStore rows, persists changes through mailCache/mailStore, validates the current session and role, emits the client mailing-list notifications, and sends welcome mail through the existing mail writer.

Added a real Host socket regression for the ten methods. It sends CallReq packets to the registered mailingListsMgr route with strict RPC dispatch enabled, round-trips replies through Marshal, checks forwarded argument values and session character identity, and sends the full accepted 1,000-entry member/entity collections. Join returns a util.KeyVal with the id field consumed by the client; SendWelcomeMail returns its message ID; the other listed mutation replies are None.

The test callback records the real service's Host-provider boundary. Existing production GameStore persistence and notification code was inspected but is not invoked by this callback-backed wire regression. No database was used.

## Client, EveJS, and Host evidence

| Row | V24.01 client calls and consumes | EveJS v0.12.9 behavior | Host behavior |
|---|---|---|---|
| RPC-01895 Join | mailingListsSvc.py:73-76 calls Join(name), reads ret.id, updates myMailingLists, and scatters OnMyMaillistChanged; evemail.py:2915-2917 closes only for a non-None result. | mailingListsMgrService.js:145-151 returns the mailing-list info KeyVal on success and null on failure. | MailingListComposition.cs:163-218 resolves name/access, persists membership, and returns MailingListJoinValue; wire test checks KeyVal id. |
| RPC-01896 KickMembers | mailingListsSvc.py:99-102 invalidates cached GetMembers; evemailingListConfig.py:284-290 calls it and refreshes the access/member UI. | mailingListsMgrService.js:163-170 removes authorized members and returns null. | MailingListComposition.cs:277-307 persists changes and sends OnMailingListLeave; client consumes it in mailingListsSvc.py:161-169. |
| RPC-01897 Leave | mailingListsSvc.py:79-86 removes the local list and scatters OnMyMaillistChanged; evemail.py:501-502 ignores the RPC result. | mailingListsMgrService.js:153-156 leaves and returns null. | MailingListComposition.cs:233-244 removes the caller from members and persists; the UI-level local list update follows the service path. |
| RPC-01898 SaveWelcomeMail | mailingListsSvc.py:184-185; evemailingListConfig.py:500-506 saves when “send to all” is off and ignores the result. | mailingListsMgrService.js:256-264 saves and returns null. | MailingListComposition.cs:390-418 stores bounded welcome text and timestamps; the wire test checks None. |
| RPC-01899 SendWelcomeMail | mailingListsSvc.py:187-188; evemailingListConfig.py:503-505 calls it when “send to all” is on and ignores the result. | mailingListsMgrService.js:266-274 sends through mail state and returns the created message ID or null. | MailingListComposition.cs:390-418 persists the welcome text and calls the existing mail writer; the wire test checks message-ID passthrough. |
| RPC-01900 SetDefaultAccess | mailingListsSvc.py:136-138; evemailingListConfig.py:453-456 sends the default access and member role and ignores the result. | mailingListsMgrService.js:229-238 updates defaults and returns null. | MailingListComposition.cs:366-380 normalizes and persists defaults; wire test checks None. |
| RPC-01901 SetEntitiesAccess | mailingListsSvc.py:109-115 maps single-entity access to a dictionary; evemailingListConfig.py:355-357 sends bulk access then refreshes settings. | mailingListsMgrService.js:184-191 normalizes and applies entity access, then returns null. | MailingListComposition.cs:316-344 validates manager access and persists normalized access; wire test checks the dictionary and 1,000-entry boundary. |
| RPC-01902 SetMembersClear | mailingListsSvc.py:131-134 invalidates cached members; evemailingListConfig.py:269-275,288-290 clears selected members then refreshes the member list. | mailingListsMgrService.js:220-227 restores the default member role and returns null. | MailingListComposition.cs:277-307 applies default role, persists, and sends OnMailingListSetClear; the client consumes this in mailingListsSvc.py:155-159. |
| RPC-01903 SetMembersMuted | mailingListsSvc.py:121-124 invalidates cached members; evemailingListConfig.py:276-282,288-290 mutes selected members then refreshes. | mailingListsMgrService.js:202-209 mutes authorized members and returns null. | MailingListComposition.cs:277-307 persists and sends OnMailingListSetMuted; the client consumes this in mailingListsSvc.py:149-153. |
| RPC-01904 SetMembersOperator | mailingListsSvc.py:126-129 invalidates cached members; evemailingListConfig.py:283-290 promotes selected members then refreshes. | mailingListsMgrService.js:211-218 assigns operator role and returns null. | MailingListComposition.cs:277-307 persists and sends OnMailingListSetOperator; the client consumes this in mailingListsSvc.py:143-147. |

The client-side decision points above were searched across the full /home/user/eve-logic tree, including downstream notification handlers in mailingListsSvc.py. No machoVersion occurrence is present in the mailing-list client service file.

## Verification

- PASS: MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Mailing_list_manager_mutations_are_forwarded_through_registered_host_wire' — 1 passed, 0 failed, 0 skipped.
- The same focused test was rerun after tightening the Join KeyVal field assertion — 1 passed, 0 failed, 0 skipped.
- GitHub Actions agent-test run 40212503977 for commit 6fbb4951 succeeded: the selected Host test passed, with no failed tests or jobs.
- `python3 tools/dev/ci-status.py failures 6fbb4951` returned “No ci.yml run found” when checked after the focused run.
- A real graphical client walk was not performed.
