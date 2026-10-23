# Marshal Value Budget Handoff

## Goal

Reject inbound marshal streams whose values or count-driven collections exceed a shared decode allocation budget before reserving the corresponding memory. Preserve existing depth, wire-length, and zlib inflation bounds.

## Implementation

- `MarshalCodec.MaxDecodedAllocationUnits` is 1,000,000 per top-level decode.
- The decoder charges each visited value, sequence/dictionary slots and minimum child values, save-table slots, ObjectEx entries, packed-row descriptor/layout/value/byte storage.
- Nested PySubStreams share their parent's budget; a budget failure is not converted into a raw PySubStream.
- Host sessions are disconnected with `InvalidPacket` when this budget is exceeded. Other malformed marshal packets keep their existing drop-and-continue behavior.
- EveJS v0.12.9 `server/src/network/tcp/utils/marshal.js:1311-1634` and 261018.1 `src/server/src/network/tcp/utils/marshal.js:1506-1836` grow tuple/list/dict values without a shared allocation budget. eve.rs retains their in-budget decode results and applies a server-side resource limit.

## Regression coverage

- Input-valid large flat list, tuple, and dictionary counts reject before collection capacity allocation.
- Nested collections and nested streams share the total budget.
- Oversized save tables reject before the saved-value array is allocated; saved references within budget retain their decoded value.
- Compressed over-budget input is rejected after inflation.
- Tuple, list, and dictionary payloads at the allocation boundary decode and re-encode with identical wire bytes.
- Host coverage verifies over-budget refusal and session disconnect before service dispatch, no success response or mutation, and a valid request on a retry session.

## Verified local checks

- `dotnet build dotnet/tests/Eve.Marshal.Tests/Eve.Marshal.Tests.csproj`: succeeded, 0 warnings and 0 errors.
- Focused parser allocation test: passed 3 cases, 0 skipped.
- `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj`: succeeded, 0 warnings and 0 errors.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:BuildProjectReferences=false`: succeeded, 0 warnings and 0 errors, after referenced projects had built.
- Focused Host refusal-and-retry test: passed 1 case, 0 skipped.
- An earlier all-reference Host.Tests build had `csc` killed with exit 137. The serialized Host build and test-project build completed afterward.
- No PostgreSQL database was used.
- No separate live headless malformed-packet walk was added: existing live cases use high-level calls and have no malformed-payload injection case. The Host socket test covers refusal and retry.
- Selected Actions run 39971341433 for `Eve.Marshal.Tests` and `Eve.Host.Tests`: success, with no reported skips.
- Follow-up selected Actions run 39975271743 on SHA `97743562`, after merged Host updates: both targeted marshal and Host regression tests passed; no failed tests or jobs.
- Push-triggered CI run 39971311558 for commit `99696f0d9b3e`: failed in the fast live E2E market-history request (`MachoError 2`) and `Eve.Ships.Tests.ShipTests.Cargo_capacity_modules_cannot_be_offlined_or_unfitted_past_the_hold_limit` (expected `ShipRefused`, received `ShipChanged`). Both failures were outside the marshal changes; the market-history issue was already tracked in `docs/project/CI_FIX_HANDOFF.md`, and no Ship files changed in this task. The required CI gate failed.
- The later push-triggered CI run 39973533394 for merged main commit `556f3ccd` also failed: E2E market history returned `SessionRpcBudgetExceeded`, and two `SpaceContainerTests` failed. Marshal and Host changes compile in this run; their selected Action run above passed. The fast E2E log reported 8 skipped cases (skill-training-speed and slow live cases). Skips are not counted as passes.

## Worktree

The handoff file was absent when this worktree was inspected. The worktree also had unrelated pre-existing deletions under crates and an unrelated TODO edit; those changes remain untouched. Update this note after each push with the commit and Actions results.
