[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$DestinationDirectory = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $DestinationDirectory = Join-Path $repo "vendor\ffmpeg\win-x64"
}
$source = [IO.Path]::GetFullPath($SourceDirectory)
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "FFMPEG_SOURCE_NOT_FOUND: $source" }
if ($source -eq $destination) { throw "FFMPEG_SOURCE_EQUALS_DESTINATION" }

New-Item -ItemType Directory -Force -Path $destination | Out-Null
$files = foreach ($name in @("ffmpeg.exe", "ffprobe.exe")) {
    $input = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $input -PathType Leaf)) { throw "FFMPEG_BINARY_MISSING: $input" }
    $output = Join-Path $destination $name
    Copy-Item -LiteralPath $input -Destination $output -Force
    $item = Get-Item -LiteralPath $output
    [ordered]@{
        file = $name
        size = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

[ordered]@{
    schemaVersion = 1
    product = "ffmpeg"
    platform = "windows-x64"
    version = $Version.Trim()
    files = @($files)
    stagedAtUtc = [DateTimeOffset]::UtcNow
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination "ffmpeg-manifest.json") -Encoding utf8
Write-Host "Pinned FFmpeg payload staged at $destination" -ForegroundColor Green
