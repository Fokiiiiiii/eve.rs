<#
.SYNOPSIS
  Starts the eve.rs server on Windows, the same stack and order as tools/server/start-server.sh.

.DESCRIPTION
  Checks the prerequisites, builds the Rust binaries (cargo) and the host (dotnet), creates the PostgreSQL 18
  database if it is missing, loads the static data (eve-sde ensure), then starts the space simulation (eve-sim),
  the service host (eve-host) and the network front (eve-net), in that order, all over TCP. Each one is waited for
  until it listens. Ctrl-C stops the three: the front first, then the host, then the simulation.

  Settings come from the environment, then from server.env in the repository root, then from the defaults below.
  See tools/windows/README.md.

.PARAMETER Dev
  Development start: honours EVE_TRACE_RPC and EVE_STRICT_RPC. Without it they are cleared, with a warning.

.PARAMETER Trace
  Sets EVE_TRACE_RPC=full (implies -Dev).

.PARAMETER Strict
  Sets EVE_STRICT_RPC=1 (implies -Dev).
#>
[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$Trace,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
if ($Trace -or $Strict) { $Dev = $true }

$isWindowsHost = [Environment]::OSVersion.Platform -eq 'Win32NT'
$exeSuffix = if ($isWindowsHost) { '.exe' } else { '' }
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stateDir = Join-Path $root '.cache/server'
$logDir = Join-Path $stateDir 'logs'
$pidFile = Join-Path $stateDir 'server.pids'
$releaseDir = Join-Path $(if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $root 'target' }) 'release'

# The defaults every script passes on. The host's own defaults are Unix sockets, which Windows does not use here.
$defaults = [ordered]@{
    EVE_PG_URL            = 'postgres://postgres@127.0.0.1:5432/eve'
    EVE_HOST              = 'tcp:127.0.0.1:26010'
    EVE_SIM_LISTEN        = '127.0.0.1:26011'
    EVE_SIM_SOCKET        = 'tcp:127.0.0.1:26011'
    EVE_NET_LISTEN        = '127.0.0.1:26000'
    EVE_DEV_AUTO_CREATE   = '1'
    EVE_DEV_SKIP_PASSWORD = '0'
    # the first start would fetch the NPC market from CCP's ESI; docker-compose.yml and CI skip it too
    EVE_MARKET_SEED       = 'skip'
}

$children = New-Object System.Collections.ArrayList
$exitCode = 0

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Hide-Password([string]$url) { [regex]::Replace($url, '(://[^:/@]+):[^@]*@', '$1:***@') }

