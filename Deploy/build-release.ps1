<#
.SYNOPSIS
    Builds a release of Jade Tables on your PC: runs the tests, publishes for IIS and zips the result.

.DESCRIPTION
    Run from anywhere:
        powershell -ExecutionPolicy Bypass -File Deploy\build-release.ps1

    Writes Deploy\out\jadetables-<date>-<commit>.zip. Copy that zip to the server (Remote Desktop
    copy-paste works) and run install-release.ps1 there. With -CopyTo, the zip is also copied to a
    folder, e.g. a shared folder on the server.

.PARAMETER SkipTests
    Publish without running the tests first (not recommended).

.PARAMETER CopyTo
    Optional folder to copy the finished zip to, e.g. \\your-server\deploy.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$CopyTo
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot           # the repository root
$out = Join-Path $PSScriptRoot 'out'
$publishDir = Join-Path $root 'GameWeb\bin\publish\iis'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

Push-Location $root
try {
    # Name the release after the date and the commit, and warn about uncommitted changes.
    $commit = (git rev-parse --short HEAD 2>$null)
    if (-not $commit) { $commit = 'nogit' }
    $dirty = (git status --porcelain 2>$null)
    if ($dirty) { Write-Warning "There are uncommitted changes: this release won't match commit $commit exactly." }
    $name = "jadetables-$(Get-Date -Format 'yyyyMMdd-HHmm')-$commit"

    if (-not $SkipTests) {
        Step 'Running tests'
        # A separate build folder, so a copy of the site already running on this PC doesn't block the build.
        # Strength comparisons between computer levels are timed and statistical, so a busy PC can flip
        # one; they are left to normal test runs and don't block a release.
        dotnet test GameWeb.Tests --nologo -v q --artifacts-path (Join-Path $root '.preview-build\tests') --filter "Category!=Strength"
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed: nothing was published.' }
    }

    Step 'Publishing for IIS'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish GameWeb -p:PublishProfile=IIS --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if (-not (Test-Path (Join-Path $publishDir 'GameWeb.dll'))) { throw "Publish produced no GameWeb.dll in $publishDir." }

    # Record what this release is, for the server script and for you.
    @{
        name      = $name
        commit    = $commit
        built     = (Get-Date).ToString('s')
        uncommitted = [bool]$dirty
    } | ConvertTo-Json | Set-Content -Path (Join-Path $publishDir 'release.json') -Encoding UTF8

    Step 'Zipping'
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $zip = Join-Path $out "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal
    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)

    if ($CopyTo) {
        Step "Copying to $CopyTo"
        Copy-Item $zip -Destination $CopyTo -Force
    }

    # Keep the last 10 zips here.
    Get-ChildItem $out -Filter 'jadetables-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 10 | Remove-Item -Force

    Write-Host "`nRelease ready: $zip ($sizeMb MB)" -ForegroundColor Green
    Write-Host 'Next: copy it to the server and run Deploy\install-release.ps1 there.'
}
finally {
    Pop-Location
}
