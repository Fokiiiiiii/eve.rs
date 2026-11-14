<#
.SYNOPSIS
  Starts the eve.rs server from the packaged Windows build (eve-rs-windows-x64-*.zip).

.DESCRIPTION
  The zip is the output of the Windows CD workflow: bin\ (eve-net, eve-sim, eve-sde), host\ (the published C# host),
  server.env.example and this script (shipped as start-server.ps1). It starts the same stack in the same order as
  tools/windows/start-server.ps1, but it builds nothing: it needs the .NET 10 runtime, PostgreSQL 18 (psql on PATH
  or under Program Files) and, for the first start, a network connection (eve-sde downloads the pinned static data
  with curl). Ctrl-C stops the three processes: the front first, then the host, then the simulation.

  Settings come from the environment, then from server.env next to this script, then from the defaults below
  (see server.env.example).

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
if ($Trace -or $Strict) { $Dev = $true }

$root = $PSScriptRoot
$binDir = Join-Path $root 'bin'
$hostDll = Join-Path $root 'host/eve-host.dll'
$stateDir = Join-Path $root '.cache/server'
$logDir = Join-Path $stateDir 'logs'
$pidFile = Join-Path $stateDir 'server.pids'

# The defaults every start script passes on. The host's own defaults are Unix sockets, which Windows does not use here.
$defaults = [ordered]@{
    EVE_PG_URL            = 'postgres://postgres@127.0.0.1:5432/eve'
    EVE_HOST              = 'tcp:127.0.0.1:26010'
    EVE_SIM_LISTEN        = '127.0.0.1:26011'
    EVE_SIM_SOCKET        = 'tcp:127.0.0.1:26011'
    EVE_NET_LISTEN        = '127.0.0.1:26000'
    EVE_DEV_AUTO_CREATE   = '1'
    EVE_DEV_SKIP_PASSWORD = '0'
    # the first start would fetch the NPC market from CCP's ESI; the other start scripts and CI skip it too
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
    $startParameters = @{
        FilePath               = $file
        WorkingDirectory       = $root
        NoNewWindow            = $true
        PassThru               = $true
        RedirectStandardOutput = (Join-Path $logDir "$name.out.log")
        RedirectStandardError  = (Join-Path $logDir "$name.err.log")
    }
    if (-not [string]::IsNullOrEmpty($argumentLine)) { $startParameters.ArgumentList = $argumentLine }
    $process = Start-Process @startParameters
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
            Write-Warning "$key is ignored by a production start; use -Dev to honour it"
            [Environment]::SetEnvironmentVariable($key, $null, 'Process')
        }
    }
    Write-Step ("settings: PostgreSQL {0}; host {1}; simulation {2}; front {3}" -f `
        (Hide-Password $env:EVE_PG_URL), $env:EVE_HOST, $env:EVE_SIM_LISTEN, $env:EVE_NET_LISTEN)

    # Prerequisites: the packaged files, the .NET 10 runtime, curl (eve-sde downloads with it) and PostgreSQL 18
    foreach ($binary in 'eve-net', 'eve-sim', 'eve-sde') {
        if (-not (Test-Path -LiteralPath (Join-Path $binDir ($binary + '.exe')))) { throw "bin\$binary.exe is missing: unpack the whole zip" }
    }
    if (-not (Test-Path -LiteralPath $hostDll)) { throw 'host\eve-host.dll is missing: unpack the whole zip' }

    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) { throw '.NET runtime not found: install the .NET 10 runtime from https://dotnet.microsoft.com/download' }
    $dotnet = $dotnetCmd.Source
    $runtimes = & $dotnet --list-runtimes 2>$null
    if ($LASTEXITCODE -ne 0 -or -not ($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' })) {
        throw 'the .NET 10 runtime (Microsoft.NETCore.App 10.x) is required; dotnet --list-runtimes does not list it'
    }
    if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw 'curl.exe not found (eve-sde downloads the static data with it)' }

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

    # The static data: a no-op when the database already holds the pinned build (the simulation reads .cache\sde here)
    Write-Step 'loading the static data (eve-sde ensure; the pinned build is downloaded on the first start)'
    Push-Location $root
    try {
        & (Join-Path $binDir 'eve-sde.exe') ensure --pg $env:EVE_PG_URL
        if ($LASTEXITCODE -ne 0) { throw 'eve-sde ensure failed' }
    }
    finally { Pop-Location }

    New-Item -ItemType Directory -Force -Path $logDir | Out-Null

    Write-Step "starting eve-sim on $env:EVE_SIM_LISTEN"
    $sim = Start-Child 'eve-sim' (Join-Path $binDir 'eve-sim.exe') ''
    Wait-Until 'eve-sim' $sim { Test-TcpListening $env:EVE_SIM_LISTEN } 300

    $hostErr = Join-Path $logDir 'eve-host.err.log'
    Write-Step "starting eve-host on $env:EVE_HOST"
    $hostProcess = Start-Child 'eve-host' $dotnet ('"' + $hostDll + '"')
    # the first start loads the catalogs and runs the migrations: give it up to ten minutes
    Wait-Until 'eve-host' $hostProcess { (Test-Path -LiteralPath $hostErr) -and (Select-String -LiteralPath $hostErr -SimpleMatch 'eve-host: listening on' -Quiet) } 600

    Write-Step "starting eve-net on $env:EVE_NET_LISTEN"
    $net = Start-Child 'eve-net' (Join-Path $binDir 'eve-net.exe') ''
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
