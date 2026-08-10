param(
    [string]$VoiceHostPath,
    [string]$SidFile,
    [switch]$Remove
)
$ErrorActionPreference = "Stop"
$valueName = "WhisperXAtomVoiceHost"
if ($Remove) {
    $removeKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    if (-not [string]::IsNullOrWhiteSpace($SidFile) -and (Test-Path -LiteralPath $SidFile -PathType Leaf)) {
        $targetSid = (Get-Content -LiteralPath $SidFile -Raw).Trim()
        [void][Security.Principal.SecurityIdentifier]::new($targetSid)
        $removeKey = "Registry::HKEY_USERS\$targetSid\Software\Microsoft\Windows\CurrentVersion\Run"
    }
    Remove-ItemProperty -LiteralPath $removeKey -Name $valueName -ErrorAction SilentlyContinue
    exit 0
}
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ([string]::IsNullOrWhiteSpace($VoiceHostPath) -or -not (Test-Path -LiteralPath $VoiceHostPath -PathType Leaf)) { throw "VoiceHost binary not found." }
if ([string]::IsNullOrWhiteSpace($SidFile)) { throw "SID output path is required." }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
[void][Security.Principal.SecurityIdentifier]::new($sid)
$parent = Split-Path -Parent $SidFile
New-Item -ItemType Directory -Force -Path $parent | Out-Null
Set-Content -LiteralPath $SidFile -Value $sid -Encoding ASCII -NoNewline
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -LiteralPath $runKey -Name $valueName -Type String -Value ('"{0}"' -f $VoiceHostPath)