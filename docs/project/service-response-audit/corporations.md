# Corporation audit handoff

## 2026-10-23: mutual war invite manager

`mutualWarInviteMgr` is now wired in Host to `MutualWarInviteRuntimeState`. Invite and block records use the shared RelationStore; there is no corporation-specific duplicate table. The service returns the reference list row shape, keeps sent dates as client filetime strings, removes stale invitations at expiry, and sends `OnMutualWarsUpdated_Remote` after the RPC response to sessions in either affected corporation or alliance. Sending, withdrawing, accepting, declining and changing the block setting commit before refresh events are queued.

Accepting an invite changes the war and removes its ally relations in the same transaction. A new mutual war records a 24 hour startup; an existing war becomes mutual while retaining its start time. Invitations expire after 30 days. The 24 hour startup follows the EVE University war startup rule and CCP's mutual-war flow, which says mutual wars begin like normal wars. The 30 day expiry follows [CCP's Mutual War support page](https://support.eveonline.com/hc/en-us/articles/360005659379-Mutual-War); EveJS v0.12.9 retains invite records without that expiry and starts a newly accepted mutual war immediately.

### Checks

- `dotnet build` passed for `Eve.Relations`, `Eve.Services.Corporations`, and `Eve.Host`.
- The focused `OrganizationContactsNotificationTests.Mutual_war_updates_reach_both_owners_after_the_response` test passed (1 passed, 0 skipped); it uses no database.
- This task prohibits PostgreSQL use, so RelationStore persistence across restart, concurrent replies, and database transaction rollback were not exercised here. Keep the TODO row OPEN until a database-backed regression check covers those transitions.
- The real V24.01 graphical client was not used.

### Remaining corporation audit work

The war negotiation callbacks and `billMgr.CharPayBill` remain open in `docs/project/TODO.md`. The mutual-invite state transitions have compile and notification coverage, but still need a database-backed regression check before this row can close.
