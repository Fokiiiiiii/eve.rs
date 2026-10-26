# Windows production start and client connection

## First run

Open PowerShell in the repository root. Install PostgreSQL 18 with the Windows installer, retain the
`postgresql-x64-18` service, and add its `bin` folder to `PATH`. Also install Git, Rust with the
MSVC toolchain and Visual Studio C++ Build Tools, .NET 10, Python 3.12, and `curl.exe`.

Copy `server.env.example` to the ignored `server.env`. Set `EVE_PG_URL` to the local `eve`
database and URL-encode any reserved characters in the password:

```powershell
Copy-Item server.env.example server.env
# Edit server.env and set EVE_PG_URL=postgres://postgres:<URL-encoded-password>@127.0.0.1:5432/eve
tools\windows\pg18.ps1 start
tools\windows\pg18.ps1 status
tools\windows\start-server.bat
```

The service script manages the installed PostgreSQL Windows service; it does not install PostgreSQL or
initialize another cluster. The server launcher requires PostgreSQL 18, creates the configured database
if missing, and uses the password in `EVE_PG_URL`. Do not put a real password in a tracked file.

On the first server start, `eve-sde ensure` downloads and checks the build pinned in `sde/lock.json`:
build 3396210, SHA-256 `17ae0c7f00fec4c2b79f785a9c35debdc5ae9edde558d18dc26d51cf87279b3e`.
It loads the SDE before the server processes start. The Host runs its outstanding database migrations
during startup. Wait for `eve.rs is up`; the launcher only reports ready after the Host opens its
listener and the front accepts connections.

## Ports and client launch

The Windows production defaults are:

| Endpoint | Default | Connection |
|---|---:|---|
| Network front | `127.0.0.1:26000` | V24.01 client |
| Host | `tcp:127.0.0.1:26010` | Network front |
| Simulation | `127.0.0.1:26011` | Host |

Only port 26000 is the client connection. The Linux live-test helper uses different ports by default:
front `26100`, public gateway `26101`, and test SSO issuer `26261`.

From the directory containing the unmodified EVE client executable for V24.01 build 3396210, launch:

```powershell
.\exefile.exe /server:127.0.0.1 /port:26000
```

V24.01 reads `/server:` and `/port:` from the process arguments in
`/home/user/eve-logic/utillib/__init__.py:102-122`. The login screen uses the resulting values for its
status request and login in `/home/user/eve-logic/eve/client/script/ui/login/loginII.py:351-360,475`.
These callsites were cross-checked across the client tree: `serverInfo.GetServerInfo()` supplies UI and
diagnostic metadata after a connection; the server and port sent to `connection.Login` come from
`utillib.GetServerName()` and `utillib.GetServerPort()`.

The defaults `EVE_DEV_AUTO_CREATE=1` and `EVE_DEV_SKIP_PASSWORD=0` create an account on its first
unknown username and then validate that account's password on later logins.

Press Ctrl+C in the server window for a graceful stop. If that window is gone, run
`tools\windows\stop-server.bat` from the repository root.

## Settings and checks

Configuration precedence is process environment, then ignored root `server.env`, then launcher defaults.
The usual local database URL is `postgres://postgres@127.0.0.1:5432/eve`; use the installed service's
password in `server.env`. To print the URL for another database name without connecting:

```powershell
tools\windows\pg18.ps1 url eve
```

The complete PowerShell check sequence is:

```powershell
tools\windows\check-all.ps1 -InstallPythonDependencies
```

It includes PostgreSQL-backed tests and the live client walk. The Windows scripts can be run directly
under PowerShell 5.1; Bash scripts under `tools/dev` require Git Bash, WSL, or Linux. On Linux, check
shell syntax without starting PostgreSQL or the server:

```sh
bash -n tools/dev/run-eve-live.sh tools/dev/run-evejs-live.sh tools/dev/pg18.sh tools/dev/db-lock.sh tools/server/start-server.sh
```

The Linux production start is `tools/server/start-server.sh`. The test helper
`tools/dev/run-eve-live.sh` starts an in-memory account-only server without `EVE_PG_URL`, or creates
scratch schemas when `EVE_PG_URL` is set; that helper is not the Windows production launcher.
