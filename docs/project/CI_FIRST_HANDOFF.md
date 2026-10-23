# CI First Handoff

## Current state

- Main push runs use a SHA-specific concurrency group with cancellation disabled. The required CI gate and its dependencies are unchanged.
- The remaining work is to add the selected-test workflow, extend `ci-status.py`, add the nightly coverage workflow, and document the agent test protocol.
- This handoff is the continuation point for the CI-first testing change; the repository did not contain the requested handoff file before this change.

## Verification

- The source changes are ready for the first push. The Actions result for that commit will be recorded after it is available.

## Next steps

1. Finish `agent-test.yml` with the requested .NET, live-test and Rust selections, PostgreSQL/SDE setup, logs, and failure summary.
2. Add the nightly coverage for slow live tests, skill training speed, benchmarks, Windows, and extended RPC fuzzing.
3. Add parser unit tests and the `dispatch-agent-test`, `wait`, and `failures` commands to `ci-status.py`.
4. Push, dispatch the focused workflow against `main`, inspect its result and the required CI run, and update this handoff with the run IDs and outcomes.
