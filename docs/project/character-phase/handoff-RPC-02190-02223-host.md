# RPC-02190–02223 Host wire handoff

## Scope and implementation

The no-database test, `CharacterRpcWireTests.Character_and_support_rpc_contracts_reach_registered_host_services`, sends calls through `HostServer`, `PacketDispatcher`, and `ServiceRegistry`. It registers the real `PaperDollServerRpcService` and the normal `CoreServices.Create` services. Production registration is `dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:76` for paperDollServer and `dotnet/src/Eve.Services.Core/CoreServices.cs:60,66` for petitioner and peaceTreatyMgr.

The test asserts paper-doll absence as None, portrait as (empty rows, None), update acknowledgements as None, the two-rowset treaty tuple, and petitioner results consumed by the client (None for rating/cancel/close, zero for claim, false for create). The existing paper-doll persistence and owner checks remain in `CharacterRpcServices.cs:128-143`; the wire test uses an unpopulated service because it does not require PostgreSQL.

## Client call and consumer evidence (whole eve-logic search)

- RPC-02190–02195, RPC-02200–02201 `GetPaperDollData`: callers are `eve/client/script/ui/shared/preview.py:133,144,835`, `eve/client/script/ui/shared/neocom/avatarHeaderPreview.py:26`, and `eve/client/script/environment/spaceObject/corpse.py:61`. Preview checks None and raises `CharacterHasNoDNA` at `preview.py:135-136,146-147`; avatar preview passes the result to its preview routine at `avatarHeaderPreview.py:27`.
- RPC-02204–02205 `GetPaperDollPortraitDataFor`: sole caller `eve/client/script/ui/services/ccSvc.py:145`; it unpacks `data, _`, returns the first row when present at lines 146-148, otherwise None.
- RPC-02208–02209 `UpdateExistingCharacterFull`: creation UIs call the cc service at `characterCreationContainer.py:1879` and `characterCreationContainer_new.py:1343`; `ccSvc.py:114-122` forwards the update, clears paper-doll cache, invalidates GetPaperDollData and refreshes selection data.
- RPC-02210–02211 `UpdateExistingCharacterLimited`: creation UIs call the cc service at `characterCreationContainer.py:1881` and `characterCreationContainer_new.py:1345`; `ccSvc.py:124-130` forwards the update and refreshes the same caches.
- RPC-02213–02214 `GetPeaceTreatiesForSession`: sole caller `eve/client/script/ui/services/wars/war_cso.py:350`; it returns the outgoing/incoming pair at lines 351-352.
- RPC-02215–02216 `AddPetitionRating`: caller `eve/client/script/ui/shared/neocom/petition.py:450`; its result is ignored while local rating fields update at lines 451-457.
- RPC-02217–02218 `CancelPetition`: caller `petition.py:1004-1007`; result is ignored before refresh and close.
- RPC-02219–02220 `ClaimPetition`: caller `petition.py:1050-1058`; positive results mark it claimed, zero shows the failure dialog.
- RPC-02221–02222 `ClosePetition`: caller `petition.py:1016-1019`; result is ignored before refresh and close.
- RPC-02223 `CreatePetition`: callers `petition.py:715` and `petitionWindow.py:308`. The former checks false and shows failure; the latter emits `OnPetitionCreated` at line 309.

## EveJS v0.12.9 comparison

- `server/src/services/character/paperDollServerService.js:186-194,219-227` returns stored doll data and portrait tuple; full update persists owned-character appearance and returns null at lines 229-258. Limited update uses the same store at lines 260-289.
- `server/src/services/corporation/peaceTreatyMgrService.js:63-74` returns outgoing and incoming treaty rowsets selected by alliance, then corporation.
- `server/src/services/support/petitionerService.js:206-213,252-293` returns false for CreatePetition, zero for ClaimPetition, and null for rating/cancel/close acknowledgements while recording audit events. eve.rs uses the same client-visible shapes; no deviation is recorded.

## Verification boundary

Passed locally: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Character_and_support_rpc_contracts_reach_registered_host_services'` (1 passed, 0 skipped). This covers real Host dispatch and the client-consumed reply shapes without PostgreSQL. It does not check persisted appearance writes, treaty rows populated by the war backend, petition state mutations, or the graphical client.
