$ErrorActionPreference = "Stop"
$serviceName = "WhisperXAtomRecorder"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.Status -ne "Stopped") { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
    sc.exe delete $serviceName | Out-Null
}

[Environment]::SetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID", $null, "Machine")
[Environment]::SetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH", $null, "Machine")
[Environment]::SetEnvironmentVariable("ATOM_AGENT_FFPROBE_PATH", $null, "Machine")
[Environment]::SetEnvironmentVariable("WHISPERX_RECORDER_HOST_EXE", $null, "Machine")
