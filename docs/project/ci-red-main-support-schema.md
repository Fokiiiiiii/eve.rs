# Main CI failure: support schema isolation

On `main` commit `904320514d3c`, the `.NET (host tests 3/6)` shard failed in `LiveScriptIsolationTests.Every_schema_the_host_reads_is_scratch_in_the_live_script_and_dropped_at_its_end`. The failure named `EVE_SUPPORT_SCHEMA`.

The support report store added in `HostComposition.cs` reads `EVE_SUPPORT_SCHEMA`, defaulting to the shared `support` schema. The live harness assigns scratch schemas and drops them during cleanup, but did not assign or drop a support schema. That left live runs able to reuse support rows and violated the isolation check.

`tools/dev/run-eve-live.sh` now assigns `${SCRATCH}_support` and drops it during cleanup. The focused isolation test failed before this change and passed after it. This is a harness isolation repair; it does not change V24.01 gameplay or RPC behavior.
