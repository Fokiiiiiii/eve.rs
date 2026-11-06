# Comparing eve.rs with EVE.js through the MITM

`tools/mitm` (see [`tools/mitm/README.md`](../../tools/mitm/README.md)) is the capture and comparison tool: a transparent
proxy that logs every MachoNet packet, decoded with the headless client's own decoder (`tools/headless-client/evecli`),
and `mitm diff`, which compares two captures. This note is the exact recipe for one scenario, run as written. The
recipe and the rule are in [`docs/design/REFERENCE-TRACES.md`](../design/REFERENCE-TRACES.md).

## Commands

Run from the repository root. `SDE` is a directory with the pinned build (`sde/lock.json`, build 3396210) and
`EVEJS` is a scratch copy of EVE.js (see step 1). Each heavy run takes the shared lock.

```
export SDE=/path/to/eve-online-static-data-3396210-jsonl
export EVEJS=/path/to/scratch/evejs-v0.12.9
export NAME=login                                     # captures/evejs-$NAME.jsonl, captures/eve-rs-$NAME.jsonl

# 0. the headless client (once)
python3 -m venv tools/headless-client/.venv && tools/headless-client/.venv/bin/pip install -r tools/headless-client/requirements-dev.txt

# 1. EVE.js, copied out of its sibling checkout (its node_modules and generated data stay out of the repo)
cp -r /home/user/evejs-v0.12.9 "$EVEJS" && rm -rf "$EVEJS/.git"
( cd "$EVEJS" && npm ci --no-audit --no-fund ) && ( cd "$EVEJS/server" && npm ci --no-audit --no-fund )

# (both proxies are stopped with kill -TERM: SIGINT does not work in a non-interactive background job, see below)
# 2. the reference server, through the proxy (proxy 26200 -> EVE.js 26000); the default scenario is tests/test_live.py
flock -o -w 7200 /tmp/eve-heavy.lock timeout 1800 bash -c 'tools/mitm/mitm capture --upstream 127.0.0.1:26000 --listen 127.0.0.1:26200 --out captures/evejs-$NAME.jsonl --quiet & p=$!; sleep 1; EVE_LIVE_HOST=127.0.0.1:26200 tools/dev/run-evejs-live.sh "$EVEJS" "$SDE"; s=$?; kill -TERM $p; wait $p; exit $s'

# 3. eve.rs, through the proxy (proxy 26200 -> eve.rs front 26100); the database lane `eve`, markets and LP store skipped
export CARGO_INCREMENTAL=0 CARGO_TARGET_DIR=/home/user/cargo-target
flock -o -w 7200 /tmp/eve-heavy.lock timeout 1800 bash -c 'tools/mitm/mitm capture --upstream 127.0.0.1:26100 --listen 127.0.0.1:26200 --out captures/eve-rs-$NAME.jsonl --quiet & p=$!; sleep 1; EVE_DEV_AUTO_CREATE=1 EVE_LIVE_HOST=127.0.0.1:26200 EVE_SDE_DIR="$SDE" EVE_MARKET_SEED=skip EVE_LP_STORE_SEED=skip EVE_PG_URL=$(tools/dev/pg18.sh url) EVE_RUN="cd tools/headless-client && .venv/bin/python -m pytest -q tests/test_live.py" tools/dev/run-eve-live.sh; s=$?; kill -TERM $p; wait $p; exit $s'

# 4. compare (the first divergence, exit 1 when there is one)
tools/mitm/mitm diff captures/evejs-$NAME.jsonl captures/eve-rs-$NAME.jsonl --ignore-volatile
tools/mitm/mitm show captures/eve-rs-$NAME.jsonl --service machoNet      # read either side by service
```

Each side took about five minutes (the EVE.js boot) and about five minutes (the eve.rs build, then the tests). Captures
are git-ignored (`/captures/`).

## Things that bit the run

- **Stop the proxy with `kill -TERM`, not Ctrl-C or `kill -INT`.** A background job of a non-interactive shell ignores
  SIGINT, so an interrupt never reaches the proxy and `wait` hangs while the lock is held. Each record is flushed as it
  is written, so a TERM loses nothing.
- `mitm diff` stops at the first divergence. To see the others, use `mitm show` on both sides and compare the calls
  (`--service` / `--method` filter them); the list of differences is in the run notes, not in the tool.
