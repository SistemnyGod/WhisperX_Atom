[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\desktop\TtsHost'),
    [string]$ModelRoot = (Join-Path $PSScriptRoot '..\artifacts\tts-host\Models\silero-v5_5_ru'),
    [string]$PythonExe = '',
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dirty = @(cmd.exe /d /s /c "git -C `"$repoRoot`" status --porcelain --untracked-files=normal 2>NUL")
if ($dirty.Count -gt 0 -and $env:WHISPERX_ALLOW_DIRTY_RELEASE -notin @('1','true','yes')) { throw 'TTS_BUILD_DIRTY_WORKTREE' }
$python = if ($PythonExe) { (Resolve-Path $PythonExe).Path } else { Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe' }
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw "TTS_BUILD_PYTHON_MISSING: $python" }
$identity = "1.0.1+" + (& git -C $repoRoot rev-parse HEAD).Trim()
if ($identity -match '(?i)(dev|dirty)' -or $identity -notmatch '^1\.0\.1\+[0-9a-f]{40}$') { throw 'TTS_BUILD_IDENTITY_INVALID' }
$source = Join-Path $repoRoot 'apps\tts-host'
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
    & $venvPython -m pip install --disable-pip-version-check --requirement (Join-Path $source 'requirements.cpu.lock.txt')
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
('{"schemaVersion":1,"id":"publish-smoke","op":"ping","buildIdentity":"' + $identity + '"}') | & (Join-Path $built 'TtsHost.exe') --parent-pid $PID
if ($LASTEXITCODE -ne 0) { throw 'TTS_HOST_SMOKE_FAILED' }
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
Move-Item -LiteralPath $built -Destination $out
$forbidden = @(Get-ChildItem -LiteralPath $out -Recurse -File | Where-Object { $_.Extension -in '.py','.pyc','.pyo' -or $_.Name -ieq 'python.exe' })
if ($forbidden.Count) { throw "TTS_PRODUCTION_PAYLOAD_CONTAINS_PYTHON: $($forbidden.FullName -join ', ')" }
@{ schemaVersion=1; component='TtsHost'; buildIdentity=$identity; modelSha256=(Get-FileHash (Join-Path $out 'Models\silero-v5_5_ru\v5_5_ru.pt') -Algorithm SHA256).Hash.ToLowerInvariant(); generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content (Join-Path $out 'build-identity.json') -Encoding utf8
Write-Host "TTS_HOST_PUBLISHED=$out"
