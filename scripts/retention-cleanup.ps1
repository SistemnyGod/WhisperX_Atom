[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$Apply,
    [string]$RepoPath,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoPath)) { $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $RepoPath "artifacts\release\retention-report.json" }
if (-not $Apply) { $DryRun = $true }
if ($Apply -and $DryRun) { throw "RETENTION_MODE_CONFLICT: use either -DryRun or -Apply" }

$envFile = Join-Path $RepoPath ".env"
if (Test-Path -LiteralPath $envFile -PathType Leaf) {
    . (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
    Import-WhisperXDotEnv -RepoPath $RepoPath
}

function Get-EnvNumber([string]$Name, [double]$Fallback) {
    $value = 0d
    if ([double]::TryParse([Environment]::GetEnvironmentVariable($Name), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$value) -and $value -ge 0) { return $value }
    return $Fallback
}

$now = [DateTimeOffset]::UtcNow
$retention = [ordered]@{
    transportGraceHours = Get-EnvNumber "WHISPERX_RETENTION_TRANSPORT_GRACE_HOURS" 24
    rawRecoveryGraceHours = Get-EnvNumber "WHISPERX_RETENTION_RAW_GRACE_HOURS" 24
    temporaryProcessingHours = Get-EnvNumber "WHISPERX_RETENTION_TEMP_HOURS" 72
    logDays = Get-EnvNumber "WHISPERX_RETENTION_LOG_DAYS" 30
    diagnosticDays = Get-EnvNumber "WHISPERX_RETENTION_DIAGNOSTIC_DAYS" 14
    localMasterDays = Get-EnvNumber "WHISPERX_RETENTION_LOCAL_MASTER_DAYS" 0
    serverArchiveDays = Get-EnvNumber "WHISPERX_RETENTION_SERVER_ARCHIVE_DAYS" 0
}

$dataRoot = if ($env:ATOM_AGENT_DATA_ROOT) { $env:ATOM_AGENT_DATA_ROOT } elseif ($env:WHISPERX_DATA_HOST) { $env:WHISPERX_DATA_HOST } else { Join-Path $env:ProgramData "WhisperXAtom\Agent" }
$archiveRoot = if ($env:ATOM_AGENT_ARCHIVE_ROOT) { $env:ATOM_AGENT_ARCHIVE_ROOT } elseif ($env:WHISPERX_ARCHIVE_HOST) { $env:WHISPERX_ARCHIVE_HOST } else { Join-Path $env:PUBLIC "Documents\WhisperX Atom" }
$items = [System.Collections.Generic.List[object]]::new()
$protected = [System.Collections.Generic.List[object]]::new()
$protectedReasons = [System.Collections.Generic.List[string]]::new()

function Add-Protected([string]$Path, [string]$Reason) {
    $protected.Add([pscustomobject]@{ path = $Path; reason = $Reason })
    if (-not $protectedReasons.Contains($Reason)) { $protectedReasons.Add($Reason) }
}

function Add-StateCandidate([string]$Path, [string]$Category, [string]$SessionId, [string]$PurgeAfter) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $file = Get-Item -LiteralPath $Path
    $items.Add([pscustomobject]@{ path = $file.FullName; category = $Category; sessionId = $SessionId; sizeBytes = $file.Length; purgeAfterUtc = $PurgeAfter })
}

# The database is authoritative. This script deliberately does not infer state
# from folder/file names; without a readable SQLite state store it fails closed.
$dbPath = Join-Path $dataRoot "agent.db"
$sqlite = Get-Command sqlite3 -ErrorAction SilentlyContinue
if (-not $sqlite -or -not (Test-Path -LiteralPath $dbPath -PathType Leaf)) {
    Add-Protected $dbPath "state_store_or_sqlite3_unavailable"
} else {
    $nowSql = $now.ToString("o")
    $transportSql = "SELECT s.id || char(9) || s.transport_purge_after || char(9) || c.local_path FROM recording_sessions s JOIN recording_chunks c ON c.session_id=s.id AND c.status='CONFIRMED' WHERE s.media_validated_at IS NOT NULL AND s.transport_purge_after IS NOT NULL AND s.transport_purge_after <= '$nowSql' AND s.delivery_state IN ('CONFIRMED','COMPLETED') AND NOT EXISTS (SELECT 1 FROM recording_chunks p WHERE p.session_id=s.id AND p.status<>'CONFIRMED') AND NOT EXISTS (SELECT 1 FROM recording_raw_chunks r WHERE r.session_id=s.id AND r.status<>'READY');"
    foreach ($line in @(& $sqlite.Source -noheader $dbPath $transportSql)) {
        $parts = $line -split "`t", 3
        if ($parts.Count -eq 3) { Add-StateCandidate $parts[2] "transport" $parts[0] $parts[1] }
    }
    $rawSql = "SELECT r.session_id || char(9) || r.raw_purge_after || char(9) || r.raw_path FROM recording_raw_chunks r WHERE r.status='READY' AND (r.error IS NULL OR r.error='') AND r.raw_purge_after IS NOT NULL AND r.raw_purge_after <= '$nowSql' AND EXISTS (SELECT 1 FROM recording_chunks c WHERE c.track_id=r.track_id AND c.sequence=r.sequence AND c.status IN ('READY','UPLOADING','CONFIRMED'));"
    foreach ($line in @(& $sqlite.Source -noheader $dbPath $rawSql)) {
        $parts = $line -split "`t", 3
        if ($parts.Count -eq 3) { Add-StateCandidate $parts[2] "rawRecovery" $parts[0] $parts[1] }
    }
    # LOCAL_MASTER_DAYS=0 is represented by NULL local_archive_purge_after and is never selected.
    $archiveSql = "SELECT id || char(9) || local_archive_purge_after || char(9) || archive_path FROM recording_sessions WHERE media_validated_at IS NOT NULL AND delivery_state IN ('CONFIRMED','COMPLETED') AND local_finalize_state='LOCAL_READY' AND local_archive_purge_after IS NOT NULL AND local_archive_purge_after <= '$nowSql' AND local_archive_purged_at IS NULL;"
    foreach ($line in @(& $sqlite.Source -noheader $dbPath $archiveSql)) {
        $parts = $line -split "`t", 3
        if ($parts.Count -eq 3) {
            Add-StateCandidate (Join-Path $parts[2] "export\master.flac") "localMaster" $parts[0] $parts[1]
            Add-StateCandidate (Join-Path $parts[2] "export\preview.opus") "localMaster" $parts[0] $parts[1]
        }
    }
}

