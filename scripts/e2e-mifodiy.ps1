[CmdletBinding()]
param(
    [ValidateSet("Contract", "Installed")][string]$Mode = "Contract",
    [string]$BaseUrl,
    [string]$Username,
    [string]$Password,
    [string]$MeetingId,
    [string]$OtherMeetingId,
    [string]$VoiceHostPath,
    [string]$DesktopPath,
    [string]$OutputPath,
    [ValidateRange(1, 500)][int]$VoiceTarget = 50,
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 240,
    [switch]$RunMicrophone,
    [switch]$RunBroker,
    [switch]$RestartDesktop
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repo "artifacts\acceptance\mifodiy-v1.json"
}
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = if ($env:WHISPERX_DEV_API_URL) { $env:WHISPERX_DEV_API_URL } else { "http://127.0.0.1:8080" }
}
$BaseUrl = $BaseUrl.TrimEnd("/")
if ([string]::IsNullOrWhiteSpace($Username)) {
    $Username = if ($env:BOOTSTRAP_ADMIN_USERNAME) { $env:BOOTSTRAP_ADMIN_USERNAME } else { "admin" }
}
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = $env:BOOTSTRAP_ADMIN_PASSWORD }
if ([string]::IsNullOrWhiteSpace($VoiceHostPath)) {
    $VoiceHostPath = Join-Path ${env:ProgramFiles} "WhisperX Atom\VoiceHost\WhisperX.Atom.Voice.Host.exe"
}
if ([string]::IsNullOrWhiteSpace($DesktopPath)) {
    $DesktopPath = Join-Path ${env:ProgramFiles} "WhisperX Atom\Desktop\WhisperX.Atom.Desktop.exe"
}

$terminalStatuses = @("READY", "ANSWERED", "ANSWERED_WITH_WARNING", "FAILED", "NEEDS_REVIEW", "NO_EVIDENCE", "LOW_TRANSCRIPT_QUALITY", "GROUNDING_REJECTED", "LLM_UNAVAILABLE")
$safeErrorSpeech = @(
    "В стенограмме не найден подтверждённый ответ.",
    "Не удалось подтвердить ответ по стенограмме.",
    "Сначала проверьте качество стенограммы."
)
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$temporaryFiles = [System.Collections.Generic.List[string]]::new()
$assistantResults = [System.Collections.Generic.List[object]]::new()
$errors = [System.Collections.Generic.List[string]]::new()

function Write-Result([object]$value) {
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $value | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Output (ConvertTo-Json ([ordered]@{ output = $OutputPath; status = $value.status }) -Compress)
}

function Get-HttpStatus([object]$exception) {
    try { return [int]$exception.Exception.Response.StatusCode.value__ } catch { return 0 }
}

function Invoke-Api([string]$Method, [string]$Path, [object]$Body = $null, [switch]$AllowError) {
    $parameters = @{
        Uri = "$BaseUrl$Path"
        Method = $Method
        WebSession = $session
        ErrorAction = "Stop"
        TimeoutSec = 20
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 10 -Compress
    }
    try {
        $response = Invoke-WebRequest @parameters
        $content = if ([string]::IsNullOrWhiteSpace($response.Content)) { $null } else { $response.Content | ConvertFrom-Json }
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = $content }
    }
    catch {
        $code = Get-HttpStatus $_
        if (-not $AllowError) { throw }
        return [pscustomobject]@{ StatusCode = $code; Body = $null; Error = $_.Exception.Message }
    }
}

function Login([string]$LoginName, [string]$LoginPassword) {
    if ([string]::IsNullOrWhiteSpace($LoginPassword)) { throw "MIFODIY_AUTH_PASSWORD_REQUIRED" }
    $result = Invoke-Api POST "/api/auth/login" @{ username = $LoginName; password = $LoginPassword }
    if ($result.StatusCode -ne 200) { throw "MIFODIY_AUTH_REJECTED:$($result.StatusCode)" }
    return $result.Body
}

function Get-InstalledIdentity([string]$Path, [string]$ExpectedRoot) {
    $validPath = $false
    $identity = $null
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $fullPath = [IO.Path]::GetFullPath($Path)
        $root = ([IO.Path]::GetFullPath($ExpectedRoot)).TrimEnd('\') + '\'
        $validPath = $fullPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
        if ($validPath) { $identity = [Diagnostics.FileVersionInfo]::GetVersionInfo($fullPath).ProductVersion }
    }
    $identityValid = $validPath -and -not [string]::IsNullOrWhiteSpace($identity) -and
        $identity -notmatch "(?i)(^|[+.-])(dev|dirty)([+.-]|$)"
    return [pscustomobject]@{ path = if ($validPath) { [IO.Path]::GetFullPath($Path) } else { $null }; buildIdentity = $identity; pathValid = $validPath; identityValid = $identityValid }
}

