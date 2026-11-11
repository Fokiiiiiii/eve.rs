# Wire shape and marshal audit

## Scope

This audit samples the high-traffic `charMgr`, `invbroker`, `config`, `dogmaIM`, contact notification and mail notification paths. It follows the V24.01 client callsites under `/home/user/eve-logic`, compares the RPC and marshal behavior with EveJS at `/home/user/evejs-261108.1`, and exercises the registered Host routes. The changes are limited to wire representation, shared marshal behavior, and the sampled `charMgr` values; they do not add new gameplay operations.

Before selecting these paths, the complete `/home/user/eve-logic` tree was searched for the relevant `GetPublicInfo`, `GetPublicInfo3`, `GetCapacity`, notification, contact-edit and dogma weapon-bank consumers. The line references below identify the concrete client read/call sites for each sampled route.

## Confirmed contracts and corrections

### Strings and Python 2 byte strings

- The client consumes character descriptions and names as text (`characterInfoWindow.py:68-75`; `characterOverviewPanel/characterOverviewPanel.py:47`). EveJS `marshal.js:304-317` sends ASCII JavaScript strings as Python 2 byte strings and non-ASCII text as UTF-8 WString. `MarshalEncoder.WriteString` now follows that split. Explicit `PyBytes` stays bytes.
- Host wire coverage checks non-ASCII names/descriptions as WString and verifies the byte-exact public-info CRowset payload (`CharMgrPublicInfoHostTests.cs:39-63, 67-97`). The marshal golden vectors now include the WString form; `CodecTests.cs` keeps the explicit byte-string case.

### Integer width, packed rows, and FILETIME

- EveJS `marshal.js:537-565` chooses the smallest integer opcode for JavaScript safe integer values and writes JavaScript `BigInt` as signed 64-bit. The Host-side `Py.Number(long)` keeps IDs lossless while letting `MarshalEncoder.Integer` select the wire width (`Eve.Services/Py.cs:31`; `MarshalEncoder.cs:81-109`). This replaces explicit PyLong wrappers around ordinary IDs in the sampled notifications.
- Packed row UI4 is consumed as signed 32-bit by the V24 Windows client. EveJS records the installed-client behavior at `marshal.js:945-952` and `1126-1130`. UI8 and FILETIME use unsigned 64-bit reads (`1122-1125`), while values beyond JavaScript's safe integer range remain BigInt; CY uses the signed 64-bit 1/10000 representation and decodes as a real (`920-943`, `1115-1120`). .NET, Rust and the Python headless codec now use the same conversions and range checks. Golden cases cover signed UI4 bits, maximum UI8, FILETIME, CY rounding/boundaries, and invalid encoder ranges.
- Repeated `blue.DBRowDescriptor` objects are shared by saved-stream references: EveJS starts/finalizes the sharing table at `marshal.js:202-239` and recognizes the descriptor at `213-217`. `MarshalEncoder.ObjectEx` now writes the same save/reference form for repeated row descriptors (`MarshalEncoder.cs:389-424`). Decoding nested substreams retains their original bytes so an opaque nested payload can be re-encoded without changing it.

### Character public information and rowset layouts

- The V24 character window calls `charMgr.GetPublicInfo3`, reads the first CRowset row, then accesses `description`, `startDateTime`, `militiaFactionID` and `bloodlineID` as row attributes (`characterInfoWindow.py:64-75, 194-198`). The bounty flow invalidates the cached `GetPublicInfo3` call (`bountyWindow.py:452`). EveJS documents and emits a one-row seven-column `carbon.common.script.sys.crowset.CRowset` (`charMgrService.js:432-463`). The Host test asserts its constructor, column order/types, null and empty fields, and the raw substream bytes (`CharMgrPublicInfoHostTests.cs:39-63`).
- The V24 character sheet and standings code also call `GetPublicInfo` and read it by attribute (`characterOverviewPanel/characterOverviewPanel.py:47`; `panelStandings.py:121-127`). EveJS's `buildPublicInfoKeyVal` returns the ordered 17-field `util.KeyVal` shape (`charMgrService.js:465-496`). The service now emits those fields in that order, uses `None` where the source has no career/alliance/faction value, and retains the empty description/title values where the client expects strings (`CharMgrService.cs:140-177`). The prior extra medal projection was removed; the V24 CRowset contract has seven fields and the GetPublicInfo reference has seventeen.
- Long character IDs and FILETIME values are retained without conversion through floating point. Tests include an ID above signed 32-bit range and FILETIME values above the JavaScript safe integer range (`CharMgrPublicInfoHostTests.cs:99-151`).

### Notifications, dicts, KeyVals, and error replies

