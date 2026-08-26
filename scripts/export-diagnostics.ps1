[CmdletBinding()]
param(
    [string]$RepoPath,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoPath)) { $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $RepoPath "artifacts\release\diagnostics-safe.zip" }
$stage = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-diagnostics-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    $safeRoot = Join-Path $stage "safe"
    New-Item -ItemType Directory -Force -Path $safeRoot | Out-Null
    $sources = @(
        (Join-Path $RepoPath "artifacts\release\mvp-release-audit.json"),
        (Join-Path $RepoPath "artifacts\release\mvp-release-gate.json"),
        (Join-Path $RepoPath "artifacts\release\runtime-manifest.json"),
        (Join-Path $RepoPath "artifacts\release\retention-report.json"),
        (Join-Path $RepoPath "artifacts\runtime\state.json")
    )
    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $name = [IO.Path]::GetFileName($source)
        if ($name -match "env|token|secret|password|audio|transcript|summary|content") { continue }
        Copy-Item -LiteralPath $source -Destination (Join-Path $safeRoot $name) -Force
    }
    $manifest = [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        contents = "safe runtime and release status only"
        excluded = @(".env", "secrets", "tokens", "cookies", "raw audio", "transcript content", "summary content")
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $safeRoot "diagnostics-manifest.json") -Encoding utf8
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
    if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }
    Compress-Archive -Path (Join-Path $safeRoot "*") -DestinationPath $OutputPath -CompressionLevel Optimal
    [ordered]@{ output = $OutputPath; excluded = $manifest.excluded; safe = $true } | ConvertTo-Json -Depth 8
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
