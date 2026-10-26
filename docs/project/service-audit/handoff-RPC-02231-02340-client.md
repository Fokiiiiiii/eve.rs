# Handoff — client wire verification batch 7

## Status: 10/30 observations match, 19 need Host wire tests, 1 behavior mismatch

The verified matches in `CLIENT_VERIFIED.md` are RPC-02233–02237 (rewards), RPC-02275–02277 (shared settings), and RPC-02331–02332 (ship stance). Registered Host tests cover the reward list shapes, shared-setting KeyVal plus subsequent lookup, and stance response/notification.

NO-TEST rows are RPC-02231, RPC-02305–02324, and RPC-02334–02340. Direct service tests exist for some mutations, but there is no successful registered-Host reply assertion. Do not treat the no-character/null-dependency skill Host test as success coverage.

RPC-02232 is a MISMATCH: `RepairItemsInStructure` rejects the call while EveJS runs structure repair. The client ignores its return at `base_repairshop.py:68-71`; the server behavior and state transition differ. No fix has been made.

## Verification and next steps

- Focused reward Actions run 40296391366 for 05564516 passed (1 test, no skips).
- Focused shared-settings Actions run 40303581991 for 7f508341 passed (1 passed, 0 skipped), covering the KeyVal and subsequent lookup. Wire string values are PyBytes.
- Commit `1aa1cf6f` restores the PyBytes assertion and verifies text with `JsSemantics.StringValue`. Focused Actions run 40304737073 is queued/in progress; push CI run 40304724470 is queued.
- Earlier compile failures on older refs were fixed: Map Host test namespace import, missing `Eve.Macho` using, and nullable dictionary key conversion.
- A previous focused Actions run 40298615792 was cancelled before tests began because setup provisioned PostgreSQL; it is not a test result.
- Local Host test could not compile because NuGet assets are absent; restore stalled at package-source resolution. No local pass is claimed.

No PostgreSQL client or database-backed test was run locally. Continue with the structure-repair mismatch and meaningful registered-Host tests for the NO-TEST rows. Update this handoff after each push.
