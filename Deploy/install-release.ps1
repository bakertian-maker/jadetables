<#
.SYNOPSIS
    Installs a Jade Tables release on the server, keeping the previous version so it can be put back.

.DESCRIPTION
    Run on the server in an administrator PowerShell:
        powershell -ExecutionPolicy Bypass -File install-release.ps1 -Zip C:\Deploy\jadetables-20261111-1200-abc1234.zip

    Without -Zip it installs the newest jadetables-*.zip in the script's folder.

    What it does:
      1. Copies the current site to <BackupRoot>\site-<date> (the last 5 are kept).
      2. Puts app_offline.htm in the site folder: IIS stops the app and shows a short "back in a moment" page.
      3. Copies the new files over the site (logs and app_offline.htm are left alone).
      4. Removes app_offline.htm, so the site starts again, and checks that the home page answers.
    If the check fails, it tells you how to roll back:
        powershell -ExecutionPolicy Bypass -File install-release.ps1 -Rollback

    Note: rooms are kept in memory, so games in progress end when the site restarts. Install when it's quiet.

.PARAMETER Zip
    The release zip made by build-release.ps1.

.PARAMETER Site
    The site's folder in IIS. Default C:\Websites\JadeTables.

.PARAMETER BackupRoot
    Where copies of previous versions are kept. Default C:\Websites\_releases.

.PARAMETER Url
    Address to check after installing. Default https://www.jadetables.com/.

.PARAMETER NoCheck
    Don't check the site afterwards (e.g. when trying the script on a copy).

.PARAMETER Rollback
    Put back the most recent copy from BackupRoot instead of installing.
#>
[CmdletBinding()]
param(
    [string]$Zip,
    [string]$Site = 'C:\Websites\JadeTables',
    [string]$BackupRoot = 'C:\Websites\_releases',
    [string]$Url = 'https://www.jadetables.com/',
    [switch]$Rollback,
    [switch]$NoCheck,
    [int]$Keep = 5
)

$ErrorActionPreference = 'Stop'
function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

$offline = Join-Path $Site 'app_offline.htm'
$offlinePage = @'
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="15"><title>Jade Tables · back in a moment</title>
<style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b2e26;color:#f3ead4;font:18px/1.5 system-ui,sans-serif;text-align:center}</style>
</head><body><div><h1>Jade Tables</h1><p>We're updating the site. Back in a moment.</p><p>玉桌正在更新，請稍候。</p></div></body></html>
'@

# Copy a folder's contents over another, leaving logs and app_offline.htm alone. robocopy exit codes below 8 mean success.
function Mirror($from, $to) {
    robocopy $from $to /E /R:3 /W:2 /NFL /NDL /NJH /NJS /NP /XD logs /XF app_offline.htm | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy exit code $LASTEXITCODE)." }
}

function TakeOffline {
    Set-Content -Path $offline -Value $offlinePage -Encoding UTF8
    Start-Sleep -Seconds 3   # give IIS a moment to stop the app and release its files
}

function BringOnline {
    if (Test-Path $offline) { Remove-Item $offline -Force }
}

function CheckSite {
    if ($NoCheck -or -not $Url) { return $true }
    Step "Checking $Url"
    for ($i = 1; $i -le 6; $i++) {
        try {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 30
            if ($r.StatusCode -eq 200) { Write-Host "The site answers (HTTP 200)." -ForegroundColor Green; return $true }
        }
        catch { Write-Host "  attempt $i`: $($_.Exception.Message)" }
        Start-Sleep -Seconds 5
    }
    return $false
}

if (-not (Test-Path $Site)) { throw "Site folder not found: $Site" }
New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null

if ($Rollback) {
    $last = Get-ChildItem $BackupRoot -Directory -Filter 'site-*' | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $last) { throw "No previous version found in $BackupRoot." }
    Step "Rolling back to $($last.Name)"
    TakeOffline
    try { Mirror $last.FullName $Site } finally { BringOnline }
    if (CheckSite) { Write-Host "`nRolled back to $($last.Name)." -ForegroundColor Green }
    else { Write-Warning 'Rolled back, but the site still does not answer: check the logs (see DEPLOY.md).' }
    return
}

if (-not $Zip) {
    $Zip = Get-ChildItem $PSScriptRoot -Filter 'jadetables-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Zip) { throw "No -Zip given and no jadetables-*.zip next to this script." }
}
if (-not (Test-Path $Zip)) { throw "Release zip not found: $Zip" }
Write-Host "Installing $Zip into $Site"

Step 'Unpacking the release'
$staging = Join-Path $env:TEMP ("jadetables-" + [guid]::NewGuid().ToString('N'))
Expand-Archive -Path $Zip -DestinationPath $staging -Force
if (-not (Test-Path (Join-Path $staging 'GameWeb.dll'))) { throw 'The zip does not look like a Jade Tables release (no GameWeb.dll).' }
if (Test-Path (Join-Path $staging 'release.json')) {
    $info = Get-Content (Join-Path $staging 'release.json') -Raw | ConvertFrom-Json
    Write-Host "Release $($info.name), built $($info.built)"
}

Step 'Keeping a copy of the current version'
$backup = Join-Path $BackupRoot ("site-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Mirror $Site $backup
Get-ChildItem $BackupRoot -Directory -Filter 'site-*' | Sort-Object Name -Descending | Select-Object -Skip $Keep | Remove-Item -Recurse -Force
Write-Host "Saved to $backup"

Step 'Installing'
TakeOffline
try {
    Mirror $staging $Site
}
finally {
    BringOnline
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

if (CheckSite) {
    Write-Host "`nDone. The previous version is in $backup." -ForegroundColor Green
}
else {
    Write-Warning "The site does not answer. To put the previous version back, run:`n  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Rollback"
    exit 1
}
