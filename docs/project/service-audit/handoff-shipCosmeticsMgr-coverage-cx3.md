# shipCosmeticsMgr Coverage Handoff (cx3)

## Completed

The Host wire regression `Ship_cosmetics_gm_license_calls_persist_and_keep_client_shapes_over_host_wire` covers the six `GiveSkin` rows (RPC-02301 through RPC-02306) and both `GMExpireSkinLicense` rows (RPC-02293 and RPC-02294). It sends client-shaped calls through the Host service registry, checks `None` replies and readback fields, flushes the real `GameStoreCache` to PostgreSQL, and checks the persisted expiry value.

The test exposed a default-value mismatch in `GiveSkin`: the handler inferred duration and single-use values from the SDE license when the client omitted those keyword arguments. V24.01 calls the same RPC without keywords for permanent licenses, uses `duration` for time-limited licenses, and uses `isSingleUse=True` for limited licenses. The handler now treats omitted duration as zero and omitted `isSingleUse` as false, matching EveJS 261028.1's `Handle_GiveSkin` and `giveSkin` path (`server/src/services/ship/shipCosmeticsMgrService.js:363-377`, `server/src/services/ship/shipCosmeticsState.js:327-384`).

## Client trace

- `eve/client/script/ui/shared/skins/devMenuFunctions.py:14-15` calls `GiveSkin(skinID, duration=duration)`; lines 19-20 call it without keywords for permanent licenses; lines 25-26 call it with `isSingleUse=True`; lines 34-35 call `GMExpireSkinLicense`.
- `eve/client/script/ui/shared/skins/skinPanel.py:632-652` exposes those operations in the GM skin menu.
- `cosmetics/client/cosmeticsSvc.py:114-127` reads personal licenses for the collection and ship type; lines 133-135 reads one license record. These consumers use `skinID`, `expires`, and `isSingleUse` when building the UI.
- The broader manager's other V24.01 callsites are `cosmetics/client/cosmeticsSvc.py:89-90,160-162`, `eve/client/script/ui/cosmetics/ship/shipUtil.py:26-27`, and `shipcosmetics/client/fittingsgateway/quasar.py:33-35`.
- The GM `GiveSkin`/`GMExpireSkinLicense` legacy RPCs return `None` and have no client notification consumer. EveJS also writes their state and returns `null`; current `shipSkinLicenseNoticeMessenger.py:16-32` handles public-gateway third-party license notices, a separate protocol. The Host wire test therefore verifies reply/readback and durable state, with no fabricated notification.

## Still open

RPC-02307 and RPC-02308 (`RemoveSkin`) remain `IMPLEMENTED-UNTESTED`. The client caller `devMenuFunctions.py:77-83` sends `session.corpid` for structure skins and `session.charid` for character skins. Current eve.rs and EveJS removal only support the character-license path; covering the corporation input needs the structure-license owner/state path and a client-visible readback check. Do not mark these rows tested until that path is handled and tested.

## Verification

- `dotnet build dotnet/src/Eve.Host -p:UseSharedCompilation=false`: passed.
- `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false`: passed.
- Focused Host/PostgreSQL test on `eve_p3`: 1 passed, 0 skipped.
- Related Host/PostgreSQL tests on `eve_p3` (`ShipCosmeticsMgrRpcServiceTests`, `CompatibilityRpcServiceTests`, `HomeStationHostTests`, existing cosmetics NS Host test): 25 passed, 0 skipped.
- The real graphical client was not run.
