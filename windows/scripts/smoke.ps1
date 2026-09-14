<#
.SYNOPSIS
    Smoke test: starts the client with AVAIBE_SMOKE_TEST=1 and checks that the log contains SMOKE_OK.
    Exit code 0 on success, 1 on failure.
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "publish\AvaibeExam.exe"

if (-not $NoBuild -or -not (Test-Path $exe)) {
    & (Join-Path $PSScriptRoot "build.ps1") -Configuration Debug | Out-Null
}
if (-not (Test-Path $exe)) { Write-Error "Executable not found: $exe"; exit 1 }

$logDir = Join-Path $env:LOCALAPPDATA "AvaibeExam\logs"
$logFile = Join-Path $logDir ("avaibe-" + (Get-Date -Format "yyyyMMdd") + ".log")
$marker = "SMOKE-" + [guid]::NewGuid().ToString("N")

$env:AVAIBE_SMOKE_TEST = "1"
Remove-Item Env:AVAIBE_AUTO_RUN -ErrorAction SilentlyContinue
$before = if (Test-Path $logFile) { (Get-Content $logFile | Measure-Object -Line).Lines } else { 0 }

Write-Host "Starting smoke test ($exe)"
$proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Error "Client did not exit within $TimeoutSeconds s"
    try { $proc.Kill() } catch {}
    exit 1
}

$ok = $false
if (Test-Path $logFile) {
    $lines = Get-Content $logFile
    $new = $lines | Select-Object -Skip $before
    $ok = ($new | Select-String -SimpleMatch "SMOKE_OK" | Measure-Object).Count -gt 0
}

if ($ok -and $proc.ExitCode -eq 0) {
    Write-Host "SMOKE OK (exit code 0, SMOKE_OK found in $logFile)"
    exit 0
}
Write-Error "SMOKE FAILED (exit code $($proc.ExitCode), SMOKE_OK found: $ok, log: $logFile)"
exit 1
