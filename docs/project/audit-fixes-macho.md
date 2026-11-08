# MachoNet R14 audit fixes

This records the verified findings from `/home/user/reports/90-audit-eve-macho-r14.md`. Source review compared the current host against the V24.01 client in `/home/user/eve-logic` and EveJS v0.12.9 in `/home/user/evejs-261028.1`.

The configured code-search MCP and Serena tools were unavailable in this environment. I used line-numbered source search and reads as the fallback. The existing repository README files were not edited.

## Confirmed findings

### P2: Fractional packet and address discriminators were truncated

`MachoPacket.Decode` and `MachoAddress.Decode` previously cast numeric values directly to `int`. A `PyReal(6.5)` therefore became `CALL_REQ` (6), and a `PyReal(8.5)` became the `ANY` address kind (8). The new shared discriminator reader accepts only finite, integral values in the `Int32` range. An invalid packet type stays unknown and is not dispatched; an invalid address becomes `Unknown` and cannot supply a service route.

V24.01 constructs packet commands from each packet class's command constant and serializes that command as the first state field (`eve-logic/carbon/common/script/net/machoNetPacket.py:43,57-84`). `MachoAddress.__getstate__` emits the defined address constants (`eve-logic/carbon/common/script/net/machoNetAddress.py:64-72`); its decoder selects address branches by equality with those constants (`:76-84`). EveJS compares the decoded discriminator against its command/address cases (`server/src/common/pyPacket.js:41-49`; `server/src/common/machoAddress.js:48-50`). The client does not produce fractional discriminator values.

Regression coverage sends both malformed forms through a real Host socket session, checks that no unintended service call runs, and confirms a following valid call still succeeds. `Eve.Services.Tests.MachoTests` also checks the decoded values directly.

### P2: PING_RSP discarded the request timing entries

The response now copies the request's `times` list and appends one `(FILETIME, FILETIME, "proxy::turnaround")` entry using the Host's injected clock. The client declares `times` as the PING_REQ/PING_RSP parameter (`eve-logic/carbon/common/script/net/machoNetPacket.py:327-337`), and V24.01 appends the turnaround sample and returns a copy of the full list (`eve-logic/carbon/common/script/net/machoNet.py:4310-4313`). The keepalive path reads the first returned sample (`machoNet.py:3566-3571`). The PING wire regression checks preservation, ordering, timestamp values, and the appended label through Host registration.

This intentionally differs from EveJS's six synthetic samples (`server/src/network/packetDispatcher.js:881-927`): the V24.01 source appends to the received list, and the keepalive consumer uses that returned list. The host clock supplies one millisecond-resolution FILETIME snapshot for both time fields; separate wall-clock and monotonic clock sources are not available in this Host path.

### P2: The Honeycomb write key was not protected

The protected key is now the exact V24.01 setting name, `monolith_honeycomb_public_write_key`. Writes through `machoNet.SetGlobalConfigValue` are refused, and `GlobalConfigStore.Current()` omits that key even when a stale persisted override exists. This matches EveJS's setter refusal and final client-payload filter (`server/src/services/machoNet/globalConfig.js:21,82-103`). V24.01 defines the key as `globalConfig/const.py:81` and reads it as the Honeycomb client write credential (`monolithhoneycomb/__init__.py:137-142`), so returning it to the client would enable external telemetry.

The Host wire regression checks the protected-key error and a later handshake. The PostgreSQL persistence regression seeds a stale value, reopens the store, and verifies it is absent from the client configuration.

### P3: `isDelete=false` with `None` was persisted as a deletion

`GlobalConfigStore.ApplyAsync` now writes a tombstone only when `isDelete` is true. When false, `PyNone` remains a stored value and appears in the next server configuration; connected clients still receive the V24.01 `None` update notification. V24.01 deletes only under `if isDelete`, otherwise it selects/inserts/updates the supplied value (`eve-logic/carbon/common/script/net/machoNet.py:1479-1492`). Its update handler removes a key from the live client map when notified with `None` (`machoNet.py:3356-3366`). EveJS likewise deletes only for `options.isDelete === true` (`server/src/services/machoNet/globalConfig.js:108-115`).

Coverage includes a real Host RPC and notification, in-memory restart behavior, and a PostgreSQL-backed store restart. The explicit-delete path remains covered separately.

### P3: A `PyList` in the positional-arguments slot was expanded

`CallRequest.Decode` now rejects `PyList` when it occupies the outer positional-arguments slot; it no longer invokes a method with the list's elements as separate arguments. A list remains valid as an argument nested inside the required `PyTuple`.

V24.01 creates the call payload as `(1, method, args, kwargs)` (`eve-logic/carbon/common/script/net/ServiceCallGPCS.py:173-174,233-234`), where Python's `*args` supplies a tuple. EveJS's Marshal decoder preserves Python lists as tagged `{type: "list", items: ...}` objects (`server/src/network/tcp/utils/marshal.js:1818-1836`), and `decodeCallRequest` wraps non-array values as one argument (`server/src/common/pyPacket.js:205-215`). I executed that decoder with a tagged list; it returned one list argument. Since a top-level list is not the V24.01 positional tuple, eve.rs refuses this malformed request before the service executes; the Host regression verifies the error and that the service saw no call. This is a deliberate malformed-input difference from EveJS's fallback handling, with no change to valid client calls.

## Verification

The new discriminator, ping, Honeycomb, and `None` regressions failed against the original code before their fixes. The new Host `PyList` regression also failed before the parser change: the request returned `CALL_RSP` and invoked the method instead of being refused.

After the fixes:

- `Eve.Services.Tests.MachoTests`: 21 passed, 0 failed, 0 skipped.
- Selected Host regression tests, including the PostgreSQL-backed restart test on `eve_p4`: 9 passed, 0 failed, 0 skipped.
- The full referenced Host test classes (`HostServerTests`, `GlobalConfigStoreTests`, `GlobalConfigWireTests`, and `GlobalConfigPersistenceTests`) on `eve_p4`: 438 passed, 0 failed, 0 skipped (including the merge-updated Host test class).
- Release builds with `-warnaserror` for `Eve.Macho`, `Eve.Host`, `Eve.Services.Tests`, and `Eve.Host.Tests`: all succeeded with 0 warnings and 0 errors.
- EveJS runtime checks: `decodeCallRequest` returned a tagged PyList as one list argument; `decodePacket` retained command `6.5` as `UNKNOWN(6.5)`, and `decodeAddress` returned `unknown` for address type `8.5`. Calling EveJS's PingReq handler with one client timing entry returned six synthetic entries and did not preserve the client entry. Its global-config store refused the Honeycomb key (`false` result), hid it from the client config, and retained a `None` override.

The graphical V24.01 client was not run. The PING test uses the Host's injected clock and verifies the marshal wire shape; live-client acceptance remains unchecked.
