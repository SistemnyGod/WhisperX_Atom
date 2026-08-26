[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$OutputPath = '',
    [string[]]$KeepIdentity = @('64c19f9686a0','41b23dc'),
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path ((Resolve-Path (Join-Path $PSScriptRoot '..')).Path) 'artifacts\docker-cleanup-preview.json' }

function Invoke-Docker([string[]]$Arguments) {
    $text = & docker @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "DOCKER_UNAVAILABLE: $($text.Trim())" }
    return $text
}

$containerLines = @(Invoke-Docker @('ps','-a','--no-trunc','--format','{{json .}}') -split "`r?`n" | Where-Object { $_.Trim() })
$containers = @($containerLines | ForEach-Object { $_ | ConvertFrom-Json })
$usedIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$protectedIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($container in $containers) {
    $inspect = Invoke-Docker @('inspect','--format','{{.Image}}',[string]$container.ID)
    $imageId = $inspect.Trim()
    if ($imageId) { [void]$usedIds.Add($imageId) }
    if ([string]$container.Names -match '(?i)(patrol360|inventory|atom_obhod|dsk)') { if ($imageId) { [void]$protectedIds.Add($imageId) } }
}
$imageLines = @(Invoke-Docker @('image','ls','--no-trunc','--format','{{json .}}') -split "`r?`n" | Where-Object { $_.Trim() })
$images = @($imageLines | ForEach-Object { $_ | ConvertFrom-Json })
$candidates = @($images | Where-Object {
    $image = $_
    $keep = $false
    foreach ($identity in $KeepIdentity) { if (([string]$image.Repository + ':' + [string]$image.Tag) -match [regex]::Escape([string]$identity)) { $keep = $true; break } }
    [string]$image.Repository -like 'whisperx-atom-*' -and
        [string]$image.ID -notin $usedIds -and
        [string]$image.ID -notin $protectedIds -and
        -not $keep
} | ForEach-Object { [ordered]@{ id = [string]$_.ID; repository = [string]$_.Repository; tag = [string]$_.Tag; size = [string]$_.Size; reason = 'unused WhisperX image; no container references image ID' } })
$report = [ordered]@{ mode = if ($Apply) { 'apply' } else { 'preview' }; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); keepIdentity = @($KeepIdentity); containerCount = $containers.Count; imageCount = $images.Count; protectedPatrol360ImageIds = @($protectedIds); candidates = $candidates; safety = [ordered]@{ volumesChanged = $false; patrol360Changed = $false; globalPrune = $false; buildCacheChanged = $false } }
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$report | ConvertTo-Json -Depth 10
if (-not $Apply) { exit 0 }
foreach ($candidate in $candidates) {
    if ($PSCmdlet.ShouldProcess([string]$candidate.id, "Remove unused WhisperX image $($candidate.repository):$($candidate.tag)")) {
        Invoke-Docker @('image','rm',[string]$candidate.id) | Out-Null
    }
}
