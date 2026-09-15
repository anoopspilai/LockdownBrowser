<#
.SYNOPSIS
    Restores, builds and publishes AvaibeExam.exe (win-x64, framework-dependent).

.DESCRIPTION
    Release output goes to windows\publish\ (what the installer packages).
    Debug output goes to windows\publish-debug\ (what run.ps1 / smoke.ps1 use).
    Keeping them apart guarantees an installer can never be built from a Debug build, which
    still contains the developer switches (AVAIBE_AUTO_*, AVAIBE_SMOKE_TEST). Every publish
    writes build-info.json {configuration, version, time, commit} next to the exe;
    make-installer.ps1 refuses anything but configuration = "Release".

.EXAMPLE
    .\scripts\build.ps1                          # Release -> windows\publish\AvaibeExam.exe
    .\scripts\build.ps1 -Configuration Debug     # Debug   -> windows\publish-debug\AvaibeExam.exe
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
$publishDir = if ($Configuration -eq "Release") { Join-Path $root "publish" } else { Join-Path $root "publish-debug" }

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

Write-Host "==> dotnet publish ($Configuration, win-x64, framework-dependent, not single-file) -> $publishDir"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish $project -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=false -o $publishDir --no-restore
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $publishDir "AvaibeExam.exe"
if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }

# build-info.json: read by make-installer.ps1 to refuse non-Release payloads.
$version = "unknown"
try {
    $csproj = [xml](Get-Content $project)
    $v = $csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
    if ($v) { $version = "$v" }
} catch { }
$commit = ""
try { $commit = (git -C $root rev-parse --short HEAD 2>$null); if (-not $commit) { $commit = "" } } catch { $commit = "" }
$buildInfo = [ordered]@{
    configuration = $Configuration
    version       = $version
    time          = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    commit        = "$commit"
}
$buildInfo | ConvertTo-Json | Set-Content -Path (Join-Path $publishDir "build-info.json") -Encoding UTF8

if ($Sign) {
    if ($Configuration -ne "Release") { throw "Only Release builds may be signed" }
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
Write-Host "Published ($Configuration): $exe"
Write-Output $exe
