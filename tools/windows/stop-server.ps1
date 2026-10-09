<#
.SYNOPSIS
  Stops the eve.rs processes that start-server.ps1 recorded, front first.

.DESCRIPTION
  Use it when the start window was closed before the shutdown ran, or when the server runs elsewhere under the same
  root. Each recorded process is stopped only if its start time matches the recorded one (to within a second), so a
  reused process id is never touched. The stop is forceful (Stop-Process -Force): the children are not sent Ctrl-C. Ctrl-C in the
  start window is the graceful way to stop.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$pidFile = Join-Path $root '.cache/server/server.pids'

if (-not (Test-Path -LiteralPath $pidFile)) {
    Write-Host 'nothing to stop: there is no server.pids file'
    exit 0
}

$entries = @(Get-Content -LiteralPath $pidFile | Where-Object { $_ -match ',' })
$stopped = 0
for ($i = $entries.Count - 1; $i -ge 0; $i--) {
    $name, $processId, $ticks = $entries[$i] -split ','
    $process = Get-Process -Id ([int]$processId) -ErrorAction SilentlyContinue
    # the start time may differ by a few microseconds between reads (Linux computes it from boot time): one second is plenty
    if ($process -and [Math]::Abs($process.StartTime.Ticks - [long]$ticks) -lt 10000000) {
        Write-Host "stopping $name (process $processId)"
        Stop-Process -Id $process.Id -Force
        $stopped++
    }
    else {
        Write-Host "$name is not running"
    }
}
Remove-Item -LiteralPath $pidFile
Write-Host "stopped $stopped process(es)"
