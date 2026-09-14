<#
.SYNOPSIS
    Restores, builds and publishes AvaibeExam.exe (win-x64, framework-dependent).

.EXAMPLE
    .\scripts\build.ps1                 # Release publish -> windows\publish\AvaibeExam.exe
    .\scripts\build.ps1 -Configuration Debug
    .\scripts\build.ps1 -Sign -CertThumbprint 0123ABCD... -TimestampUrl http://timestamp.digicert.com
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Sign,
    [string]$CertThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "AvaibeExam\AvaibeExam.csproj"
$publishDir = Join-Path $root "publish"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet SDK not found. Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}
$sdk = (dotnet --version)
Write-Host "Using .NET SDK $sdk"
if (-not $sdk.StartsWith("8.")) {
    Write-Warning "This project targets net8.0-windows; SDK $sdk may still work if it is newer."
}

Write-Host "==> dotnet restore"
dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "restore failed" }

Write-Host "==> dotnet build ($Configuration)"
dotnet build $project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "==> dotnet publish (win-x64, framework-dependent, not single-file)"
dotnet publish $project -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=false -o $publishDir --no-restore
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $publishDir "AvaibeExam.exe"
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
    Get-ChildItem $publishDir -Filter "AvaibeExam*.dll" | ForEach-Object {
        & $signtool.Source sign /sha1 $CertThumbprint /fd SHA256 /td SHA256 /tr $TimestampUrl $_.FullName | Out-Null
    }
}

Write-Host ""
Write-Host "Published: $exe"
Write-Output $exe
