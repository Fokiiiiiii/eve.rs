# Chat and client UI startup deep correctness

## Scope and evidence

This review followed character selection into the session change, the V24.01 chat login and channel restore flow, XMPP room membership and roster metadata, mail synchronization and notifications, and character settings/window startup. The client references are from `/home/user/eve-logic`; the functional comparison is `/home/user/evejs-261108.1`.

## Verified divergences and fixes

### XMPP did not follow live session changes

V24's `XmppChatSvc.OnSessionChanged` reacts to corporation, alliance, fleet, and faction changes by leaving the old category room. Its login path restores saved chat channels, then asks `ResyncSystemChannelAccess` for the current system and session rooms (`xmppchatclient/xmppchatsvc.py:35-45,149-164,247-260,345-368,370-380`). EveJS calls `synchronizeSessionChatState` after sending the session change (`server/src/network/clientSession.js:809-818`). That synchronizer explicitly covers `corpid`, `allianceid`, `warfactionid`, and `fleetid`, and refreshes presence for `corpid`, `allianceid`, `warfactionid`, `role`, and `corprole` (`server/src/services/chat/sessionChatSync.js:7-33,104-164`).

The C# XMPP hub exposed current access through resync but did not subscribe to session changes. A regression test was added first; it failed because an enlisted pilot stayed in the former militia room and was not admitted to the new one. The hub now tracks connected sessions, leaves rooms whose access was lost, autojoins changed corporation/alliance/fleet/militia rooms, and refreshes room presence when organization or role fields change. The existing static access calculation also includes `faction_<warfactionid>`. The flow is covered by `XmppOrganizationRoomTests.Faction_warfare_session_changes_move_live_xmpp_occupants_to_the_new_militia_channel` and `Chat_role_session_changes_refresh_each_occupied_room_presence`.

The session-change packet is sent before `ClientSession.AttributesChanged` is raised (`dotnet/src/Eve.Services/ClientSession.cs:369-392`), so XMPP presence moves follow the client-visible session update. The militia-move regression now asserts this order across the session packet and XMPP stream writes.

Fleet membership is reconciled on `fleetid` changes, but `fleetid` alone does not trigger a role/presence refresh. EveJS documents that distinction: refreshing the new fleet occupant immediately after joining can make V24 treat the unavailable stanza as a leave and close the fleet window (`sessionChatSync.js:17-33`).

The first role-refresh implementation also excluded an invisible occupant from the recipient snapshot before it selected that occupant's own link. The new regression test failed with no refresh delivered to that client. It now includes the occupant's own connection while continuing to exclude that occupant from other pilots' presence updates. EveJS refreshes both unavailable and available presence for each room member (`xmppStubServer.js:1961-2006`); the resulting C# behavior is covered by `Chat_role_session_changes_refresh_an_invisible_occupants_own_roster`.

### MUC rosters lacked EVE user data and the requested IQ response

After a room join, V24 reads the `eve_user_data` extension and requests it by character JID to prime owner metadata (`xmppchatclient/xmppchatwindow.py:486-500`; `xmppchatclient/xmppchatsvc.py:782-802,1758-1777`). EveJS includes that extension and the occupant JID in MUC presence, and answers `urn:xmpp:eve_user_data` IQ requests (`server/src/services/chat/xmppStubServer.js:624-690,2685-2691,2958-2973`). C# previously emitted only the MUC user item and did not implement that IQ.

`CharMgrService.ReadChatUserDataAsync` now supplies character identity, type, gender, and the current organization projection from the existing character and organization readers. Host composition passes that reader to XMPP. MUC presence now includes the EVE metadata in EveJS's attribute shape/order, the bound user JID, and the client-requested IQ returns the same metadata for online or offline characters. The regression test `Muc_presence_and_user_data_iq_publish_client_roster_metadata` checks the wire XML and JSON `info` value; `CharMgrPublicInfoHostTests.Chat_user_data_uses_character_identity_and_current_militia_projection` checks its authoritative read model.

### Persistent character settings

V24's character settings service stores chat filter state through `charMgr.GetCharacterSettings`, `SaveCharacterSetting`, and `DeleteCharacterSetting` (`eve/client/script/ui/services/characterSettingsSvc.py:20-57`; `xmppchatclient/xmppchatsvc.py:1717-1734`). The registered-host database test now saves `chatFilters`, restarts the Host, reads it back, deletes it, restarts again, and verifies deletion: `CharacterContactNotepadHostTests.Character_contact_note_and_setting_rpcs_round_trip_through_registered_host`.

## Traced paths with no server divergence found

- Character selection runs `SelectCharacterID` inside `PerformSessionChange` (`eve/client/script/ui/login/charSelection/characterSelection.py:705-714`). V24's `gameui.OnSessionChanged` emits local `OnCharacterSelected` when `charid` changes and reloads settings (`eve/client/script/parklife/gameui.py:433-458,504-506`). The existing compatibility record `docs/project/ci-character-selection-duplicate-notification-followup.md` covers the server notification ordering; this change does not add a duplicate character-selected notification.
- V24 restores saved channel windows/settings in the client chat service, and creates/removes category windows from the rooms returned by system-channel resync (`xmppchatclient/xmppchatsvc.py:247-260,345-368,370-380`). General window location/session handling and reset layout are client-side (`eve/client/script/ui/services/windowsvc.py:131-143,153-189`); no server window RPC was found in this path.
- V24 `SyncMail` reconciles cached headers against `newMail`, `oldMail`, and status rows (`eve/client/script/ui/services/mail/mailSvc.py:133-221`). `OnMailSent` updates the client cache and calls its new-mail notification path (`mailSvc.py:1119-1153,1213-1218`). EveJS returns the same mailbox categories and relies on the client's `OnMailSent` handler to build the notification (`server/src/services/mail/mailMgrService.js:170-185`; `server/src/services/mail/mailState.js:2095-2130,1236-1265`). No mail implementation change was indicated by this source comparison.

## Verification limits

The checks recorded for this change are headless Host/service tests and Release builds. They exercise Host registration, XML payloads, session-driven room moves, user-data reads, mail RPCs/notifications, and database persistence. The recorded test runs are on the isolated `eve_p8` database with the pinned SDE loaded: `CharacterHostTests` (68 passed, 0 failed, 1 optional real-simulation test skipped), all XMPP-named tests (38 passed, 0 failed, 0 skipped), the focused mail Host-wire cases (4 passed, 0 failed, 0 skipped), the character contacts/settings Host class (2 passed, 0 failed, 0 skipped), and the focused CharMgr chat user-data test (1 passed, 0 failed, 0 skipped). The invisible-occupant regression was also observed failing before its fix and passing afterward.

These checks do not verify startup in the unmodified graphical V24.01 client. In particular, no real-client trace was captured for restored chat windows, notification display timing, neocom/window arrangement, or settings loaded across a live character-selection session. Mail behavior was compared from the V24 client and EveJS source and Host RPC tests; the actual client notification UI was not launched. The V24 `SetEveUserData` helper was not observed at a callsite and its setter behavior was not exercised.
