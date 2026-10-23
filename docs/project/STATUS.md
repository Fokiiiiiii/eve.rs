# Project status

This page records the current integration state. `TODO.md` remains the detailed service and RPC work list; `README.md` records the RPC inventory and compatibility evidence.

## Built and verified end to end

- The Rust simulation, .NET host, PostgreSQL persistence, Macho transport, and headless client are built together by the Linux CI workflow.
- The fast headless suite covers the login and character-entry path and the checked gameplay and service walks. Runs [#502](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39876149156), [#503](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39878292277), and [#504](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39879349839) completed the Required CI gate successfully in sequence. Run #504 validates `32f5c97d`; Linux end-to-end took 8.8 minutes and the longest job, Windows, took 13.5 minutes. Rust tests, Rust format and clippy, .NET build, all .NET test shards, fixture generation, and Python client tests passed in these runs.
- Analysis beacon reward collection is wired through the host and inventory. The standard scan integration test, including moving a reward stack from the public wreck into a different pilot's ship cargo, passed on PostgreSQL lane `eve_p4` in this checkout.
- The local Release build of `EveRs.slnx` at `32f5c97d` succeeded with zero warnings and zero errors. `cargo fmt --all -- --check`, workspace clippy with `-D warnings`, the full Rust workspace tests, and the Python headless-client suite passed locally.
- Focused regression checks passed locally: the market RPC budget test (1/1), analysis beacon rules and pinned SDE checks (12/12), analysis beacon reward flow (1/1), public wreck/container tests (19/19), and blueprint-copy loot materialization (2/2). Rust link-tail checks passed (3/3) and resistance checks passed (17/17).

## Partial

- Implemented service behavior varies by RPC. A registered route or passing unit test does not establish complete client-visible behavior; use the per-service entries in `TODO.md` and the evidence in `DEVIATIONS.md` before treating an individual RPC as complete.
- The CI end-to-end suite is headless. Its successful result establishes the covered walks only and does not establish graphical acceptance for every feature.

## Open

- Native V24.01 graphical-client acceptance remains open.
- SignalTracker full-state publishing is wired, while its snapshot producer remains open.
- Abyssal and Void fallback contracts are present, while their runtime gate providers remain open.

## Verification scope

The local Python run used `requirements-dev.txt`; live tests skipped without a running server. Run #505 checks the same code SHA `32f5c97d`; its build, Rust, Python, end-to-end, and .NET test jobs have passed, and its Windows job is still running. The three-run required-gate streak (#502–#504) is complete; #505 has no result yet.
