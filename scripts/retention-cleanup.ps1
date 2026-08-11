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
$runtimeRoot = Join-Path $RepoPath "artifacts\runtime"
$diagnosticRoot = Join-Path $RepoPath "artifacts\diagnostics"

$items = [System.Collections.Generic.List[object]]::new()
$protected = [System.Collections.Generic.List[object]]::new()
$protectedReasons = [System.Collections.Generic.List[string]]::new()

function Add-Protected([string]$Path, [string]$Reason) {
    $protected.Add([pscustomobject]@{ path = $Path; reason = $Reason })
    if (-not $protectedReasons.Contains($Reason)) { $protectedReasons.Add($Reason) }
}

function Add-Candidates([string]$Root, [string]$Category, [TimeSpan]$Age) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue)) {
        $relative = $file.FullName.Substring($Root.TrimEnd('\').Length).TrimStart('\')
        $name = $file.Name.ToLowerInvariant()
        $pathLower = $file.FullName.ToLowerInvariant()
        $protectedReason = $null
        if ($name -in @("agent.db", "agent.db-wal", "agent.db-shm") -or $name -eq "manifest.json") { $protectedReason = "state_or_manifest" }
        elseif ($name -eq "master.flac" -or $name -eq "preview.opus") { $protectedReason = "permanent_archive_or_preview" }
        elseif ($pathLower -match "active|pending|failed|recovery|unconfirmed") { $protectedReason = "active_or_unconfirmed_state" }
        elseif ($Category -eq "transport" -and $pathLower -notmatch "media[-_]?ready|validated") { $protectedReason = "media_confirmation_missing" }
        if ($protectedReason) { Add-Protected $file.FullName $protectedReason; continue }
        if ($Age -le [TimeSpan]::Zero -or $file.LastWriteTimeUtc -gt $now.UtcDateTime.Subtract($Age)) { continue }
        $items.Add([pscustomobject]@{ path = $file.FullName; category = $Category; sizeBytes = $file.Length; lastWriteTimeUtc = $file.LastWriteTimeUtc.ToUniversalTime().ToString("o") })
    }
}

Add-Candidates (Join-Path $dataRoot "spool") "transport" ([TimeSpan]::FromHours($retention.transportGraceHours))
Add-Candidates (Join-Path $dataRoot "recordings") "transport" ([TimeSpan]::FromHours($retention.transportGraceHours))
Add-Candidates (Join-Path $dataRoot "raw") "rawRecovery" ([TimeSpan]::FromHours($retention.rawRecoveryGraceHours))
Add-Candidates (Join-Path $dataRoot "temp") "temporaryProcessing" ([TimeSpan]::FromHours($retention.temporaryProcessingHours))
Add-Candidates $runtimeRoot "logs" ([TimeSpan]::FromDays($retention.logDays))
Add-Candidates $diagnosticRoot "diagnostics" ([TimeSpan]::FromDays($retention.diagnosticDays))
if ($retention.localMasterDays -gt 0) { Add-Candidates (Join-Path $archiveRoot "Meetings") "localMaster" ([TimeSpan]::FromDays($retention.localMasterDays)) }

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
    note = "Only explicitly eligible files are candidates. Active, pending, failed, unconfirmed, state, manifest, master and preview files remain protected."
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$report | ConvertTo-Json -Depth 12
