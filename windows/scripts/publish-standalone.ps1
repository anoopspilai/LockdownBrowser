<#
.SYNOPSIS
    Publishes ONE self-contained, single-file AvaibeExam.exe (win-x64).

.DESCRIPTION
    "Self-contained" means the .NET 8 runtime is bundled INSIDE the .exe, so the target PC does
    NOT need .NET installed. "Single file" means the dozens of DLLs are packed into that one .exe
    and unpacked into a temp folder at start-up. The result is one big file (roughly 150-180 MB
    with compression on) that can be copied to another Windows PC and double-clicked.

    STILL REQUIRED ON THE TARGET PC: the Microsoft Edge WebView2 Evergreen Runtime.
    It is part of Windows 11 and is present on almost every Windows 10 machine that has Edge.
    It is a separate Microsoft component and can NEVER be bundled into this .exe. If it is
    missing, install it from https://developer.microsoft.com/microsoft-edge/webview2/ .

    Compare with scripts\build.ps1, which produces the normal framework-dependent output in
    windows\publish (small, many files, needs the .NET 8 Desktop Runtime on the target PC).
    That folder is what the Inno Setup installer packages; this script is for "copy one file".

.EXAMPLE
    .\scripts\publish-standalone.ps1
    .\scripts\publish-standalone.ps1 -Configuration Debug
    .\scripts\publish-standalone.ps1 -OutputDir C:\temp\avaibe-portable
    .\scripts\publish-standalone.ps1 -Sign -CertThumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$Sign,
    [string]$CertThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "AvaibeExam\AvaibeExam.csproj"
if (-not $OutputDir) { $OutputDir = Join-Path $root "publish-standalone" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet SDK not found. Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}
$sdk = (dotnet --version)
Write-Host "Using .NET SDK $sdk"
if (-not $sdk.StartsWith("8.")) {
    Write-Warning "This project targets net8.0-windows; SDK $sdk may still work if it is newer."
}

# A self-contained publish needs the win-x64 runtime packs. They are resolved during restore,
# so this script lets `dotnet publish` run its own restore (no --no-restore here) instead of
# reusing the framework-dependent restore that build.ps1 performs.
Write-Host "==> dotnet publish (win-x64, self-contained, single file, compressed)"
dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $OutputDir "AvaibeExam.exe"
if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }

if ($Sign) {
    if (-not $CertThumbprint) { throw "-Sign requires -CertThumbprint" }
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
    Write-Host "==> signing $exe"
    & $signtool.Source sign /sha1 $CertThumbprint /fd SHA256 /td SHA256 /tr $TimestampUrl $exe
    if ($LASTEXITCODE -ne 0) { throw "signing failed" }
}

$item = Get-Item $exe
$sizeMb = [math]::Round($item.Length / 1MB, 1)
$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash

Write-Host ""
Write-Host "Published (self-contained, single file): $exe"
Write-Host "Size    : $sizeMb MB"
Write-Host "SHA-256 : $hash"
Write-Host ""
Write-Host "Copy this one file to any Windows 10 (build 19041+) / Windows 11 x64 PC and run it."
Write-Host "The PC does NOT need .NET installed, but it DOES need the WebView2 runtime"
Write-Host "(inbox on Windows 11; otherwise https://developer.microsoft.com/microsoft-edge/webview2/)."
Write-Output $exe
