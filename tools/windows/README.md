# Running the eve.rs server on Windows

This folder has the Windows start and stop scripts. They start the same stack as `tools/server/start-server.sh` on
Linux, in the same order and with the same settings: PostgreSQL 18, the static data, the space simulation, the
service host and the network front, all over TCP (Windows has no Unix sockets in this setup).

| Script | What it does |
|---|---|
| `start-server.bat` | Production-style start. Runs `start-server.ps1`. |
| `start-server-dev.bat` | Development start. Also honours `EVE_TRACE_RPC` and `EVE_STRICT_RPC` (optional, see below). |
| `stop-server.bat` | Stops the processes a start recorded (forceful). For a window that was closed before shutdown ran. |
| `start-server.ps1`, `stop-server.ps1` | The implementation. Use them directly for switches (`-Trace`, `-Strict`) or with PowerShell 7. |

The `.bat` files only launch PowerShell with `-ExecutionPolicy Bypass`. `cmd.exe` cannot read the settings file, wait
for ports or supervise child processes, so the logic lives in the `.ps1` files.

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` reports 10.x; `global.json` pins 10.0.401).
- **Rust** with `cargo` (https://rustup.rs). Without `cargo`, the three binaries must already be in `target\release`.
- **PostgreSQL 18**, installed and running (the installer from https://www.postgresql.org/download/windows/), with
  `psql.exe` on `PATH` or under `C:\Program Files\PostgreSQL\18\bin`. The scripts create the `eve` database if it is
  missing; they do not install or start PostgreSQL.
- **curl.exe** (shipped with Windows 10 and later). `eve-sde` downloads the pinned static-data build with it on the first start.

Run the scripts from anywhere: they find the repository from their own location.

## Quick start

```
tools\windows\start-server.bat
```

The first start downloads the static data (about 0.6 GB unpacked) and loads it into PostgreSQL, then the host runs its
migrations; this can take several minutes. Later starts only check. When the console prints
`eve.rs is up`, point the client at the front (default `127.0.0.1:26000`). Logs are in `.cache\server\logs`.

Stop it with **Ctrl-C** in the start window. The front stops first, then the host, then the simulation. cmd.exe may
then ask `Terminate batch job (Y/N)?`: answer **N** so the shutdown can finish (not tested on Windows). Closing the window
should stop the processes too, but without the orderly shutdown (not tested); run `stop-server.bat` afterwards if anything
is still running.

## Settings

Precedence: the environment, then `server.env` in the repository root, then the defaults. Copy
`server.env.example` to `server.env` (git-ignored) to set a password or change a port.

| Variable | Default | Notes |
|---|---|---|
| `EVE_PG_URL` | `postgres://postgres@127.0.0.1:5432/eve` | Must be a `postgres://` URL. Put the password in `server.env`. The database is created if missing. |
| `EVE_HOST` | `tcp:127.0.0.1:26010` | Where the host listens. Numeric IPv4 addresses only. |
| `EVE_SIM_LISTEN` | `127.0.0.1:26011` | Where the simulation listens. |
| `EVE_SIM_SOCKET` | `tcp:127.0.0.1:26011` | Where the host connects to the simulation. Must match `EVE_SIM_LISTEN`. |
| `EVE_NET_LISTEN` | `127.0.0.1:26000` | The client's port. |
| `EVE_DEV_AUTO_CREATE` | `1` | Unknown user names create an account (as in `docker-compose.yml`). |
| `EVE_DEV_SKIP_PASSWORD` | `0` | `1` accepts any password. |
| `EVE_MARKET_SEED` | `skip` | `auto` fetches the NPC market from CCP's ESI on the first start (needs internet). |

Optional RPC diagnostics, for development starts only (`start-server-dev.bat`, or `start-server.ps1 -Dev`):

- `EVE_TRACE_RPC=full` traces every RPC call (verbose). `-Trace` sets it.
- `EVE_STRICT_RPC=1` makes the server fail on RPCs without a handler. `-Strict` sets it.

A production start clears both, with a warning, so they cannot leak into a normal run.

## Build outputs

`start-server.ps1` runs `cargo build --release -p eve-net -p eve-sim -p eve-sde` and
`dotnet build -c Release src/Eve.Host` every time, so the binaries match the sources. It honours `CARGO_TARGET_DIR`.

## Linux

`tools/server/start-server.sh [--dev]` is the same flow for Linux (needs `dotnet`, `cargo`, `psql`, `curl`, `python3`).
It stops on Ctrl-C or SIGTERM, with the same order and exit status 130 for an interrupt.

## Recorded checks

Tested here (Linux VM, PostgreSQL 18.4 binaries run on port 5433, PowerShell 7.6.6 for Linux):

- `start-server.ps1` and `start-server.sh` each started the whole stack (`eve-sde ensure`, simulation, host, front) and
  were waited for. The headless client's `tests/test_live.py` (5 tests) passed against the front.
- Ctrl-C (SIGINT to the process group, as a console Ctrl-C would send it) stopped all three in about 2.5 s with no
  process left behind. `start-server.sh` forwarded SIGINT and exited with 130.
- `stop-server.ps1` stopped the processes of a start whose PowerShell had been killed (no shutdown ran).
- Unreachable PostgreSQL exits with status 1; `server.env` is read; environment variables win over it; the password
  is masked in the printed settings.
- `cargo check --workspace --all-targets --target x86_64-pc-windows-gnu` passed (no linker needed for `check`).
- `eve-sim` shuts down on Ctrl-C while a host is connected (this was broken: the accept loop was not polled).

These checks ran on a Linux VM with PowerShell 7.6.6 and PostgreSQL 18.4. They exercise the scripts and TCP paths on Linux, not native Windows console semantics.

The Windows workflow now builds and tests the workspace on `windows-latest`, runs the .NET and Python suites, and walks the login-to-character path through the complete stack. Its run history is the source for native Windows CI evidence.

## Known limits

- The host's `tcp:` listener accepts numeric IPv4 addresses only (`localhost` fails); the scripts use numeric addresses.
- `tools/dev/*.sh` remain bash scripts for Linux, Git Bash, or WSL; Windows equivalents for local checks and headless tests are in `tools/windows`.
- `global.json` lists `../.dotnet` as an SDK path; CI and the owner checkout should confirm SDK selection from Windows paths.
- Closing the window does not run the orderly shutdown; the children then stop when the console closes.
