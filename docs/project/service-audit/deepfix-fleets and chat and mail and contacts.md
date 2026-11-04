# Deep fix: fleets, chat, mail, and contacts

Date: 2026-11-01
Worktree: `wt-cx4`

## Result

Fixed the resource wars chat entry path end to end. Before the fix, the registered-Host wire test reached `EnsureResourceWarsChannelExists` and received a remote-error frame (opcode 15) where the success response (opcode 7) was expected. In the client, `rwservice.py:90-112` first calls this RPC, then joins `resourcewars_<instanceID>`; the missing method therefore stopped the flow before the join.

The Host now registers the RPC, keeps ensured instance IDs in the existing `XmppChatHub`, admits only ensured room IDs, and removes the ephemeral room when the last occupant leaves. A later entry recreates it through the same RPC. Valid IDs return the EveJS room name; non-positive IDs return `None`. XMPP sends the normal self-presence after a successful join. The final Host wire regression covers valid and invalid arguments, join presence, rejection before ensure and after final leave, and recreation.

## Evidence by sampled flow

### Resource wars chat

- V24.01 calls `EnsureResourceWarsChannelExists(instanceID)`, then calls `JoinChannel('resourcewars_%d' % instanceID)`, tracks the active channel, and switches away from its prior channel on a new entry: `/home/user/eve-logic/resourcewars/client/rwservice.py:90-112`.
- The Host route and argument conversion are in `dotnet/src/Eve.Host/Xmpp/XmppChatMgrService.cs:20-22`. Room state is owned by `XmppChatHub.cs:85-102`; `XmppMuc.cs:70-90` checks access before joining, lines 91-119 send presence, and lines 132-146 retire a room after its last occupant leaves. Disconnects use the same leave path: `XmppClientSession.cs:162-163`.
- EveJS 261028.1 registers the route in `server/src/services/chat/xmppChatMgrService.js:248-258`, creates `resourcewars_<id>` with `destroyWhenEmpty: true` in `server/src/_secondary/chat/chatRuntime.js:1085-1098`, and deletes empty channels in `chatRuntime.js:978-1003`. Host lifecycle follows that ephemeral behavior.
- The Host wire regression is `dotnet/tests/Eve.Host.Tests/XmppChatMgrHostWireTests.cs:61-75`. The pre-fix failure and post-fix result are recorded below.

### Fleet invite, join, and voluntary leave

- The client menu invokes `FleetSvc.LeaveFleet` (`eve/client/script/ui/shared/fleet/fleetwindow.py:178`); the service calls the bound fleet object's `LeaveFleet` and clears its local fleet state (`eve/client/script/parklife/fleetSvc.py:365-371`). `OnFleetJoin` and `OnFleetLeave` update service state and scatter UI events at `fleetSvc.py:1203-1249`; UI consumers include `fleet.py:245-276` and `overviewWindow.py:505-510`.
- Host `FleetObjectService.LeaveFleet` clears the leaving session and publishes member changes after the store mutation: `dotnet/src/Eve.Services.Space/FleetObjectService.cs:283-291`. EveJS removes the target without notifying that same session and returns success in `server/src/services/fleets/fleetRuntime.js:1987-2006`.
- DB tests `Fleet_invite_join_and_leave_are_persisted_with_the_member_hierarchy` and `Fleet_invites_and_membership_publish_client_notifications_and_session_changes` verify persistence, invite/join notification sequence, session changes, and leave recipients. No defect was found in this sampled flow. Boss departure and succession were outside this check.

### Mail send and sync

- The existing whole-service audit `docs/project/service-audit/DEEPCHECK_mailMgr.md` records all mail RPC callers, consumers, state writes, and notification order. Relevant client paths include `eve/client/script/ui/services/mailSvc.py:156-234` for sync and `eve/client/script/ui/services/mailSvc.py:959-992` for send completion; EveJS uses `server/src/services/mail/mailState.js:2081-2121` for sync and `mailState.js:84-96` for title/body limits.
- No mail code changed in this job. The existing audit reports no mail regression for these flows; the mail test project was not rerun here.

### Personal contacts

- The client loads `GetContactList` into owner-scoped address rows and reads `contactID`, `inWatchlist`, `relationshipID`, and `labelMask`: `eve/client/script/ui/shared/neocom/addressBook/addressbookService.py:206-238`. Add/edit sends the selected standing, watchlist flag, notification choice, and message to `charMgr.AddContact` or `EditContact`: `addressbookService.py:770-795`; the client updates its local row and scatters `OnContactChange` afterward.
- EVE University lists the five personal standing choices as -10, -5, 0, +5, and +10 ([Contacts](https://wiki.eveuniversity.org/Contacts)). The Host's integer `RelationshipId`, sorted rowset, and persisted upsert are at `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:11,630-653,941-968`; these match the personal-contact choices. No defect was found in this sampled flow. This check does not cover corporate/alliance contacts, blocking, or label mutation.

## Checks run

- Before fix: focused Host wire test failed at `EnsureResourceWarsChannelExists` (expected success frame 7; received error frame 15).
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- `EVE_PG_URL=$(tools/dev/pg18.sh url eve_p4) tools/dev/db-lock.sh dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build --filter 'FullyQualifiedName~Xmpp' -p:UseSharedCompilation=false`: passed, 34 passed, 0 failed, 0 skipped. This includes the PostgreSQL-backed player-room persistence regression.
- `EVE_PG_URL=$(tools/dev/pg18.sh url eve_p4) tools/dev/db-lock.sh dotnet test dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj --filter 'FullyQualifiedName~Fleet_invite_join_and_leave_are_persisted_with_the_member_hierarchy|FullyQualifiedName~Fleet_invites_and_membership_publish_client_notifications_and_session_changes' -p:UseSharedCompilation=false`: passed, 2 passed, 0 failed, 0 skipped.
- No graphical V24.01 client session was started. The mail and personal-contact paths were inspected but not exercised by tests in this job.
