$ErrorActionPreference = "Stop"
$serviceName = "WhisperXAtomRecorder"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.Status -ne "Stopped") { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
    sc.exe delete $serviceName | Out-Null
}

[Environment]::SetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID", $null, "Machine")
