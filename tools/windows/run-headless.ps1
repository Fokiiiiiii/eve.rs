[CmdletBinding()]
param(
    [string[]]$PytestArgs = @('tests/test_marshal_golden.py', 'tests/test_handshake_mock.py'),
    [switch]$Live,
    [string]$Python = 'python'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$client = Join-Path $root 'tools/headless-client'
$venvPython = Join-Path $client '.venv/Scripts/python.exe'
if (Test-Path -LiteralPath $venvPython) { $Python = $venvPython }
Push-Location $client
try {
    if ($Live) { $env:EVE_LIVE_SERVER = 'eve.rs' }
    & $Python -m pytest -q -rs @PytestArgs
    if ($LASTEXITCODE -ne 0) { throw "pytest failed with exit code $LASTEXITCODE" }
} finally {
    if ($Live) { Remove-Item Env:EVE_LIVE_SERVER -ErrorAction SilentlyContinue }
    Pop-Location
}
