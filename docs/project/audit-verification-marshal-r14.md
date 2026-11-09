# R14 Marshal Audit Follow-up

## Result

All four findings in `/home/user/reports/90-audit-eve-marshal-r14.md` describe real compatibility or input-validation bugs. They are already fixed in the current branch history, so this follow-up required no codec changes. No finding was dismissed as a false positive. The earlier implementation and evidence record is [audit-fixes-marshal.md](audit-fixes-marshal.md).

## Source comparison

V24.01 routes Macho packet bytes through `blue.marshal.Save` and `blue.marshal.Load` in `/home/user/eve-logic/carbon/common/script/net/machobase.py:18-24`; packaged Destiny actions are also loaded with `blue.marshal.Load` in `/home/user/eve-logic/eve/client/script/remote/michelle.py:491-499`. The whole client search also found the same marshal API in object caching (`carbon/common/script/net/objectCaching.py:642-700`), persisted rowsets (`carbon/common/script/sys/crowset.py:193-260`), settings (`carbonui/util/settings.py:94,209`), and Dogma state (`eve/common/script/dogma/baseDogmaLocation.py:1835`). This confirms the wire format is shared across Macho and other client data paths. The eve.rs receive path decodes before dispatch in `dotnet/src/Eve.Host/HostServer.cs:741-743`.

The matching executable reference is `/home/user/evejs-261028.1/server/src/network/tcp/utils/marshal.js`:

- **P1, `TY_PICKLER`: confirmed and fixed.** EveJS emits opcode plus pickle bytes without a marshal length (`marshal.js:510-523`) and scans pickle opcodes through `STOP` (`1590-1630`, `1936-1943`). eve.rs now uses `PickleBoundary.FindEnd` within the payload limit (`dotnet/src/Eve.Marshal/MarshalDecoder.cs:500-502`) and writes the opcode followed directly by the pickle (`MarshalEncoder.cs:328-335`).
- **P1, signed `TY_VARINT`: confirmed and fixed.** EveJS interprets the little-endian payload as two's-complement (`marshal.js:1685-1709`). The .NET decoder now constructs a signed `BigInteger` from those bytes and retains signed large values (`MarshalDecoder.cs:516-537`); its encoder adds a zero sign byte only for unsigned positive values whose high bit is set (`MarshalEncoder.cs:344-353`).
- **P2, payload/save-table bounds and ObjectEx terminators: confirmed and fixed.** EveJS bounds reads by `valueEnd` and validates trailer indices before decoding (`marshal.js:1392-1414`, `1444-1512`); ObjectEx requires both terminators (`1878-1897`). .NET derives `_valueEnd` before parsing and validates each save-table entry (`MarshalDecoder.cs:85-109`), routes payload reads through that limit (`130-181`), and rejects either missing ObjectEx terminator (`551-584`).
- **P2, invalid references and container identity: confirmed and fixed.** EveJS rejects references to unpublished slots (`marshal.js:1945-1960`) and publishes lists and dictionaries before decoding children (`1828-1852`). .NET validates the save index (`MarshalDecoder.cs:185-201`), publishes lists and dictionaries before their children (`319-331`, `361-375`), and rejects an unpublished reference rather than returning `None` (`503-510`).

The Host integration regression test sends raw client-style pickle, negative varint, and saved-list-reference bytes through a registered service and asserts the received values and list identity (`dotnet/tests/Eve.Host.Tests/HostServerTests.cs:2513-2546`). The malformed save-table boundary test verifies the packet is refused before dispatch (`2482-2511`). The decoder regression class covers negative-width boundaries, missing ObjectEx terminators, truncated values, and unpublished references (`dotnet/tests/Eve.Marshal.Tests/MarshalAuditRegressionTests.cs`).

## Verification in this follow-up

- Release build of `Eve.Marshal.Tests`: passed, 0 warnings and 0 errors.
- Release build of `Eve.Host.Tests` and dependencies: passed, 0 warnings and 0 errors.
- `Eve.Marshal.Tests` Release: 1,157 passed, 0 failed, 0 skipped.
- Host Marshal-ingress tests (`Client_marshal_values_reach_a_registered_service_with_their_wire_identity` and `A_payload_reading_into_the_save_table_disconnects_before_dispatch_and_the_front_survives`): 2 passed, 0 failed, 0 skipped.
- DB-backed Host owner-row wire test: 1 passed on `eve_p3` and 1 passed on `eve_p10`.
- Direct EveJS 261028.1 execution: the pickle `STOP` boundary preserved the following marshal value, and `2f 01 ff` decoded to `-1`.
- A run filtered to the `HostServerTests` partial class was interrupted after unrelated composed-host tests failed with PostgreSQL error `53100` (`No space left on device`) while loading SDE rows. The test filter spans many partial-class files; it was too broad for the available disk. Those failures are environmental and are not counted as passes. The focused ingress and DB-backed wire checks above passed afterward.

The test-first red check for the original fixes is recorded in [audit-fixes-marshal.md](audit-fixes-marshal.md). This follow-up did not repeat that check against pre-fix commits because the fixes were already present in the branch.

## Not checked

No graphical V24.01 client was launched, and no live EveJS server session was run. The client behavior was checked from its V24.01 source callsites, and EveJS was executed directly for the pickle-boundary and signed-varint vectors.
