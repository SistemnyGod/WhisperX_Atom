[CmdletBinding()]
param(
    [string]$OutputRoot = '',
    [string]$ModelRoot = '',
    [string]$PythonExe = '',
    [string]$WheelhouseRoot = '',
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\desktop\TtsHost'
}
if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
    $ModelRoot = Join-Path $repoRoot 'artifacts\tts-host\Models\silero-v5_5_ru'
}
$dirty = @(cmd.exe /d /s /c "git -C `"$repoRoot`" status --porcelain --untracked-files=normal 2>NUL")
$dirtyAllowed = $env:WHISPERX_ALLOW_DIRTY_RELEASE -in @('1','true','yes')
if ($dirty.Count -gt 0 -and -not $dirtyAllowed) { throw 'TTS_BUILD_DIRTY_WORKTREE' }
$python = if ($PythonExe) { (Resolve-Path $PythonExe).Path } else { Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe' }
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw "TTS_BUILD_PYTHON_MISSING: $python" }
$dirtySuffix = if ($dirty.Count -gt 0) { '-dirty' } else { '' }
$identity = "1.0.1+" + (& git -C $repoRoot rev-parse HEAD).Trim() + $dirtySuffix
if ($identity -match '(?i)dev' -or $identity -notmatch '^1\.0\.1\+[0-9a-f]{40}(-dirty)?$') { throw 'TTS_BUILD_IDENTITY_INVALID' }
$source = Join-Path $repoRoot 'apps\tts-host'
$wheelhouse = if ($WheelhouseRoot) { [IO.Path]::GetFullPath($WheelhouseRoot) } elseif ($env:WHISPERX_TTS_WHEELHOUSE) { [IO.Path]::GetFullPath($env:WHISPERX_TTS_WHEELHOUSE) } else { '' }
$model = Join-Path $ModelRoot 'v5_5_ru.pt'
if (-not (Test-Path -LiteralPath $model -PathType Leaf)) { throw "TTS_MODEL_MISSING: stage it with prepare-silero-tts.ps1" }
$modelManifest = Join-Path $ModelRoot 'model-manifest.json'
if (-not (Test-Path -LiteralPath $modelManifest -PathType Leaf)) { throw 'TTS_MODEL_MANIFEST_MISSING' }
$stagedManifest = Get-Content -LiteralPath $modelManifest -Raw | ConvertFrom-Json
$modelHash = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace([string]$stagedManifest.sha256) -or $modelHash -ne ([string]$stagedManifest.sha256).ToLowerInvariant()) { throw 'TTS_MODEL_HASH_MISMATCH' }
$staging = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-tts-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$venv = Join-Path $staging '.venv'
& $python -m venv $venv
if ($LASTEXITCODE -ne 0) { throw 'TTS_VENV_CREATE_FAILED' }
$venvPython = Join-Path $venv 'Scripts\python.exe'
if (-not $NoInstall) {
    $requirements = Join-Path $source 'requirements.cpu.lock.txt'
    if ($wheelhouse) {
        if (-not (Test-Path -LiteralPath $wheelhouse -PathType Container)) { throw "TTS_WHEELHOUSE_MISSING: $wheelhouse" }
        $wheelManifest = Join-Path $wheelhouse 'wheelhouse-manifest.json'
        if (-not (Test-Path -LiteralPath $wheelManifest -PathType Leaf)) { throw 'TTS_WHEELHOUSE_MANIFEST_MISSING' }
        $manifest = Get-Content -LiteralPath $wheelManifest -Raw | ConvertFrom-Json
        if ($manifest.schemaVersion -ne 1 -or @($manifest.files).Count -eq 0) { throw 'TTS_WHEELHOUSE_MANIFEST_INVALID' }
        foreach ($entry in @($manifest.files)) {
            $wheel = Join-Path $wheelhouse ([string]$entry.name)
            if (-not (Test-Path -LiteralPath $wheel -PathType Leaf)) { throw "TTS_WHEEL_FILE_MISSING: $($entry.name)" }
            $actual = (Get-FileHash -LiteralPath $wheel -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "TTS_WHEEL_HASH_MISMATCH: $($entry.name)" }
        }
        if (-not (@($manifest.files | Where-Object { [string]$_.name -match '^torch-2\.8\.0(?:\+cpu|\.post).*\.whl$' }).Count)) { throw 'TTS_TORCH_CPU_WHEEL_MISSING' }
        & $venvPython -m pip install --disable-pip-version-check --no-index --find-links $wheelhouse --requirement $requirements
    } else {
        & $venvPython -m pip install --disable-pip-version-check --requirement $requirements
    }
    if ($LASTEXITCODE -ne 0) { throw 'TTS_DEPENDENCY_INSTALL_FAILED' }
}
$out = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $out | Out-Null
$versionFile = Join-Path $staging 'version_info.txt'
@"
VSVersionInfo(
  ffi=FixedFileInfo(filevers=(1,0,1,0), prodvers=(1,0,1,0), mask=0x3f, flags=0x0, OS=0x4, fileType=0x1, subtype=0x0, date=(0,0)),
  kids=[StringFileInfo([StringTable('040904B0',[StringStruct('CompanyName','WhisperX Atom'),StringStruct('FileDescription','WhisperX Atom Silero TTS Host'),StringStruct('ProductVersion','$identity'),StringStruct('FileVersion','$identity')])]), VarFileInfo([VarStruct('Translation',[0x0409,1200])])]
)
"@ | Set-Content -LiteralPath $versionFile -Encoding ascii
& $venvPython -m PyInstaller --clean --noconfirm --onedir --name TtsHost --version-file $versionFile --distpath $staging\dist --workpath $staging\build --specpath $staging --paths $source (Join-Path $source 'tts_host.py')
if ($LASTEXITCODE -ne 0) { throw 'TTS_PYINSTALLER_FAILED' }
$built = Join-Path $staging 'dist\TtsHost'
New-Item -ItemType Directory -Force -Path (Join-Path $built 'Models\silero-v5_5_ru') | Out-Null
Copy-Item -LiteralPath $model -Destination (Join-Path $built 'Models\silero-v5_5_ru\v5_5_ru.pt') -Force
Copy-Item -LiteralPath $modelManifest -Destination (Join-Path $built 'Models\silero-v5_5_ru\model-manifest.json') -Force
Copy-Item -LiteralPath (Join-Path $source 'THIRD_PARTY_NOTICES.txt') -Destination (Join-Path $built 'THIRD_PARTY_NOTICES.txt') -Force
$env:WHISPERX_BUILD_IDENTITY = $identity
$smokeRequest = [ordered]@{ schemaVersion=1; id='publish-smoke'; op='ping'; buildIdentity=$identity } | ConvertTo-Json -Compress
$smokeStart = [Diagnostics.ProcessStartInfo]::new()
$smokeStart.FileName = Join-Path $built 'TtsHost.exe'
# Windows PowerShell 5.1 targets .NET Framework, where ArgumentList is not
# available. The only argument is a validated integer generated locally.
$smokeStart.Arguments = "--parent-pid $PID"
$smokeStart.UseShellExecute = $false
$smokeStart.CreateNoWindow = $true
$smokeStart.RedirectStandardInput = $true
$smokeStart.RedirectStandardOutput = $true
$smokeStart.RedirectStandardError = $true
$smokeProcess = [Diagnostics.Process]::Start($smokeStart)
$smokeBytes = [Text.UTF8Encoding]::new($false).GetBytes($smokeRequest + "`n")
$smokeProcess.StandardInput.BaseStream.Write($smokeBytes, 0, $smokeBytes.Length)
$smokeProcess.StandardInput.BaseStream.Flush()
$smokeProcess.StandardInput.BaseStream.Close()
$smokeStdout = $smokeProcess.StandardOutput.ReadToEnd()
$smokeStderr = $smokeProcess.StandardError.ReadToEnd()
if (-not $smokeProcess.WaitForExit(120000)) {
    try { $smokeProcess.Kill($true) } catch { }
    throw 'TTS_HOST_SMOKE_TIMEOUT'
}
if ($smokeProcess.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($smokeStdout)) {
    throw "TTS_HOST_SMOKE_FAILED: $smokeStderr"
}
$smokeLines = @($smokeStdout -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
try { $smoke = $smokeLines[-1] | ConvertFrom-Json } catch { throw 'TTS_HOST_SMOKE_PROTOCOL_INVALID' }
if (-not $smoke.ok -or $smoke.state -ne 'READY' -or $smoke.buildIdentity -ne $identity) { throw "TTS_HOST_SMOKE_FAILED: $($smoke.errorCode)" }
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
Move-Item -LiteralPath $built -Destination $out
# PyTorch's frozen runtime intentionally contains vendor modules used by
# torch.package/JIT. Reject our application sources and a standalone Python
# interpreter; neither is required or allowed in the installed product.
$applicationSources = @('tts_host.py','protocol.py','silero_runtime.py','text_normalizer.py')
$forbidden = @(Get-ChildItem -LiteralPath $out -Recurse -File | Where-Object {
    $_.Name -ieq 'python.exe' -or $_.Name -iin $applicationSources -and $_.FullName -notmatch '[\\/]_internal[\\/]torch[\\/]'
})
if ($forbidden.Count) { throw "TTS_PRODUCTION_PAYLOAD_CONTAINS_PYTHON: $($forbidden.FullName -join ', ')" }
@{ schemaVersion=1; component='TtsHost'; buildIdentity=$identity; modelSha256=(Get-FileHash (Join-Path $out 'Models\silero-v5_5_ru\v5_5_ru.pt') -Algorithm SHA256).Hash.ToLowerInvariant(); generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content (Join-Path $out 'build-identity.json') -Encoding utf8
Write-Host "TTS_HOST_PUBLISHED=$out"
