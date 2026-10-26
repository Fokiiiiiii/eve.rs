# Handoff — client wire verification batch 7

## Status: 10/30 observations match, 19 need Host wire tests, 1 behavior mismatch

The verified matches in `CLIENT_VERIFIED.md` are RPC-02233–02237 (rewards), RPC-02275–02277 (shared settings), and RPC-02331–02332 (ship stance). Registered Host tests cover the reward list shapes, shared-setting KeyVal plus subsequent lookup, and stance response/notification.

NO-TEST rows are RPC-02231, RPC-02305–02324, and RPC-02334–02340. Direct service tests exist for some mutations, but there is no successful registered-Host reply assertion. Do not treat the no-character/null-dependency skill Host test as success coverage.

RPC-02232 is a MISMATCH: `RepairItemsInStructure` rejects the call while EveJS runs structure repair. The client ignores its return at `base_repairshop.py:68-71`; the server behavior and state transition differ. No fix has been made.

## Verification and next steps

- Focused reward Actions run 40296391366 for 05564516 passed (1 test, no skips).
- Focused shared-settings Actions run 40303581991 for 7f508341 passed (1 passed, 0 skipped), covering the KeyVal wire value and subsequent GetStoredSharedSetting call. Hash and stored string fields arrive as PyBytes and are consumed as text by the client.
- Earlier push CI runs failed on stale commits: c19c223e/5ede8e99 had wars/map Host test byte-string/import issues; 2d63f140/e5a632a9 had successive shared-test type/compile issues. Those root causes are fixed in later commits.
- Push CI run 40303569025 for 7f508341 remained in progress at last check.
- A previous focused Actions run 40298615792 was cancelled before tests began because setup provisioned PostgreSQL; it is not a test result.
- Local Host test could not compile because NuGet assets are absent; restore stalled at package-source resolution. No local pass is claimed.

No PostgreSQL client or database-backed test was run locally. Continue with the structure-repair mismatch and meaningful registered-Host tests for the NO-TEST rows. Update this handoff after each push.
