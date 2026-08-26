#requires -Version 7.0

param([string]$OutputRoot = (Join-Path $PSScriptRoot "..\apps\voice-host\Assets\VoiceResponses"))
$ErrorActionPreference = "Stop"
# This file is UTF-8 without a BOM. Windows PowerShell 5.1 reads such scripts
# using the active ANSI code page and can silently turn Russian phrases into
# mojibake before synthesis. Requiring PowerShell 7 makes the text decoding
# deterministic and prevents publishing corrupted voice responses.
Add-Type -AssemblyName System.Speech
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$responses = [ordered]@{
    "listening.wav" = "Слушаю"
    "recording-started.wav" = "Запись начата"
    "recording-paused.wav" = "Запись приостановлена"
    "recording-resumed.wav" = "Запись продолжена"
    "recording-stopped.wav" = "Совещание завершено"
    "marker-added.wav" = "Метка установлена"
    "decision-added.wav" = "Решение отмечено"
    "action-added.wav" = "Поручение отмечено"
    "confirmation-required.wav" = "Подтвердите остановку записи"
    "command-rejected.wav" = "Команда не распознана"
    "error.wav" = "Ошибка выполнения команды"
}
$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
    $voice = $synth.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq "ru-RU" } | Select-Object -First 1
    if ($null -eq $voice) { throw "A ru-RU Windows TTS voice is required to prepare offline responses." }
    $synth.SelectVoice($voice.VoiceInfo.Name)
    $synth.Rate = 1
    foreach ($entry in $responses.GetEnumerator()) {
        $path = Join-Path $OutputRoot $entry.Key
        $synth.SetOutputToWaveFile($path)
        $synth.Speak($entry.Value)
        $synth.SetOutputToNull()
    }
} finally { $synth.Dispose() }
Write-Host "Prepared $($responses.Count) voice responses in $OutputRoot"
