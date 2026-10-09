<#
.SYNOPSIS
    Starts an unmodified EVE client (build 3396210) against a local eve.rs server.
.DESCRIPTION
    Run patch_client.py on the client copy first; this script refuses to start
    the client unless start.ini already points at -Server with cryptoPack = Placebo.
    It starts bin64\exefile.exe with /port and /noCrashReportUpload and points
    EO_REMOTEFILECACHEFOLDER at the ResFiles folder beside the client's tq folder.
.PARAMETER ClientDir
    The client folder that holds start.ini and bin64 (the EVE 'tq' folder).
.PARAMETER Server
    The eve.rs front's IPv4 address, as given to patch_client.py. Default 127.0.0.1.
.PARAMETER Port
    Game port for /port:. Default 26000 (eve-net's default). To route through a
    proxy such as the tools/mitm capture (debug-tools branch, not required here),
    pass the proxy's listen port and have the proxy forward to eve.rs.
.EXAMPLE
    .\launch-client.ps1 -ClientDir "C:\EVE\SharedCache\tq" -Port 26000
#>
param(
    [Parameter(Mandatory = $true)][string]$ClientDir,
    [string]$Server = "127.0.0.1",
    [ValidateRange(1, 65535)][int]$Port = 26000
)

$ErrorActionPreference = "Stop"

$tq = (Resolve-Path -LiteralPath $ClientDir).Path
$bin = Join-Path $tq "bin64"
$exe = Join-Path $bin "exefile.exe"
$ini = Join-Path $tq "start.ini"
$resFiles = Join-Path (Split-Path -Parent $tq) "ResFiles"

foreach ($required in @($exe, $ini)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing $required" }
}
if (-not (Test-Path -LiteralPath $resFiles -PathType Container)) {
    throw "ResFiles folder is missing beside the tq folder: $resFiles. Copy the full EVE shared cache."
}

$text = Get-Content -LiteralPath $ini -Raw
if ($text -notmatch ("(?im)^\s*server\s*=\s*" + [regex]::Escape($Server) + "\s*$")) {
    throw "start.ini does not say server = $Server. Run patch_client.py --client-dir `"$tq`" --server $Server first."
}
if ($text -notmatch "(?im)^\s*cryptoPack\s*=\s*Placebo\s*$") {
    throw "start.ini does not say cryptoPack = Placebo. Run patch_client.py first."
}

$env:EO_REMOTEFILECACHEFOLDER = $resFiles
$process = Start-Process -FilePath $exe -ArgumentList @("/port:$Port", "/noCrashReportUpload") `
    -WorkingDirectory $bin -PassThru
Write-Host "Started the EVE client (pid $($process.Id)) for $Server`:$Port."
