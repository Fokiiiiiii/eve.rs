# Claude Instructions for eve.rs

## Read First
- **`AGENTS.md` is the authoritative, repository-wide development policy.** Read it and the nearest directory-specific `AGENTS.md` before making changes.
- Also follow `AGENT_OVERRIDE.md` for feature-completion checks and `docs/design/FOUNDATIONS.md` for shared infrastructure.
- If an older ADR or historical porting instruction suggests copying EveJS internals, follow the current `AGENTS.md`: preserve verified gameplay/client behavior, not bugs, inefficiency or architecture.
- Use `docs/project/README.md` for verified status, `docs/project/TODO.md` for current work, and `docs/reference/compat/DEVIATIONS.md` for justified compatibility differences. Historical handoffs are not active assignments.

## Claude Workflow
- Inspect the current source and relevant client/EveJS callsites before editing. Search code via the configured codebase-memory-mcp and Serena tools when available.
- Work on end-to-end, observable functionality; test real Host registration, wire shapes, state transitions, persistence and notifications. Never report route coverage as functional completeness.
- Run relevant focused tests, then wider checks as appropriate. Database tests need `EVE_PG_URL`; skipped tests do not count as passed.
- Reference commands: `README.md` ("Checks"), `tools/dev/run-eve-live.sh`, `tools/dev/run-evejs-live.sh`.
- Use the pinned SDE in `sde/lock.json`; do not commit downloaded SDE data. Use `tools/spec-extract` and the separate `Fokiiiiiii/eve-logic` repository to inspect V24.01 client contracts.
- Report in Japanese: verified findings, changes, tests actually run, remaining gaps and unverified behavior.

## Git
- Use `main` unless instructed otherwise. Do not create a pull request unless requested; never force-push without explicit direction.
- Commit as `Fokiiiiiii <229531129+Fokiiiiiii@users.noreply.github.com>`. Do not add Claude attribution or co-author trailers.
- Write code-related documents and commit messages in English.
