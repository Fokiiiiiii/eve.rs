# CI run 1726 failure audit

## Scope

Run 1726 on `583bd6dc1c86` failed before this worktree's fixes were added: [GitHub Actions run 1726](https://github.com/Fokiiiiiii/eve.rs/actions/runs/43024359089). This records the observed causes and the source checks behind the corrections.

## Findings and corrections

### Notification integer and FILETIME shapes

`CrimewatchKillRightCreationHostTests` expected a forced `PyLong` for a live notification ID. Reproducing the test locally showed the notification callback's strict type assertion failed before its retry could deliver the notification; the reported delivery count of zero was a test callback failure, not evidence that the producer omitted the notice. EveJS `encodeInteger` uses its ordinary integer encoding for JavaScript numbers and `encodeLong` explicitly writes long integers (`server/src/network/tcp/utils/marshal.js:537-573`). Its kill-right-created payload contains ordinary numeric IDs and an explicitly tagged FILETIME (`server/src/services/security/crimewatchState.js:2921-2927`), and sends that payload with `charid`; a source comment records that this matches retail captures (`:2967-2976`). EveJS also sends kill-right-used IDs as ordinary JavaScript numbers (`server/src/space/combat/killmailTracker.js:812-815`). The V24.01 consumers invalidate or reload their kill-right data in `eve/client/script/ui/shared/bountySvc.py:154-165` and `eve/client/script/ui/shared/neocom/charsheet/killRightsPanel.py:80-86`.

The Host now uses `InventoryWire.Id` for these numeric IDs and routes `OnKillRightCreated` with `charid`. The tests retain exact argument, route, and delivery-count assertions, using the same numeric shape as EveJS.

For regular notification payloads EveJS builds notification ID, type ID, and sender ID from ordinary numbers, but builds `created` as a FILETIME long (`server/src/services/notifications/notificationState.js:478-498`). Moon extraction rows likewise use ordinary numbers for identifiers and duration, and explicitly encode their time fields as FILETIMEs (`server/src/services/structure/moonExtractionsService.js:531-547`). The notification tests now distinguish those number fields from the FILETIME field and keep exact values and payload shape asserted.

### Packed FILETIME values

The ReadSide test's packed `I8` value `9000000000` is within JavaScript's safe integer range and decodes as `PyInt`. Its FILETIME exceeds that range, so the marshal decoder retains it as `PyBigUnsigned` rather than rounding it (`dotnet/src/Eve.Marshal/PyValue.cs:132-157`, `MarshalDecoder.cs:515-535`). The corporation application row has the same packed FILETIME behavior. EveJS also preserves packed 64-bit values as `BigInt` when they exceed its safe-number range (`server/src/network/tcp/utils/marshal.js:1122-1125`). Tests now assert the exact integer representation and compare the decoded bytes to the stored creation time; no time-value expectation was relaxed.

### Scoop after unanchoring

The failed live case unanchored a control tower and immediately called the server's `Scoop` RPC. The ship can drift out of range during the unanchor timer. In V24.01, `menusvc.py:3238-3239` routes `Scoop` through `GetCloseAndTryCommand`; `autopilot.py:550-553` determines interaction range from the hull surface distance. EveJS checks the same hull-to-hull distance against 2,500 m (`server/src/services/ship/cargoContainerRuntime.js:1134-1163`), and eve.rs enforces its 2,500 m range in `dotnet/src/Eve.Services.Space/SpaceContainers.Scoop.cs:63-80`.

The live test now issues the client's `CmdFollowBall` movement command toward the tower at 1,000 m before attempting the RPC. It retries only `TargetTooFar` while movement is in progress and still fails on any other refusal or timeout. This makes the test exercise the intended in-range scoop transition instead of relying on the ship remaining at its prior position.

## Validation

- Release builds passed with 0 warnings and 0 errors for `Eve.Host`, `Eve.Host.Tests`, `Eve.ReadSide.Tests`, `Eve.Corporations.Tests`, `Eve.Services.Space`, and `Eve.Services.Space.Tests`.
- On the dedicated `eve_p4` database: Host regression classes 25 passed / 0 skipped; `ReadSideTests` 11 / 0; `CorporationProtocolTests` 55 / 0; `DroneHostTests` and `MutatedDroneHostTests` 29 / 0.
- The edited Python test module passed `py_compile`.
- The focused live headless structure scenario and a graphical V24.01 client session were not run locally; the post-push CI run is required to verify the live scenario.
