# Main CI 1626 MachoNet expectation failures

Run `42440293069` at `b5b7f0c01817` failed two cases in `Eve.Services.Tests.DispatchGoldenTests`: `ping:response` and `packets:arguments` (`dotnet-test-results-other-1`). A third failure in `Eve.Host.Tests.HostInputHardeningTests.Positional_arguments_accept_tuple_or_list_and_ignore_macho_version(listArguments: True)` came from the same stale expectation that expanded a top-level list into positional arguments (`dotnet-test-results-host-3`). The dispatch golden failures reproduced locally before adjustment: 19 passed, 2 failed, 0 skipped.

The ping golden was a stale EveJS byte-for-byte expectation for intentional V24 behavior. The list case needed a fuller call-path review, recorded below.

- V24 declares `times` on both `PingReq` and `PingRsp` (`/home/user/eve-logic/carbon/common/script/net/machoNetPacket.py:327-337`). Its server appends a `(wallclock, wallclockNow, mode + '::turnaround')` entry and returns a copy of the received list (`machoNet.py:4307-4313`). EveJS instead emits six synthetic samples (`/home/user/evejs-261028.1/server/src/network/packetDispatcher.js:881-927`). The golden dispatch test now checks the V24 response class, swapped addresses, echoed user/context, received timing prefix, and exact fixed-clock turnaround.
- The V24 client normally constructs positional `args` as a Python tuple from `*args` (`/home/user/eve-logic/carbon/common/script/net/ServiceCallGPCS.py:675-688,230-234`). `CallUp` passes the received value to native `ClockThisWithoutTheStars` (`ServiceCallGPCS.py:120-132`); the helper's behavior for a malformed PyList is not established in the available Python source. The standard client contract is tuple-shaped, so eve.rs rejects a top-level PyList instead of expanding its elements. EveJS accepts the tagged PyList as one argument (`server/src/common/pyPacket.js:205-215`), and its real `PacketDispatcher` response is retained in the fixture. The golden test special-cases this out-of-contract row to assert eve.rs's wrapped error. The Host socket regression checks that the service is not invoked and that a list nested in a tuple remains accepted.
- `HostInputHardeningTests` checks tuple expansion, outer-list rejection, and nested-list acceptance. This keeps valid V24 calls unchanged and records the defensive difference from EveJS for malformed input.

Commit `72454d069` corrected the stale tests while preserving the existing rejection behavior. A later source review retained that behavior because the standard V24 sender emits a tuple; EveJS's single-list fallback is documented as a malformed-input difference. Ping uses the V24 behavior and intentionally differs from EveJS's synthetic six-entry timing list.

## Verification

- Before adjustment, the focused golden class had 19 passed, 2 failed, 0 skipped; the failures were the stale list and ping byte expectations.
- The focused `Eve.Services.Tests.MachoTests` and `DispatchGoldenTests` classes: 221 passed, 0 failed, 0 skipped.
- The seven related Host classes (`HostServerTests`, `HostInputHardeningTests`, `GlobalConfigStoreTests`, `GlobalConfigWireTests`, `GlobalConfigPersistenceTests`, `MachoNetAuditWireTests`, and `MachoNetServiceTests`) on `eve_p4`: 462 passed, 0 failed, 0 skipped.
- Release builds of `Eve.Macho`, `Eve.Host`, `Eve.Services.Tests`, and `Eve.Host.Tests` with warnings as errors: 0 warnings, 0 errors.
- GitHub Actions run 1627 (`42442020309`) for commit `72454d069bce` completed successfully. Its TRX files recorded 8,658 passed, 0 failed, and 25 `NotExecuted`; those 25 are not counted as passes: 22 abyssal simulation tests require `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE`, one Universe export test requires `EVE_CLIENT_SDE_DIR`, and the Dogma and Market benchmarks each require their opt-in environment variable.
- The graphical V24 client was not run.
