<#
.SYNOPSIS
    Builds the app and then compiles windows\installer\AvaibeExam.iss into a Setup.exe.

.DESCRIPTION
    Two steps:
      1. scripts\build.ps1  -> fills windows\publish\ with AvaibeExam.exe and its DLLs.
      2. ISCC.exe (the Inno Setup Compiler) reads windows\installer\AvaibeExam.iss,
         packs everything from windows\publish\ and writes
         windows\installer\output\AvaibeExam-Setup-0.1.0.exe

    Inno Setup is free and must be installed first:
        https://jrsoftware.org/isdl.php   (pick "Inno Setup 6" - the innosetup-6.x.x.exe file)

    The produced Setup.exe can be run by hand, or silently by school IT:
        AvaibeExam-Setup-0.1.0.exe /VERYSILENT /NORESTART

.EXAMPLE
    .\scripts\make-installer.ps1
    .\scripts\make-installer.ps1 -NoBuild
    .\scripts\make-installer.ps1 -Sign -CertThumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$Sign,
    [string]$CertThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $root "installer\AvaibeExam.iss"
$publishDir = Join-Path $root "publish"
$outputDir = Join-Path $root "installer\output"

if (-not (Test-Path $script)) { throw "Installer script not found: $script" }

# ---- 1. Build / publish the app -------------------------------------------------------
if (-not $NoBuild) {
    Write-Host "==> building the app first (scripts\build.ps1)"
    $buildArgs = @{ Configuration = $Configuration }
    if ($Sign) {
        if (-not $CertThumbprint) { throw "-Sign requires -CertThumbprint" }
        $buildArgs["Sign"] = $true
        $buildArgs["CertThumbprint"] = $CertThumbprint
        $buildArgs["TimestampUrl"] = $TimestampUrl
    }
    & (Join-Path $PSScriptRoot "build.ps1") @buildArgs | Out-Null
}

$payload = Join-Path $publishDir "AvaibeExam.exe"
if (-not (Test-Path $payload)) {
    throw "Nothing to package: $payload does not exist. Run .\scripts\build.ps1 first (or drop -NoBuild)."
}

# ---- 2. Locate the Inno Setup compiler ------------------------------------------------
$iscc = $null

# a) already on PATH?
$onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($onPath) { $iscc = $onPath.Source }

# b) the two default install locations of Inno Setup 6.
#    Built as plain strings (not Join-Path) so a missing environment variable cannot throw.
if (-not $iscc) {
    $candidates = @()
    if (${env:ProgramFiles(x86)}) { $candidates += "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
    if ($env:ProgramFiles)        { $candidates += "$env:ProgramFiles\Inno Setup 6\ISCC.exe" }
    foreach ($c in $candidates) {
        if (Test-Path $c) { $iscc = $c; break }
    }
}

if (-not $iscc) {
    throw @"
Inno Setup compiler (ISCC.exe) not found.

Install Inno Setup 6 (free) from:
    https://jrsoftware.org/isdl.php

Looked in:
    PATH
    ${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe
    $env:ProgramFiles\Inno Setup 6\ISCC.exe

If you installed it somewhere else, add that folder to PATH and run this script again.
"@
}
Write-Host "Using Inno Setup compiler: $iscc"

# ---- 3. Compile the installer ---------------------------------------------------------
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }

Write-Host "==> ISCC $script"
& $iscc $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit code $LASTEXITCODE)" }

$setup = Get-ChildItem $outputDir -Filter "AvaibeExam-Setup-*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw "ISCC reported success but no AvaibeExam-Setup-*.exe was found in $outputDir" }

# ---- 4. Optionally sign the Setup.exe itself ------------------------------------------
if ($Sign) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending
        foreach ($k in $kits) {
            $candidate = Join-Path $k.FullName "x64\signtool.exe"
            if (Test-Path $candidate) { $signtool = Get-Command $candidate; break }
        }
    }
    if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)" }
    Write-Host "==> signing $($setup.FullName)"
    & $signtool.Source sign /sha1 $CertThumbprint /fd SHA256 /td SHA256 /tr $TimestampUrl $setup.FullName
    if ($LASTEXITCODE -ne 0) { throw "signing the installer failed" }
    $setup = Get-Item $setup.FullName
}

$sizeMb = [math]::Round($setup.Length / 1MB, 1)
$hash = (Get-FileHash -Path $setup.FullName -Algorithm SHA256).Hash

Write-Host ""
Write-Host "Installer: $($setup.FullName)"
Write-Host "Size     : $sizeMb MB"
Write-Host "SHA-256  : $hash"
Write-Host ""
Write-Host "Silent install (for school IT / Intune):"
Write-Host "    `"$($setup.Name)`" /VERYSILENT /NORESTART"
Write-Output $setup.FullName