- `run-evejs-live.sh` ends with `rm: cannot remove ... Directory not empty` when the server is still writing its data
  root at shutdown. It is harmless.

## Limits

- The headless client drives only what its tests script. `tests/test_live.py` is the login and session walk (4 tests,
  1 skipped: the strict-password test). `run-evejs-live.sh` runs only that file; `run-eve-live.sh` takes any selection
  through `EVE_RUN`, so a larger scenario needs the same selection run against the EVE.js proxy by hand.
- Not the graphical client. Its handshake and calls are checked against evecli and the recorded fixtures only.

## Insider walk (GM and QA slash commands, 2026-10-18)

The Insider window's server calls (`tools/headless-client/tests/test_insider_gm_live.py`): money, standing, skills, the GM helpers (`/givestarterskills`, `/skillscopy`, `/dogmaupdate`, `/killright`, `/skyhook state`, `/entity deploy`, `/asteroid rewards`, `/randomizeskills` last, because its levels differ on each run), the heal and the ship. Run on the eve_p2 lane for eve.rs and on EVE.js with its own port.

```
# eve.rs: the lane's live port 26101 behind the proxy 26211 (MITM port B); the GM roles by the identity fixture
EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p2 tools/dev/db-lock.sh bash -c 'tools/mitm/mitm capture --upstream 127.0.0.1:26101 --listen 127.0.0.1:26211 --out captures/eve-rs-insider.jsonl --quiet & p=$!; sleep 1; EVE_IDENTITY_SCHEMA=1 EVE_DEV_AUTO_CREATE=1 EVE_LIVE_PORT=26101 EVE_LIVE_HOST=127.0.0.1:26211 EVE_SDE_DIR=<sde>/eve-online-static-data-3396210-jsonl EVE_MARKET_SEED=skip EVE_LP_STORE_SEED=skip EVE_ADJUSTED_PRICE_SEED=skip EVE_GM_TRANSCRIPT=captures/eve-rs-insider.transcript.jsonl EVE_RUN="cd tools/headless-client && .venv/bin/python -m pytest -q tests/test_insider_gm_live.py" tools/dev/run-eve-live.sh; s=$?; kill -TERM $p; wait $p; exit $s'

# EVE.js: its port 26020 behind the proxy 26111 (MITM port A); the lane lock; a node heap of 6 GB (the default heap ran out at startup)
flock /tmp/eve-heavy-eve_p2.lock timeout 3000 bash -c 'tools/mitm/mitm capture --upstream 127.0.0.1:26020 --listen 127.0.0.1:26111 --out captures/evejs-insider.jsonl --quiet & p=$!; sleep 1; EVE_LIVE_LOG_DIR=captures/evejs-logs EVE_LIVE_PORT=26020 EVE_LIVE_HOST=127.0.0.1:26111 EVE_LIVE_BOOT_SECONDS=900 EVE_MARKET_SEED=skip EVE_LP_STORE_SEED=skip EVE_ADJUSTED_PRICE_SEED=skip EVE_GM_TRANSCRIPT=captures/evejs-insider.transcript.jsonl EVE_RUN="cd tools/headless-client && .venv/bin/python -m pytest -q tests/test_insider_gm_live.py" tools/dev/run-evejs-live.sh <evejs> <sde>/eve-online-static-data-3396210-jsonl; s=$?; kill -TERM $p; wait $p; exit $s'
```

Notes from the run:

- EVE.js stopped with `listen EADDRINUSE` on 26001 while another listener held it, and the runner's readiness check passes against any listener on its port; the reference therefore ran on 26020, and a port must be checked free before a run.
- The runner's `node` gets a 6 GB heap from a wrapper on `PATH` (the runner sets `NODE_OPTIONS` itself).
- The eve.rs side logs in twice (the identity fixture grants the role at a login), so its capture has two more `GetServiceInfo` calls before the walk. The calls by service and method otherwise match one for one (25 on EVE.js, 27 on eve.rs).
- The EVE.js walk fails its own outcome check on the new commands (it answers `Unknown command`); that is the comparison, not a fault of the walk. The eve.rs walk passes its outcome checks.
- `mitm diff` stops at the first divergence, which is the extra login above; the comparison that matters is the transcript and the call multiset. The answers that differ are recorded in DEVIATIONS G26.
