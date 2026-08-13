Set-StrictMode -Version Latest

function Resolve-RecorderRuntime {
    param(
        [string]$Override = "",
        [string]$MachineConfigPath = (Join-Path $env:ProgramData "WhisperXAtom\client-config.json")
    )

    $engine = if (-not [string]::IsNullOrWhiteSpace($Override)) { $Override } else { [string]$env:AUDIO_CAPTURE_ENGINE }
    $source = if (-not [string]::IsNullOrWhiteSpace($engine)) { "ENVIRONMENT" } else { $null }

    if ([string]::IsNullOrWhiteSpace($engine) -and (Test-Path -LiteralPath $MachineConfigPath -PathType Leaf)) {
        try {
            $config = Get-Content -LiteralPath $MachineConfigPath -Raw | ConvertFrom-Json
            $candidate = [string]$config.audioConfiguration.captureEngine
            if ($candidate -in @("AUDIOGRAPH", "LEGACY_WASAPI")) {
                $engine = $candidate
                $source = "MACHINE_CONFIG"
            }
        }
        catch { }
    }

    $engine = ([string]$engine).Trim().ToUpperInvariant()
    if ($engine -notin @("AUDIOGRAPH", "LEGACY_WASAPI")) {
        $engine = "AUDIOGRAPH"
        $source = "RELEASE_DEFAULT"
    }

    [pscustomobject]@{
        CaptureEngine = $engine
        PipeName = if ($engine -eq "AUDIOGRAPH") { "WhisperXAtomRecorderHost" } else { "WhisperXAtomAgent" }
        ProtocolVersion = if ($engine -eq "AUDIOGRAPH") { 6 } else { 5 }
        Source = $source
    }
}
