# Windows development and server operation

Windows uses the same Rust simulation, .NET service host, and Rust network front as Linux. The host listens on TCP by default on Windows, and the host and simulation communicate over the configured loopback TCP endpoint. Unix builds retain their Unix domain socket defaults. The PowerShell scripts in `tools/windows` support Windows PowerShell 5.1.

## Prerequisites

- Windows 10 or later, PowerShell 5.1, Git, and a checkout of this repository.
- Rust from [rustup](https://rustup.rs/) with the MSVC target and Visual Studio Build Tools C++ workload.
- .NET 10 SDK. The repository's `global.json` selects 10.0.401 or a later feature band.
- Python 3.12 and PostgreSQL 18. Install PostgreSQL 18 with the EDB installer, retain the `postgresql-x64-18` service, and add `C:\Program Files\PostgreSQL\18\bin` to `PATH`. The local database used by the scripts is `eve`.
- `curl.exe`, which is included with current Windows versions. The first SDE setup downloads the pinned data set.

Set the PostgreSQL connection in the process environment or in an ignored `server.env`. Use the database password configured by the PostgreSQL installer:

```powershell
$env:EVE_PG_URL = 'postgres://postgres:<password>@127.0.0.1:5432/eve'
$env:PGPASSWORD = '<password>'
```

Start and inspect the PostgreSQL service:

```powershell
tools\windows\pg18.ps1 start
tools\windows\pg18.ps1 status
tools\windows\pg18.ps1 url
```

`pg18.ps1` manages an installed PostgreSQL 18 service; it does not install PostgreSQL or initialize another cluster.

## Start and stop the server

From any directory, run:

```powershell
tools\windows\start-server.bat
```

The script builds the Rust binaries and Host, applies the pinned static data, and starts the simulation, Host, then network front. The client connects to `127.0.0.1:26000` by default. Logs are written under `.cache\server\logs`. Press Ctrl+C in that window to stop the stack. For a development start with RPC diagnostics, use `tools\windows\start-server-dev.bat` or `tools\windows\start-server.ps1 -Trace`.

To stop processes from a second PowerShell window:

```powershell
tools\windows\stop-server.bat
```

## Run checks

Install the headless-client dependencies and run the local check sequence:

```powershell
tools\windows\check-all.ps1 -InstallPythonDependencies
```

The sequence runs format, clippy, Rust workspace tests, .NET tests, fixture generators, Python tests, and a short live client walk. Omit expensive checks with `-Skip @('live','dotnet')`. Run an explicit headless test set against a running server with:

```powershell
tools\windows\run-headless.ps1 -Live -PytestArgs @('tests/test_live.py','tests/test_login_to_character.py')
```

Without `-Live`, the headless runner defaults to the marshal golden and mock handshake tests. Database-backed tests use the `eve` database and the connection string supplied by `check-all.ps1`.

## CI coverage

The `windows` job in `.github/workflows/ci.yml` runs on `windows-latest` when a push changes Rust, .NET, Windows/developer tooling, headless-client files, the SDK selection, line-ending rules, or the workflow itself. It can also be selected with `workflow_dispatch` using `jobs=windows`, and runs weekly. The job installs PostgreSQL 18 on the runner, loads the SDE, runs Rust formatting, clippy and workspace tests, builds and tests .NET, runs Python tests, invokes the headless runner under Windows PowerShell 5.1, then starts and stops the full stack through the batch entry points while exercising the login-to-character client path. The golden marshal fixture is UTF-8 JSON, and its reader names UTF-8 explicitly so Windows' default code page does not change decoding.

The required CI gate includes the Windows selection and Windows execution jobs. Linux jobs retain their push and manual triggers; the weekly schedule selects the Windows coverage. A green Windows job proves the CI runner path, not the owner's installed Windows environment or a graphical EVE client session.
