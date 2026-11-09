# Audit fixes: `eve-behaviors` crate (r15)

Audit source: `/home/user/reports/90-audit-crate-behaviors-r15.md`.

## Confirmed finding

### P2 — timer monitor re-entry duplicated blackboard observers

The finding is real. `SetBooleanValueAndResetAfterTimeout::on_enter` subscribes on every entry (`crates/eve-behaviors/src/monitors/timers.rs:106-115`). A timeout sends `None` to that channel and dispatches its observers (`crates/eve-behaviors/src/tree.rs:359-385`, `912-939`). That callback writes to `channelAddress` and requests a tree reset (`monitors/timers.rs:117-132`); the reset cleans up and starts the root again (`tree.rs:757-764`). The monitor does not unsubscribe, so an unconditional `Vec::push` in `MessageChannel::add_observer` retained the same `(tree, task, handler)` repeatedly (`crates/eve-behaviors/src/blackboards.rs:240-249`).

The V24.01 client makes the same subscription on each entry (`/home/user/eve-logic/behaviors/monitors/timers.py:96-112`), but its `MessageChannel` delegates to `signals.Signal` (`/home/user/eve-logic/behaviors/blackboards/__init__.py:13-20,88-100`). `Signal.connect` stores bound method functions in a `WeakSet` for each observer object (`/home/user/eve-logic/signals/signal.py:8-14,46-56`), so reconnecting the same task method leaves one subscription. This confirms the expected observer identity and registration behavior.

The new tree-level regression test reproduced the failure before the fix: after two timeout/reset/re-entry cycles, the output channel had `[1, 3]` updates instead of `[1, 2]`; the second timeout called the task twice. `MessageChannel::add_observer` now keeps the first registration and ignores a duplicate while retaining distinct observers and their insertion order (`crates/eve-behaviors/src/blackboards.rs:240-244`). The direct channel test also checks deduplication and removal.

## Additional mismatch found in the same flow

While comparing the full re-entry transition, the client source exposed a second Rust mismatch in this same monitor path. V24.01's `Timer.__init__` sends the timer ID to its channel immediately (`/home/user/eve-logic/behaviors/timers/__init__.py:8-17`). On re-entry, the old observer therefore receives that ID while `SetBooleanValueAndResetAfterTimeout::OnEnter` is running, sends `trueOrFalse` to `channelAddress`, and does not request a reset because the message is not `None` (`behaviors/monitors/timers.py:96-112`). Rust also sends the ID before subscribing (`crates/eve-behaviors/src/tree.rs:808-817`), but the self-notification was lost: `with_node` temporarily takes the task out of its slot (`tree.rs:651-669`), and `dispatch_blackboard` previously ignored the resulting missing node (`tree.rs:932-939`).

`dispatch_blackboard` now queues a blackboard delivery when the target task is currently executing. The tree owner already drains pending deliveries after ticking trees (`crates/eve-space/src/brains/mod.rs:339-345`). The expanded timer regression verifies two full cycles: the output counts after timeouts are `[1, 3]`, after re-entry they are `[2, 4]`, the observer remains singular, and each reset is requested once. Before this dispatch fix, the expanded test failed because the second timeout count was `[1, 2]`; the timer-ID update from re-entry had been dropped.

## EveJS comparison

Searching `/home/user/evejs-261028.1/server/src` found no `SetBooleanValueAndResetAfterTimeout`, `MessageChannel`, or equivalent client behavior-tree observer API. The closest NPC behavior path is a controller tick loop (`server/src/space/npc/npcBehaviorLoop.js:5082-5109,5616-5670`), rather than this client monitor. EveJS therefore has no equivalent scenario to execute for this internal subscription contract; this finding is based on the complete V24.01 source comparison and the Rust tree-level reproduction.

## Other findings

The r15 report contains no other findings. None were skipped as false. The additional same-flow mismatch above was found during client verification and fixed as part of completing this monitor transition.

## Verification

### Confirmed

- Before the observer fix, `cargo test --release -p eve-behaviors timer_monitor_reentry_keeps_one_callback_per_timer_message -- --nocapture` failed with duplicate timeout output `[1, 3]` instead of `[1, 2]`.
- After observer deduplication and before deferred self-delivery, the expanded test failed because the re-entry timer-ID update was absent (`timeout_outputs` was `[1, 2]`, expected `[1, 3]`).
- After both fixes, the timer test passed; `monitors::timers::tests` passed 4 tests, the blackboard-related slice passed 38, `tree::tests` passed 12, and the owner-side `eve-space::brains::tests` slice passed 19.
- `cargo fmt --all`, `cargo fmt --all -- --check`, `cargo build --release -p eve-behaviors`, `cargo check --release -p eve-behaviors`, and `cargo clippy --release -p eve-behaviors --all-targets -- -D warnings` completed successfully.
- `eve-behaviors` has no database dependency; no database-backed tests exist in this crate.

### Not checked

- The full `eve-behaviors` test suite and a graphical V24.01 client run were not performed.
- No authored FSD tree using this monitor was confirmed; `.cache/sde` was not present in this worktree.
- EveJS does not expose the same monitor/runtime, so no same-scenario execution was possible there.
