# CI/CD instead of local builds

Nothing in this repository needs a local full build or test run. GitHub Actions does it, with caches, and
`tools/dev/ci-status.py` (needs `GH_TOKEN`) starts runs and prints the failing tests with log excerpts.

## Validate a change before it reaches main

1. Push the work to a branch.
2. Start a selected run (checks out that branch or SHA):
   `python3 tools/dev/ci-status.py dispatch-agent-test --ref <branch> --projects <Eve.Foo.Tests|all|""> --filter "" --live "" --rust <none|tests|clippy|all>`
   It prints the run id.
3. Wait and read the result: `python3 tools/dev/ci-status.py wait <run id>`
   (failing tests with the log tail, or "No failed tests or jobs"). Full logs are the `agent-test-logs-<run id>`
   artifact.
4. Everything for a commit on main: `python3 tools/dev/ci-status.py failures <sha>`; the run overview:
   `python3 tools/dev/ci-status.py status --branch main --limit 5`.

## Workflows

| Workflow | Trigger | Covers |
| --- | --- | --- |
| `ci.yml` | push to main, weekly, by hand (`jobs` input) | Rust fmt, clippy, tests, .NET build and 41 test shards, Python, fixtures, live end-to-end, Windows |
| `agent-test.yml` | by hand | selected .NET projects/filters, live tests, Rust checks on any branch or SHA |
| `live-slow.yml` | by hand | slow end-to-end tests |
| `ci-nightly.yml`, `dependency-audit.yml` | schedule | nightly and dependency checks |
| `cd.yml` | tag `v*`, by hand | release binaries and C# host tarball, container image, GitHub release |

## Build caches

* Rust: `Swatinem/rust-cache` in every Rust job (`cd.yml` uses its own key).
* .NET: the NuGet package folder is cached by the hash of the project and props files.
* Static data: `.cache/sde` is cached by the pinned SDE build and `sde/lock.json`.
* Container image: `cd.yml` restores and saves a buildx layer cache (`/tmp/buildx-cache`, newest only) with
  `actions/cache`.
* The .NET build output is built once in `dotnet-build` and shared with the test shards as an artifact.

## CD

`cd.yml` never gates main. A `v*` tag builds `eve-rs-linux-x64-<tag>.tar.gz` (`bin/eve-net`, `bin/eve-sim`,
`bin/eve-sde`, `host/`, `server.env.example`), pushes `ghcr.io/<owner>/<repo>:<tag>` and creates or updates the
GitHub release. A manual run builds the same things and only pushes the image when `push_image` is set. No secrets
are used: the workflow's own `GITHUB_TOKEN` does the pushing.
