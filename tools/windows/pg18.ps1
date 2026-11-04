[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'url')]
    [string]$Action = 'status',
    [string]$Database = 'eve'
)

$ErrorActionPreference = 'Stop'
$service = Get-Service -Name 'postgresql-x64-18' -ErrorAction SilentlyContinue
if ($Action -eq 'url') {
    if ($Database -notmatch '^[a-z0-9_]+$') { throw 'Database must contain lowercase letters, digits, and underscores.' }
    $port = 5432
    if ($env:PGPORT -match '^\d+$' -and [int]$env:PGPORT -gt 0) { $port = [int]$env:PGPORT }
    Write-Output "postgres://postgres@127.0.0.1:$port/$Database"
    return
}

if (-not $service) { throw 'PostgreSQL 18 service postgresql-x64-18 was not found. Install PostgreSQL 18 and select its server component.' }
if ($Action -eq 'start') {
    if ($service.Status -ne 'Running') { Start-Service -Name $service.Name }
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
} elseif ($Action -eq 'stop') {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $service.Name; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
} else {
    $service.Refresh()
    Write-Output $service.Status
    if ($service.Status -eq 'Running') { return }
    throw 'PostgreSQL 18 service is stopped.'
}

