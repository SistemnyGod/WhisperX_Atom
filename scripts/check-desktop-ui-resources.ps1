$ErrorActionPreference = 'Stop'

$projectRoot = Join-Path $PSScriptRoot '..\apps\desktop\WhisperX.Atom.Desktop'
$runtimeFiles = @(Get-ChildItem -Path $projectRoot -Recurse -File | Where-Object {
    $_.Extension -eq '.xaml' -and $_.FullName -notmatch '\\(Legacy|obj|bin)\\'
})

$resourceKeys = [System.Collections.Generic.HashSet[string]]::new()
foreach ($file in $runtimeFiles) {
    $content = Get-Content -Raw -Encoding utf8 $file.FullName
    foreach ($match in [regex]::Matches($content, 'x:Key="([^"]+)"')) {
        [void]$resourceKeys.Add($match.Groups[1].Value)
    }
}

$missing = [System.Collections.Generic.HashSet[string]]::new()
foreach ($file in $runtimeFiles) {
    $content = Get-Content -Raw -Encoding utf8 $file.FullName
    foreach ($match in [regex]::Matches($content, 'StaticResource\s+([A-Za-z0-9_.-]+)')) {
        $key = $match.Groups[1].Value
        if (-not $resourceKeys.Contains($key)) {
            [void]$missing.Add($key)
        }
    }
}

if ($missing.Count -gt 0) {
    throw "Missing WinUI StaticResource keys: $([string]::Join(', ', ($missing | Sort-Object)))"
}

"Desktop runtime StaticResource validation passed: $($runtimeFiles.Count) XAML files checked."
