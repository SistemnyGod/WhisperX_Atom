[CmdletBinding()]
param(
    [string]$ArtifactsRoot = (Join-Path $PSScriptRoot "..\artifacts\desktop"),
    [string]$InstalledRoot = "C:\Program Files\WhisperX Atom",
    [string]$ServerManifestPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\acceptance\clean-runtime\runtime-identity.json"),
    [switch]$RequireInstalled,
    [switch]$CheckRunningProcesses
)

$ErrorActionPreference = "Stop"
$artifacts = [IO.Path]::GetFullPath($ArtifactsRoot)
$installed = [IO.Path]::GetFullPath($InstalledRoot)
$manifestPath = Join-Path $artifacts "build-identity.json"

function Fail([string]$Code, [string]$Detail = "") {
    if ([string]::IsNullOrWhiteSpace($Detail)) { throw $Code }
    throw "${Code}: $Detail"
}

function Get-ProductIdentity([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Fail "RUNTIME_COMPONENT_MISSING" $Path }
    $identity = [string](Get-Item -LiteralPath $Path).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($identity)) { Fail "RUNTIME_COMPONENT_IDENTITY_MISSING" $Path }
    return $identity.Trim()
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { Fail "RUNTIME_MANIFEST_MISSING" $manifestPath }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
$expected = [string]$manifest.buildIdentity
$commit = [string]$manifest.commit
if ([string]::IsNullOrWhiteSpace($expected) -or $expected -match '(?i)(dev|dirty)' -or $expected -notmatch '^1\.0\.1\+[0-9a-fA-F]{40}$') {
    Fail "RUNTIME_RELEASE_IDENTITY_INVALID" $expected
}
if ($commit -notmatch '^[0-9a-fA-F]{40}$' -or $expected -ne "1.0.1+$commit") {
    Fail "RUNTIME_COMMIT_IDENTITY_MISMATCH" "$commit / $expected"
}
if ([bool]$manifest.dirty) { Fail "RUNTIME_MANIFEST_DIRTY" }

$required = [ordered]@{
    Desktop = "Desktop\WhisperX.Atom.Desktop.exe"
    RecorderHost = "RecorderHost\WhisperX.Atom.Recorder.Host.exe"
    VoiceHost = "VoiceHost\WhisperX.Atom.Voice.Host.exe"
    Updater = "Desktop\WhisperX.Atom.Updater.exe"
    TtsHost = "TtsHost\TtsHost.exe"
}
$artifactResults = [ordered]@{}
foreach ($entry in $required.GetEnumerator()) {
    $path = Join-Path $artifacts $entry.Value
    $identity = Get-ProductIdentity $path
    if ($identity -ne $expected) { Fail "RUNTIME_ARTIFACT_IDENTITY_MISMATCH" "$($entry.Key)=$identity expected=$expected" }
    $record = @($manifest.components | Where-Object { [string]$_.name -eq $entry.Key }) | Select-Object -First 1
    if ($null -eq $record) { Fail "RUNTIME_COMPONENT_NOT_IN_MANIFEST" $entry.Key }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne ([string]$record.sha256).ToLowerInvariant()) { Fail "RUNTIME_ARTIFACT_HASH_MISMATCH" $entry.Key }
    $artifactResults[$entry.Key] = [ordered]@{ path = $entry.Value; buildIdentity = $identity; sha256 = $hash }
}

$legacyPayload = @(Get-ChildItem -LiteralPath $artifacts -Recurse -File -ErrorAction Stop | Where-Object {
    $_.Name -ieq "app.py" -or $_.Extension -iin @(".py", ".pyc", ".pyo") -or $_.Name -match "(?i)^python(?:\.exe)?$"
})
if ($legacyPayload.Count -gt 0) { Fail "RUNTIME_LEGACY_PYTHON_IN_PAYLOAD" ($legacyPayload.FullName -join ", ") }

