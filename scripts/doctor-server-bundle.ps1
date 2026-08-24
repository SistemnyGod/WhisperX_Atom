[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [ValidateSet('Quick','Release')][string]$Mode = 'Quick'
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
$manifestPath = Join-Path $bundle "release-manifest.json"
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "SERVER_RELEASE_MANIFEST_MISSING" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.buildIdentity -match 'dev|dirty') { throw "SERVER_RELEASE_IDENTITY_INVALID" }
$identity = [string]$manifest.buildIdentity
$tag = [string]$manifest.releaseTag
if ([string]::IsNullOrWhiteSpace($tag) -or $tag -match 'dev|dirty|latest') { throw "SERVER_RELEASE_TAG_INVALID" }
function Read-EnvValue([string]$name) {
    $line = Get-Content -LiteralPath $envFile -Encoding utf8 | Where-Object { $_ -match "^$name=" } | Select-Object -First 1
    if ($null -eq $line) { return $null }
    return ($line -replace "^$name=", '').Trim()
}
$env:WHISPERX_RELEASE_TAG = $tag
$env:WHISPERX_RELEASE_VERSION = $identity
$env:WHISPERX_BUILD_IDENTITY = $identity
$env:WHISPERX_REVISION = [string]$manifest.commit
$env:APP_VERSION = $identity
$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',(Join-Path $bundle 'compose.dev.yml'),'-f',(Join-Path $bundle 'compose.lan.yml'),'-f',(Join-Path $bundle 'compose.release.yml'))
$profiles = @('--profile','core','--profile','gpu','--profile','lan')
$assistantEnabled = (Read-EnvValue 'ASSISTANT_ENABLED') -ne 'false'
$summaryEnabled = (Read-EnvValue 'AUTO_SUMMARY_ENABLED') -eq 'true'
if ($assistantEnabled -or $summaryEnabled) { $profiles += @('--profile','llm') }
if ((Read-EnvValue 'MEETING_MEMORY_ENABLED') -ne 'false') { $profiles += @('--profile','memory') }
$configText = (& docker @compose @profiles config | Out-String)
if ($LASTEXITCODE -ne 0) { throw "SERVER_RELEASE_COMPOSE_INVALID" }
if ($configText -match '(?im)image:\s*[^\r\n]*:(dev|latest)\b') { throw "SERVER_RELEASE_COMPOSE_UNPINNED_IMAGE" }
docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw "DOCKER_ENGINE_UNAVAILABLE" }
$states = (& docker @compose @profiles ps --format '{{.Service}} {{.State}} {{.Health}}' | Out-String).Trim()
$checks = [System.Collections.Generic.List[object]]::new()
$checks.Add([ordered]@{ name = 'compose'; status = 'READY'; detail = 'valid and immutable' })
$checks.Add([ordered]@{ name = 'docker'; status = 'READY'; detail = 'engine available' })

