# Handoff: mailingListsMgr RPC-01895–01904

## Result

Added a Host socket regression for all ten management calls. The test registers the real `MailingListService` under `mailingListsMgr`, sends requests through `HostServer` and `PacketDispatcher`, and checks callback forwarding, session identity and client reply shapes. It uses no database.

## Client trace

A search across `/home/user/eve-logic/eve/client/script` found these call sites and consumers:

- **Join (01895):** `mailingListsSvc.py:73-77` sends the name and stores the returned `KeyVal` by `ret.id`; `evemail.py:2915-2916` consumes the returned ID.
- **KickMembers (01896):** `mailingListsSvc.py:99-102`; `evemailingListConfig.py:285-288` sends selected member IDs, then refreshes the access/member view.
- **Leave (01897):** `mailingListsSvc.py:79-87`; `evemail.py:500-501,553-554` invokes it from the mailing-list menu. The reply is ignored and the local joined-list entry is removed.
- **SaveWelcomeMail (01898):** `mailingListsSvc.py:184-185`; `evemailingListConfig.py:493-505` sends title/body when the send-to-all checkbox is clear; the result is ignored.
- **SendWelcomeMail (01899):** `mailingListsSvc.py:187-188`; `evemailingListConfig.py:493-505` sends title/body when send-to-all is checked; the result is ignored.
- **SetDefaultAccess (01900):** `mailingListsSvc.py:136-138`; `evemailingListConfig.py:454-456` sends list ID, default access and default member role. The setup UI reads those values and the entity access map from `GetSettings` at `evemailingListConfig.py:376-397,417-429`.
- **SetEntitiesAccess (01901):** `mailingListsSvc.py:109-115`; `evemailingListConfig.py:355-356` sends an entity-to-access dictionary and reloads settings.
- **SetMembersClear (01902):** `mailingListsSvc.py:131-134`; `evemailingListConfig.py:270-289` sends selected member IDs and reloads members.
- **SetMembersMuted (01903):** `mailingListsSvc.py:121-124`; `evemailingListConfig.py:276-289` sends selected member IDs and reloads members.
- **SetMembersOperator (01904):** `mailingListsSvc.py:126-129`; `evemailingListConfig.py:283-289` sends selected member IDs and reloads members.

For the refreshed UI, `evemailingListConfig.py:197-204,210-232` reads a member-ID-to-role map and filters/sorts it; `evemailingListConfig.py:482-489` reads a welcome-mail list of KeyVals and consumes the first row's `title` and `body`. Bulk member operations invalidate the cached `GetMembers` call in the client service (`mailingListsSvc.py:101,123,128,133`). No additional callers for the ten RPC methods appeared in the client-script search.

## EveJS comparison

The matching v0.12.9 handlers are in `server/src/services/mail/mailingListsMgrService.js`: Join builds a mailing-list KeyVal (`144-150)); KickMembers and Leave return null (`152-170)); SetEntitiesAccess, SetMembersMuted, SetMembersOperator, SetMembersClear and SetDefaultAccess return null after calling their matching `mailState` mutation (`183-238)); SaveWelcomeMail returns null and SendWelcomeMail returns the message ID or null (`255-273`). eve.rs keeps these same client-visible reply shapes. EveJS resolves the character from the session and invokes the corresponding state functions in that file.

## Host and test evidence

- `dotnet/src/Eve.Services.Core/MailingListService.cs:52-140` registers and dispatches these manager methods; writes require the mutation callback. The service enforces the 1,000-entry bulk limit for member and entity updates.
- `dotnet/src/Eve.Host/Composition/MailingListComposition.cs:100-160` validates the active character session, serializes mutations through the gate, and applies Create/Join/Leave/Delete state changes to the existing mail GameStore. Later cases persist member/access/settings/welcome-mail changes; kick and role changes send the matching client notifications after persistence. Welcome mail sending uses the existing mail stores/writer.
- `dotnet/tests/Eve.Host.Tests/MailingListHostWireTests.cs` exercises the socket route for all ten RPC rows and asserts Join's KeyVal, null mutation replies, SendWelcomeMail's message ID, forwarded arguments and caller session identity.
- Verified locally: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` succeeded; the focused `Mailing_lists_management_calls_cross_the_registered_host_service_on_wire` test passed (1 passed, 0 skipped). No PostgreSQL database was used.

## Remaining checks

The socket test supplies a deterministic callback, so it checks Host routing and wire behavior but does not exercise the production GameStore flush, mail fan-out, live member notifications, or process restart. The production callback and persistence path were inspected, not run. The graphical client was not used. Run the focused Actions test and inspect push-triggered CI after the commit.