$installedResults = [ordered]@{}
$installedPresent = (Test-Path -LiteralPath $installed -PathType Container) -and ($RequireInstalled -or $CheckRunningProcesses)
if ($RequireInstalled -and -not $installedPresent) { Fail "RUNTIME_INSTALLED_ROOT_MISSING" $installed }
if ($installedPresent) {
    foreach ($entry in $required.GetEnumerator()) {
        $path = Join-Path $installed $entry.Value
        $identity = Get-ProductIdentity $path
        if ($identity -ne $expected) { Fail "RUNTIME_INSTALLED_IDENTITY_MISMATCH" "$($entry.Key)=$identity expected=$expected" }
        $installedResults[$entry.Key] = [ordered]@{ path = $entry.Value; buildIdentity = $identity; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $legacyInstalled = @(Get-ChildItem -LiteralPath $installed -Recurse -File -ErrorAction Stop | Where-Object {
        $_.Name -ieq "app.py" -or $_.Extension -iin @(".py", ".pyc", ".pyo") -or $_.Name -match "(?i)^python(?:\.exe)?$"
    })
    if ($legacyInstalled.Count -gt 0) { Fail "RUNTIME_LEGACY_PYTHON_INSTALLED" ($legacyInstalled.FullName -join ", ") }
}

$runningResults = @()
if ($CheckRunningProcesses) {
    $processNames = @("WhisperX.Atom.Desktop", "WhisperX.Atom.Recorder.Host", "WhisperX.Atom.Voice.Host", "TtsHost")
    $runningProcesses = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
    $ttsProcesses = @($runningProcesses | Where-Object { $_.ProcessName -ieq "TtsHost" })
    if ($ttsProcesses.Count -gt 1) { Fail "RUNTIME_MULTIPLE_TTS_HOSTS" "count=$($ttsProcesses.Count)" }
    foreach ($process in $runningProcesses) {
        try { $path = [IO.Path]::GetFullPath($process.MainModule.FileName) }
        catch { Fail "RUNTIME_PROCESS_UNINSPECTABLE" "PID=$($process.Id) Name=$($process.ProcessName)" }
        if (-not $path.StartsWith($installed + "\", [StringComparison]::OrdinalIgnoreCase)) {
            Fail "RUNTIME_PROCESS_PATH_MISMATCH" "PID=$($process.Id) Path=$path"
        }
        $identity = Get-ProductIdentity $path
        if ($identity -ne $expected) { Fail "RUNTIME_PROCESS_IDENTITY_MISMATCH" "PID=$($process.Id)=$identity expected=$expected" }
        $runningResults += [ordered]@{ processId = $process.Id; name = $process.ProcessName; path = $path; buildIdentity = $identity }
    }
}

$serverResult = $null
if (-not [string]::IsNullOrWhiteSpace($ServerManifestPath)) {
    $serverPath = [IO.Path]::GetFullPath($ServerManifestPath)
    if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) { Fail "RUNTIME_SERVER_MANIFEST_MISSING" $serverPath }
    $server = Get-Content -LiteralPath $serverPath -Raw -Encoding utf8 | ConvertFrom-Json
    $serverIdentity = [string]$server.buildIdentity
    if ($serverIdentity -ne $expected) { Fail "RUNTIME_SERVER_IDENTITY_MISMATCH" "server=$serverIdentity expected=$expected" }
    if ($serverIdentity -match '(?i)(dev|dirty)') { Fail "RUNTIME_SERVER_IDENTITY_INVALID" $serverIdentity }
    $serverResult = [ordered]@{ path = $serverPath; buildIdentity = $serverIdentity; releaseTag = [string]$server.releaseTag }
}

$output = [ordered]@{
    schemaVersion = 1
    product = "WhisperX Atom"
    status = "CLEAN_RUNTIME_VERIFIED"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    buildIdentity = $expected
    commit = $commit
    productionPath = @("Desktop", "AudioGraph Recorder Host", "Voice Host", "Docker core", "Host GPU Worker")
    legacyPython = [ordered]@{ allowedInRepository = $true; allowedInProductionPayload = $false; appPyUsedAsProductionEntryPoint = $false }
    artifacts = $artifactResults
    installed = if ($installedPresent) { $installedResults } else { $null }
    runningProcesses = @($runningResults)
    server = $serverResult
}
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$part = "$OutputPath.part"
$output | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $part -Encoding utf8
Move-Item -LiteralPath $part -Destination $OutputPath -Force
Write-Host "CLEAN_RUNTIME_VERIFIED=true"
Write-Host "BUILD_IDENTITY=$expected"
Write-Host "ARTIFACTS_ROOT=$artifacts"
if ($installedPresent) { Write-Host "INSTALLED_ROOT=$installed" }
