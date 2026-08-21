[CmdletBinding()]
param(
    [switch]$Latest,
    [string]$SessionId,
    [string]$DataRoot = "",
    [string]$OutputRoot = "",
    [PSCredential]$Credential
)

$ErrorActionPreference = "Stop"
$isWindowsPowerShell = $PSVersionTable.PSEdition -eq "Desktop"
if ($isWindowsPowerShell) {
    # The Recorder runtime targets .NET 10 and cannot be loaded into the
    # .NET Framework host used by Windows PowerShell 5.1. Re-enter through
    # PowerShell 7 when available instead of exposing an assembly/type error.
    if ($Credential) { throw "SQLITE_RUNTIME_HOST_REQUIRED: run this command with pwsh when -Credential is used." }
    $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($null -eq $pwsh) { throw "SQLITE_RUNTIME_HOST_REQUIRED: install PowerShell 7 or run the diagnostic on the Recorder host." }
    $forward = @()
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        $forward += "-$($entry.Key)"
        if ($entry.Value -isnot [switch]) { $forward += [string]$entry.Value }
    }
    & $pwsh.Source -NoProfile -File $PSCommandPath @forward
    exit $LASTEXITCODE
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $env:ProgramData "WhisperXAtom\Agent" }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repo "artifacts\diagnostics" }
$sourceDbPath = Join-Path $DataRoot "agent.db"
if (-not (Test-Path -LiteralPath $sourceDbPath -PathType Leaf)) { throw "AGENT_DB_NOT_FOUND: $sourceDbPath" }

$runtime = Join-Path $repo "apps\recorder-agent\bin\service\net10.0-windows\win-x64"
$sqliteAssembly = Join-Path $runtime "Microsoft.Data.Sqlite.dll"
if (-not (Test-Path -LiteralPath $sqliteAssembly)) { throw "SQLITE_RUNTIME_MISSING: run the Recorder build first." }
$oldPath = $env:PATH
try {
    # Microsoft.Data.Sqlite resolves SQLitePCLRaw dependencies while its
    # assembly is loaded. Put the self-contained runtime directory first and
    # load native/provider dependencies before the managed Sqlite assembly.
    $env:PATH = "$runtime;$env:PATH"
    foreach ($dependency in @("SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "SQLitePCLRaw.batteries_v2.dll")) {
        $path = Join-Path $runtime $dependency
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "SQLITE_RUNTIME_DEPENDENCY_MISSING: $dependency" }
        [void][Reflection.Assembly]::LoadFrom($path)
    }
    [void][Reflection.Assembly]::LoadFrom($sqliteAssembly)
    [SQLitePCL.Batteries_V2]::Init()
}
catch {
    throw "SQLITE_RUNTIME_LOAD_FAILED: $($_.Exception.Message)"
}
finally {
    $env:PATH = $oldPath
}

# The live Recorder process can keep the SQLite WAL/VFS locked while the
# diagnostic is running. Query an isolated copy so diagnostics never compete
# with capture or delivery. Old copies are cleaned on the next invocation.
$diagnosticTempRoot = Join-Path $env:TEMP "WhisperXAtom\Diagnostics"
New-Item -ItemType Directory -Force -Path $diagnosticTempRoot | Out-Null
Get-ChildItem -LiteralPath $diagnosticTempRoot -Filter "agent-*.db" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-24) } |
    Remove-Item -Force -ErrorAction SilentlyContinue
$dbPath = Join-Path $diagnosticTempRoot ("agent-" + [Guid]::NewGuid().ToString("N") + ".db")
try {
    Copy-Item -LiteralPath $sourceDbPath -Destination $dbPath -Force
    foreach ($sidecar in @("-wal", "-shm")) {
        $sourceSidecar = "$sourceDbPath$sidecar"
        if (Test-Path -LiteralPath $sourceSidecar -PathType Leaf) {
            Copy-Item -LiteralPath $sourceSidecar -Destination "$dbPath$sidecar" -Force
        }
    }
}
catch { throw "SQLITE_DB_COPY_FAILED" }

function Invoke-Sql([string]$sql, [hashtable]$parameters = @{}) {
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbPath;Mode=ReadOnly;Cache=Shared")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        foreach ($key in $parameters.Keys) { $null = $command.Parameters.AddWithValue($key, $parameters[$key]) }
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($i = 0; $i -lt $reader.FieldCount; $i++) { $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) } }
                [pscustomobject]$row
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

