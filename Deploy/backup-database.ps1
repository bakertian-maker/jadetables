<#
.SYNOPSIS
    Backs up the Jade Tables database (Games) and keeps the last 14 days. Can install itself as a nightly task.

.DESCRIPTION
    One-time setup, on the server in an administrator PowerShell:
        powershell -ExecutionPolicy Bypass -File C:\Deploy\backup-database.ps1 -Install

    That gives the Windows SYSTEM account permission to back up the Games database, then creates the
    scheduled task "Jade Tables database backup", which runs this script every night at 03:30.

    Run a backup by hand at any time:
        powershell -ExecutionPolicy Bypass -File C:\Deploy\backup-database.ps1

    SQL Server writes the backup straight into -Destination (the script gives its service account
    permission there) and checks every page as it writes. Backups older than -KeepDays are deleted.
    Express edition can't compress backups, so each file is about the size of the database.

    Keep a copy somewhere other than this server too (another PC, cloud storage): a backup on the same
    disk doesn't help if the server is lost.

.PARAMETER Destination
    Folder for the backup files. Default C:\Backups\JadeTables.

.PARAMETER Server
    SQL Server instance. Default .\SQLEXPRESS.

.PARAMETER Database
    Database name. Default Games.

.PARAMETER KeepDays
    Delete backups older than this. Default 14.

.PARAMETER SqlUser
    Use a SQL login instead of Windows sign-in (the password is read from the SQLCMDPASSWORD environment
    variable). The nightly task uses Windows sign-in as SYSTEM.

.PARAMETER Install
    Set up the nightly task (see above).
#>
[CmdletBinding()]
param(
    [string]$Destination = 'C:\Backups\JadeTables',
    [string]$Server = '.\SQLEXPRESS',
    [string]$Database = 'Games',
    [int]$KeepDays = 14,
    [string]$SqlUser,
    [switch]$Install,
    [string]$At = '03:30'
)

$ErrorActionPreference = 'Stop'
$taskName = 'Jade Tables database backup'

function Find-Sqlcmd {
    $cmd = Get-Command sqlcmd.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = Get-ChildItem 'C:\Program Files\Microsoft SQL Server' -Recurse -Filter SQLCMD.EXE -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending
    if ($candidates) { return $candidates[0].FullName }
    throw 'sqlcmd.exe not found. Install the SQL Server command-line tools (sqlcmd), or add it to PATH.'
}

# Runs a query and returns its output lines; stops on any SQL error.
# The query goes to sqlcmd as a file (-i): older versions of sqlcmd misread a multi-line query passed on
# the command line ("Unknown Option").
function Invoke-Sql([string]$query, [string]$db = 'master') {
    # @(...) keeps this a list: a one-item list from an if would turn into plain text, which sqlcmd misreads.
    $auth = @(if ($SqlUser) { '-U'; $SqlUser } else { '-E' })
    $file = Join-Path $env:TEMP ("jadetables-" + [guid]::NewGuid().ToString('N') + '.sql')
    [IO.File]::WriteAllText($file, "SET NOCOUNT ON;`r`n$query`r`n", (New-Object Text.UTF8Encoding $false))
    try {
        $output = & $script:sqlcmd -S $Server -d $db @auth @script:trust -b -h -1 -W -f 65001 -i $file 2>&1
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $($output -join ' ')" }
        return $output
    }
    finally { Remove-Item $file -Force -ErrorAction SilentlyContinue }
}

$script:sqlcmd = Find-Sqlcmd
# Newer sqlcmd encrypts connections and needs -C to accept the server's own certificate; older versions
# don't encrypt by default and may not know -C at all.
$help = (& $script:sqlcmd '-?' 2>&1) -join "`n"
$script:trust = @(if ($help -match '-C\b') { '-C' })

if ($Install) {
    Write-Host "Giving the SYSTEM account permission to back up [$Database]..."
    # Runs as you (the administrator who installed SQL Server is normally a SQL sysadmin).
    $grant = @"
IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NULL CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
USE [$Database];
IF USER_ID(N'NT AUTHORITY\SYSTEM') IS NULL CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
ALTER ROLE db_backupoperator ADD MEMBER [NT AUTHORITY\SYSTEM];
"@
    Invoke-Sql $grant | Out-Null

    Write-Host "Creating the scheduled task '$taskName' (every day at $At)..."
    $scriptPath = $PSCommandPath
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Destination `"$Destination`" -Server `"$Server`" -Database `"$Database`" -KeepDays $KeepDays"
    $trigger = New-ScheduledTaskTrigger -Daily -At $At
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
        -User 'SYSTEM' -RunLevel Highest -Force | Out-Null

    Write-Host 'Running a first backup now to check everything works...'
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# SQL Server itself writes the backup file, as its service account (NT SERVICE\MSSQL$<instance>), so that
# account needs permission to write to the destination folder.
$instance = if ($Server -match '\\(.+)$') { $Matches[1] } else { 'MSSQLSERVER' }
$serviceAccount = if ($instance -eq 'MSSQLSERVER') { 'NT SERVICE\MSSQLSERVER' } else { "NT SERVICE\MSSQL`$$instance" }
$acl = (Get-Acl $Destination).Access | Where-Object { $_.IdentityReference -eq $serviceAccount }
if (-not $acl) {
    icacls $Destination /grant "${serviceAccount}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not give $serviceAccount write access to $Destination; the backup may fail." }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$target = Join-Path $Destination "$Database-$stamp.bak"

Write-Host "Backing up [$Database] to $target ..."
# CHECKSUM makes SQL Server check every page as it writes the backup and fail if anything is damaged.
# (RESTORE VERIFYONLY would need CREATE DATABASE permission, which the backup account shouldn't have.)
Invoke-Sql "BACKUP DATABASE [$Database] TO DISK = N'$target' WITH INIT, CHECKSUM, NAME = N'$Database backup $stamp';" | Out-Null
$sizeMb = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "Saved $target ($sizeMb MB)" -ForegroundColor Green

# Remove old backups.
$cutoff = (Get-Date).AddDays(-$KeepDays)
Get-ChildItem $Destination -Filter "$Database-*.bak" | Where-Object { $_.LastWriteTime -lt $cutoff } | ForEach-Object {
    Remove-Item $_.FullName -Force
    Write-Host "Deleted old backup $($_.Name)"
}

if ($Install) {
    Write-Host "`nNightly backups are set up. They go to $Destination and are kept for $KeepDays days." -ForegroundColor Green
}
