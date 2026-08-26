$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..\apps\desktop\WhisperX.Atom.Desktop'
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
$markers = @(
    [string][char]0x00C3,
    [string][char]0x00C2,
    [string][char]0x00E2,
    ([string][char]0x0432 + [char]0x0402),
    ([string][char]0x0412 + [char]0x00B7),
    ([string][char]0x0420 + [char]0x041F),
    ([string][char]0x0420 + [char]0x00B5),
    ([string][char]0x0420 + [char]0x0455),
    ([string][char]0x0420 + [char]0x0451)
)
$files = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    $_.Extension -in '.xaml', '.cs' -and $_.FullName -notmatch '[\\/]Legacy[\\/]'
}
$errors = [System.Collections.Generic.List[string]]::new()

foreach ($file in $files) {
    try {
        $text = $utf8.GetString([System.IO.File]::ReadAllBytes($file.FullName))
    }
    catch {
        $errors.Add("Invalid UTF-8: $($file.FullName)")
        continue
    }

    foreach ($marker in $markers) {
        if ($text.IndexOf($marker, [System.StringComparison]::Ordinal) -ge 0) {
            $errors.Add("Possible mojibake '$marker': $($file.FullName)")
            break
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "Desktop runtime text encoding is valid: $($files.Count) files checked."