function Get-VoiceGate([string]$Path) {
    $expectedRoot = Join-Path ${env:ProgramFiles} "WhisperX Atom\VoiceHost"
    $identity = Get-InstalledIdentity $Path $expectedRoot
    $result = [ordered]@{
        requested = $true
        executed = $false
        target = $VoiceTarget
        detections = $null
        aliases = [ordered]@{ myfodiy = 0; mefodiy = 0; atom = 0 }
        falseActivations = $null
        audioQueueDrops = $null
        effectiveMicrophone = $null
        errorCode = $null
        build = $identity
    }
    if (-not $identity.pathValid) { $result.errorCode = "VOICE_HOST_BUILD_MISMATCH"; return [pscustomobject]$result }
    if (-not $RunMicrophone) { $result.requested = $false; $result.errorCode = "VOICE_MICROPHONE_GATE_NOT_RUN"; return [pscustomobject]$result }

    $tempLog = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-mifodiy-" + [guid]::NewGuid().ToString("N") + ".log")
    $tempErr = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-mifodiy-" + [guid]::NewGuid().ToString("N") + ".err")
    $temporaryFiles.Add($tempLog); $temporaryFiles.Add($tempErr)
    $target = [Math]::Max(50, [Math]::Clamp($VoiceTarget, 1, 500))
    $result.target = $target
    $args = "--mic-acceptance $target"
    try {
        $process = Start-Process -FilePath $Path -ArgumentList $args -NoNewWindow -PassThru -RedirectStandardOutput $tempLog -RedirectStandardError $tempErr
        if (-not $process.WaitForExit([Math]::Min($TimeoutSeconds, 900) * 1000)) {
            try { $process.CloseMainWindow() | Out-Null } catch { }
            $result.errorCode = "VOICE_ACCEPTANCE_TIMEOUT"
            return [pscustomobject]$result
        }
        $jsonLine = @(Get-Content -LiteralPath $tempLog -ErrorAction SilentlyContinue) |
            Where-Object { $_ -match '"mode"\s*:\s*"mic-acceptance-dry-run"' } | Select-Object -Last 1
        if (-not $jsonLine) { $result.errorCode = "VOICE_ACCEPTANCE_RESULT_MISSING"; return [pscustomobject]$result }
        $parsed = $jsonLine | ConvertFrom-Json
        $result.executed = $true
        $result.detections = [int]$parsed.detections
        $result.falseActivations = [int]$parsed.falseActivations
        $result.audioQueueDrops = [int64]$parsed.audioQueueDrops
        $result.effectiveMicrophone = [ordered]@{ deviceId = [string]$parsed.microphoneDeviceId; name = [string]$parsed.microphoneName }
        foreach ($alias in @("myfodiy", "mefodiy", "atom")) {
            $property = $parsed.aliases.PSObject.Properties[$alias]
            if ($property) { $result.aliases[$alias] = [int]$property.Value }
        }
        $required = [Math]::Ceiling($target * 0.90)
        $requiredPrimaryAlias = [Math]::Ceiling($target * 0.54)
        $acceptedAliases = [int]$result.aliases.myfodiy + [int]$result.aliases.mefodiy + [int]$result.aliases.atom
        if ([int]$result.detections -lt $required -or $acceptedAliases -lt $required -or [int]$result.aliases.myfodiy -lt $requiredPrimaryAlias -or $result.falseActivations -ne 0 -or $result.audioQueueDrops -ne 0) {
            $result.errorCode = if ([int]$result.aliases.myfodiy -lt $requiredPrimaryAlias) { "VOICE_ALIAS_COVERAGE_NOT_READY" } else { "VOICE_GATE_NOT_READY" }
        }
    }
    catch { $result.errorCode = "VOICE_ACCEPTANCE_FAILED" }
    return [pscustomobject]$result
}

