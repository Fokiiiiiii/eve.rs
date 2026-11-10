# CharacterInfo service audit fixes

This record verifies the findings in `/home/user/reports/90-audit-services-characterinfo-r15.md` against the current repository, the V24.01 client source in `/home/user/eve-logic`, and the requested EveJS checkout `/home/user/evejs-261108.1`.

## P2: Persist logoff time on abrupt transport close

**Finding confirmed.** Before this change, `HostServer.SessionActor.RunAsync` called `_session.Close()` before its close callback. The composed close handler then removed presence (`HostComposition.cs`, `session.Closed` handler), but only the explicit `UserLogOffCharacter` flow persisted `Character.LastLogoffAt` (`CharacterLogoff.cs`). The database value is read by `CorpRegistryService.MemberTrackingRows` to produce `lastOnline` (`CorpRegistryService.cs:823-828`).

The whole V24.01 client source search for `lastLogoff`, `lastLogoffAt`, `LastLogoff`, `logoffDateTime`, `lastOnline`, and `logoffDate` finds the member tracking consumer in `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_tracking.py`. It calls `GetMemberTrackingInfo` at line 103, filters and renders `member.lastOnline` at lines 127-139, sorts on it at lines 159-167, and shows `member.logoffDateTime` to directors at lines 157-170. This confirms the corporation member tracking impact.

EveJS 261108.1 calls `disconnectCharacterSession` from the TCP socket lifecycle (`server/src/network/tcp/index.js:263-275`). That handler persists disconnect state at `server/src/services/_shared/sessionDisconnect.js:353-355`; its online transition handler stores the transition timestamp before notifying observers at `server/src/services/online/onlineStatusRuntime.js:140-155`.

The server now runs `HostOptions.SessionClosingAsync` before `ClientSession.Close()` (`dotnet/src/Eve.Host/HostServer.cs:44-51, 697-718`). The Host composition takes the character lifecycle lock, verifies that this is still the character's current session, and stores the timestamp before existing close handlers remove presence (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:594-606`). Explicit logoff already stores its timestamp and clears the session character binding, so the close callback does not overwrite that value.

The new PostgreSQL Host test sends the real `charUnboundMgr.SelectCharacterID` RPC over a socket, abruptly closes the transport, checks the persisted time when the observer receives `OnContactLoggedOff`, and reads `lastOnline` through the director-only `corpRegistry.GetMemberTrackingInfo` wire rowset. It then verifies that explicit logoff's timestamp is not rewritten by a later transport close. The test failed before the fix because `LastLogoffAt` remained null.

**Not substantiated:** The audit also attributed an incorrect character-selection sort to this field. The whole-client search above found no V24.01 consumer of the server's `logoffDate` character-selection field. I have not counted that as a confirmed impact; the corporation member tracking impact is independently confirmed.

## P3: Ignore unknown IDs in personal `AssignLabels`

**Finding confirmed.** The previous `CharMgrService.AssignLabels` implementation inserted a new `CharMgrContact` when an ID was absent. V24.01 `addressbookService.py:1096-1116` calls `AssignLabels` before opening the add-contact modal; when the modal is canceled, the method returns without adding the contact. The modal only calls `AddContact` after confirmation (`addressbookService.py:767-790`). Thus the previous server behavior persisted a ghost contact on cancel.

EveJS 261108.1 `server/src/services/character/charMgrService.js:1035-1055` updates label masks only for contacts already present. The Host creates `CharMgrService` in `dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:76-87`, and `HostComposition.cs:482-485` registers the composed character services, so the behavior is exercised through the registered Host route.

`AssignLabels` now updates only existing contacts (`dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:754-771`). Service and PostgreSQL Host regressions verify that an unknown ID remains absent, then `AddContact` followed by `AssignLabels` persists the requested mask. The Host regression failed before the fix because the contact list contained the unknown ID.

## Verification record

- Test-first failures were observed for both findings before their fixes: null `LastLogoffAt` after abrupt close, and an unexpected contact row after assigning a label to an unknown ID.
- Focused tests passed on the job's database `eve_p4`: the abrupt disconnect/explicit logoff Host flow, the contact-label Host persistence/restart flow, and the CharacterInfo service label mutation flow. Each focused run reported zero skipped tests.
- The pinned SDE data was loaded into `eve_p4` for the Host fixture. No SDE data is part of this change.
- No graphical V24.01 client session or fresh packet capture was run.
- The `CLAUDE.md`-requested CBM/Serena MCP code tools were not available in this environment; source verification used local line-numbered files and full-tree `rg` searches instead.
