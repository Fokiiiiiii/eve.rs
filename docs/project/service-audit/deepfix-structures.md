# Deep correctness: structures, starbases, and moon mining

## Scope and decision

The high-frequency player flows reviewed were structure anchoring/deployment and refinery moon extraction: opening the moon schedule, reading the active extraction/resources, starting a pull, fracturing/cancelling it, and reflecting updates in the UI. I found and fixed an authorization defect in the three moon extraction read RPCs. The player's controlled structure and corp role are server-side session/state facts; the old Host route read the shared legacy extraction state without validating either.

The separate starbase tower read route remains an explicit gap: `posMgr.GetControlTowers` is listed OPEN in `docs/project/TODO.md` because no authoritative tower provider exists. I did not invent tower rows or replace a missing domain source.

## Client evidence (whole V24.01 eve-logic)

- `eve/client/script/ui/moonmining/schedulingSvc.py:22-25` opens the schedule by calling `GetExtractionAndEventIDFromStructureID()`, then `GetMoonResourcesForStructure()`; both are no-argument RPCs. Lines 32-34 unpack the response as `(eventID, extraction)`. Lines 36-40 locally check Station Manager before `GetExtractionsForCorp()`. Lines 42-55 validate the selected extraction duration and call `StartNewExtraction(structureID, durationSeconds, addToCalendar)`. Lines 60-65 call `FractureChunkWithStructure()`; lines 70-78 confirm and call `CancelExtraction()`, and read moon resources.
- UI consumers: `eve/client/script/ui/moonmining/scheduling.py:80-82` falls back to the resource RPC; lines 103-119 handle a missing extraction and refresh event/button state; lines 165-173 stop/restart the drill. `moonminingExtraHudBtns.py:62-64,105-108,181-182` reads the extraction and consumes `OnMoonExtractionUpdate`; `structure/structureBrowser/moonminingEventsCont.py:94-99` consumes the corp extraction list. `schedulingSvc.py:111-113` clears its laser timestamp on the update. The client therefore does not supply structure/corporation identity for these reads: the service must derive and authorize from the current session, and the list's local role check is not a server security boundary.
- Error display: the client catches the fracture RPC exception and resets the local laser-fired timestamp (`schedulingSvc.py:60-65`); a cancel is only sent after confirmation (`:70-74`). The Host uses EveJS-compatible `StructureDefenseDenied` for unauthorized structure reads and the exact Station Manager notice for corp listing.
- EveJS reference: `/home/user/evejs-261028.1/src/server/src/services/structure/moonExtractionsService.js:1108-1122` validates structure read access for extraction/event; 1124-1142 derives the corp and requires the same corp plus Station Manager for listing; 1144-1166 validates resource reads. Its shared `canReadStructureExtraction` at 176-183 allows either owner-corporation Station Manager or a controlling structure session with moon service access; 185-189 maps denial to `STRUCTURE_CONTROL_DENIED`. Error translation is at 232-268. This is the behavior implemented here.
- EVE University mechanics cross-check: [Moon mining](https://wiki.eveuniversity.org/Moon_mining) describes 6–56 day pulls, 30,000 m³ per hour, and automatic chunk detonation after roughly three hours; the page flags that it may need updating. The current domain uses 518,460–4,838,400 seconds, 720,000 m³/day, and a 30-second fracture completion (`dotnet/src/Eve.Structures/MoonExtractions.cs:30-50`). No mechanics constants were changed. The task's access-control fix does not depend on SDE-derived values.

## Implementation and observable behavior

- `dotnet/src/Eve.Services.Core/StructureDataRpcServices.cs:18-28,53-55` adds an optional read authorization callback and executes it before reading state.
- Production registration supplies `StructureMoonAuthority.ValidateReadAccessAsync` (`dotnet/src/Eve.Host/Rpc/StructureRpc.cs:63-66`). The authority enforces same-corporation + Station Manager for `GetExtractionsForCorp` (`dotnet/src/Eve.Host/Persistence/Structures/StructureMoonAuthority.cs:46-56`), and owner-corporation Station Manager or a persisted current control seat with structure-specific moon access for structure reads (`:58-86`). It responds with the reference's `Station Manager role is required.` notice or `StructureDefenseDenied`.
- Mutation flow was inspected but not changed: start commits the extraction before sending `OnMoonExtractionUpdate` (`StructureMoonAuthority.cs:96-115`). Domain mutations persist extraction/event/deadline jobs together (`MoonExtractions.cs`); the timer and store path are covered by the existing tests below.
- Anchoring delay persistence was checked as an adjacent first-hours flow: deployment anchoring duration is stored in the structure snapshot and used to schedule the transition (`StructureModel.cs:143-144`, `StructureTimers.cs:83-84,167-168`, `StructureStore.cs:29,116,152-167`). No anchoring code change was needed.
- Regression: `MoonExtractionStartHostTests.Moon_reads_reject_other_corporations_and_uncontrolled_structures` (`dotnet/tests/Eve.Host.Tests/MoonExtractionStartHostTests.cs:343-388`) proves owner read succeeds, unrelated structure resources are denied, corp list gives the named Station Manager notice, and a cross-corp list stays denied even for a manager. It runs through the DB-backed Host route with the real stores and authority.

## Checks performed

- `Eve.Services.Core` build: passed, 0 warnings/errors.
- `Eve.Host` build: passed, 0 warnings/errors.
- Host focused moon tests on PostgreSQL `eve_p4`: 14 passed, 0 failed, 0 skipped.
- Selected Host neighboring tests on `eve_p4` (`MoonExtractionStartHostTests`, `MoonExtractionStoreTests`, `StructureDataRpcPersistenceTests`, `Rpc01790HostCoverageTests`, `Rpc01800To02100HostWireTests`, `CharacterRpcWireTests`): 18 passed, 0 failed, 0 skipped.
- `Eve.Services.Core.Tests.StructureDataRpcServicesTests`: 14 passed, 0 failed, 0 skipped.
- `Eve.Structures.Tests.StructureTimerAnchoringDelayTests` on `eve_p4`: 3 passed, 0 failed, 0 skipped.
- The native graphical client was not exercised. The wiki article itself warns that its mechanics text may be stale; no game build or SDE mechanic change was based solely on it.