function Get-VoiceCommandGate([string]$Path) {
    $expectedRoot = Join-Path ${env:ProgramFiles} "WhisperX Atom\VoiceHost"
    $identity = Get-InstalledIdentity $Path $expectedRoot
    $result = [ordered]@{
        requested = $true
        executed = $false
        passed = $false
        recorderInvocations = 0
        brokerInvocations = 0
        cases = @()
        aliases = @()
        build = $identity
        errorCode = $null
    }
    if (-not $identity.pathValid) { $result.errorCode = "VOICE_HOST_BUILD_MISMATCH"; return [pscustomobject]$result }

    $tempLog = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-mifodiy-command-" + [guid]::NewGuid().ToString("N") + ".log")
    $tempErr = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-mifodiy-command-" + [guid]::NewGuid().ToString("N") + ".err")
    $temporaryFiles.Add($tempLog); $temporaryFiles.Add($tempErr)
    try {
        $process = Start-Process -FilePath $Path -ArgumentList "--command-acceptance" -NoNewWindow -PassThru -RedirectStandardOutput $tempLog -RedirectStandardError $tempErr
        if (-not $process.WaitForExit(30000)) {
            try { $process.CloseMainWindow() | Out-Null } catch { }
            $result.errorCode = "VOICE_COMMAND_GATE_TIMEOUT"
            return [pscustomobject]$result
        }
        $jsonLine = @(Get-Content -LiteralPath $tempLog -ErrorAction SilentlyContinue) |
            Where-Object { $_ -match '"mode"\s*:\s*"command-acceptance"' } | Select-Object -Last 1
        if (-not $jsonLine) { $result.errorCode = "VOICE_COMMAND_GATE_RESULT_MISSING"; return [pscustomobject]$result }
        $parsed = $jsonLine | ConvertFrom-Json
        $result.executed = $true
        $result.passed = [bool]$parsed.passed
        $result.recorderInvocations = [int]$parsed.recorderInvocations
        $result.brokerInvocations = [int]$parsed.brokerInvocations
        $result.cases = @($parsed.cases | ForEach-Object {
            [ordered]@{
                id = [string]$_.id
                expectedIntent = [string]$_.expectedIntent
                actualIntent = [string]$_.actualIntent
                hasWakeWord = [bool]$_.hasWakeWord
                parameterPresent = [bool]$_.parameterPresent
                accepted = [bool]$_.accepted
            }
        })
        $result.aliases = @($parsed.aliases | ForEach-Object {
            [ordered]@{ intent = [string]$_.intent; accepted = [bool]$_.accepted }
        })
        if (-not $result.passed -or $result.recorderInvocations -ne 0 -or $result.brokerInvocations -ne 0) {
            $result.errorCode = "VOICE_COMMAND_GATE_NOT_READY"
            $result.passed = $false
        }
    }
    catch { $result.errorCode = "VOICE_COMMAND_GATE_FAILED" }
    return [pscustomobject]$result
}

function Get-VoiceSnapshot {
    $pipe = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'WhisperXAtomVoiceHost', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect(1500)
        $reader = [IO.StreamReader]::new($pipe)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine((@{ command = 'STATUS'; payload = @{} } | ConvertTo-Json -Compress))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { return [ordered]@{ observed = $false; errorCode = 'VOICE_STATUS_EMPTY' } }
        $response = $line | ConvertFrom-Json
        if (-not $response.ok -or $null -eq $response.data) { return [ordered]@{ observed = $false; errorCode = [string]$response.error } }
        $data = $response.data
        return [ordered]@{
            observed = $true
            buildIdentity = [string]$data.buildIdentity
            state = [string]$data.state
            requestedVoice = [string]$data.requestedVoiceName
            effectiveVoice = [string]$data.effectiveVoiceName
            culture = [string]$data.effectiveVoiceCulture
            voiceFallbackUsed = [bool]$data.voiceFallbackUsed
            requestedMicrophoneDeviceId = [string]$data.requestedMicrophoneDeviceId
            effectiveMicrophoneName = [string]$data.effectiveMicrophoneName
            speechQueueDrops = [int64]$data.speechQueueDrops
            audioQueueDrops = [int64]$data.audioQueueDrops
        }
    }
    catch { return [ordered]@{ observed = $false; errorCode = 'VOICE_STATUS_UNAVAILABLE' } }
    finally { if ($pipe) { $pipe.Dispose() } }
}

function Invoke-VoiceHostText([string]$Text, [string]$Path) {
    $pipe = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'WhisperXAtomVoiceHost', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect(3000)
        $reader = [IO.StreamReader]::new($pipe)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $request = [ordered]@{
            command = 'TEXT'
            payload = [ordered]@{ text = $Text }
        }
        $writer.WriteLine(($request | ConvertTo-Json -Depth 8 -Compress))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { return [ordered]@{ ok = $false; errorCode = 'VOICE_BROKER_EMPTY_RESPONSE' } }
        $response = $line | ConvertFrom-Json
        $data = $response.data
        return [ordered]@{
            ok = [bool]$response.ok
            errorCode = [string]$response.error
            queryId = if ($data) { [string]$data.queryId } else { $null }
            acceptedForPlayback = if ($data) { [bool]$data.acceptedForPlayback } else { $false }
            playbackState = if ($data) { [string]$data.playbackState } else { $null }
            answerStatus = if ($data) { [string]$data.answerStatus } else { $null }
            resolvedMode = $null
        }
    }
    catch {
        return [ordered]@{ ok = $false; errorCode = 'VOICE_BROKER_UNAVAILABLE' }
    }
    finally { if ($pipe) { $pipe.Dispose() } }
}

