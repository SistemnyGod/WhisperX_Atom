[CmdletBinding()]
param([switch]$SkipDocker)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repo

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
}

Assert-File "scripts\backup.ps1"
Assert-File "scripts\restore.ps1"
Assert-File "compose.dev.yml"
Assert-File "apps\server\WhisperX.Atom.Api\Dockerfile"
Assert-File "apps\server\WhisperX.Atom.Api\WhisperX.Atom.Api.csproj"

$migrationFiles = @(Get-ChildItem "apps\server\WhisperX.Atom.Api\Migrations" -Filter "*.sql" -File | Sort-Object Name)
if ($migrationFiles.Count -eq 0) { throw "No migrations found" }
$migrationVersions = @($migrationFiles | ForEach-Object {
    if ($_.BaseName -notmatch '^(\d{3})_') { throw "Migration has no numeric prefix: $($_.Name)" }
    [int]$Matches[1]
})
if (($migrationVersions | Select-Object -Unique).Count -ne $migrationVersions.Count) { throw "Duplicate migration version detected" }
$sortedMigrationVersions = @($migrationVersions | Sort-Object)
if (($migrationVersions -join ",") -ne ($sortedMigrationVersions -join ",")) { throw "Migration files are not lexically ordered" }

$dockerfile = Get-Content "apps\server\WhisperX.Atom.Api\Dockerfile" -Raw
if ($dockerfile -notmatch "HEALTHCHECK") { throw "API Dockerfile has no HEALTHCHECK" }
if ($dockerfile -notmatch "COPY\s+\.\s+\.") { throw "API Dockerfile does not copy project sources into the build stage" }
$projectFile = Get-Content "apps\server\WhisperX.Atom.Api\WhisperX.Atom.Api.csproj" -Raw
if ($projectFile -notmatch "Migrations") { throw "API project does not package migrations" }

if (-not $SkipDocker) {
    & docker compose -f compose.dev.yml --profile core config --quiet
    if ($LASTEXITCODE -ne 0) { throw "Compose config failed" }
}

Write-Host "Backend deployment verification passed. Migration count: $($migrationFiles.Count)" -ForegroundColor Green
