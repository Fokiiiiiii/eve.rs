# Handoff — client wire verification batch 7

## Status: 10/30 observations match, 19 need Host wire tests, 1 behavior mismatch

The verified matches in `CLIENT_VERIFIED.md` are RPC-02233–02237 (rewards), RPC-02275–02277 (shared settings), and RPC-02331–02332 (ship stance). Registered Host tests cover the reward list shapes, shared-setting KeyVal plus subsequent lookup, and stance response/notification.

NO-TEST rows are RPC-02231, RPC-02305–02324, and RPC-02334–02340. Direct service tests exist for some mutations, but there is no successful registered-Host reply assertion. Do not treat the no-character/null-dependency skill Host test as success coverage.

RPC-02232 is a MISMATCH: `RepairItemsInStructure` rejects the call while EveJS runs structure repair. The client ignores its return at `base_repairshop.py:68-71`; the server behavior and state transition differ. No fix has been made.

## Verification and next steps

- Focused reward Actions run 40296391366 for 05564516 passed (1 test, no skips).
- Push CI run 40296376456 failed on a merged wars-info Host assertion: a wire-decoded string was expected as PyStr but is PyBytes. The assertion now uses `JsSemantics.StringValue` in commit 915cb727, pushed in c19c223e.
- Follow-up Actions run 40298615792 for c19c223e was cancelled before tests began because setup provisioned PostgreSQL, which this task explicitly forbids. It is not a test result.
- Push CI for c19c223e was queued at last check; inspect it with `python3 tools/dev/ci-status.py failures c19c223e`.
- Local `Eve.Host.Tests` build passed with 0 warnings and errors after removing an invalid `using Eve.Services.ObjectCaching;` from `ClientRowsMapHostWireTests.cs` (ObjectCaching is a type in Eve.Services).
- Focused local delayed-rewards Host test passed (1 passed, 0 skipped).
- Focused local LP-log Host test `Incursion_lp_logs_return_the_row_list_consumed_by_the_client_through_host_dispatch` passed (1 passed, 0 skipped).

No PostgreSQL database or graphical client was used locally. Continue with the repair behavior gap and meaningful DB-free registered-Host tests for the NO-TEST rows. Update this handoff after each push.