function Wait-VoiceHostListening {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $snapshot = Get-VoiceSnapshot
        if ($snapshot.observed -and [string]$snapshot.state -eq 'Listening') { return $true }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Select-Meeting([string]$requestedId) {
    if (-not [string]::IsNullOrWhiteSpace($requestedId)) {
        $meeting = Invoke-Api GET "/api/meetings/$requestedId"
        if ($meeting.StatusCode -ne 200) { throw "MIFODIY_MEETING_NOT_ACCESSIBLE" }
        return $meeting.Body
    }
    $meetingsResponse = Invoke-Api GET "/api/meetings?limit=200&offset=0"
    foreach ($candidate in @($meetingsResponse.Body)) {
        try {
            $transcript = Invoke-Api GET "/api/meetings/$($candidate.id)/transcript" -AllowError
            if ($transcript.StatusCode -eq 200 -and $transcript.Body.status -in @("READY", "PARTIAL_READY")) { return $candidate }
        } catch { }
    }
    throw "MIFODIY_READY_MEETING_NOT_FOUND"
}

function Get-QueryOutcome([string]$CaseId, [string]$Question, [string]$RequestedMode, [string]$ActiveMeetingId) {
    $startedAt = [DateTimeOffset]::UtcNow
    $commandId = [guid]::NewGuid().ToString("N")
    $traceId = [guid]::NewGuid().ToString("N")
    $request = Invoke-Api POST "/api/assistant/requests" @{
        question = $Question
        requestedMode = $RequestedMode
        activeMeetingId = if ($ActiveMeetingId) { $ActiveMeetingId } else { $null }
        source = "VOICE"
        commandId = $commandId
        traceId = $traceId
    }
    if ($request.StatusCode -ne 202 -or $null -eq $request.Body) { throw "MIFODIY_ASSISTANT_REQUEST_REJECTED:$CaseId" }
    $queryId = [string]$request.Body.queryId
    $conversationId = [string]$request.Body.conversationId
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $query = $null
    do {
        $poll = Invoke-Api GET "/api/assistant/queries/$queryId" -AllowError
        if ($poll.StatusCode -eq 200) { $query = $poll.Body }
        if ($query -and $query.status -in $terminalStatuses) { break }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    if (-not $query -or $query.status -notin $terminalStatuses) { throw "MIFODIY_ASSISTANT_TIMEOUT:$CaseId" }

    $evidence = @()
    if ($query.PSObject.Properties.Name -contains "evidence" -and $null -ne $query.evidence) { $evidence = @($query.evidence) }
    $evidenceMeetingIds = @($evidence | ForEach-Object { if ($_.meetingId) { [string]$_.meetingId } elseif ($_.MeetingId) { [string]$_.MeetingId } } | Where-Object { $_ } | Select-Object -Unique)
    $status = [string]$query.status
    $safeNoFact = $true
    if ($status -in @("NO_EVIDENCE", "GROUNDING_REJECTED", "LOW_TRANSCRIPT_QUALITY")) {
        $speech = [string]$query.voiceAnswer
        if (-not [string]::IsNullOrWhiteSpace($speech) -and $speech -notin $safeErrorSpeech) { $safeNoFact = $false }
    }
    $outcome = [pscustomobject]@{
        case = $CaseId
        queryId = $queryId
        conversationId = $conversationId
        requestedMode = $RequestedMode
        resolvedMode = [string]$query.assistantMode
        meetingId = [string]$query.meetingId
        status = $status
        errorCode = [string]$query.errorCode
        groundingStatus = [string]$query.groundingStatus
        elapsedMs = [int](([DateTimeOffset]::UtcNow - $startedAt).TotalMilliseconds)
        evidenceCount = $evidence.Count
        evidenceMeetingIds = $evidenceMeetingIds
        safeNoConfirmedFact = $safeNoFact
        commandId = $commandId
        traceId = $traceId
    }
    $assistantResults.Add($outcome)
    return $outcome
}

function Get-QueryOutcomeById([string]$CaseId, [string]$QueryId, [string]$ExpectedMeetingId) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $query = $null
    do {
        $poll = Invoke-Api GET "/api/assistant/queries/$QueryId" -AllowError
        if ($poll.StatusCode -eq 200) { $query = $poll.Body }
        if ($query -and $query.status -in $terminalStatuses) { break }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    if (-not $query -or $query.status -notin $terminalStatuses) {
        return [ordered]@{ case = $CaseId; queryId = $QueryId; status = 'TIMEOUT'; meetingId = $null; evidenceCount = 0; evidenceMeetingIds = @(); safeNoConfirmedFact = $false }
    }
    $evidence = if ($query.PSObject.Properties.Name -contains 'evidence' -and $null -ne $query.evidence) { @($query.evidence) } else { @() }
    $evidenceMeetingIds = @($evidence | ForEach-Object { if ($_.meetingId) { [string]$_.meetingId } elseif ($_.MeetingId) { [string]$_.MeetingId } } | Where-Object { $_ } | Select-Object -Unique)
    $status = [string]$query.status
    $safeNoFact = $true
    if ($status -in @('NO_EVIDENCE', 'GROUNDING_REJECTED', 'LOW_TRANSCRIPT_QUALITY')) {
        $speech = [string]$query.voiceAnswer
        if (-not [string]::IsNullOrWhiteSpace($speech) -and $speech -notin $safeErrorSpeech) { $safeNoFact = $false }
    }
    return [ordered]@{
        case = $CaseId
        queryId = $QueryId
        requestedMode = 'CURRENT_MEETING'
        resolvedMode = [string]$query.assistantMode
        meetingId = [string]$query.meetingId
        expectedMeetingId = $ExpectedMeetingId
        status = $status
        errorCode = [string]$query.errorCode
        groundingStatus = [string]$query.groundingStatus
        evidenceCount = $evidence.Count
        evidenceMeetingIds = $evidenceMeetingIds
        safeNoConfirmedFact = $safeNoFact
    }
}

function Get-VoiceBrokerGate([string]$Path, [string]$ExpectedMeetingId) {
    $result = [ordered]@{
        requested = [bool]$RunBroker
        executed = $false
        passed = $false
        cases = @()
        errorCode = $null
        build = (Get-InstalledIdentity $Path (Join-Path ${env:ProgramFiles} 'WhisperX Atom\VoiceHost'))
    }
    if (-not $RunBroker) { $result.errorCode = 'VOICE_BROKER_GATE_NOT_REQUESTED'; return [pscustomobject]$result }
    if (-not $result.build.pathValid -or -not $result.build.identityValid) { $result.errorCode = 'VOICE_HOST_BUILD_MISMATCH'; return [pscustomobject]$result }
    $questions = @(
        @{ id = 'question-repair'; text = 'Мифодий, кто отвечал за ремонт?' },
        @{ id = 'question-deadline'; text = 'Мифодий, какой срок назвали?' },
        @{ id = 'question-pump'; text = 'Мифодий, что решили по насосу?' }
    )
    $cases = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $questions) {
        if (-not (Wait-VoiceHostListening)) {
            $cases.Add([pscustomobject]@{ id = $item.id; requestAccepted = $false; queryId = $null; acceptedForPlayback = $false; playbackState = $null; answerStatus = $null; errorCode = 'VOICE_HOST_BUSY'; finalStatus = $null; resolvedMode = $null; meetingId = $null; evidenceCount = 0; evidenceMeetingIds = @(); safeNoConfirmedFact = $false })
            continue
        }
        $response = Invoke-VoiceHostText $item.text $Path
        $outcome = if ($response.queryId) { Get-QueryOutcomeById $item.id ([string]$response.queryId) $ExpectedMeetingId } else { $null }
        $case = [ordered]@{
            id = $item.id
            requestAccepted = [bool]$response.ok
            queryId = [string]$response.queryId
            acceptedForPlayback = [bool]$response.acceptedForPlayback
            playbackState = [string]$response.playbackState
            answerStatus = [string]$response.answerStatus
            errorCode = [string]$response.errorCode
            finalStatus = if ($outcome) { [string]$outcome.status } else { $null }
            resolvedMode = if ($outcome) { [string]$outcome.resolvedMode } else { $null }
            meetingId = if ($outcome) { [string]$outcome.meetingId } else { $null }
            evidenceCount = if ($outcome) { [int]$outcome.evidenceCount } else { 0 }
            evidenceMeetingIds = if ($outcome) { @($outcome.evidenceMeetingIds) } else { @() }
            safeNoConfirmedFact = if ($outcome) { [bool]$outcome.safeNoConfirmedFact } else { $false }
        }
        $cases.Add([pscustomobject]$case)
    }
    $result.executed = $true
    $result.cases = @($cases)
    $result.passed = $cases.Count -eq 3 -and ($cases | Where-Object {
        -not $_.requestAccepted -or [string]::IsNullOrWhiteSpace($_.queryId) -or -not $_.acceptedForPlayback -or
        $_.finalStatus -notin @('READY', 'ANSWERED', 'ANSWERED_WITH_WARNING', 'NO_EVIDENCE', 'GROUNDING_REJECTED', 'NEEDS_REVIEW') -or
        $_.resolvedMode -ne 'CURRENT_MEETING' -or $_.meetingId -ne $ExpectedMeetingId -or
        (@($_.evidenceMeetingIds | Where-Object { $_ -ne $ExpectedMeetingId }).Count -gt 0)
    }).Count -eq 0
    if (-not $result.passed) { $result.errorCode = 'VOICE_BROKER_GATE_NOT_READY' }
    return [pscustomobject]$result
}

function Invoke-SafeDesktopRestart([string]$Path) {
    $expectedRoot = Join-Path ${env:ProgramFiles} "WhisperX Atom\Desktop"
    $identity = Get-InstalledIdentity $Path $expectedRoot
    if (-not $identity.pathValid) { return [pscustomobject]@{ executed = $false; passed = $false; errorCode = "DESKTOP_BUILD_MISMATCH" } }
    $expectedRootPrefix = ([IO.Path]::GetFullPath($expectedRoot)).TrimEnd('\') + '\'
    $processes = @(Get-Process -Name "WhisperX.Atom.Desktop" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path)).StartsWith($expectedRootPrefix, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($process in $processes) {
        if ($process.MainWindowHandle -eq 0 -or -not $process.CloseMainWindow()) { return [pscustomobject]@{ executed = $false; passed = $false; errorCode = "DESKTOP_CLOSE_NOT_CONFIRMED" } }
    }
    $deadline = (Get-Date).AddSeconds(20)
    do { if (-not (Get-Process -Name "WhisperX.Atom.Desktop" -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $deadline)
    if (Get-Process -Name "WhisperX.Atom.Desktop" -ErrorAction SilentlyContinue) { return [pscustomobject]@{ executed = $false; passed = $false; errorCode = "DESKTOP_CLOSE_TIMEOUT" } }
    Start-Process -FilePath $Path -WorkingDirectory (Split-Path -Parent $Path) | Out-Null
    return [pscustomobject]@{ executed = $true; passed = $true; errorCode = $null }
}

$voiceGate = [pscustomobject]@{ requested = $false; executed = $false; errorCode = "NOT_RUN"; build = $null; snapshot = $null }
$voiceCommandGate = [pscustomobject]@{ requested = $false; executed = $false; passed = $false; errorCode = "NOT_RUN"; cases = @(); aliases = @(); build = $null }
$voiceBrokerGate = [pscustomobject]@{ requested = [bool]$RunBroker; executed = $false; passed = $false; errorCode = "NOT_RUN"; cases = @(); build = $null }
$installed = [ordered]@{ requested = $Mode -eq "Installed"; desktop = $null; voiceHost = $null; recorderHost = $null; sameBuildIdentity = $null }
$logoutCheck = [ordered]@{ executed = $false; unauthenticatedAfterLogout = $null; secondUserProvided = $false; contextCleared = $null }
$duplicateDelivery = [ordered]@{ executed = $false; passed = $false; errorCode = "DESKTOP_RESTART_NOT_REQUESTED"; tombstoneObserved = $false }
$assistantPassed = $false
$meeting = $null

try {
    if ($Mode -eq "Installed") {
        $installed.desktop = Get-InstalledIdentity $DesktopPath (Join-Path ${env:ProgramFiles} "WhisperX Atom\Desktop")
        $installed.voiceHost = Get-InstalledIdentity $VoiceHostPath (Join-Path ${env:ProgramFiles} "WhisperX Atom\VoiceHost")
        $recorderPath = Join-Path ${env:ProgramFiles} "WhisperX Atom\RecorderHost\WhisperX.Atom.Recorder.Host.exe"
        $installed.recorderHost = Get-InstalledIdentity $recorderPath (Join-Path ${env:ProgramFiles} "WhisperX Atom\RecorderHost")
        $ids = @($installed.desktop.buildIdentity, $installed.voiceHost.buildIdentity, $installed.recorderHost.buildIdentity) | Where-Object { $_ }
        $installed.sameBuildIdentity = $ids.Count -eq 3 -and (@($ids | Select-Object -Unique).Count -eq 1) -and ($ids[0] -notmatch "(?i)(dev|dirty)")
        $voiceGate = Get-VoiceGate $VoiceHostPath
        $voiceCommandGate = Get-VoiceCommandGate $VoiceHostPath
        if (-not $voiceCommandGate.passed) { $errors.Add([string]($voiceCommandGate.errorCode ?? "VOICE_COMMAND_GATE_NOT_READY")) }
        $voiceGate.snapshot = Get-VoiceSnapshot
        if (-not $voiceGate.snapshot.observed) { $errors.Add([string]$voiceGate.snapshot.errorCode) }
        elseif ([string]$voiceGate.snapshot.effectiveVoice -notmatch "(?i)Irina" -or [string]$voiceGate.snapshot.culture -notmatch "(?i)^ru") { $errors.Add("VOICE_RUSSIAN_VOICE_NOT_CONFIRMED") }
        if (-not $installed.sameBuildIdentity) { $errors.Add("VOICE_HOST_BUILD_MISMATCH") }
    }

    if ($Mode -eq "Installed" -or -not [string]::IsNullOrWhiteSpace($MeetingId)) {
        if ([string]::IsNullOrWhiteSpace($Password)) { throw "MIFODIY_AUTH_PASSWORD_REQUIRED" }
        Login $Username $Password | Out-Null
        $meeting = Select-Meeting $MeetingId
        $effectiveMeetingId = [string]$meeting.id
        if ([string]::IsNullOrWhiteSpace($OtherMeetingId)) {
            try {
                $allMeetings = Invoke-Api GET "/api/meetings?limit=200&offset=0"
                $other = @($allMeetings.Body) | Where-Object { [string]$_.id -ne $effectiveMeetingId } | Select-Object -First 1
                if ($other) { $OtherMeetingId = [string]$other.id }
            } catch { }
        }

        $known = Get-QueryOutcome "known-answer" "Какой подтверждённый итог или решение обсуждалось на этом совещании?" "CURRENT_MEETING" $effectiveMeetingId
        if ($Mode -eq "Installed" -and $RunBroker) {
            $voiceBrokerGate = Get-VoiceBrokerGate $VoiceHostPath $effectiveMeetingId
            if (-not $voiceBrokerGate.passed) { $errors.Add([string]($voiceBrokerGate.errorCode ?? "VOICE_BROKER_GATE_NOT_READY")) }
        }
        $repairQuestion = Get-QueryOutcome "question-repair" "Кто отвечал за ремонт?" "CURRENT_MEETING" $effectiveMeetingId
        $deadlineQuestion = Get-QueryOutcome "question-deadline" "Какой срок назвали?" "CURRENT_MEETING" $effectiveMeetingId
        $pumpQuestion = Get-QueryOutcome "question-pump" "Что решили по насосу?" "CURRENT_MEETING" $effectiveMeetingId
        $noEvidence = Get-QueryOutcome "no-evidence" "Назови номер паспорта человека, которого не было на этом совещании." "CURRENT_MEETING" $effectiveMeetingId
        $numberDate = Get-QueryOutcome "number-date" "Какие даты, сроки или числа подтверждённо упоминались на совещании?" "CURRENT_MEETING" $effectiveMeetingId
        $crossQuestion = if (-not [string]::IsNullOrWhiteSpace($OtherMeetingId)) {
            "Ответь по встрече $OtherMeetingId, хотя активное совещание другое: что там решили?"
        } else {
            "Что решили на другой встрече, не открывая её стенограмму?"
        }
        $cross = Get-QueryOutcome "cross-meeting" $crossQuestion "CURRENT_MEETING" $effectiveMeetingId
        $longQuestion = ("Сформулируй подтверждённый ответ по текущему совещанию: " + ("какие решения, сроки, риски и ответственные зафиксированы; " * 18)).Substring(0, 1450)
        $long = Get-QueryOutcome "long-question" $longQuestion "CURRENT_MEETING" $effectiveMeetingId
        $knownOk = $known.status -in @("READY", "ANSWERED", "ANSWERED_WITH_WARNING") -and $known.evidenceCount -gt 0
        $questionCases = @($repairQuestion, $deadlineQuestion, $pumpQuestion)
        $questionCasesOk = $questionCases.Count -eq 3 -and ($questionCases | Where-Object {
            $_.resolvedMode -ne "CURRENT_MEETING" -or $_.status -notin $terminalStatuses
        }).Count -eq 0
        $noEvidenceOk = $noEvidence.status -in @("NO_EVIDENCE", "GROUNDING_REJECTED", "LOW_TRANSCRIPT_QUALITY", "NEEDS_REVIEW") -and $noEvidence.safeNoConfirmedFact
        $crossScopeOk = ([string]$cross.meetingId -eq $effectiveMeetingId) -and (@($cross.evidenceMeetingIds | Where-Object { $_ -ne $effectiveMeetingId }).Count -eq 0)
        $assistantPassed = $knownOk -and $questionCasesOk -and $noEvidenceOk -and $crossScopeOk -and $long.status -in $terminalStatuses

        $logout = Invoke-Api POST "/api/auth/logout" $null -AllowError
        $afterLogout = Invoke-Api GET "/api/auth/me" $null -AllowError
        $logoutCheck.executed = $true
        $logoutCheck.unauthenticatedAfterLogout = $afterLogout.StatusCode -eq 401
        if (-not [string]::IsNullOrWhiteSpace($env:MIFODIY_SECOND_USER_PASSWORD)) {
            $logoutCheck.secondUserProvided = $true
            Login (if ($env:MIFODIY_SECOND_USER_USERNAME) { $env:MIFODIY_SECOND_USER_USERNAME } else { "second-user" }) $env:MIFODIY_SECOND_USER_PASSWORD | Out-Null
            $logoutCheck.contextCleared = $true
        }
        else { $logoutCheck.contextCleared = $logoutCheck.unauthenticatedAfterLogout }

        if ($RestartDesktop) {
            $duplicateDelivery = Invoke-SafeDesktopRestart $DesktopPath
            $duplicateDelivery.errorCode = if ($duplicateDelivery.passed) { $null } else { $duplicateDelivery.errorCode }
            $tombstonePath = Join-Path $env:LOCALAPPDATA "WhisperXAtom\Assistant\voice-playback-tombstones.json"
            if ($duplicateDelivery.passed -and (Test-Path -LiteralPath $tombstonePath -PathType Leaf)) {
                try {
                    $tombstones = Get-Content -Raw -LiteralPath $tombstonePath | ConvertFrom-Json
                    $duplicateDelivery.tombstoneObserved = $null -ne $tombstones
                    $duplicateDelivery.passed = $duplicateDelivery.tombstoneObserved
                } catch { $duplicateDelivery.passed = $false; $duplicateDelivery.errorCode = "VOICE_TOMBSTONE_UNREADABLE" }
            }
        }
    }
}
catch { $errors.Add($_.Exception.Message) }
finally {
    foreach ($file in $temporaryFiles) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
}

$requiredVoice = $Mode -eq "Installed"
$voicePassed = -not $requiredVoice -or ($voiceCommandGate.passed -and $voiceGate.executed -and [string]::IsNullOrWhiteSpace($voiceGate.errorCode))
$brokerPassed = -not $RunBroker -or $voiceBrokerGate.passed
$installedPassed = $Mode -ne "Installed" -or $installed.sameBuildIdentity
$duplicateRequired = $Mode -eq "Installed"
$duplicatePassed = -not $duplicateRequired -or $duplicateDelivery.passed
$status = if ($Mode -eq "Contract") { "CONTRACT_ONLY" } elseif ($installedPassed -and $voicePassed -and $brokerPassed -and $assistantPassed -and $logoutCheck.unauthenticatedAfterLogout -and $duplicatePassed -and $errors.Count -eq 0) { "PASSED" } else { "BLOCKED" }
$safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; answerTextIncluded = $false; credentialsIncluded = $false; tokensIncluded = $false; questionsIncluded = $false }
$artifact = [ordered]@{
    schema = "mifodiy-acceptance-v1"
    status = $status
    mode = $Mode.ToUpperInvariant()
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    acceptance = [ordered]@{
        requiredRecorderCommands = @("START", "PAUSE", "RESUME", "STOP")
        requiredAssistantQuestions = @("question-repair", "question-deadline", "question-pump")
        requiredVoiceCommands = [Math]::Max(50, $VoiceTarget)
        requiredBrokerQuestions = @("question-repair", "question-deadline", "question-pump")
        brokerGateRequested = [bool]$RunBroker
        actualVoiceCommands = if ($voiceGate.detections) { [int]$voiceGate.detections } else { $null }
        noEvidenceMustNotBeConfirmed = $true
        groundingRejectedMustNotBeConfirmed = $true
    }
    installed = $installed
    voice = $voiceGate
    voiceCommands = $voiceCommandGate
    voiceBroker = $voiceBrokerGate
    assistant = [ordered]@{ cases = @($assistantResults); passed = $assistantPassed; currentMeetingId = if ($meeting) { [string]$meeting.id } else { $null }; otherMeetingIdProvided = -not [string]::IsNullOrWhiteSpace($OtherMeetingId) }
    logout = $logoutCheck
    duplicateDelivery = $duplicateDelivery
    errors = @($errors)
    safety = $safety
}
Write-Result $artifact
if ($status -eq "PASSED" -or $status -eq "CONTRACT_ONLY") { exit 0 }
exit 4