# Supervisor health is intentionally a separate authenticated endpoint. Do
# not print the token or any response payload containing user data.
$readinessStatus = 'NOT_CHECKED'
$readinessReason = $null
$healthToken = Read-EnvValue 'SUPERVISOR_HEALTH_TOKEN'
$apiBase = Read-EnvValue 'API_BASE_URL'
if ([string]::IsNullOrWhiteSpace($apiBase)) { $apiBase = 'http://127.0.0.1:8080' }
if ([string]::IsNullOrWhiteSpace($healthToken)) {
    $readinessStatus = 'FAILED'; $readinessReason = 'SUPERVISOR_HEALTH_TOKEN_MISSING'
} else {
    try {
        $headers = @{ 'X-WhisperX-Supervisor-Token' = $healthToken }
        $proxyEnvironment = @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','http_proxy','https_proxy','all_proxy')
        $savedProxy = @{}
        foreach ($proxyName in $proxyEnvironment) { $savedProxy[$proxyName] = [Environment]::GetEnvironmentVariable($proxyName, 'Process'); [Environment]::SetEnvironmentVariable($proxyName, '', 'Process') }
        try {
            $readiness = Invoke-RestMethod -Method Get -Uri ($apiBase.TrimEnd('/') + '/api/internal/runtime/readiness') -Headers $headers -TimeoutSec 10
        } finally {
            foreach ($proxyName in $proxyEnvironment) { [Environment]::SetEnvironmentVariable($proxyName, $savedProxy[$proxyName], 'Process') }
        }
        $reportedIdentity = [string]$readiness.buildIdentity
        if ([string]::IsNullOrWhiteSpace($reportedIdentity)) {
            $readinessStatus = 'FAILED'; $readinessReason = 'RUNTIME_IDENTITY_MISSING'
        } elseif ($reportedIdentity -ne $identity) {
            $readinessStatus = 'FAILED'; $readinessReason = 'RUNTIME_IDENTITY_MISMATCH'
        } else {
            $readinessStatus = 'READY'
        }
    } catch {
        $readinessStatus = 'FAILED'; $readinessReason = 'AUTHENTICATED_READINESS_UNAVAILABLE'
    }
}
$checks.Add([ordered]@{ name = 'authenticatedReadiness'; status = $readinessStatus; detail = $readinessReason })

