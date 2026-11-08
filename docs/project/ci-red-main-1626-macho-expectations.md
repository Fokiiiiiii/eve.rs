# Main CI 1626 MachoNet expectation failures

Run `42440293069` at `b5b7f0c01817` failed two cases in `Eve.Services.Tests.DispatchGoldenTests`: `ping:response` and `packets:arguments` (`dotnet-test-results-other-1`). A third failure in `Eve.Host.Tests.HostInputHardeningTests.Positional_arguments_accept_tuple_or_list_and_ignore_macho_version(listArguments: True)` came from the same obsolete outer-list expectation (`dotnet-test-results-host-3`). The dispatch golden failures reproduced locally before adjustment: 19 passed, 2 failed, 0 skipped.

Both were stale EveJS byte-for-byte expectations for intentional V24 behavior already covered by client-source review and Host regressions:

- V24 declares `times` on both `PingReq` and `PingRsp` (`/home/user/eve-logic/carbon/common/script/net/machoNetPacket.py:327-337`). Its server appends a `(wallclock, wallclockNow, mode + '::turnaround')` entry and returns a copy of the received list (`machoNet.py:4307-4313`). EveJS instead emits six synthetic samples (`/home/user/evejs-261028.1/server/src/network/packetDispatcher.js:881-927`). The golden dispatch test now checks the V24 response class, swapped addresses, echoed user/context, received timing prefix, and exact fixed-clock turnaround.
- V24 constructs positional `args` as a Python tuple through `RemoteServiceCall(..., *args, ...)` and `payload = (1, method, args, kw2)` (`/home/user/eve-logic/carbon/common/script/net/ServiceCallGPCS.py:173-174,230-234`). A top-level list is not a valid client argument tuple. EveJS treats a tagged list as one argument, while eve.rs rejects that malformed outer slot. The golden test now expects V24's wrapped error for this case; the Host regression also verifies the service is not invoked and a nested list inside a tuple remains accepted.
- `HostInputHardeningTests` had kept an older expectation that a top-level `PyList` is expanded as multiple positional arguments. It now checks tuple acceptance, rejects a top-level list without calling the service, and keeps nested-list values inside a tuple valid. This matches the Host socket regression and the V24 call construction above.

No gameplay or packet implementation was changed for this CI repair; the existing behavior matches the V24 client. The test suite was corrected to compare known protocol divergences against V24 semantics rather than the EveJS recording.

## Verification

- Before adjustment: `Eve.Services.Tests.DispatchGoldenTests` had 19 passed, 2 failed, 0 skipped.
- After adjustment: the full `DispatchGoldenTests` class had 200 passed, 0 failed, 0 skipped.
- After adjustment: `HostInputHardeningTests` had 3 passed, 0 failed, 0 skipped.
- `Eve.Host.Tests` ping and argument-tuple cases on `eve_p3`: 3 passed, 0 failed, 0 skipped.
- Release builds of `Eve.Services.Tests` and `Eve.Host.Tests` with warnings as errors: 0 warnings, 0 errors.
- GitHub Actions run 1627 for the fix commit `72454d069bce` completed successfully. Its downloaded TRX files recorded 8,658 passed, 0 failed, and 25 `NotExecuted`; those 25 are not counted as passes: 22 abyssal simulation tests require `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE`, one Universe export test requires `EVE_CLIENT_SDE_DIR`, and the Dogma and Market benchmarks each require their opt-in environment variable. The local focused runs above had 0 skipped.
- The graphical V24 client was not run.
