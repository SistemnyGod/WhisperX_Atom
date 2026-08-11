[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$desktop = Join-Path $repo "apps/desktop/WhisperX.Atom.Desktop"

function Assert-Contains([string]$Path, [string]$Pattern, [string]$Description) {
    $content = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ($content -notmatch [regex]::Escape($Pattern)) {
        throw "Missing $Description in $Path"
    }
}

foreach ($style in @(
    "PrimaryButtonStyle", "SecondaryButtonStyle", "DangerButtonStyle", "GhostButtonStyle",
    "ToolbarButtonStyle", "IconButtonStyle", "CardButtonStyle", "SearchInputStyle",
    "StatusBadgeStyle", "InspectorPanelStyle", "TableHeaderStyle", "TableCellStyle"
)) {
    Assert-Contains (Join-Path $desktop "Themes/Controls.xaml") $style "style $style"
}

foreach ($control in @(
    "PageHeader", "StatusBadge", "EmptyState", "AudioLevelMeter", "DeviceSelector", "ProcessingStepper"
)) {
    $xaml = Join-Path $desktop "Controls/$control.xaml"
    $code = Join-Path $desktop "Controls/$control.xaml.cs"
    if (-not (Test-Path -LiteralPath $xaml) -or -not (Test-Path -LiteralPath $code)) {
        throw "Shared control is incomplete: $control"
    }
}

$mapper = Join-Path $desktop "Services/UiStatusMapper.cs"
Assert-Contains $mapper "public static class UiStatusMapper" "central status mapper"
Assert-Contains $mapper "PARTIAL_READY" "partial transcript status"
Assert-Contains $mapper "UiErrorFormatter" "localized error formatter"
Assert-Contains $mapper "HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }" "localized HTTP 401 formatter"

$apiClient = Join-Path $desktop "ServerApiClient.cs"
Assert-Contains $apiClient "GetCookieHeader(BaseAddress)" "lossless session cookie persistence"
Assert-Contains $apiClient "SetCookies(BaseAddress, cookieHeader)" "lossless session cookie restoration"

$launcher = Join-Path $PSScriptRoot "launch-desktop.ps1"
Assert-Contains $launcher "Published Desktop is stale" "stale Desktop artifact detection"

$activeCode = Get-ChildItem (Join-Path $desktop "Pages"), (Join-Path $desktop "ViewModels") -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch "\\Legacy\\" } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }
if ($activeCode -match "ex\.Message|ErrorInfoBar\.Message\s*=\s*ex\.Message|ShowError\(ex\.Message") {
    throw "Active Desktop code exposes a raw exception message"
}

$mainWindow = Join-Path $desktop "MainWindow.xaml"
foreach ($route in @("home", "recording", "meetings", "transcripts", "settings")) {
    Assert-Contains $mainWindow ('Tag="' + $route + '"') "navigation route $route"
}
Assert-Contains $mainWindow "AutomationProperties.Name" "navigation accessibility names"

$recording = Join-Path $desktop "Pages/RecordingPage.xaml"
foreach ($binding in @("CanStart", "CanPause", "CanMark", "CanStop", "CanSelectDevices", "MicrophoneLevel", "SystemAudioLevel")) {
    Assert-Contains $recording "{Binding $binding}" "recording binding $binding"
}
if ((Get-Content -LiteralPath $recording -Raw -Encoding UTF8) -match "DeviceId") {
    throw "Recording page exposes a technical DeviceId"
}

$meetings = Join-Path $desktop "Pages/MeetingsPage.xaml"
foreach ($binding in @("FilteredMeetings", "StatusFilters", "TranscriptSegments", "Jobs", "Speakers", "Tasks", "Media")) {
    Assert-Contains $meetings "{Binding $binding}" "meetings binding $binding"
}
Assert-Contains $meetings "OpenWorkspaceButton" "explicit meeting workspace action"

& (Join-Path $PSScriptRoot "check-desktop-ui-encoding.ps1")
Write-Output "Desktop UI contract check passed."
