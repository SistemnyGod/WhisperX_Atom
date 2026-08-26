[CmdletBinding()]
param(
    [string[]]$Project,
    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 60,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$logRoot = Join-Path $repo "artifacts\test-logs"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$tempRoot = Join-Path $repo "artifacts\test-temp\dotnet"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$nugetCache = Join-Path $repo "artifacts\nuget-packages"
New-Item -ItemType Directory -Force -Path $nugetCache | Out-Null
if (@(Get-ChildItem -LiteralPath $nugetCache -Directory -ErrorAction SilentlyContinue).Count -eq 0) {
    & (Join-Path $PSScriptRoot 'prepare-nuget-cache.ps1') -Destination $nugetCache
}

if ($Project.Count -eq 0) {
    $Project = @(Get-ChildItem -Path $repo -Recurse -Filter "*Tests.csproj" -File -ErrorAction SilentlyContinue |
        Where-Object {
            # Release worktrees are detached snapshots used for maintenance
            # and must never be included in the active checkout's test gate.
            # Otherwise the runner silently executes an older tree as well and
            # reports duplicate suites as if they were independent coverage.
            $_.FullName -notmatch "[\\/]obj[\\/]|[\\/]bin[\\/]|[\\/]\.release-worktrees[\\/]"
        } |
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
    $runTemp = Join-Path $tempRoot ("{0}-{1}" -f $name, [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $runTemp | Out-Null
    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

    $childEnvironment = @{ TEMP = $runTemp; TMP = $runTemp; NUGET_PACKAGES = $nugetCache; HTTP_PROXY = ''; HTTPS_PROXY = ''; ALL_PROXY = ''; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; MSBuildEnableWorkloadResolver = 'false' }
    if (-not $NoRestore) {
        # Restore has its own network/cache behavior and must not consume the
        # testhost hang budget.  All variables are process-scoped and restored
        # immediately, which also works on Windows PowerShell 5.1.
        $restoreEnvironment = @{}
        foreach ($entry in $childEnvironment.GetEnumerator()) {
            $restoreEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
        }
        try {
            # Treat the repository cache as a hierarchical offline feed as
            # well as the global package folder.  This avoids even probing
            # nuget.org during a local release test run.
            & dotnet restore $resolved --source $nugetCache --packages $nugetCache --verbosity minimal -p:NuGetAudit=false
            if ($LASTEXITCODE -ne 0) { throw "DOTNET_RESTORE_FAILED: $name exit=$LASTEXITCODE" }
        } finally {
            foreach ($entry in $restoreEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
        }
    }

    # Start-Process joins ArgumentList into a command line; quote the project
    # path explicitly because the workspace path contains spaces.
    $arguments = @("test", ('"' + $resolved + '"'), "-c", $Configuration, "--no-restore", "--verbosity", "minimal", "-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=true")
    $effectiveTimeout = $TimeoutSeconds
    if ($name -match 'Desktop') {
        # WinUI/pipe tests can otherwise leave a testhost alive while MSBuild
        # waits forever.  Ask VSTest for a dump and keep the outer process
        # bound tighter than the generic server/worker test budget.
        $effectiveTimeout = [Math]::Min($TimeoutSeconds, 60)
        $arguments += @("--blame-hang", "--blame-hang-timeout", "60s")
        # The installed SDK image does not contain the workload locator SDKs.
        # Desktop test projects do not need workload discovery, so disable it
        # explicitly instead of allowing an MSB4276 build failure.
        $arguments += @(
            "-p:MSBuildEnableWorkloadResolver=false",
            "-p:EnableMsixTooling=false",
            "-p:DisableMsixProjectCapabilityAddedByProject=true",
            "-p:DisableHasPackageAndPublishMenuAddedByProject=true",
            "-p:UseSharedCompilation=false",
            "-m:1",
            "-nodeReuse:false"
        )
    }
    Write-Host "Running $name (timeout ${effectiveTimeout}s)"
    try {
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            $process = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repo `
                -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru -Environment $childEnvironment
        } else {
            # Windows PowerShell 5.1 has no Start-Process -Environment. Set
            # process-scoped values only while creating the child; restore the
            # caller environment immediately afterwards.
            $savedEnvironment = @{}
            foreach ($entry in $childEnvironment.GetEnumerator()) {
                $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
                [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
            }
            try {
                $process = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repo `
                    -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
            } finally {
                foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
            }
        }
        if (-not $process.WaitForExit($effectiveTimeout * 1000)) {
            $children = @(Get-CimInstance Win32_Process -Filter ("ParentProcessId={0}" -f $process.Id) -ErrorAction SilentlyContinue |
                Select-Object ProcessId, Name, CommandLine)
            $children | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $logRoot "$name.timeout-processes.json") -Encoding utf8
            try { $process.Kill($true) } catch { }
            $process.WaitForExit(5000) | Out-Null
            Write-Host (Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue)
            Write-Host (Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue)
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
    finally {
        Remove-Item -LiteralPath $runTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "DOTNET_TESTS_OK=$($Project.Count)"
