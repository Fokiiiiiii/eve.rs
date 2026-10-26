# Handoff — client wire verification batch 7

## Scope and status

This batch covers 30 implemented-untested audit observations, RPC-02231 through RPC-02340. The reward manager slice is documented in `CLIENT_VERIFIED.md` and has five row observations verified:

- RPC-02233 and RPC-02234, `rewardMgr.GetDelayedRewardsByGroupIDs`: MATCHES.
- RPC-02235 through RPC-02237, `rewardMgr.GetRewardLPLogs`: MATCHES.

A registered Host wire test was added for the empty LP log list consumed by the client. Existing registered Host coverage already exercises non-empty delayed reward rows. No database or graphical client was used.

## Remaining rows

- RPC-02231–02232: repair service
- RPC-02275–02277: shared settings
- RPC-02305–02324: ship cosmetics
- RPC-02331–02332: ship stance
- RPC-02334–02340: skill handler

RepairItemsInStructure is a known behavior gap: the eve.rs Host method rejects the call while EveJS proceeds through its structure repair runtime. The client ignores the return at `base_repairshop.py:64-71`; the mismatch affects behavior/state, not reply shape. No fix has been made yet.

## Verification state

The focused local Host test could not run because the worktree lacks restored NuGet assets; restore attempts stalled at package-source resolution and were cancelled. No test result is claimed. Run the focused Eve.Host.Tests Actions job after the reward slice is pushed, then inspect the push-triggered CI result. Continue documenting every remaining row with whole-tree eve-logic callers/consumers, Host evidence, and EveJS comparison. Update this handoff after each subsequent push.
