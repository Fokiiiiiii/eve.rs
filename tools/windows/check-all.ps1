[CmdletBinding()]
param(
    [string[]]$Skip = @(),
    [switch]$InstallPythonDependencies
)

$ErrorActionPreference = 'Stop'
$env:CARGO_INCREMENTAL = '0'
$env:CARGO_TERM_COLOR = 'always'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:EVE_MARKET_SEED = 'skip'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
$toolchain = if ($env:RUST_TOOLCHAIN) { $env:RUST_TOOLCHAIN } else { '1.99.0' }
$pgUrl = 'postgres://postgres@127.0.0.1:5432/eve'
if ($env:EVE_PG_URL) { $pgUrl = $env:EVE_PG_URL }
$parsedPgUrl = [Uri]$pgUrl
$credentialText = [Uri]::UnescapeDataString($parsedPgUrl.UserInfo)
$credentialSeparator = $credentialText.IndexOf(':')
$pgUser = if ($credentialSeparator -ge 0) { $credentialText.Substring(0, $credentialSeparator) } else { $credentialText }
$pgPassword = if ($credentialSeparator -ge 0) { $credentialText.Substring($credentialSeparator + 1) } else { '' }
if (-not $pgUser) { $pgUser = 'postgres' }
$pgDatabase = $parsedPgUrl.AbsolutePath.TrimStart('/')
$pgPort = if ($parsedPgUrl.Port -gt 0) { $parsedPgUrl.Port } else { 5432 }
$pgNpgsql = "Host=$($parsedPgUrl.Host);Port=$pgPort;Username=$pgUser;Password=$pgPassword;Database=$pgDatabase"
$env:EVE_PG_URL = $pgUrl

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    if ($Skip -contains $Name) { Write-Host "Skipping $Name"; return }
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

Invoke-Step 'fmt' { cargo "+$toolchain" fmt --all --check }
Invoke-Step 'clippy' { cargo "+$toolchain" clippy --workspace --all-targets -- -D warnings }
Invoke-Step 'rust' {
    & (Join-Path $PSScriptRoot 'pg18.ps1') start
    $env:EVE_PG_URL = $pgUrl
    cargo "+$toolchain" run -q -p eve-sde -- ensure
    if ($LASTEXITCODE -ne 0) { throw 'eve-sde ensure failed' }
    cargo "+$toolchain" test --workspace
}
Invoke-Step 'dotnet' {
    & (Join-Path $PSScriptRoot 'pg18.ps1') start
    $env:EVE_PG_URL = $pgUrl
    cargo "+$toolchain" run -q -p eve-sde -- ensure --pg $pgUrl
    if ($LASTEXITCODE -ne 0) { throw 'eve-sde ensure failed' }
    Push-Location (Join-Path $root 'dotnet')
    try {
        cargo "+$toolchain" run -q -p eve-sde -- ensure --missions --pg $pgUrl
        if ($LASTEXITCODE -ne 0) { throw 'Mission data loading failed.' }
        $env:EVE_PG_URL = $pgNpgsql
        dotnet test EveRs.slnx -c Release -nologo --verbosity minimal -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }
    } finally {
        dotnet build-server shutdown
            Pop-Location
    }
}
Invoke-Step 'fixtures' {
    python tools/dev/gen-simlink-fixtures.py
    if ($LASTEXITCODE -ne 0) { throw 'simulation fixture generation failed' }
    python tools/dev/gen-hostlink-fixtures.py
    if ($LASTEXITCODE -ne 0) { throw 'host fixture generation failed' }
    git diff --exit-code -- oracle/
}
Invoke-Step 'python' {
    $pythonExe = 'python'
    $venvPython = Join-Path $root 'tools/headless-client/.venv/Scripts/python.exe'
    if (-not (Test-Path -LiteralPath $venvPython)) {
        python -m venv (Join-Path $root 'tools/headless-client/.venv')
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the headless client virtual environment.' }
    }
    $pythonExe = $venvPython
    if ($InstallPythonDependencies) {
        & $pythonExe -m pip install -r (Join-Path $root 'tools/headless-client/requirements-dev.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Could not install Python test dependencies.' }
    }
    & $pythonExe -m pytest -q -rs
}
Invoke-Step 'live' {
    & (Join-Path $PSScriptRoot 'pg18.ps1') start
    $startScript = Join-Path $PSScriptRoot 'start-server.ps1'
    $server = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', ('-File "' + $startScript + '"')) -PassThru
    try {
        $deadline = (Get-Date).AddMinutes(10)
        $ready = $false
        while ((Get-Date) -lt $deadline) {
            if ($server.HasExited) { throw "Server startup stopped with exit code $($server.ExitCode)." }
            $client = New-Object System.Net.Sockets.TcpClient
            try { $client.Connect('127.0.0.1', 26000); $ready = $true } catch { Start-Sleep -Milliseconds 500 } finally { $client.Close() }
            if ($ready) { break }
        }
        if (-not $ready) { throw 'The server did not listen on 127.0.0.1:26000 within ten minutes.' }
        & (Join-Path $PSScriptRoot 'run-headless.ps1') -Live -PytestArgs @('tests/test_live.py', 'tests/test_login_to_character.py')
        if ($LASTEXITCODE -ne 0) { throw 'The Windows live client tests failed.' }
    } finally {
        & (Join-Path $PSScriptRoot 'stop-server.ps1')
        if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    }
}
