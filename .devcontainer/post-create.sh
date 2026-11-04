#!/usr/bin/env bash
# What a fresh dev container needs once: the system packages, the toolchain's parts, the Python client's
# environment, the packages of the Rust and .NET projects, and the static data in PostgreSQL.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

sudo apt-get update -qq
sudo apt-get install -y --no-install-recommends postgresql-client pkg-config libssl-dev

rustup component add rustfmt clippy

python3 -m venv tools/headless-client/.venv
tools/headless-client/.venv/bin/pip install -q -r tools/headless-client/requirements-dev.txt

cargo fetch --locked
(cd dotnet && dotnet restore EveRs.slnx)

# the static data: fetched once (kept in .cache/sde, never committed) and loaded into PostgreSQL
until pg_isready -q -h db -U postgres; do sleep 1; done
cargo run -q -p eve-sde -- ensure --pg "$EVE_PG_URL"

cat <<'TEXT'

eve.rs is ready.
  cargo test --workspace                          the Rust tests
  (cd dotnet && dotnet test EveRs.slnx)           the .NET tests (PostgreSQL is at $EVE_PG_URL)
  tools/dev/run-eve-live.sh                       the server stack and the headless client, end to end
  docker compose up --build                       the server itself, for an EVE client (see docker-compose.yml)
TEXT
