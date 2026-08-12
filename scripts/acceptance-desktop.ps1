[CmdletBinding()]
param(
    [switch]$SkipDocker,
    [switch]$SkipGpu,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "[RUN] $Name" -ForegroundColor Cyan
    try {
        & $Action
        if ($LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
        Write-Host "[OK]  $Name" -ForegroundColor Green
    } catch {
        $failures.Add($Name)
        Write-Host "[FAIL] $Name - $($_.Exception.Message)" -ForegroundColor Red
    }
}

Invoke-Step "Desktop build" {
    dotnet build apps/desktop/WhisperX.Atom.Desktop/WhisperX.Atom.Desktop.csproj --no-restore --nologo
}
Invoke-Step "Recorder Service build" {
    dotnet build apps/recorder-agent/WhisperX.Atom.Recorder.Service.csproj --no-restore --nologo
}
Invoke-Step "Python tests" {
    # Keep test-only files in a fresh system temp directory. Child processes
    # such as ffmpeg may be denied by repository ACLs on managed desktops.
    $testTemp = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-acceptance-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $testTemp | Out-Null
    $savedTemp = @{
        TEMP = [Environment]::GetEnvironmentVariable("TEMP", "Process")
        TMP = [Environment]::GetEnvironmentVariable("TMP", "Process")
        TMPDIR = [Environment]::GetEnvironmentVariable("TMPDIR", "Process")
    }
    try {
        $env:TEMP = $testTemp
        $env:TMP = $testTemp
        $env:TMPDIR = $testTemp
        py -3.12 -m unittest discover -s tests -q
    }
    finally {
        foreach ($name in $savedTemp.Keys) {
            if ($null -eq $savedTemp[$name]) {
                Remove-Item "Env:$name" -ErrorAction SilentlyContinue
            }
            else {
                Set-Item "Env:$name" $savedTemp[$name]
            }
        }
        if (Test-Path -LiteralPath $testTemp) {
            Remove-Item -LiteralPath $testTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
Invoke-Step "Python compile" {
    py -m compileall -q whisperx_atom workers
}
Invoke-Step "FFmpeg available for Recorder Service" {
    if ($null -eq (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)) {
        throw "FFmpeg is not on PATH; install it before installing WhisperXAtomRecorder"
    }
}

Invoke-Step "Desktop package files" {
    foreach ($path in @(
        "apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml",
        "apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml.cs",
        "apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs",
        "apps/recorder-agent/AgentPipeHost.cs"
    )) {
        if (-not (Test-Path -LiteralPath $path)) { throw "missing $path" }
        $content = Get-Content -LiteralPath $path -Raw
        if ($content -match '\?{4,}') { throw "encoding artefact in $path" }
    }
}

if (-not $SkipDocker) {
    Invoke-Step "Compose validation" {
        docker compose -f compose.dev.yml --profile core config --quiet
    }
    Invoke-Step "Core services healthy" {
        $json = docker compose -f compose.dev.yml ps --format json | ConvertFrom-Json
        $required = @("api", "postgres", "nats", "media-worker", "import-worker", "outbox-relay")
        foreach ($service in $required) {
            $row = @($json | Where-Object { $_.Service -eq $service }) | Select-Object -First 1
            if ($null -eq $row -or $row.Health -ne "healthy") {
                throw "$service is not healthy"
            }
        }
    }
}

if (-not $SkipGpu) {
    Invoke-Step "GPU runtime" {
        docker run --rm --gpus all nvidia/cuda:12.8.1-base-ubuntu24.04 nvidia-smi
    }
}

if (-not $SkipInstaller) {
    Invoke-Step "Installer artifact" {
        $path = Join-Path $repo "artifacts/installer/WhisperXAtom-Setup.exe"
        if (-not (Test-Path -LiteralPath $path)) { throw "installer not found; run scripts/build-installer.ps1" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        Write-Host "       SHA256: $hash"
        if ((Get-Item -LiteralPath $path).Length -lt 1MB) { throw "installer is unexpectedly small" }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Desktop acceptance failed: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "Desktop acceptance passed. Web UI is not required; browser diagnostics remain optional." -ForegroundColor Green
