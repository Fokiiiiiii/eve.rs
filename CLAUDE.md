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

## MCP Tool Policy (mandatory)
- Code search, reading, symbol lookup and editing go through MCP tools only: `codebase-memory-mcp` first (`search_graph` -> `trace_path` -> `get_code_snippet` -> `query_graph`; CLI form `codebase-memory-mcp cli <tool> '<json>'` if the server is not connected), then Serena (`find_symbol`, `find_referencing_symbols`, `get_symbols_overview`, `search_for_pattern`, `find_file`, `list_dir`, `read_file`, `replace_symbol_body`, `insert_before_symbol`/`insert_after_symbol`, `replace_content`, `replace_in_files`, `create_text_file`). Serena covers C# and Rust.
- Do not use the built-in `Edit`, `Write`, `Glob`, `Grep` or `NotebookEdit` tools (they are denied in settings). Use Bash only for builds, tests, git and `psql`; use Context7 only for current external library docs.
- Reason: MCP gives far better token efficiency, search accuracy and edit safety. Mention in the report when an MCP tool was unavailable and what fallback was used.

## Git
- Use `main` unless instructed otherwise. Do not create a pull request unless requested; never force-push without explicit direction.
- Commit as `Fokiiiiiii <229531129+Fokiiiiiii@users.noreply.github.com>`. Do not add Claude attribution or co-author trailers.
- Write code-related documents and commit messages in English.