- The V24 client passes `OnNotificationReceived(notificationID, typeID, senderID, created, data)` directly to `Notification` (`notificationSvc.py:403-415`). EveJS contact edits create type 90 with `{level, message}` data (`charMgrService.js:1083-1128`), and its notification marshaller turns ordinary objects into dicts and arrays into lists (`notificationState.js:230-285`). The Host test checks the full client notification wrapper, inner five-argument tuple, integer widths, ASCII message bytes, persisted record, and recipient route (`ContactEditHostWireTests.cs:13-67`).
- EveJS converts JavaScript object properties by iterating `Object.entries` and preserving each property name as a string (`notificationState.js:230-285`). A PostgreSQL-backed Host regression now includes nested JSON key `"42"`; it failed before the fix because the shared numeric helper emitted a numeric PyLong key. The mail and mailbox JSON converters now preserve object keys as PyStr while still converting numeric values with `Py.Number` (`PersistenceMailSendHostAdapter.cs:772-803`; `PersistenceTransactionMailbox.cs:238-258`). The focused Host and notification tests assert the decoded key remains Python byte-string `b"42"`.
- For new-mail display, V24 `NewMailFormatter.MakeData` returns an ordinary dictionary with `senderName`, `subject`, and `msg`; `msg` is the mail KeyVal built in `mailSvc.OnMailSent` (`newMail.py:16-18`; `mailSvc.py:1119-1150,1213-1218`). The existing biomass mail notification differed: its outer data was a `util.KeyVal`, and ordinary IDs were forced to PyLong. A strengthened PostgreSQL-backed Host test first failed on the outer type (`PyObject`, expected `PyDict`). `NotificationData` now uses a dict outside and a KeyVal for `msg`, and emits numeric IDs through `Py.Number` (`PersistenceMailSendHostAdapter.cs:801-832`; `CharacterHostTests.cs:1249-1257`). The focused test then passed.
- The mail notification callback is an existing Host flow. EveJS's mail path sends `OnMailSent` (`mailNotifications.js:41-60`), after which the V24 client constructs its local type-1004 notification (`mailSvc.py:1213-1218`). EveJS character deletion does not currently create this biomass mail notification; its gameplay parity is outside this wire-only audit and is listed below as unchecked.
- The sampled `config` error route was exercised through a real Host connection and its `MachoMessage.ErrorResponse` decoded to the expected `UserError` class/code, while a successful empty result stayed an empty PyList (`ConfigIdentifierHostWireTests.cs:13-72`). No error envelope change was needed.

### Other sampled Host replies

- `invbroker` was exercised through `GetInventory`, the resulting bound object, `List`, and `GetCapacity`; the route test checks the returned `util.KeyVal` capacity and row/list shapes (`InvBrokerInventoryHostWireTests.cs:21-100`). The reference implementation uses the same `GetInventory` and `GetCapacity` bound flow (`invBrokerService.js:10824-10860, 12062-12080`).
- `dogmaIM` weapon-bank RPCs were invoked through Host registration and checked for the map of master module IDs to slave-ID lists and subsequent unlink state (`DogmaWeaponBanksHostWireTests.cs:14-100`). The client calls `remoteDogmaLM.LinkWeapons` and `UnlinkModule` (`clientDogmaLocation.py:752-785`); the sampled response required no shape change.

## Verification recorded

All database-backed tests below used `eve_p3` (`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p3`).

- After merging current `origin/main`, Release builds with `TreatWarningsAsErrors=true` succeeded with 0 warnings and 0 errors for `Eve.Marshal`, `Eve.Host`, `Eve.Services`, `Eve.Services.CharacterInfo`, `Eve.Marshal.Tests`, `Eve.Host.Tests` and `Eve.Services.CharacterInfo.Tests`.
- `Eve.Marshal.Tests` golden suite: 1,192 passed, 0 failed, 0 skipped.
- Rust `eve-marshal` tests: 25 passed, 0 failed, 0 ignored; `eve-oracle --test marshal_golden`: 1 passed. `cargo fmt --all -- --check`, `cargo check -p eve-marshal -p eve-oracle` and `cargo clippy -p eve-marshal -p eve-oracle --all-targets -- -D warnings` passed.
- Python `test_marshal_golden.py`: 999 passed, no skips.
- CharacterInfo DB-backed test project: 83 passed, 0 failed, 0 skipped (including `CharMgrTests` and `DogmaServiceTests`).
- Host `HostServerTests`: 473 passed, 0 failed, 0 skipped. `ContactEditHostWireTests`: 1 passed. `EntosisOwnerNotificationTests`: 2 passed. The biomass persistence/restart/mail notification test is included in the full `CharacterHostTests` run.
- Host inventory, config-error and weapon-bank scenarios: 3 passed, 0 failed, 0 skipped.
- After merging current `origin/main`, the full `CharacterHostTests` class on `eve_p3`: 80 passed, 0 failed, 3 skipped (83 total). All three skips are `[RealAbyssalSimPgFact]` cases (killmail persistence, ESS placement, and clone-jump space entry) requiring `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE`, which are unavailable locally; they are not counted as passes.
- The biomass notification dict mismatch was observed as a failing end-to-end assertion before the code change, then passed after the correction.
- The nested numeric-looking JSON key mismatch was observed as a failing end-to-end assertion before the code change, then passed after both Host converters preserved JSON object keys as strings.

## Not checked

- The unmodified graphical V24.01 client was not launched. The verification is against source callsites and raw Host packets decoded by the test client.
- This is a sample of high-traffic Host services, not an exhaustive RPC-by-RPC audit of the full client or all Host registrations.
- The bitmap/notification UI after the client consumes the mail event was not observed in a graphical session. The payload was checked against `NewMailFormatter` and `OnNotificationReceived` source behavior.
- EveJS biomass deletion does not implement the Host's existing corporate mail notification flow, so equivalence of that gameplay behavior is not asserted here.
- The marshal fixtures and selected focused slices were checked; full repository test suites and the live graphical-client suite were not run locally. The two real-simulation tests noted above remain unverified.
