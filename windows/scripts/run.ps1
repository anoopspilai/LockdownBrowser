<#
.SYNOPSIS
    Builds (unless -NoBuild) and starts Avaibe Exam from the matching publish folder,
    optionally in dev auto-run mode.

.DESCRIPTION
    Debug builds live in windows\publish-debug\ and are the only builds that honour the
    developer switches (AVAIBE_BASE_URL, AVAIBE_AUTO_*). Release builds (windows\publish\)
    ignore every AVAIBE_* environment variable, so -AutoRun / -BaseUrl only work with
    -Configuration Debug (the default here).

.EXAMPLE
    .\scripts\run.ps1
    .\scripts\run.ps1 -BaseUrl https://exam.school.example
    .\scripts\run.ps1 -AutoRun -Token <token> -Student 1025 -Exam DEMO -ExitAfter 120
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "",
    [switch]$AutoRun,
    [string]$Token = "",
    [string]$Student = "1025",
    [string]$Exam = "DEMO",
    [int]$ExitAfter = 0,
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = if ($Configuration -eq "Release") { Join-Path $root "publish" } else { Join-Path $root "publish-debug" }
$exe = Join-Path $publishDir "AvaibeExam.exe"

if (-not $NoBuild -or -not (Test-Path $exe)) {
    & (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration | Out-Null
}
if (-not (Test-Path $exe)) { throw "Executable not found: $exe" }

if ($Configuration -eq "Release" -and ($AutoRun -or $BaseUrl)) {
    Write-Warning "Release builds ignore AVAIBE_* switches; -AutoRun / -BaseUrl have no effect. Use -Configuration Debug."
}

if ($BaseUrl) { $env:AVAIBE_BASE_URL = $BaseUrl } else { Remove-Item Env:AVAIBE_BASE_URL -ErrorAction SilentlyContinue }
if ($AutoRun) {
    $env:AVAIBE_AUTO_RUN = "1"
    if ($Token) { $env:AVAIBE_AUTO_TOKEN = $Token } else { Remove-Item Env:AVAIBE_AUTO_TOKEN -ErrorAction SilentlyContinue }
    $env:AVAIBE_AUTO_STUDENT = $Student
    $env:AVAIBE_AUTO_EXAM = $Exam
    if ($ExitAfter -gt 0) { $env:AVAIBE_AUTO_EXIT_AFTER = "$ExitAfter" } else { Remove-Item Env:AVAIBE_AUTO_EXIT_AFTER -ErrorAction SilentlyContinue }
    Write-Warning "AUTO RUN: the client will enroll/start the exam '$Exam' automatically and lock the screen. Have the teacher console ready to release it."
} else {
    Remove-Item Env:AVAIBE_AUTO_RUN -ErrorAction SilentlyContinue
}

Write-Host "Starting $exe ($Configuration)"
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
Write-Host "Logs: $env:LOCALAPPDATA\AvaibeExam\logs"
