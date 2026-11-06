#!/usr/bin/env bash
# The CI jobs of .github/workflows/ci.yml, run here in the same order with the same environment: the primary check before a push.
#
#   tools/dev/check-all.sh                         every step
#   CHECK_SKIP=live,dotnet tools/dev/check-all.sh  leave out the named steps
#
# Steps: fmt, clippy, rust (cargo test), dotnet (dotnet test), fixtures, python, live (the end-to-end run of the server stack).
# The PostgreSQL 18 cluster comes from tools/dev/pg18.sh (started if it is not running). Each step stops the run when it fails.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."
export CARGO_TERM_COLOR=always CARGO_INCREMENTAL=0
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export EVE_MARKET_SEED=skip   # the host would seed the NPC market from CCP's ESI on its first start

TOOLCHAIN="${RUST_TOOLCHAIN:-1.94.1}"   # keep the pre-push check on the required CI toolchain
PG_URL="$(tools/dev/pg18.sh url)"
# the .NET tests take an Npgsql keyword string (some hand the variable to Npgsql unchanged)
PG_NPGSQL="Host=127.0.0.1;Port=${PGPORT:-5432};Username=postgres;Password=postgres;Database=eve"

want() { case ",${CHECK_SKIP:-}," in *",$1,"*) return 1 ;; *) return 0 ;; esac; }
step() { printf '\n== %s\n' "$1"; }

if want fmt; then
  step "fmt"
  cargo "+$TOOLCHAIN" fmt --all --check
fi

if want clippy; then
  step "clippy"
  cargo "+$TOOLCHAIN" clippy --workspace --all-targets -- -D warnings
fi

if want rust; then
  step "rust: the static data, then cargo test with PostgreSQL"
  tools/dev/pg18.sh status >/dev/null 2>&1 || tools/dev/pg18.sh start
  cargo "+$TOOLCHAIN" run -q -p eve-sde -- ensure
  EVE_PG_URL="$PG_URL" cargo "+$TOOLCHAIN" test --workspace
fi

if want dotnet; then
  step "dotnet: the static data into PostgreSQL, then dotnet test"
  tools/dev/pg18.sh status >/dev/null 2>&1 || tools/dev/pg18.sh start
  cargo "+$TOOLCHAIN" run -q -p eve-sde -- ensure --pg "$PG_URL"
  (cd dotnet && EVE_PG_URL="$PG_NPGSQL" dotnet test EveRs.slnx -c Release -nologo --verbosity minimal)
fi

if want fixtures; then
  step "fixtures: the generators reproduce the committed fixtures"
  python3 tools/dev/gen-simlink-fixtures.py
  python3 tools/dev/gen-hostlink-fixtures.py
  git diff --exit-code -- oracle/
fi

if want python; then
  step "python: the headless client's tests"
  (
    cd tools/headless-client
    [ -x .venv/bin/python ] || python3 -m venv .venv
    .venv/bin/pip install -q -r requirements-dev.txt
    .venv/bin/python -m pytest -q -rs
  )
fi

if want live; then
  step "live: the server stack and the fast end-to-end tests"
  tools/headless-client/.venv/bin/python -c 'import pytest' 2>/dev/null || {
    echo "run the python step first (it creates tools/headless-client/.venv)" >&2; exit 1; }
  EVE_PG_URL="$PG_URL" EVE_LIVE_LOG_DIR="$PWD/.live-logs" KEEP_LOGS=1 tools/dev/run-eve-live.sh
fi

printf '\nall checks passed\n'
