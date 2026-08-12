<#
.SYNOPSIS
    Backs up the SolarApp database, verifies the file, and prunes old copies.

.DESCRIPTION
    SQL Server Express has no SQL Agent, so nothing schedules a backup for you.
    Until this script is running on a schedule, the only copy of every
    customer's production history, every cleaning record and every photo
    reference is on one disk in one machine — and the app now soft-deletes
    records specifically so that history survives operator mistakes. It does
    not survive a dead drive.

    What it does:
      1. FULL backup with CHECKSUM (so a corrupt page is caught at write time,
         not on the day you try to restore).
      2. RESTORE VERIFYONLY — a backup that has never been read is a guess.
      3. Optional copy to a second location. A backup on the same disk as the
         database protects you from exactly one failure mode: someone typing
         DELETE. It does nothing about the drive itself.
      4. Deletes copies older than the retention window.

.PARAMETER ServerInstance
    e.g. localhost\SQLEXPRESS

.PARAMETER Database
    Database name. Default: SolarAppDb

.PARAMETER BackupPath
    Local folder for backup files. Created if missing.

.PARAMETER OffsitePath
    Second location — a mapped drive, a UNC share, a synced folder. Skipped if
    omitted, and the script says so rather than pretending it's covered.

.PARAMETER RetentionDays
    Delete backups older than this. Default 30.

.EXAMPLE
    .\backup-database.ps1 -ServerInstance "localhost\SQLEXPRESS" `
                          -BackupPath "D:\Backups\SolarApp" `
                          -OffsitePath "\\nas\backups\solarapp"

.NOTES
    Schedule with Task Scheduler, daily, as an account that can write to both
    paths AND has SQL rights to BACKUP DATABASE:

      Program:   powershell.exe
      Arguments: -NoProfile -ExecutionPolicy Bypass -File "C:\ops\backup-database.ps1"
                 -ServerInstance "localhost\SQLEXPRESS" -BackupPath "D:\Backups\SolarApp"
      Run whether user is logged on or not: yes

    Then do the thing everyone skips: restore one of these to a scratch
    database and open the app against it. An untested backup is a belief, not
    a backup.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServerInstance,
    [string]$Database = "SolarAppDb",
    [Parameter(Mandatory = $true)][string]$BackupPath,
    [string]$OffsitePath,
    [int]$RetentionDays = 30
)

$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$file = Join-Path $BackupPath "$Database`_$stamp.bak"
$logFile = Join-Path $BackupPath "backup-log.txt"

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Write-Host $line
    # Best effort: a failure to log must never be the reason a backup aborts.
    try { Add-Content -Path $logFile -Value $line -ErrorAction Stop } catch { }
}

try {
    if (-not (Test-Path $BackupPath)) {
        New-Item -ItemType Directory -Path $BackupPath -Force | Out-Null
        Write-Log "Created backup folder $BackupPath"
    }

    # Invoke-Sqlcmd ships with the SqlServer module. sqlcmd.exe is the fallback
    # because a bare SQL Express box often has neither module installed.
    $useModule = $null -ne (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)
    if (-not $useModule -and -not (Get-Command sqlcmd.exe -ErrorAction SilentlyContinue)) {
        throw "Neither Invoke-Sqlcmd nor sqlcmd.exe is available. Install the SqlServer PowerShell module or SQL Server command-line tools."
    }

    function Invoke-Sql {
        param([string]$Query, [int]$TimeoutSeconds = 3600)
        if ($useModule) {
            Invoke-Sqlcmd -ServerInstance $ServerInstance -Query $Query -QueryTimeout $TimeoutSeconds
        } else {
            $out = & sqlcmd.exe -S $ServerInstance -E -b -t $TimeoutSeconds -Q $Query 2>&1
            if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $out" }
            $out
        }
    }

    Write-Log "Starting FULL backup of [$Database] on $ServerInstance"

    # CHECKSUM: verify pages as they're written. COMPRESSION is Standard+ only,
    # so it's deliberately not used — Express would reject the whole statement.
    Invoke-Sql -Query @"
BACKUP DATABASE [$Database]
TO DISK = N'$file'
WITH INIT, CHECKSUM, STATS = 10,
     NAME = N'$Database full backup $stamp';
"@

    if (-not (Test-Path $file)) { throw "Backup reported success but $file does not exist" }

    $sizeMb = [math]::Round((Get-Item $file).Length / 1MB, 1)
    Write-Log "Backup written: $file ($sizeMb MB)"

    # Read it back. A backup nobody has ever read is a guess about the future.
    Write-Log "Verifying backup"
    Invoke-Sql -Query "RESTORE VERIFYONLY FROM DISK = N'$file' WITH CHECKSUM;"
    Write-Log "Verify OK"

    if ($OffsitePath) {
        if (-not (Test-Path $OffsitePath)) {
            New-Item -ItemType Directory -Path $OffsitePath -Force | Out-Null
        }
        Copy-Item -Path $file -Destination $OffsitePath -Force
        Write-Log "Copied to offsite location $OffsitePath"
    } else {
        Write-Log "No -OffsitePath given. This backup lives on the same machine as the database and will not survive a drive or machine failure." "WARN"
    }

    # Prune only files this script's naming scheme produced, so an unrelated
    # .bak someone parked in the folder doesn't get deleted.
    $cutoff = (Get-Date).AddDays(-$RetentionDays)
    $stale = Get-ChildItem -Path $BackupPath -Filter "$Database`_*.bak" |
             Where-Object { $_.LastWriteTime -lt $cutoff }

    foreach ($old in $stale) {
        Remove-Item $old.FullName -Force
        Write-Log "Pruned $($old.Name) (older than $RetentionDays days)"
    }

    if ($OffsitePath -and (Test-Path $OffsitePath)) {
        Get-ChildItem -Path $OffsitePath -Filter "$Database`_*.bak" |
            Where-Object { $_.LastWriteTime -lt $cutoff } |
            ForEach-Object {
                Remove-Item $_.FullName -Force
                Write-Log "Pruned offsite $($_.Name)"
            }
    }

    Write-Log "Backup completed successfully"
    exit 0
}
catch {
    # Non-zero exit so Task Scheduler records a failure — a backup job that
    # fails quietly is worse than no backup job, because it buys confidence
    # without providing cover.
    Write-Log "BACKUP FAILED: $($_.Exception.Message)" "ERROR"
    exit 1
}
