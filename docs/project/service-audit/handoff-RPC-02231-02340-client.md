# Handoff — client wire verification batch 7

## Status: 10/30 observations match, 19 need Host wire tests, 1 behavior mismatch

The verified matches in `CLIENT_VERIFIED.md` are RPC-02233–02237 (rewards), RPC-02275–02277 (shared settings), and RPC-02331–02332 (ship stance). Registered Host tests cover the reward list shapes, shared-setting KeyVal plus subsequent lookup, and stance response/notification.

NO-TEST rows are RPC-02231, RPC-02305–02324, and RPC-02334–02340. Direct service tests exist for some mutations, but there is no successful registered-Host reply assertion. Do not treat the no-character/null-dependency skill Host test as success coverage.

RPC-02232 is a MISMATCH: `RepairItemsInStructure` rejects the call while EveJS runs structure repair. The client ignores its return at `base_repairshop.py:68-71`; the server behavior and state transition differ. No fix has been made.

## Verification and next steps

- Focused reward Actions run 40296391366 for 05564516 passed (1 test, no skips).
- Push CI run 40296376456 failed on a merged wars-info Host assertion: a wire-decoded string was expected as PyStr but is PyBytes. The assertion now uses `JsSemantics.StringValue` in commit 915cb727, pushed in c19c223e.
- Follow-up Actions run 40298615792 for c19c223e was cancelled before tests began because its setup provisioned PostgreSQL, which this task explicitly forbids. It is not a test result.
- Focused shared-settings Actions run 40300261216 for d4e0fe4f is still in progress. The selected test itself uses the registered service's in-memory state and does not open PostgreSQL.
- Push CI run 40298596855 for c19c223e is queued; no push-CI run was listed yet for d4e0fe4f.
- Local Host test could not compile because NuGet assets are absent; restore stalled at package-source resolution. No local pass is claimed.

No PostgreSQL client or database-backed test was used locally. Continue with the structure-repair mismatch and meaningful registered-Host tests for the NO-TEST rows. Update this handoff after each push.
