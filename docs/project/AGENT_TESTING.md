# Agent testing protocol

Use GitHub Actions for broad test runs so concurrent agents do not saturate the local four-core machine.

## Local checks

- Compile-check only the projects changed by the task: `dotnet build` for touched .NET projects and `cargo check` for touched Rust projects.
- Run the single focused test that covers the change. Use a test filter when it narrows the check to one behavior.
- Do not run full test suites, the full headless-client suite, or broad live-server runs locally.
- For local .NET commands, set `MSBUILDDISABLENODEREUSE=1` and pass `-p:UseSharedCompilation=false`.

## GitHub Actions checks

- Dispatch selected projects and test files with `.github/workflows/agent-test.yml`; the workflow builds the .NET solution once and runs selected projects without rebuilding them.
- The push-triggered `.github/workflows/ci.yml` runs the required broad gate for changes pushed to `main`.
- `.github/workflows/ci-nightly.yml` runs slow live tests, the fast skill-training-speed walk on its own Host, the opt-in benchmarks, the full Windows workflow, and extended RPC fuzzing. It is scheduled nightly and can be dispatched manually; it does not participate in the push gate.
- Use `python3 tools/dev/ci-status.py wait <run-id>` to follow a selected run and print failed test names with their log tails. Use `python3 tools/dev/ci-status.py failures <sha>` to inspect the `ci.yml` run for a commit.
- Fix failed Actions runs promptly. A skipped test is not a pass; report skipped cases separately from passed and failed cases.

Example focused dispatch:

```sh
python3 tools/dev/ci-status.py dispatch-agent-test \
  --ref main \
  --projects Eve.Host.Tests \
  --filter 'FullyQualifiedName~Hostile_method_is_refused' \
  --live '' \
  --rust none
```
