[CmdletBinding()]
param(
    [string]$OutputPath = "",
    [switch]$IncludeGenerated
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repo "artifacts\audio-runtime\audio-stack-migration-inventory.json"
}

$excluded = @("\\bin\\", "\\obj\\", "\\artifacts\\", "\\.git\\", "\\.pytest-temp\\", "\\tmp-test-run\\")
$patterns = [ordered]@{
    "NAUDIO_API" = "NAudio|MMDevice|MMDeviceEnumerator|MMDeviceCollection|WasapiCapture|WasapiLoopbackCapture|WaveFormatExtensible|WaveFormatEncoding|AudioClient|CoreAudioApi|DataAvailable|RecordingStopped"
    "SERVICE_LIFECYCLE" = "WhisperXAtomRecorder|RecorderServiceController|ATOM_AGENT_ALLOWED_SID|allowed-user.sid|LocalSystem|ServiceController|AddWindowsService|New-Service|Start-Service|Stop-Service"
    "FFMPEG" = "ATOM_AGENT_FFMPEG_PATH|ATOM_AGENT_FFPROBE_PATH|ffmpeg\\.exe|ffprobe\\.exe|FFmpeg|ffmpeg"
    "DEVICE_MODEL" = "captureDevices|renderDevices|MicrophoneDeviceId|SystemAudioDeviceId|AudioSampleFormatResolver|DeviceHealth|AudioDeviceProbe|AudioRuntimeProbe"
    "AUDIOGRAPH" = "AudioGraph|DeviceWatcher|DeviceInformation|MediaDevice|AudioDeviceInputNode|AudioFrameOutputNode|WhisperXAtomRecorderHost"
}

function Get-Category([string]$group) {
    switch ($group) {
        "NAUDIO_API" { return "AUDIO_STACK" }
        "SERVICE_LIFECYCLE" { return "PROCESS_LIFECYCLE" }
        "FFMPEG" { return "ENCODING" }
        "DEVICE_MODEL" { return "DISCOVERY_HEALTH_FORMAT" }
        "AUDIOGRAPH" { return "NEW_STACK" }
        default { return "UNKNOWN" }
    }
}

function Get-Action([string]$path, [string]$group) {
    if ($path -match "voice-host") { return "KEEP_OPTIONAL" }
    if ($group -eq "AUDIOGRAPH") { return "KEEP_NEW" }
    if ($path -match "workers\\media_worker|app\\|whisperx_atom") { return "KEEP_SERVER" }
    if ($path -match "apps\\recorder-host\\") { return "KEEP_NEW" }
    if ($group -eq "NAUDIO_API" -and $path -match "LegacyWasapiCaptureEngine|LegacyWasapiDeviceProbe|AudioRuntimeProbe|AudioSampleFormat") { return "ISOLATED_LEGACY_ADAPTER" }
    if ($group -eq "NAUDIO_API" -and $path -match "recorder-agent\\.*Service\.csproj") { return "ISOLATED_LEGACY_SERVICE_UNTIL_GATE_G" }
    if ($group -eq "NAUDIO_API" -and $path -match "recorder-agent\\(DeviceHealth|DeviceHealthMonitor|AudioDeviceProbe)\.cs") { return "ISOLATED_LEGACY_SERVICE_UNTIL_GATE_G" }
    if ($group -eq "NAUDIO_API" -and $path -match "recorder-agent\\Program\.cs") { return "ISOLATED_LEGACY_SERVICE_UNTIL_GATE_G" }
    if ($group -eq "NAUDIO_API" -and $path -match "RecordingCoordinator") { return "LEGACY_FACADE_UNTIL_GATE_E" }
    if ($path -match "tests") { return "ADAPT_TEST" }
    if ($path -match "docs") { return "ADAPT_DOC" }
    if ($group -eq "SERVICE_LIFECYCLE") { return "DEPRECATE_AFTER_GATE_G" }
    if ($group -eq "FFMPEG") { return "KEEP_UNTIL_GATE_H" }
    return "MIGRATE"
}

