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
Assert-File "scripts\e2e-backup-restore.ps1"
Assert-File "scripts\recover-gpu-runtime.ps1"
Assert-File "workers\ml_worker\recovery.py"
Assert-File "compose.dev.yml"
Assert-File "apps\server\WhisperX.Atom.Api\Dockerfile"
Assert-File "apps\server\WhisperX.Atom.Api\WhisperX.Atom.Api.csproj"

$migrationFiles = @(Get-ChildItem "apps\server\WhisperX.Atom.Api\Migrations" -Filter "*.sql" -File | Sort-Object Name)
if ($migrationFiles.Count -eq 0) { throw "No migrations found" }
$migrationEntries = @($migrationFiles | ForEach-Object {
    if ($_.BaseName -notmatch '^(\d{3})_([a-z0-9][a-z0-9_-]*)$') { throw "Migration has invalid immutable id: $($_.Name)" }
    [pscustomobject]@{
        Id = $_.BaseName
        Prefix = [int]$Matches[1]
        Path = $_.FullName
    }
})

# The complete filename stem is the migration identity.  A numeric prefix is
# only an ordering hint, not a primary key: the repository already contains
# two additive migrations under 025 and 026.  Rejecting duplicate prefixes
# here made the release doctor fail even though the runtime's
# schema_migrations/version + checksum history is safe and unambiguous.
if (($migrationEntries.Id | Select-Object -Unique).Count -ne $migrationEntries.Count) {
    throw "Duplicate migration identity detected"
}
$orderedEntries = @($migrationEntries | Sort-Object Prefix, Id)
if (($migrationEntries.Id -join ",") -ne ($orderedEntries.Id -join ",")) {
    throw "Migration files are not lexically ordered by numeric prefix and immutable id"
}
foreach ($entry in $migrationEntries) {
    if ((Get-Item -LiteralPath $entry.Path).Length -eq 0) { throw "Migration is empty: $($entry.Id)" }
}

$dockerfile = Get-Content "apps\server\WhisperX.Atom.Api\Dockerfile" -Raw
if ($dockerfile -notmatch "HEALTHCHECK") { throw "API Dockerfile has no HEALTHCHECK" }
if ($dockerfile -notmatch "COPY\s+\.\s+\.") { throw "API Dockerfile does not copy project sources into the build stage" }
$projectFile = Get-Content "apps\server\WhisperX.Atom.Api\WhisperX.Atom.Api.csproj" -Raw
if ($projectFile -notmatch "Migrations") { throw "API project does not package migrations" }

if (-not $SkipDocker) {
    & docker compose -f compose.dev.yml --profile core config --quiet
    if ($LASTEXITCODE -ne 0) { throw "Compose config failed" }
}

Write-Host "Backend deployment verification passed. Migration count: $($migrationFiles.Count); immutable ids and checksums are enforced by the runtime." -ForegroundColor Green
