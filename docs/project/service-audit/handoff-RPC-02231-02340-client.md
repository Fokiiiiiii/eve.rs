# Handoff — client wire verification batch 7

## Scope and status

This batch covers 30 implemented-untested observations, RPC-02231 through RPC-02340. Seven rows are documented in `CLIENT_VERIFIED.md`:

- RPC-02233–02234, `rewardMgr.GetDelayedRewardsByGroupIDs`: MATCHES.
- RPC-02235–02237, `rewardMgr.GetRewardLPLogs`: MATCHES.
- RPC-02275–02277, `sharedSettingsMgr.StoreSettingLinkAndGetID`: MATCHES.

Registered Host tests cover the empty LP log response and the shared-setting KeyVal plus its subsequent read. Existing Host coverage checks the non-empty delayed reward row. No database or graphical client was used.

## Remaining rows

- RPC-02231–02232: repair service
- RPC-02305–02324: ship cosmetics
- RPC-02331–02332: ship stance
- RPC-02334–02340: skill handler

`RepairItemsInStructure` is a behavior gap: the eve.rs Host method rejects the call while EveJS executes its structure repair runtime. The client ignores the return at `base_repairshop.py:64-71`; the mismatch affects state and notifications. No fix has been made yet.

## Verification state

For commit 05564516, the focused Host Actions run 40296391366 passed (one focused test, no skips). Push CI run 40296376456 failed in an unrelated merged wars-info test because a wire-decoded string was asserted as PyStr instead of PyBytes. The assertion now uses `JsSemantics.StringValue`; this correction is in the working tree and awaits a focused Actions rerun plus push-CI verification.

The local focused test could not run because NuGet restore stalled at package-source resolution; no local pass is claimed. Continue the remaining rows with whole-tree eve-logic evidence, Host behavior, and EveJS comparison. Update this handoff after every push.
