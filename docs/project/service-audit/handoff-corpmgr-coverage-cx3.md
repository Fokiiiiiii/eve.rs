# corpmgr coverage handoff — cx3

## Completed

- RPC-01242–RPC-01243 (GetCorporationIDForCharacter from contract assignee checks) and RPC-01248–RPC-01249 (GetCorporations) are covered by CorporationPublicInfoHostWireTests.Corporation_identity_and_row_reads_use_persisted_records_through_registered_host.
- The DB-backed test registers the real CorpMgrService, dispatches Macho CallReq packets via PacketDispatcher, creates a corporation and member in PostgreSQL, then checks the member's corporation ID, None for an unknown character, the persisted util.Row header/line fields, and None for a missing corporation. These are read calls; no notification is expected.
- The test exposed a defect: an unknown character ID received the logged-in corporation ID. eve-logic calls the RPC at eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:1204-1205 and uses the result to classify a contract assignee. characterInfoWindow.py:110-121 and infoContentBase.py:471-476 use it to show a character's corporation banner. Current EveJS 261028.1 server/src/services/corporation/corpmgrService.js:507-524 returns null for an unknown non-session character; eve.rs now returns PyNone.
- GetCorporations is called at eve/client/script/ui/services/corporation/bco_corporations.py:50; its util.Row header and line are consumed by LoadCorporation at lines 64-76. EveJS 261028.1 server/src/services/corporation/corpmgrService.js:526-530 likewise returns a corporation row for a resolved corporation and null when none exists.
- Verification on database eve_p3: Host.Tests build passed; CorporationPublicInfoHostWireTests passed 2/2 with 0 skipped; CorporationProtocolTests passed 51/51 with 0 skipped. The earlier test expectation that unknown character IDs resolve to the caller's corporation was corrected from the client/EveJS evidence.
- The graphical client was not used. RPC-01244–RPC-01247 remain untested: those info-window callers request NPC character corporations, and the current code does not resolve an NPC corporation from static data.

## Remaining P1 rows

- RPC-01218–RPC-01241: GetAssetInventory and GetAssetInventoryForLocation (24 rows)
- RPC-01244–RPC-01247: GetCorporationIDForCharacter in NPC info-window paths (4 rows)
- RPC-01260–RPC-01261: SearchAssets (2 rows)

Total remaining: 30 rows.