$extensions = @("*.cs", "*.csproj", "*.ps1", "*.py", "*.md", "*.json", "*.yml", "*.yaml", "*.xaml", "*.js", "*.ts", "*.manifest")
$rootFiles = @()
$rg = Get-Command rg -ErrorAction SilentlyContinue
if ($null -ne $rg) {
    Push-Location $repo
    try {
        $rgArgs = @("--files", "--hidden")
        if (-not $IncludeGenerated) {
            $rgArgs += @("--glob", "!bin/**", "--glob", "!obj/**", "--glob", "!artifacts/**", "--glob", "!.git/**", "--glob", "!.pytest-temp/**", "--glob", "!tmp-test-run/**")
        }
        foreach ($extension in $extensions) { $rgArgs += @("--glob", $extension) }
        $rootFiles = @(& rg @rgArgs 2>$null | ForEach-Object { Get-Item -LiteralPath (Join-Path $repo $_) -ErrorAction SilentlyContinue } | Where-Object { $_.Name -ne "audit-audio-stack-migration.ps1" })
    }
    finally { Pop-Location }
}
else {
    $rootFiles = @(Get-ChildItem -LiteralPath $repo -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -ne "audit-audio-stack-migration.ps1" -and
        $_.Extension -in @(".cs", ".csproj", ".ps1", ".py", ".md", ".json", ".yml", ".yaml", ".xaml", ".js", ".ts", ".manifest")
    })
}

$references = [System.Collections.Generic.List[object]]::new()
foreach ($file in $rootFiles) {
    $relative = $file.FullName.Substring($repo.Length).TrimStart('\\')
    $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
    if ($null -eq $text) { continue }
    foreach ($entry in $patterns.GetEnumerator()) {
        $matches = [regex]::Matches($text, $entry.Value, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($matches.Count -eq 0) { continue }
        $symbols = @($matches | ForEach-Object { $_.Value } | Sort-Object -Unique | Select-Object -First 12)
        $references.Add([ordered]@{
            file = $relative
            category = Get-Category $entry.Key
            patternGroup = $entry.Key
            symbols = $symbols
            matchCount = $matches.Count
            action = Get-Action $relative $entry.Key
        })
    }
}

$report = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow
    currentHead = (& git -C $repo rev-parse HEAD).Trim()
    branch = (& git -C $repo branch --show-current).Trim()
    repositoryRoot = $repo
    referenceCount = $references.Count
    filesWithReferences = @($references.file | Sort-Object -Unique).Count
    references = @($references)
    unclassifiedLegacyReferences = @($references | Where-Object {
        $_.action -eq "MIGRATE" -and $_.patternGroup -in @("NAUDIO_API", "SERVICE_LIFECYCLE", "FFMPEG")
    })
    runtimeGateEvidence = [ordered]@{
        releaseDefaultArtifact = Join-Path $repo "artifacts\audio-runtime\audiograph-release-default.json"
        present = Test-Path -LiteralPath (Join-Path $repo "artifacts\audio-runtime\audiograph-release-default.json") -PathType Leaf
        note = "A-E hardware evidence is required before production default can change."
    }
    releaseSwitchBlocked = $true
    policy = [ordered]@{
        microphoneNaudio = "REPLACE"
        systemLoopback = "KEEP_UNTIL_GATE_F"
        service = "DEPRECATE_AFTER_GATE_G"
        clientFfmpeg = "KEEP_UNTIL_GATE_H"
        voiceHostNaudio = "KEEP_OPTIONAL"
    }
}

$report.releaseSwitchBlocked = @($report.unclassifiedLegacyReferences).Count -gt 0 -or -not $report.runtimeGateEvidence.present

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Audio stack migration inventory: $OutputPath"
Write-Host "References=$($report.referenceCount) Files=$($report.filesWithReferences) HEAD=$($report.currentHead)"
