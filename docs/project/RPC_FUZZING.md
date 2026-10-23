# RPC fuzzing

`Eve.Host.Tests.RpcFuzzTests` sends deterministic hostile arguments through `PacketDispatcher` using a real `ClientSession` and packet sink.
It discovers a service's wire methods from generated `MethodAttribute` metadata and IL string constants, then retains only names accepted by that service's `HasMethod`. This covers generated and hand-written RPC services without keeping a second route list.

The regular test mode builds the real `HostComposition` on scratch schemas in the configured PostgreSQL database and sends one deterministically rotated argument shape to every registered method. The seed is `0x455645`. Inputs across the method inventory include no arguments, `None`, oversized strings, 1,024-element ID lists, a 16,384-ID facility request, nested containers, negative and zero IDs, wrong types and arities, and Unicode edge cases. `EVE_RPC_FUZZ_LONG=1` raises the cases to 64 per method and expands the hostile values. Both modes compare every scratch-schema table before and after dispatch. The comparison allows Host-owned lifecycle state (`_persistence_owners`, `industryRuntime`, industry cost indexes, scheduler jobs) and allows `overviewSharedPresets` only when a `StoreLinkAndGetID` call returned a normal response; all other database changes fail the test.

Every dispatch has a ten-second timeout, must be handled and must send exactly one `CallRsp` or `ErrorResponse`; session and service notifications may accompany that response. Per-dispatch allocations are capped at 64 MiB. The test uses a production `ClientSession` with no persisted account or character, except for one targeted empty Asset Safety read that uses a positive character ID against empty scratch data. Scratch schema names are unique to the run and are dropped during cleanup.

Run the regular suite case on the `eve` lane:

```sh
EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve MSBUILDDISABLENODEREUSE=1 tools/dev/db-lock.sh dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --filter FullyQualifiedName~RpcFuzzTests
```

Run the extended Host composition mode:

```sh
EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve EVE_RPC_FUZZ_LONG=1 MSBUILDDISABLENODEREUSE=1 tools/dev/db-lock.sh dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --filter FullyQualifiedName~RpcFuzzTests
```

## Findings

On the final merged tree, the regular-mode run passed on the `eve` lane: 175 distinct service instances, 1,304 registered RPC methods and 1,304 dispatches with seed `0x455645`. The focused normal test took 23 seconds in its latest run, including Host composition. The extended-mode run passed with 83,456 dispatches in 2m21s (28.2 seconds to compose Host, 1.3 seconds to snapshot, and 108.5 seconds to dispatch). It exposed excessive SDE loads: empty lookup calls loaded the corporation/location directory before returning no results, empty Asset Safety reads loaded the full SDE catalog even when there were no wraps or due deliveries, and repeated `planetMgr.GetPlanetResourceInfo` calls queried the same raw resource groups. Empty lookup calls now return before directory access; Asset Safety reads and delivery scans load catalog data only when needed; planetary raw resource type IDs are cached per service. Oversized lookup text is refused with `CustomInfo` before directory access. A 16,384-ID `GetFacilitiesByID` request exposed per-ID persistence reads; facility lookup now retrieves requested structures with one backing-store read. Focused lookup regression tests cover empty and oversized corporation searches; the regular RPC matrix includes the large facility request and the authenticated empty Asset Safety read.

The test also encountered protocol `SessionChangeNotification` packets accompanying a response and a successful `overviewPresetMgr.StoreLinkAndGetID` result from a nested dictionary payload. These are valid Host behavior, not refusals. The database check accounts for the successful preset write and the Host-owned lifecycle tables listed above.

In the final normal-mode detailed run, the changed scratch tables were `_persistence_owners`, `industryRuntime`, `overviewSharedPresets`, `industry_cost_index_state`, `industry_cost_indices`, and `jobs`. No other scratch tables changed. `overviewSharedPresets` changed after a successful preset-store response.

The focused `LookupSvcTests` passed 10/10. After merging `origin/main`, the full `Eve.Services.Core.Tests` project passed 894/894. The related Host Asset Safety, structure directory and fuzz selection passed 15/15 on the preceding merge; the latest shipcaster merge passed the fuzz and Shipcaster selection (3/3) and the extended fuzz case (1/1). The harness dispatches serially; concurrency, process restart and graphical-client behavior are outside these runs.

Both modes exercise every distinct service instance registered by production Host composition, including aliases collapsed by `ServiceRegistry`. Neither mode connects a graphical EVE client.
