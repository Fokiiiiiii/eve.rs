# Shared rules for every eve.rs implementer (read fully before starting)

Repo: /home/user/eve.rs (main). Plan: THIS DIR/structures_phase.md (read your package section + sections 0,1,2-shared files). Decisions: DECISIONS.md.
Decisions for the open items of the plan: D-1 size class: SDE first (type/group attributes), fall back to a small table taken from EveJS structureConstants.js, record in DEVIATIONS. D-2 reinforcement-hour change: follow the wiki (pending, takes effect after the stated delay), record the EveJS difference. D-3 sim: structure = body with health. D-4 fuel burn: SDE attributes first, then wiki, then EveJS. D-5 hacking loot: wiki, else EveJS. Never stop for "no source": wiki decides, then EveJS.

## Source priority (user rule)
Stats/numbers/attributes: SDE first (postgres db `eve`, schema `sde`, jsonb `doc`; psql). Logic/mechanics: EVE University wiki (implement as written), then EveJS (/home/user/evejs-v0.12.9/server/src), CCP docs, eve-logic (/home/user/eve-logic) at equal rank. Client wire shapes: eve-logic decides. Record every conflict in docs/reference/compat/DEVIATIONS.md with the chosen source. Never write not confirmed / inference labels in repo files.

## Git / process
- Create your OWN worktree: git -C /home/user/eve.rs worktree add /home/user/wt-<pkg> -b wt-<pkg> origin/main (fetch first). Work there. Delete the worktree when done.
- Push every green increment immediately: git fetch origin main && git rebase origin/main (or merge) then git push origin HEAD:main. No force, no PR, no side branches pushed to the remote. If the push is rejected, fetch, merge, re-test the touched areas, push again.
- Commit identity: git -c user.name=Fokiiiiiii -c user.email=229531129+Fokiiiiiii@users.noreply.github.com commit. English messages. NO Claude attribution, NO Co-Authored-By trailers.
- Never edit any existing README.md. Docs go to docs/project/TODO.md, docs/reference/compat/DEVIATIONS.md, ADRs (docs/design/adr/, next free number) and new docs files. Update TODO (status rows) for what you finish.
- Wire protocol: PROTOCOL_VERSION (crates/eve-space/src/link/mod.rs, SimCodec.cs, tools/dev/gen-simlink-fixtures.py Hello/HelloAck, ADR 0013, oracle/fixtures/simlink/messages.json regenerated with the generator). Take the NEXT FREE number at push time (fetch first, check current value), never earlier.
- Tests must be real: Host registration, wire shape, state transitions, persistence, notifications. A route test is not functional completeness. Never weaken, skip, or loosen a test to get green; root-cause failures. No "known failures".
- Code style: match surrounding code; comments only for the why; no unrelated rewrites; no new packages without need.

## Machine rules (disk and CPU are tight)
- export CARGO_TARGET_DIR=<the existing shared target dir used by /home/user/eve.rs (check `ls /home/user/eve.rs/target` or env of the main checkout)>; CARGO_INCREMENTAL=0; never create a per-worktree target dir. Delete dotnet bin/obj of your worktree when finished.
- Postgres 18: tools/dev/pg18.sh start (runs as pguser), db `eve`; test DB tests need EVE_PG_URL (see README Checks / tools/dev/run-eve-live.sh). If PG is down, start it and poll `select 1`.
- Heavy builds/tests: take the lock `flock -w 1800 /tmp/eve-heavy.lock <command>` (it is a queue; always use a timeout); run the smallest relevant test first (cargo test -p eve-space <filter>; dotnet test <project> --filter), then the wider set for areas you touched before each push.
- Tools: Bash cat/grep/rg/find and built-in Edit/Write are denied in this repo. Read/search with python3 (use `python3 -I` when reading dirs that contain re.py such as eve-logic), the codebase-memory-mcp tools if available, git. Edit files with python3 scripts.
- Commit/push each green increment at once (containers restart; uncommitted work is lost). Report: files changed, tests actually run (with counts), remaining gaps, risks. Do not claim done without a passing run you saw.
