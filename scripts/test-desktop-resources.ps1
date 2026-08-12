$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\apps\desktop\WhisperX.Atom.Desktop'
$files = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.xaml' | Where-Object { $_.FullName -notmatch '\\(bin|obj|Legacy)\\' }
if ($files.Count -eq 0) { throw 'DESKTOP_XAML_NOT_FOUND' }
foreach ($file in $files) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
    if ($text.Contains([char]0xFFFD)) { throw "XAML_ENCODING_REPLACEMENT_CHARACTER: $($file.FullName)" }
    try { [xml]$text | Out-Null } catch { throw "XAML_XML_INVALID: $($file.FullName)" }
}
$keys = @{}
foreach ($file in $files) {
    foreach ($match in [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8), 'x:Key\s*=\s*"([^"]+)"')) {
        $key = $match.Groups[1].Value
        if ($keys.ContainsKey($key)) { throw "XAML_DUPLICATE_KEY: $key ($($keys[$key]), $($file.FullName))" }
        $keys[$key] = $file.FullName
    }
}
Write-Host "desktop-resource-check passed: $($files.Count) XAML files, $($keys.Count) keyed resources"
