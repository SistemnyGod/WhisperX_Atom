[CmdletBinding()]
param(
    [string[]]$Project,
    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 180,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$logRoot = Join-Path $repo "artifacts\test-logs"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

if ($Project.Count -eq 0) {
    $Project = @(Get-ChildItem -Path $repo -Recurse -Filter "*Tests.csproj" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch "[\\/]obj[\\/]|[\\/]bin[\\/]" } |
        Sort-Object FullName |
        ForEach-Object { $_.FullName })
}
if ($Project.Count -eq 0) {
    Write-Host "No .NET test projects found."
    exit 0
}

foreach ($projectPath in $Project) {
    $resolved = (Resolve-Path -LiteralPath $projectPath).Path
    $name = [IO.Path]::GetFileNameWithoutExtension($resolved)
    $stdoutPath = Join-Path $logRoot "$name.stdout.log"
    $stderrPath = Join-Path $logRoot "$name.stderr.log"
    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

    # Start-Process joins ArgumentList into a command line; quote the project
    # path explicitly because the workspace path contains spaces.
    $arguments = @("test", ('"' + $resolved + '"'), "-c", $Configuration, "--verbosity", "minimal")
    $effectiveTimeout = $TimeoutSeconds
    if ($name -match 'Desktop') {
        # WinUI/pipe tests can otherwise leave a testhost alive while MSBuild
        # waits forever.  Ask VSTest for a dump and keep the outer process
        # bound tighter than the generic server/worker test budget.
        $effectiveTimeout = [Math]::Min($TimeoutSeconds, 60)
        $arguments += @("--blame-hang", "--blame-hang-timeout", "60s")
    }
    if ($NoRestore) { $arguments += "--no-restore" }
    Write-Host "Running $name (timeout ${effectiveTimeout}s)"
    $process = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repo -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
    if (-not $process.WaitForExit($effectiveTimeout * 1000)) {
        try { $process.Kill($true) } catch { }
        Write-Host (Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue)
        Write-Error "DOTNET_TEST_TIMEOUT: $name exceeded ${effectiveTimeout}s. Logs: $stdoutPath / $stderrPath"
        exit 124
    }
    $process.Refresh()
    $exitCode = [int]$process.ExitCode

    $stdout = Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue
    $stderr = Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue
    if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host $stdout }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Host $stderr }
    if ($exitCode -ne 0) {
        Write-Error "DOTNET_TEST_FAILED: $name exit=$exitCode. Logs: $stdoutPath / $stderrPath"
        exit $exitCode
    }
}

Write-Host "DOTNET_TESTS_OK=$($Project.Count)"
