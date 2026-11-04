# Deep check: standingMgr

Source snapshot: main commit `7a3a02e9`; client: V24.01 eve-logic commit `1dbb5711a072a146e1d228365668ac05655eefe1`.

## Client call and reply flow

| Method | Client caller and arguments | Reply consumed |
|---|---|---|
| `GetNPCNPCStandings` | `eve/client/script/ui/services/standingsvc.py:113-115`, no arguments | Rowset header/rows are filtered by `toID` and `fromID`. |
| `GetCharStandings` | `standingsvc.py:117-123`, no arguments | Character rows are indexed by `fromID`; `standing` is read by standings and agent UI. |
| `GetCorpStandings` | `standingsvc.py:120-126`, no arguments, fetched alongside character standings | Corporation rows are indexed by `fromID`. |
| `GetStandingTransactions` | `standingsvc.py:173-181`, positional `(fromID, toID)`; also consumed by `resourcewars/client/firstcompletiontracker.py:35-36` | Client iterates transaction rows, scales `modification` by 10, caches by the ID pair, and reads `eventTypeID` in Resource Wars. |
| `GetStandingCompositions` | `eve/client/script/ui/shared/neocom/charsheet/standingsPanel.py:109-110` through `standingsvc.py:281-282`, positional `(fromID, toID)` | Returned cached CRowset is passed to the standings panel. |

The client uses no keyword arguments for these methods. All are read calls; there is no mutation or follow-up notification. There are no client-side `UserError` handlers on these calls.

## Host and EveJS

Production registration is `dotnet/src/Eve.Host/Rpc/CorporationRpc.cs:72`; the `standing2` alias resolves to the same service in `dotnet/src/Eve.Host/Hosting/HostComposition.cs:283`. `StandingMgrService.cs:158-231` reads NPC relations, character/corporation standings and standing history. Transaction IDs accept Python int/long; invalid or non-character-targeted requests return an empty list. Nonmembers or invalid composition IDs receive an empty cached rowset with the client’s five-minute/corporation cache metadata rather than an exception. Results use rowset, list, or cached CRowset shapes according to the caller.

EveJS v0.12.9 implements the same methods in `server/src/services/character/standingMgrService.js:194-241`; composition uses the same five-minute cache and `corpid` session information. The selected CSV rows RPC-02561–02566 are already marked IMPLEMENTED-TESTED.

## Evidence and limits

Registered Host wire checks are in `dotnet/tests/Eve.Host.Tests/StandingMgrHostWireTests.cs:13-151`; they cover rowset headers, int/long transaction IDs, invalid inputs, composition cache metadata and the alias. These tests were not executed for this documentation-only batch. No PostgreSQL database or graphical client was used.