if ($Mode -eq 'Release') {
    $modelRoot = Read-EnvValue 'WHISPERX_MODELS_HOST'
    if ([string]::IsNullOrWhiteSpace($modelRoot) -or -not (Test-Path -LiteralPath $modelRoot -PathType Container)) {
        $checks.Add([ordered]@{ name = 'modelSnapshot'; status = 'FAILED'; detail = 'MODEL_VOLUME_UNAVAILABLE' })
    } else {
        $modelChecks = [System.Collections.Generic.List[object]]::new()
        function Resolve-ModelPath([string]$configured, [string]$defaultRelative) {
            if ([string]::IsNullOrWhiteSpace($configured)) { return $null }
            if ([IO.Path]::IsPathRooted($configured) -and $configured -notlike '/models/*') {
                return [IO.Path]::GetFullPath($configured)
            }
            $relative = ($configured -replace '^/models[\\/]?', '') -replace '/', '\\'
            return [IO.Path]::GetFullPath((Join-Path $modelRoot $relative))
        }
        function Add-VerifiedModelCheck([string]$name, [string]$pathEnv, [string]$hashEnv, [string]$defaultRelative) {
            $configured = Read-EnvValue $pathEnv
            $expected = (Read-EnvValue $hashEnv)
            if ([string]::IsNullOrWhiteSpace($configured) -or [string]::IsNullOrWhiteSpace($expected)) {
                $modelChecks.Add([ordered]@{ name = $name; status = 'FAILED'; detail = 'MODEL_PATH_OR_SHA256_MISSING' }); return
            }
            $path = Resolve-ModelPath $configured $defaultRelative
            if ($null -eq $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                $modelChecks.Add([ordered]@{ name = $name; status = 'FAILED'; detail = 'MODEL_FILE_MISSING' }); return
            }
            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            $match = $actual -eq $expected.Trim().ToLowerInvariant()
            $modelChecks.Add([ordered]@{ name = $name; status = $(if ($match) { 'READY' } else { 'FAILED' }); detail = $(if ($match) { 'SHA256_MATCH' } else { 'SHA256_MISMATCH' }) })
        }
        Add-VerifiedModelCheck 'whisperx' 'WHISPERX_MODEL_PATH' 'WHISPERX_MODEL_SHA256' 'whisperx'
        Add-VerifiedModelCheck 'diarization' 'DIARIZATION_MODEL_PATH' 'DIARIZATION_MODEL_SHA256' 'diarization'
        $modelFiles = @(
            @{ env = 'LLM_MODEL_FILE'; hash = 'LLM_MODEL_SHA256'; default = 'qwen3-8b\Qwen3-8B-Q5_K_M.gguf' },
            @{ env = 'ASSISTANT_EMBEDDING_ONNX_PATH'; hash = 'ASSISTANT_EMBEDDING_ONNX_SHA256'; default = 'embeddings\paraphrase-multilingual-MiniLM-L12-v2.onnx' },
            @{ env = 'ASSISTANT_EMBEDDING_TOKENIZER_PATH'; hash = 'ASSISTANT_EMBEDDING_TOKENIZER_SHA256'; default = 'embeddings\tokenizer.json' }
        )
        foreach ($spec in $modelFiles) {
            $configured = Read-EnvValue $spec.env
            $llmModelDir = Read-EnvValue 'LLM_MODEL_DIR'
            if ([string]::IsNullOrWhiteSpace($llmModelDir)) { $llmModelDir = 'qwen3-8b' }
            $path = if ([string]::IsNullOrWhiteSpace($configured)) {
                Join-Path $modelRoot $spec.default
            } elseif ([IO.Path]::IsPathRooted($configured) -and $configured -notlike '/models/*') {
                [IO.Path]::GetFullPath($configured)
            } elseif ($spec.env -eq 'LLM_MODEL_FILE' -and $configured -notmatch '[/\\]') {
                Join-Path (Join-Path $modelRoot $llmModelDir) $configured
            } else {
                Join-Path $modelRoot (($configured -replace '^/models[\\/]?', '') -replace '/', '\\')
            }
            $expected = (Read-EnvValue $spec.hash)
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or [string]::IsNullOrWhiteSpace($expected)) {
                $modelChecks.Add([ordered]@{ name = $spec.env; status = 'FAILED'; detail = 'SNAPSHOT_OR_SHA256_MISSING' }); continue
            }
            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            $modelChecks.Add([ordered]@{ name = $spec.env; status = $(if ($actual -eq $expected.ToLowerInvariant()) { 'READY' } else { 'FAILED' }); detail = $(if ($actual -eq $expected.ToLowerInvariant()) { 'SHA256_MATCH' } else { 'SHA256_MISMATCH' }) })
        }
        $checks.Add([ordered]@{ name = 'modelSnapshot'; status = $(if (@($modelChecks | Where-Object status -eq 'FAILED').Count -eq 0) { 'READY' } else { 'FAILED' }); detail = @($modelChecks) })

        # File/hash checks are not sufficient for release readiness. Run the
        # same production JSON inference path inside summary-worker. The
        # probe reuses a resident llama-server when one is already present and
        # emits only timings/status, never generated text or credentials.
        try {
            $probeOutput = (& docker @compose @profiles exec -T summary-worker python3.12 -m workers.summary_worker.runtime_doctor 2>$null | Out-String).Trim()
            $probe = $probeOutput | ConvertFrom-Json
            $probeStatus = [string]$probe.status
            $checks.Add([ordered]@{ name = 'llmInferenceProbe'; status = $(if ($probeStatus -eq 'READY') { 'READY' } else { 'FAILED' }); detail = [ordered]@{ status = $probeStatus; modelLoadMs = $probe.modelLoadMs; firstTokenMs = $probe.probe.firstTokenMs; totalMs = $probe.probe.totalMs } })
        } catch {
            $checks.Add([ordered]@{ name = 'llmInferenceProbe'; status = 'FAILED'; detail = 'LLM_INFERENCE_PROBE_FAILED' })
        }
    }
}
$health = [ordered]@{
    schemaVersion = 2
    status = if (@($checks | Where-Object status -eq 'FAILED').Count -eq 0) { 'SERVER_DOCTOR_READY' } else { 'FAILED' }
    mode = $Mode
    buildIdentity = [string]$manifest.buildIdentity
    compose = 'VALID'
    docker = 'READY'
    services = $states
    checks = @($checks)
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$health | ConvertTo-Json -Depth 8
if ($health.status -ne 'SERVER_DOCTOR_READY') { exit 2 }