$session = if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
    Invoke-Sql "SELECT id,meeting_id,title,owner_user_id,state,started_at,finished_at,local_finalize_state,delivery_state,archive_path,last_error_code,last_error_detail,last_error_http_status,last_error_retryable,retry_count,next_retry_at,media_asset_id,processing_job_id,trace_id,pipeline_correlation_id FROM recording_sessions WHERE id=@id" @{ "@id" = $SessionId } | Select-Object -First 1
} else {
    Invoke-Sql "SELECT id,meeting_id,title,owner_user_id,state,started_at,finished_at,local_finalize_state,delivery_state,archive_path,last_error_code,last_error_detail,last_error_http_status,last_error_retryable,retry_count,next_retry_at,media_asset_id,processing_job_id,trace_id,pipeline_correlation_id FROM recording_sessions ORDER BY started_at DESC LIMIT 1" | Select-Object -First 1
}
if ($null -eq $session) { throw "RECORDING_SESSION_NOT_FOUND" }

$tracks = @(Invoke-Sql "SELECT track_id,track_type,sample_rate,channels,endpoint_id,device_name,selection_mode,profile,encoding,bits_per_sample,source_encoding,source_sub_format,valid_bits_per_sample FROM recording_track_info WHERE session_id=@session ORDER BY track_id" @{ "@session" = $session.id })
$chunks = @(Invoke-Sql "SELECT track_id,sequence,start_sample,sample_count,status,size_bytes,sha256,last_error_code FROM recording_chunks WHERE session_id=@session ORDER BY track_id,sequence" @{ "@session" = $session.id })
$raw = @(Invoke-Sql "SELECT track_id,sequence,start_sample,sample_count,status,raw_size_bytes,error,source_encoding,source_sub_format,valid_bits_per_sample FROM recording_raw_chunks WHERE session_id=@session ORDER BY track_id,sequence" @{ "@session" = $session.id })
$configPath = Join-Path $DataRoot "agent-config.json"
$machineConfigPath = Join-Path $env:ProgramData "WhisperXAtom\client-config.json"
$agentConfig = if (Test-Path -LiteralPath $configPath) { Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json } else { $null }
$machineConfig = if (Test-Path -LiteralPath $machineConfigPath) { Get-Content -LiteralPath $machineConfigPath -Raw | ConvertFrom-Json } else { $null }
$origin = if ($machineConfig -and $machineConfig.managed -eq $true) { $machineConfig.serverOrigin } elseif ($agentConfig) { $agentConfig.serverUrl } elseif ($machineConfig) { $machineConfig.serverOrigin } else { $env:WHISPERX_API_URL }

$server = [ordered]@{ origin = $origin; healthLive = $null; healthReady = $null; version = $null; authenticatedDiagnostics = "SKIPPED" }
if (-not [string]::IsNullOrWhiteSpace($origin)) {
    foreach ($check in @("health/live", "health/ready")) {
        try { $server[$check.Replace('/', '_')] = (Invoke-RestMethod -Uri ($origin.TrimEnd('/') + "/" + $check) -TimeoutSec 5) } catch { $server[$check.Replace('/', '_')] = @{ error = "unavailable" } }
    }
    try { $server.version = Invoke-RestMethod -Uri ($origin.TrimEnd('/') + "/api/system/version") -TimeoutSec 5 } catch { $server.version = @{ error = "unavailable" } }
    if ($Credential) {
        try {
            $web = New-Object Microsoft.PowerShell.Commands.WebRequestSession
            $body = @{ username = $Credential.UserName; password = $Credential.GetNetworkCredential().Password } | ConvertTo-Json
            Invoke-RestMethod -Method Post -Uri ($origin.TrimEnd('/') + "/api/auth/login") -Body $body -ContentType "application/json" -WebSession $web | Out-Null
            $server.authenticatedDiagnostics = Invoke-RestMethod -Uri ($origin.TrimEnd('/') + "/api/admin/meetings/$($session.meeting_id)/diagnostics") -WebSession $web -TimeoutSec 10
        } catch { $server.authenticatedDiagnostics = "UNAVAILABLE" }
    }
}

$safeAgentConfig = if ($agentConfig) { [ordered]@{ serverUrl = $agentConfig.serverUrl; agentId = $agentConfig.agentId; installationId = $agentConfig.installationId; encrypted = $agentConfig.encrypted; recordingProfile = $agentConfig.recordingProfile } } else { $null }
$report = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow
    session = $session
    agent = [ordered]@{ dataRoot = $DataRoot; config = $safeAgentConfig; machineConfig = if ($machineConfig) { @{ serverOrigin = $machineConfig.serverOrigin; managed = $machineConfig.managed; schemaVersion = $machineConfig.schemaVersion } } else { $null } }
    tracks = $tracks
    chunks = $chunks
    rawChunks = $raw
    server = $server
    exclusions = @("plaintext tokens", "cookies", "audio bytes", "transcript content", "summary content", "passwords")
}
$safeId = ($session.id -replace '[^A-Za-z0-9_-]', '')
$destination = Join-Path $OutputRoot ("recording-" + $safeId)
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $destination "report.json") -Encoding utf8
$report | ConvertTo-Json -Depth 8
Remove-Item -LiteralPath $dbPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "$dbPath-wal" -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "$dbPath-shm" -Force -ErrorAction SilentlyContinue