$deleted = 0
$freed = 0L
$wouldFree = 0L
foreach ($item in $items) { $wouldFree += [int64]$item.sizeBytes }
if ($Apply) {
    foreach ($item in $items) {
        try {
            Remove-Item -LiteralPath $item.path -Force -ErrorAction Stop
            $deleted++
            $freed += [int64]$item.sizeBytes
        }
            catch { Add-Protected $item.path "delete_failed" }
    }
    if ($sqlite -and (Test-Path -LiteralPath $dbPath -PathType Leaf)) {
        foreach ($group in @($items | Group-Object sessionId, category)) {
            if (@($group.Group | Where-Object { Test-Path -LiteralPath $_.path -PathType Leaf }).Count -ne 0) { continue }
            $sample = $group.Group[0]
            $session = $sample.sessionId.Replace("'", "''")
            if ($sample.category -eq "transport") {
                # State predicates are repeated in the mutation so an intervening
                # delivery error cannot turn a dry-run into a destructive action.
                $sql = "DELETE FROM recording_raw_chunks WHERE session_id='$session' AND status='READY' AND (error IS NULL OR error=''); DELETE FROM recording_chunks WHERE session_id='$session' AND status='CONFIRMED' AND NOT EXISTS (SELECT 1 FROM recording_chunks p WHERE p.session_id='$session' AND p.status<>'CONFIRMED'); UPDATE recording_sessions SET state='FINALIZED' WHERE id='$session' AND media_validated_at IS NOT NULL AND transport_purge_after <= '$nowSql' AND delivery_state IN ('CONFIRMED','COMPLETED') AND NOT EXISTS (SELECT 1 FROM recording_chunks WHERE session_id='$session');"
            } elseif ($sample.category -eq "rawRecovery") {
                $sql = "DELETE FROM recording_raw_chunks WHERE session_id='$session' AND status='READY' AND (error IS NULL OR error='') AND raw_purge_after <= '$nowSql' AND EXISTS (SELECT 1 FROM recording_chunks c WHERE c.track_id=recording_raw_chunks.track_id AND c.sequence=recording_raw_chunks.sequence AND c.status IN ('READY','UPLOADING','CONFIRMED'));"
            } else {
                $sql = "UPDATE recording_sessions SET local_archive_purged_at='$nowSql' WHERE id='$session' AND local_archive_purged_at IS NULL AND media_validated_at IS NOT NULL AND delivery_state IN ('CONFIRMED','COMPLETED') AND local_finalize_state='LOCAL_READY' AND local_archive_purge_after <= '$nowSql';"
            }
            & $sqlite.Source $dbPath $sql | Out-Null
        }
    }
}

$report = [ordered]@{
    generatedAtUtc = $now.ToString("o")
    mode = if ($DryRun) { "DRY_RUN" } else { "APPLY" }
    retention = $retention
    wouldDeleteFiles = @($items).Count
    wouldFreeBytes = $wouldFree
    deletedFiles = $deleted
    freedBytes = $freed
    protectedFiles = @($protected).Count
    protectedReasons = @($protectedReasons)
    protected = @($protected)
    candidates = @($items)
    note = "Candidates come only from SQLite session/chunk state. Pending, failed, delivery-error, unconfirmed and grace-protected audio is never selected; manifest and transcript metadata are not deleted. Apply of transport is normally performed by RecorderWorker so SQLite rows stay consistent."
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$report | ConvertTo-Json -Depth 12
