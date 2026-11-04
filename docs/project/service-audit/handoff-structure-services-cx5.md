# Handoff: structureDocking and structureJumpBridgeMgr (cx5)

## Verified

- The four `structureDocking` rows were stale. `StructureRpc.cs:75-78` registers the service. Production callbacks in `StructureComposition.cs:441-446` call `SpaceSessions.DockStructureAsync` and `UndockStructureAsync`; `StructureRpcServices.cs:181-190` preserves the client argument order and reads the `ignoreContraband` keyword. The Space transition moves the active ship into the structure hangar, persists the character location, and sends session and docking notifications. Undock restores the ship to space and clears the structure session attribute.
- V24.01 callers and consumers across the client tree:
  - `eve/client/script/ui/services/structure/structureDocking.py:25-53,64-77` sends Undock with the `ignoreContraband` keyword after a local state check, and Dock after autopilot navigation; both RPC values are ignored.
  - `eve/client/script/ui/shared/dockedUI/undockingSvc.py:126` invokes structure Undock from the undocking flow.
  - `eve/client/script/ui/services/menusvc.py:2980-2989` and `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:358` reach structure Dock through menu actions.
  - `eve/client/script/parklife/autopilot.py:348-350` and `eve/client/script/ui/services/menusvc.py:2105-2108` call `CmdJumpThroughStructureStargate`; the callers use its truth value to continue or stop movement.
  - `eve/client/script/ui/services/structure/structureDirectory.py:175-177` forwards `GetJbStructureDestination`; `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:361-363` consumes the returned solar-system ID and handles a missing destination.
  - `eve/client/script/ui/shared/info/infosvc.py:820-823` consumes `GetLinkedStructure` as a two-ID pair, then fetches the linked structure details for the info window.
- EveJS 261028.1 `server/src/services/structure/structureDockingService.js:137-239` confirms Dock/Undock return null after transitions; `structureJumpBridgeMgrService.js:1693-1711` returns destination ID or the linked pair/null. Its `CmdJumpThroughStructureStargate` implementation at `:1713-1884` performs access, range, fuel/payment and session-settlement checks.
- The current `EVE_RS_COVERAGE.csv` marks RPC-02659–02662 and RPC-02669–02672 `IMPLEMENTED-TESTED`. RPC-02667/02668 remain open: Host currently returns an error for `CmdJumpThroughStructureStargate`, with no travel backend.

## Mechanics to apply when implementing the remaining travel rows

EVE University’s [Ansiblex Jump Bridge article](https://wiki.eveuniversity.org/Pharolux_Cyno_Beacon) describes paired bridges, ACL access, a 5 LY maximum, ship-mass restrictions, and fuel/toll behavior. The [V24.01 patch notes](https://wiki.eveuniversity.org/UniWiki%3APatch_Notes/Patch_Notes_-_Version_24.01) also describe newer Ansiblex capacitor and access changes. Reconcile current wiki mechanics with the pinned game data and client contract before porting EveJS settlement behavior.

## Checks run locally on eve_p5

- `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- `dotnet build dotnet/tests/Eve.Services.Space.Tests -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- Host focused filter for docking route, jump-bridge lookups and the settings/travel wire path: 3 passed, 0 failed, 0 skipped.
- `StructureDockingTests|StructureUndockingTests`: 6 passed, 0 failed, 0 skipped, including PostgreSQL-backed transition/persistence and notification checks.
- The V24.01 graphical client was not launched.

## Remaining rows

- RPC-02667 and RPC-02668 — `structureJumpBridgeMgr.CmdJumpThroughStructureStargate` need the real structure travel, validation, persistent cost/fuel handling, session transition, notifications, and registered Host wire test. Do not turn the current refusal into a success response without that flow.