function Import-ServerEnv([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $lineNumber = 0
    foreach ($raw in Get-Content -LiteralPath $path) {
        $lineNumber++
        $line = $raw.Trim()
        if ($line -eq '' -or $line.StartsWith('#')) { continue }
        $equals = $line.IndexOf('=')
        if ($equals -lt 1) { throw "server.env line $lineNumber is not KEY=VALUE" }
        $key = $line.Substring(0, $equals).Trim()
        $value = $line.Substring($equals + 1).Trim()
        if ($value.Length -ge 2 -and $value[0] -eq $value[-1] -and ($value[0] -eq '"' -or $value[0] -eq "'")) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        if (-not [Environment]::GetEnvironmentVariable($key, 'Process')) {
            [Environment]::SetEnvironmentVariable($key, $value, 'Process')
        }
    }
}

function Test-TcpListening([string]$address) {
    $colon = $address.LastIndexOf(':')
    $hostName = $address.Substring(0, $colon)
    if ($hostName -eq '0.0.0.0') { $hostName = '127.0.0.1' }
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect($hostName, [int]$address.Substring($colon + 1))
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Wait-Until([string]$name, $process, [scriptblock]$ready, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        if ($process.HasExited) { throw "$name stopped during start-up (exit code $($process.ExitCode)); see $logDir" }
        if (& $ready) { Write-Step "$name is ready"; return }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "$name is not ready after $seconds s; see $logDir"
}

function Start-Child([string]$name, [string]$file, [string]$argumentLine) {
    # NoNewWindow: the children share this console, so Ctrl-C reaches them and they shut down cleanly.
    $process = Start-Process -FilePath $file -ArgumentList $argumentLine -WorkingDirectory $root -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $logDir "$name.out.log") -RedirectStandardError (Join-Path $logDir "$name.err.log")
    [void]$children.Add([pscustomobject]@{ Name = $name; Process = $process })
    return $process
}

function Stop-Children {
    for ($i = $children.Count - 1; $i -ge 0; $i--) {
        $child = $children[$i]
        if ($child.Process.HasExited) { continue }
        Write-Step "stopping $($child.Name)"
        if (-not $child.Process.WaitForExit(20000)) {
            Write-Host "$($child.Name) did not stop within 20 s; ending it" -ForegroundColor Yellow
            $child.Process.Kill()
        }
    }
}

try {
    Import-ServerEnv (Join-Path $root 'server.env')
    foreach ($key in $defaults.Keys) {
        if (-not [Environment]::GetEnvironmentVariable($key, 'Process')) {
            [Environment]::SetEnvironmentVariable($key, $defaults[$key], 'Process')
        }
    }
    if ($Trace) { $env:EVE_TRACE_RPC = 'full' }
    if ($Strict) { $env:EVE_STRICT_RPC = '1' }
    foreach ($key in 'EVE_TRACE_RPC', 'EVE_STRICT_RPC') {
        if (-not $Dev -and [Environment]::GetEnvironmentVariable($key, 'Process')) {
            Write-Warning "$key is ignored by a production start; use start-server-dev.bat to honour it"
            [Environment]::SetEnvironmentVariable($key, $null, 'Process')
        }
    }
    Write-Step ("settings: PostgreSQL {0}; host {1}; simulation {2}; front {3}" -f `
        (Hide-Password $env:EVE_PG_URL), $env:EVE_HOST, $env:EVE_SIM_LISTEN, $env:EVE_NET_LISTEN)
    if ($Dev) {
        Write-Step ("development start: EVE_TRACE_RPC={0}; EVE_STRICT_RPC={1}" -f $env:EVE_TRACE_RPC, $env:EVE_STRICT_RPC)
    }

    # Prerequisites
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) { throw '.NET SDK not found: install .NET 10 from https://dotnet.microsoft.com/download' }
    $dotnet = $dotnetCmd.Source
    $sdkVersion = & $dotnet --version 2>$null
    if ($LASTEXITCODE -ne 0 -or "$sdkVersion" -notmatch '^10\.') {
        throw "a .NET 10 SDK is required (dotnet --version says '$sdkVersion'; global.json pins 10.0.401)"
    }

    $cargoCmd = Get-Command cargo -ErrorAction SilentlyContinue
    if ($cargoCmd) {
        Write-Step 'building eve-net, eve-sim and eve-sde (cargo build --release)'
        Push-Location $root
        try {
            & $cargoCmd.Source build --release -p eve-net -p eve-sim -p eve-sde
            if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
        }
        finally { Pop-Location }
    }
    foreach ($binary in 'eve-net', 'eve-sim', 'eve-sde') {
        if (-not (Test-Path -LiteralPath (Join-Path $releaseDir ($binary + $exeSuffix)))) {
            throw "$binary is not built ($releaseDir): install Rust from https://rustup.rs or copy target\release from a build machine"
        }
    }

    Write-Step 'building eve-host (dotnet build -c Release)'
    Push-Location (Join-Path $root 'dotnet')
    try {
        & $dotnet build -c Release src/Eve.Host -nologo -v q -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { throw 'dotnet build of Eve.Host failed' }
    }
    finally {
        dotnet build-server shutdown
        Pop-Location
    }
    $hostDll = Join-Path $root 'dotnet/src/Eve.Host/bin/Release/net10.0/eve-host.dll'

    # PostgreSQL 18: reachable, the right major version, and the eve database exists
    if ($env:EVE_PG_URL -notmatch '^postgres(ql)?://') { throw 'EVE_PG_URL must be a postgres:// URL (the database is created from it)' }
    $pgUri = [Uri]$env:EVE_PG_URL
    $dbName = $pgUri.AbsolutePath.TrimStart('/')
    if ($dbName -notmatch '^[A-Za-z0-9_]+$') { throw "database name '$dbName' must be letters, digits and underscores" }
    $pgPort = if ($pgUri.Port -gt 0) { $pgUri.Port } else { 5432 }
    $userPart = if ($pgUri.UserInfo) { $pgUri.UserInfo + '@' } else { '' }
    $adminUrl = "postgres://$userPart$($pgUri.Host):$pgPort/postgres$($pgUri.Query)"

    $psqlCmd = Get-Command psql -ErrorAction SilentlyContinue
    $psql = $null
    if ($psqlCmd) { $psql = $psqlCmd.Source }
    elseif ($env:ProgramFiles -and (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'PostgreSQL/18/bin/psql.exe'))) {
        $psql = Join-Path $env:ProgramFiles 'PostgreSQL/18/bin/psql.exe'
    }
    if (-not $psql) { throw 'psql not found: install PostgreSQL 18 (https://www.postgresql.org/download/windows/) and add its bin folder to PATH' }

    Write-Step "checking PostgreSQL at $(Hide-Password $adminUrl)"
    $serverVersion = & $psql $adminUrl -tAc 'show server_version_num'
    if ($LASTEXITCODE -ne 0) { throw 'cannot connect to PostgreSQL (see the psql error above); check EVE_PG_URL and that the service runs' }
    if ("$serverVersion".Trim() -notmatch '^18') { throw "PostgreSQL 18 is required, the server reports version_num $serverVersion" }
    $exists = & $psql $adminUrl -tAc "select 1 from pg_database where datname = '$dbName'"
    if ("$exists".Trim() -ne '1') {
        Write-Step "creating database $dbName"
        & $psql $adminUrl -qc "create database $dbName"
        if ($LASTEXITCODE -ne 0) { throw "could not create database $dbName" }
    }

    # The static data: a no-op when the database already holds the pinned build
    Write-Step 'loading the static data (eve-sde ensure; the pinned build is downloaded on the first start)'
    Push-Location $root
    try {
        & (Join-Path $releaseDir ('eve-sde' + $exeSuffix)) ensure --pg $env:EVE_PG_URL
        if ($LASTEXITCODE -ne 0) { throw 'eve-sde ensure failed' }
    }
    finally { Pop-Location }

    New-Item -ItemType Directory -Force -Path $logDir | Out-Null

    Write-Step "starting eve-sim on $env:EVE_SIM_LISTEN"
    $sim = Start-Child 'eve-sim' (Join-Path $releaseDir ('eve-sim' + $exeSuffix)) ''
    Wait-Until 'eve-sim' $sim { Test-TcpListening $env:EVE_SIM_LISTEN } 300

    $hostErr = Join-Path $logDir 'eve-host.err.log'
    Write-Step "starting eve-host on $env:EVE_HOST"
    $hostProcess = Start-Child 'eve-host' $dotnet ('"' + $hostDll + '"')
    # the first start loads the catalogs and runs the migrations: give it up to ten minutes
    Wait-Until 'eve-host' $hostProcess { (Test-Path -LiteralPath $hostErr) -and (Select-String -LiteralPath $hostErr -SimpleMatch 'eve-host: listening on' -Quiet) } 600

    Write-Step "starting eve-net on $env:EVE_NET_LISTEN"
    $net = Start-Child 'eve-net' (Join-Path $releaseDir ('eve-net' + $exeSuffix)) ''
    Wait-Until 'eve-net' $net { Test-TcpListening $env:EVE_NET_LISTEN } 60

    $pidLines = foreach ($child in $children) { '{0},{1},{2}' -f $child.Name, $child.Process.Id, $child.Process.StartTime.Ticks }
    Set-Content -LiteralPath $pidFile -Value $pidLines
    Write-Step "eve.rs is up: point the client at $($env:EVE_NET_LISTEN). Press Ctrl-C to stop."

    while ($true) {
        foreach ($child in $children) {
            if ($child.Process.HasExited) { throw "$($child.Name) stopped (exit code $($child.Process.ExitCode)); see $logDir" }
        }
        Start-Sleep -Seconds 1
    }
}
catch {
    Write-Host "error: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    Stop-Children
    Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue
}
exit $exitCode
