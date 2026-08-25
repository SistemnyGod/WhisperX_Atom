[CmdletBinding()]
param(
    [string]$OutputRoot = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $PSScriptRoot '..\artifacts\tts-host' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$piperVersion = '2023.11.14-2'
$modelRevision = '78090a64e35bfab40db7db02ce562967e1161c78'
$piperUrl = "https://github.com/rhasspy/piper/releases/download/$piperVersion/piper_windows_amd64.zip"
$modelBase = "https://huggingface.co/jgkawell/jarvis/resolve/$modelRevision/en/en_GB/jarvis/medium"
$modelUrl = "$modelBase/jarvis-medium.onnx?download=true"
$configUrl = "$modelBase/jarvis-medium.onnx.json?download=true"
$stage = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-piper-" + [Guid]::NewGuid().ToString('N'))
$download = Join-Path $stage 'piper.zip'
$piperOut = Join-Path $OutputRoot 'Piper'
$modelOut = Join-Path $OutputRoot 'Models\piper\jarvis'

New-Item -ItemType Directory -Force -Path $stage, $piperOut, $modelOut | Out-Null
try {
    $modelPath = Join-Path $modelOut 'jarvis-medium.onnx'
    $configPath = Join-Path $modelOut 'jarvis-medium.onnx.json'
    if ($Force -or -not (Test-Path -LiteralPath $modelPath -PathType Leaf)) {
        Invoke-WebRequest -Uri $modelUrl -OutFile $modelPath
    }
    if ($Force -or -not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        Invoke-WebRequest -Uri $configUrl -OutFile $configPath
    }
    if ($Force -or -not (Test-Path -LiteralPath (Join-Path $piperOut 'piper.exe') -PathType Leaf)) {
        Invoke-WebRequest -Uri $piperUrl -OutFile $download
        Expand-Archive -LiteralPath $download -DestinationPath $stage -Force
        $exe = Get-ChildItem -LiteralPath $stage -Filter 'piper.exe' -File -Recurse | Select-Object -First 1
        if ($null -eq $exe) { throw 'PIPER_WINDOWS_BINARY_MISSING' }
        $sourceRoot = $exe.Directory.FullName
        Get-ChildItem -LiteralPath $sourceRoot -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $piperOut $_.Name) -Recurse -Force
        }
    }
    $runtimeFiles = @('piper.exe', 'piper_phonemize.dll', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'espeak-ng.dll', 'libtashkeel_model.ort')
    foreach ($runtimeFile in $runtimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $piperOut $runtimeFile) -PathType Leaf)) {
            throw "PIPER_RUNTIME_ASSET_MISSING: $runtimeFile"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $piperOut 'espeak-ng-data') -PathType Container)) {
        throw 'PIPER_ESPEAK_DATA_MISSING'
    }
    $runtimeAttestation = @($runtimeFiles | ForEach-Object {
        $runtimePath = Join-Path $piperOut $_
        [ordered]@{
            file = $_
            sizeBytes = (Get-Item -LiteralPath $runtimePath).Length
            sha256 = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        voice = 'JARVIS_EN'
        engine = 'PIPER_JARVIS'
        model = [ordered]@{
            source = 'https://huggingface.co/jgkawell/jarvis'
            revision = $modelRevision
            file = 'jarvis-medium.onnx'
            configFile = 'jarvis-medium.onnx.json'
            sha256 = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant()
            configSha256 = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        runtime = [ordered]@{
            source = 'https://github.com/rhasspy/piper'
            revision = $piperVersion
            file = 'piper.exe'
            sha256 = (Get-FileHash -LiteralPath (Join-Path $piperOut 'piper.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
            files = $runtimeAttestation
            dataDirectory = 'espeak-ng-data'
        }
        culture = 'en-GB'
        sampleRate = 22050
        license = 'MIT model/runtime; voice identity rights must be reviewed before commercial use'
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $piperOut 'piper-jarvis.manifest.json') -Encoding utf8
    Write-Host "PIPER_JARVIS_STAGED=$OutputRoot"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
