<#
.SYNOPSIS
    Builds (unless -NoBuild) and starts Avaibe Exam, optionally in dev auto-run mode.

.EXAMPLE
    .\scripts\run.ps1
    .\scripts\run.ps1 -BaseUrl http://192.168.1.20:4000
    .\scripts\run.ps1 -AutoRun -Token SCHOOL-DEMO -Student 1025 -Exam DEMO -ExitAfter 120
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "",
    [switch]$AutoRun,
    [string]$Token = "SCHOOL-DEMO",
    [string]$Student = "1025",
    [string]$Exam = "DEMO",
    [int]$ExitAfter = 0,
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "publish\AvaibeExam.exe"

if (-not $NoBuild -or -not (Test-Path $exe)) {
    & (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration | Out-Null
}
if (-not (Test-Path $exe)) { throw "Executable not found: $exe" }

$env:AVAIBE_BASE_URL = $BaseUrl
if ($AutoRun) {
    $env:AVAIBE_AUTO_RUN = "1"
    $env:AVAIBE_AUTO_TOKEN = $Token
    $env:AVAIBE_AUTO_STUDENT = $Student
    $env:AVAIBE_AUTO_EXAM = $Exam
    if ($ExitAfter -gt 0) { $env:AVAIBE_AUTO_EXIT_AFTER = "$ExitAfter" } else { Remove-Item Env:AVAIBE_AUTO_EXIT_AFTER -ErrorAction SilentlyContinue }
    Write-Warning "AUTO RUN: the client will enroll/start the exam '$Exam' automatically and lock the screen. Have the admin console (http://localhost:4000/admin) ready to release it."
} else {
    Remove-Item Env:AVAIBE_AUTO_RUN -ErrorAction SilentlyContinue
}

Write-Host "Starting $exe"
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
Write-Host "Logs: $env:LOCALAPPDATA\AvaibeExam\logs"
