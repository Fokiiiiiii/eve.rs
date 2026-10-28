# Deep check: insurgencySolarsystem

## V24 call order and consumed fields

| Method | Request and consumer |
|---|---|
| GetLocalCampaignClientSnapshot() | eve/client/script/ui/services/insurgencyCampaignSvc.py:47-56; no args. Client unpacks two values, systemID and snapshot-or-None, and retries if the returned system differs from session.solarsystemid2. The snapshot is cached. |
| GetAllVisibleCampaigns() | insurgencyCampaignSvc.py:91-93; no args. Returns a campaign list consumed by insurgenceDashboardSvc.py:98-102, map filters and starModeHandler for coveredSolarsystemIDs/pirateFactionID, and the Theater of War landing page for fsmState, originSolarsystemID, score requirements, score totals and stateExpiryTimestamp. |

The snapshot is the V24 CampaignClientSnapshot object with campaignID, pirateFactionID, warzoneID, coveredSolarsystemIDs, stateExpiryTimestamp, FSM state, origin system, structure and pirate/antipirate point totals. The campaign service consumes follow-up notifications OnInsurgencyCampaignStartedForLocation, OnInsurgencyCampaignUpdatedForLocation, and OnInsurgencyCampaignEndingForLocation at insurgencyCampaignSvc.py:60-85 to refresh the same cached object.

## Host owner, reply, and comparison

CoreRpc.cs:107 registers the service. InsurgencyComposition.cs:52-65 reads visible campaigns from InsurgencyStore and builds FILETIME timestamps. GetAllVisibleCampaigns returns a PyList of Ex2 CampaignClientSnapshot objects. GetLocalCampaignClientSnapshot returns a two-element PyList: current solarsystemid2 and the matching snapshot or None. It matches either the campaign origin or a covered system. No per-session campaign map is introduced.

InsurgencyComposition.cs:73-96 sends start/update/ending notifications after campaign state commits, scoped to covered systems where appropriate; the V24 client filters by its current system. EveJS v0.12.9 uses the same list and two-slot local snapshot shapes through the configured campaign provider (server/src/services/map/insurgencySolarsystemService.js:67-87). The Host uses InsurgencyStore as the authoritative provider.

## Wire evidence and limits

SupportRpcWireTests.cs:70-91 reaches the registered Host service with an injected campaign snapshot, checks the Ex2 class name and campaignID field, and checks local systemID plus the same snapshot. This is a packet-loop wire test of shape and lookup behavior; persistence/restart is covered by separate database tests and was not exercised here.

No PostgreSQL database or graphical V24 client was used. No code changed.
