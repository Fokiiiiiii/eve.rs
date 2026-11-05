# P1 client verification batch 7

## Completed in this handoff

- Searched all requested RPC names across the whole V24.01 eve-logic tree and followed reply consumers for all 30 audit rows.
- Compared the corresponding eve.rs handlers and EveJS 261018.1 implementations.
- Recorded all 30 row verdicts and evidence in `CLIENT_VERIFIED.md`.
- No database was accessed.

## Open work

- Add successful registered-Host wire assertions for rows marked NO-TEST (ship assembly/leave, LPStoreMgr calls, LPSvc balances, RWManager, XmppChatMgr help channel, and successful bill payment/automatic-pay reads).
- Resolve the LP mismatch rows RPC-00674, RPC-00677 and RPC-00678. Eve.rs currently refuses these calls; EveJS's two transfer operations perform state changes. Do not replace them with a fake success.
- Then compile the touched .NET projects, run the smallest applicable non-database test locally, push green increments, and verify selected Actions plus push CI.
- No code or tests were changed in this audit-only increment. No graphical client or database was used.

## Evidence

- Client sources and line references are in the batch 7 table in `CLIENT_VERIFIED.md`.
- Handler/test references and EveJS source locations are recorded per row there.
