# Handoff — client wire verification batch 7

## Status: 10/30 observations match, 19 need Host wire tests, 1 behavior mismatch

The verified matches in `CLIENT_VERIFIED.md` are RPC-02233–02237 (rewards), RPC-02275–02277 (shared settings), and RPC-02331–02332 (ship stance). Registered Host tests cover the reward list shapes, shared-setting KeyVal plus subsequent lookup, and stance response/notification.

NO-TEST rows are RPC-02231, RPC-02305–02324, and RPC-02334–02340. Direct service tests exist for some mutations, but there is no successful registered-Host reply assertion. Do not treat the no-character/null-dependency skill Host test as success coverage.

RPC-02232 is a MISMATCH: `RepairItemsInStructure` rejects the call while EveJS runs structure repair. The client ignores its return at `base_repairshop.py:68-71`; the server behavior and state transition differ. No fix has been made.

## Verification and next steps

- Focused reward Actions run 40296391366 for 05564516 passed (1 test, no skips).
- Push CI run 40298596855 for c19c223e exposed two byte-string assertion issues in the wars-info Host wire test. The wrapper name, KeyVal name, and KeyVal field names now use `JsSemantics.StringValue`; the correction was pushed in 5ede8e99 and awaits push-CI verification.
- Focused shared-settings Actions run 40300261216 for d4e0fe4f failed because the wire decoder returned PyBytes for `hashvalue`; commit 2d63f140 changed the assertion to PyBytes and preserves the value in the client key tuple. Focused rerun 40301295875 remains in progress.
- A previous focused Actions run 40298615792 was cancelled before tests began because setup provisioned PostgreSQL; it is not a test result.
- Local Host test could not compile because NuGet assets are absent; restore stalled at package-source resolution. No local pass is claimed.

No PostgreSQL client or database-backed test was run locally. Continue with the structure-repair mismatch and meaningful registered-Host tests for the NO-TEST rows. Update this handoff after each push.
