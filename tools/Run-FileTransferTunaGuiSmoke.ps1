param(
    [string]$WalletPath = ".\artifacts\tuna-poc\wallet-test-nkn.json",
    [string]$WalletPassword = "",
    [string]$SidecarPath = ".\artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe",
    [ValidateSet("helpee", "helper", "both")]
    [string]$PayerMode = "helpee",
    [ValidateSet("none", "switch-off", "sidecar-kill")]
    [string]$Fault = "switch-off",
    [ValidateSet("handoff-fallback", "preactivated", "post-fallback", "v4-restart-v6-fallback", "live-v4-switch-off", "live-multi-toggle", "live-reactivation-second-transfer", "live-regular-activation-cycle")]
    [string]$RouteMode = "handoff-fallback",
    [string]$LiveToggleSequence = "",
    [ValidateSet("helpee-to-helper", "helper-to-helpee")]
    [string]$Direction = "helpee-to-helper",
    [string]$PayloadSize = "128MiB",
    [int]$TimeoutSeconds = 900,
    [int]$ProgressTimeoutSeconds = 180,
    [string]$ArtifactDir = "",
    [string]$ExePath = ".\artifacts\portable\nLink\win-x64\nLink.exe",
    [switch]$Mixed,
    [switch]$ExercisePause,
    [switch]$Build
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-TunaGuiProcessArgument {
    param([AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrEmpty($Value)) {
        return '""'
    }

    if ($Value.IndexOfAny([char[]]@(' ', "`t", '"')) -lt 0) {
        return $Value
    }

    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Write-TunaGuiAtomicJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $temporaryPath = '{0}.{1}.tmp' -f $Path, ([guid]::NewGuid().ToString('N'))
    $stream = $null
    try {
        $json = $Value | ConvertTo-Json -Depth 12
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
        $stream = [System.IO.FileStream]::new(
            $temporaryPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $backupPath = '{0}.{1}.bak' -f $Path, ([guid]::NewGuid().ToString('N'))
            try {
                [System.IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
            }
            finally {
                if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                    Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
                }
            }
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if ($stream) {
            $stream.Dispose()
        }
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-TunaGuiFinalizationGraceSeconds {
    $defaultSeconds = 120
    $overrideText = [string]$env:NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY
    if ([string]::IsNullOrWhiteSpace($overrideText)) {
        return $defaultSeconds
    }

    $overrideSeconds = 0
    if (-not [int]::TryParse($overrideText, [ref]$overrideSeconds) -or
        $overrideSeconds -lt 1 -or
        $overrideSeconds -gt $defaultSeconds) {
        throw "NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY must be an integer from 1 through $defaultSeconds."
    }

    return $overrideSeconds
}

function Complete-TunaGuiChildOutputCapture {
    param(
        [Parameter(Mandatory = $true)]$StdoutTask,
        [Parameter(Mandatory = $true)]$StderrTask,
        [Parameter(Mandatory = $true)][string]$StdoutPath,
        [Parameter(Mandatory = $true)][string]$StderrPath
    )

    $result = [ordered]@{
        Completed = $false
        ExceptionType = ''
        ExceptionMessage = ''
        ExceptionHResult = 0
    }
    try {
        $stdout = [string]$StdoutTask.GetAwaiter().GetResult()
        $stderr = [string]$StderrTask.GetAwaiter().GetResult()
        $encoding = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($StdoutPath, $stdout, $encoding)
        [System.IO.File]::WriteAllText($StderrPath, $stderr, $encoding)
        $result.Completed = $true
    }
    catch {
        $result.ExceptionType = $_.Exception.GetType().FullName
        $result.ExceptionMessage = $_.Exception.Message
        $result.ExceptionHResult = $_.Exception.HResult
    }

    return [pscustomobject]$result
}

function Stop-TunaGuiSmokeProcessTree {
    param([Parameter(Mandatory = $true)]$Process)

    try {
        $Process.Refresh()
        if ($Process.HasExited) {
            return
        }
    }
    catch {
        return
    }

    $taskKillPath = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKillPath -PathType Leaf) {
        try {
            & $taskKillPath /PID ([string]$Process.Id) /T /F 2>&1 | Out-Null
        }
        catch {
        }
    }

    try {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
    }

    try {
        $Process.WaitForExit(5000) | Out-Null
    }
    catch {
    }
}

function Stop-TunaGuiSmokeApplicationDescendants {
    param(
        [Parameter(Mandatory = $true)]$GuiChildProcess,
        [Parameter(Mandatory = $true)][string]$ExePath,
        [int]$GraceMilliseconds = 7000
    )

    $resolvedExePath = ''
    try {
        $resolvedExePath = [System.IO.Path]::GetFullPath($ExePath)
    }
    catch {
        return
    }

    $processSnapshot = @()
    try {
        $processSnapshot = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    }
    catch {
        return
    }

    $descendantIds = [System.Collections.Generic.HashSet[int]]::new()
    [void]$descendantIds.Add([int]$GuiChildProcess.Id)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($candidate in $processSnapshot) {
            $candidateId = [int]$candidate.ProcessId
            if ($descendantIds.Contains($candidateId) -or
                -not $descendantIds.Contains([int]$candidate.ParentProcessId)) {
                continue
            }

            [void]$descendantIds.Add($candidateId)
            $changed = $true
        }
    }

    $targets = @(
        $processSnapshot |
            Where-Object {
                $descendantIds.Contains([int]$_.ProcessId) -and
                [int]$_.ProcessId -ne [int]$GuiChildProcess.Id -and
                -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
                [string]::Equals(
                    [System.IO.Path]::GetFullPath([string]$_.ExecutablePath),
                    $resolvedExePath,
                    [System.StringComparison]::OrdinalIgnoreCase)
            } |
            ForEach-Object {
                [pscustomobject]@{
                    ProcessId = [int]$_.ProcessId
                    CreationDate = [string]$_.CreationDate
                }
            }
    )
    if ($targets.Count -eq 0) {
        return
    }

    foreach ($target in $targets) {
        try {
            $application = [System.Diagnostics.Process]::GetProcessById([int]$target.ProcessId)
            [void]$application.CloseMainWindow()
            $application.Dispose()
        }
        catch {
        }
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $GraceMilliseconds) {
        $remaining = @(
            $targets |
                Where-Object {
                    $null -ne (Get-Process -Id ([int]$_.ProcessId) -ErrorAction SilentlyContinue)
                }
        )
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 100
    }

    foreach ($target in $targets) {
        try {
            $current = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f [int]$target.ProcessId) -ErrorAction SilentlyContinue
            if ($null -ne $current -and
                [string]::Equals([string]$current.CreationDate, [string]$target.CreationDate, [System.StringComparison]::Ordinal)) {
                Stop-Process -Id ([int]$target.ProcessId) -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
        }
    }
}

function Request-TunaGuiScenarioDeadline {
    param(
        [Parameter(Mandatory = $true)][string]$RequestPath,
        [Parameter(Mandatory = $true)][string]$StatePath,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$GuiChildProcess,
        [Parameter(Mandatory = $true)][DateTimeOffset]$ScenarioDeadlineUtc
    )

    $requestedUtc = [DateTimeOffset]::UtcNow
    $request = [ordered]@{
        schemaVersion = 1
        event = 'filetransfer_gui_scenario_deadline_requested'
        acceptanceRunId = [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
        scenarioRunId = [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
        artifactDir = $ArtifactDir
        guiChildProcessId = [int]$GuiChildProcess.Id
        scenarioDeadlineUtc = $ScenarioDeadlineUtc.ToString('O')
        requestedUtc = $requestedUtc.ToString('O')
        reason = 'gui_smoke_scenario_deadline_exceeded'
    }
    try {
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        foreach ($field in @(
                @{ Name = 'scenarioDeadlineRequested'; Value = $true },
                @{ Name = 'scenarioDeadlineRequestPath'; Value = $RequestPath },
                @{ Name = 'scenarioDeadlineRequestedUtc'; Value = $requestedUtc.ToString('O') },
                @{ Name = 'updatedUtc'; Value = $requestedUtc.ToString('O') })) {
            if ($state.PSObject.Properties.Name -contains $field.Name) {
                $state.($field.Name) = $field.Value
            }
            else {
                $state | Add-Member -NotePropertyName $field.Name -NotePropertyValue $field.Value
            }
        }
        Write-TunaGuiAtomicJson -Path $StatePath -Value $state
    }
    catch {
        # The immutable request remains authoritative for cooperative
        # cancellation if the diagnostic state cannot be updated.
    }
    # Publish the request only after the state write so a child that reacts
    # immediately cannot have its capture fields overwritten by this update.
    Write-TunaGuiAtomicJson -Path $RequestPath -Value $request
}

function Get-TunaGuiTerminalPhaseDeadlineRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][DateTimeOffset]$StartedUtc,
        [Parameter(Mandatory = $true)][DateTimeOffset]$OriginalScenarioDeadlineUtc,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        $request = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $requestedUtc = [DateTimeOffset]::MinValue
        $deadlineUtc = [DateTimeOffset]::MinValue
        $expectedTimeoutMilliseconds = [long]$TimeoutSeconds * 1000L
        if ($null -eq $request -or
            -not [string]::Equals([string]$request.event, 'filetransfer_gui_terminal_phase_deadline_requested', [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$request.acceptanceRunId, [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID, [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$request.scenarioRunId, [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID, [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$request.routeMode, 'live-regular-activation-cycle', [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$request.phase, 'terminal_integrity', [System.StringComparison]::Ordinal) -or
            [long]$request.phaseGeneration -ne 1L -or
            [long]$request.liveRouteEpoch -lt 4L -or
            [long]$request.actionBookmark -lt 0L -or
            [long]$request.proofEndBookmark -lt [long]$request.actionBookmark -or
            [long]$request.timeoutMilliseconds -ne $expectedTimeoutMilliseconds -or
            -not [DateTimeOffset]::TryParse([string]$request.requestedUtc, [ref]$requestedUtc) -or
            -not [DateTimeOffset]::TryParse([string]$request.deadlineUtc, [ref]$deadlineUtc)) {
            return $null
        }

        $maximumDeadlineUtc = $StartedUtc.AddSeconds(2L * [long]$TimeoutSeconds)
        $requestedDurationMilliseconds = ($deadlineUtc - $requestedUtc).TotalMilliseconds
        if ($deadlineUtc -le $OriginalScenarioDeadlineUtc -or
            $deadlineUtc -gt $maximumDeadlineUtc -or
            [Math]::Abs($requestedDurationMilliseconds - $expectedTimeoutMilliseconds) -gt 2000D) {
            return $null
        }

        return [pscustomobject]@{
            Request = $request
            DeadlineUtc = $deadlineUtc
        }
    }
    catch {
        return $null
    }
}

function Invoke-TunaGuiSmokeWithHardDeadline {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ExePath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $finalizationGraceSeconds = Get-TunaGuiFinalizationGraceSeconds
    $hardDeadlineSeconds = [long]$TimeoutSeconds + [long]$finalizationGraceSeconds
    $maximumHardDeadlineSeconds = (2L * [long]$TimeoutSeconds) + [long]$finalizationGraceSeconds
    $startedUtc = [DateTimeOffset]::UtcNow
    $originalScenarioDeadlineUtc = $startedUtc.AddSeconds($TimeoutSeconds)
    $scenarioDeadlineUtc = $originalScenarioDeadlineUtc
    $deadlineUtc = $scenarioDeadlineUtc.AddSeconds($finalizationGraceSeconds)
    $statePath = Join-Path $ArtifactDir 'filetransfer-gui-evidence-finalization-state.json'
    $timeoutPath = Join-Path $ArtifactDir 'filetransfer-gui-evidence-finalization-timeout.json'
    $scenarioDeadlineRequestPath = Join-Path $ArtifactDir 'filetransfer-gui-scenario-deadline-request.json'
    $terminalPhaseDeadlinePath = Join-Path $ArtifactDir 'filetransfer-gui-terminal-phase-deadline.json'
    $stdoutPath = Join-Path $ArtifactDir 'gui-smoke-stdout.log'
    $stderrPath = Join-Path $ArtifactDir 'gui-smoke-stderr.log'
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $RepoRoot 'tools\GuiSmoke-Windows.ps1'),
        '-ExePath', $ExePath,
        '-TimeoutSeconds', ([string]$TimeoutSeconds))
    $argumentLine = ($arguments | ForEach-Object { ConvertTo-TunaGuiProcessArgument -Value ([string]$_) }) -join ' '

    $oldStatePath = $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH
    $oldScenarioDeadlineRequestPath = $env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH
    $oldTerminalPhaseDeadlinePath = $env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH
    $process = $null
    $stdoutTask = $null
    $stderrTask = $null
    try {
        Remove-Item -LiteralPath $terminalPhaseDeadlinePath -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $terminalPhaseDeadlinePath -PathType Leaf) {
            throw "Unable to clear stale terminal-phase deadline request: $terminalPhaseDeadlinePath"
        }
        $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH = $statePath
        $env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH = $scenarioDeadlineRequestPath
        $env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH = $terminalPhaseDeadlinePath
        $state = [ordered]@{
            schemaVersion = 1
            event = 'filetransfer_gui_evidence_finalization_state'
            acceptanceRunId = [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
            scenarioRunId = [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
            artifactDir = $ArtifactDir
            guiChildProcessId = 0
            scenarioTimeoutSeconds = $TimeoutSeconds
            finalizationGraceSeconds = $finalizationGraceSeconds
            hardDeadlineSeconds = $hardDeadlineSeconds
            maximumHardDeadlineSeconds = $maximumHardDeadlineSeconds
            startedUtc = $startedUtc.ToString('O')
            scenarioDeadlineUtc = $scenarioDeadlineUtc.ToString('O')
            deadlineUtc = $deadlineUtc.ToString('O')
            scenarioDeadlineRequested = $false
            scenarioDeadlineRequestPath = $scenarioDeadlineRequestPath
            scenarioDeadlineRequestedUtc = ''
            terminalPhaseDeadlinePath = $terminalPhaseDeadlinePath
            terminalPhaseDeadlineAdopted = $false
            terminalPhaseDeadlineUtc = ''
            lastCompletedStage = 'gui_child_starting'
            timedOut = $false
            exitCode = $null
            exitCodeAvailable = $false
            updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
        Write-TunaGuiAtomicJson -Path $statePath -Value $state

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'powershell'
        $startInfo.Arguments = $argumentLine
        $startInfo.WorkingDirectory = $RepoRoot
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'Failed to start the Tuna GUI smoke child process.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        $state.guiChildProcessId = $process.Id
        $state.lastCompletedStage = 'gui_child_started'
        $state.updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Write-TunaGuiAtomicJson -Path $statePath -Value $state

        $timedOut = $false
        $scenarioDeadlineRequested = $false
        $scenarioDeadlineRequestedUtc = ''
        $terminalPhaseDeadlineAdopted = $false
        $terminalPhaseDeadlineUtc = ''
        while ($true) {
            $process.Refresh()
            if ($process.HasExited) {
                break
            }

            $nowUtc = [DateTimeOffset]::UtcNow
            if (-not $terminalPhaseDeadlineAdopted) {
                $terminalPhaseDeadline = Get-TunaGuiTerminalPhaseDeadlineRequest `
                    -Path $terminalPhaseDeadlinePath `
                    -StartedUtc $startedUtc `
                    -OriginalScenarioDeadlineUtc $originalScenarioDeadlineUtc `
                    -TimeoutSeconds $TimeoutSeconds
                if ($null -ne $terminalPhaseDeadline) {
                    $scenarioDeadlineUtc = [DateTimeOffset]$terminalPhaseDeadline.DeadlineUtc
                    $deadlineUtc = $scenarioDeadlineUtc.AddSeconds($finalizationGraceSeconds)
                    $hardDeadlineSeconds = [long][Math]::Ceiling(
                        ($deadlineUtc - $startedUtc).TotalSeconds)
                    $terminalPhaseDeadlineAdopted = $true
                    $terminalPhaseDeadlineUtc = $scenarioDeadlineUtc.ToString('O')
                }
            }
            if (-not $scenarioDeadlineRequested -and $nowUtc -ge $scenarioDeadlineUtc) {
                $process.Refresh()
                if ($process.HasExited) {
                    break
                }

                Request-TunaGuiScenarioDeadline `
                    -RequestPath $scenarioDeadlineRequestPath `
                    -StatePath $statePath `
                    -ArtifactDir $ArtifactDir `
                    -GuiChildProcess $process `
                    -ScenarioDeadlineUtc $scenarioDeadlineUtc
                $scenarioDeadlineRequested = $true
                $scenarioDeadlineRequestedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                Stop-TunaGuiSmokeApplicationDescendants `
                    -GuiChildProcess $process `
                    -ExePath $ExePath
                continue
            }

            $nextDeadlineUtc = if ($scenarioDeadlineRequested) { $deadlineUtc } else { $scenarioDeadlineUtc }
            $remainingMilliseconds = [long][Math]::Ceiling(($nextDeadlineUtc - $nowUtc).TotalMilliseconds)
            if ($remainingMilliseconds -gt 0) {
                $waitMilliseconds = [int][Math]::Min(1000L, $remainingMilliseconds)
                if ($process.WaitForExit($waitMilliseconds)) {
                    break
                }
                continue
            }

            # The exit/timeout race has one winner: refresh immediately before
            # destructive cleanup and accept a naturally completed child.
            $process.Refresh()
            if ($process.HasExited) {
                break
            }

            $timedOut = $true
            Stop-TunaGuiSmokeProcessTree -Process $process
            break
        }

        if ($timedOut) {
            $processHasExited = $false
            try {
                $process.Refresh()
                $processHasExited = [bool]$process.HasExited
            }
            catch {
            }
            $outputCapture = if ($processHasExited) {
                Complete-TunaGuiChildOutputCapture `
                    -StdoutTask $stdoutTask `
                    -StderrTask $stderrTask `
                    -StdoutPath $stdoutPath `
                    -StderrPath $stderrPath
            }
            else {
                [pscustomobject]@{
                    Completed = $false
                    ExceptionType = ''
                    ExceptionMessage = 'child_process_did_not_exit_after_timeout_cleanup'
                    ExceptionHResult = 0
                }
            }
            $lastCompletedStage = 'gui_child_started'
            $capturePath = ''
            $captureSha256 = ''
            $captureLineCount = 0L
            if (Test-Path -LiteralPath $statePath -PathType Leaf) {
                try {
                    $currentState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                    if ($currentState.PSObject.Properties.Name -contains 'lastCompletedStage' -and
                        -not [string]::IsNullOrWhiteSpace([string]$currentState.lastCompletedStage)) {
                        $lastCompletedStage = [string]$currentState.lastCompletedStage
                    }
                    if ($currentState.PSObject.Properties.Name -contains 'capturePath') {
                        $capturePath = [string]$currentState.capturePath
                    }
                    if ($currentState.PSObject.Properties.Name -contains 'captureSha256') {
                        $captureSha256 = [string]$currentState.captureSha256
                    }
                    if ($currentState.PSObject.Properties.Name -contains 'captureLineCount') {
                        $captureLineCount = [long]$currentState.captureLineCount
                    }
                }
                catch {
                }
            }

            $endedUtc = [DateTimeOffset]::UtcNow
            $timeout = [ordered]@{
                schemaVersion = 1
                event = 'filetransfer_gui_evidence_finalization_timeout'
                permanentlyNonAuthoritative = $true
                acceptanceRunId = [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
                scenarioRunId = [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
                artifactDir = $ArtifactDir
                guiChildProcessId = $process.Id
                scenarioTimeoutSeconds = $TimeoutSeconds
                finalizationGraceSeconds = $finalizationGraceSeconds
                hardDeadlineSeconds = $hardDeadlineSeconds
                maximumHardDeadlineSeconds = $maximumHardDeadlineSeconds
                startedUtc = $startedUtc.ToString('O')
                scenarioDeadlineUtc = $scenarioDeadlineUtc.ToString('O')
                scenarioDeadlineRequested = $scenarioDeadlineRequested
                scenarioDeadlineRequestPath = $scenarioDeadlineRequestPath
                scenarioDeadlineRequestedUtc = $scenarioDeadlineRequestedUtc
                terminalPhaseDeadlinePath = $terminalPhaseDeadlinePath
                terminalPhaseDeadlineAdopted = $terminalPhaseDeadlineAdopted
                terminalPhaseDeadlineUtc = $terminalPhaseDeadlineUtc
                deadlineUtc = $deadlineUtc.ToString('O')
                endedUtc = $endedUtc.ToString('O')
                elapsedMilliseconds = [long][Math]::Max(0, ($endedUtc - $startedUtc).TotalMilliseconds)
                lastCompletedStage = $lastCompletedStage
                capturePath = $capturePath
                captureSha256 = $captureSha256
                captureLineCount = $captureLineCount
                exitCode = 124
                exitCodeAvailable = $true
                exitCodeCaptureBranch = 'hard_timeout'
                processHasExited = $processHasExited
                outputCaptureCompleted = [bool]$outputCapture.Completed
                outputCaptureExceptionType = [string]$outputCapture.ExceptionType
                outputCaptureExceptionMessage = [string]$outputCapture.ExceptionMessage
                outputCaptureExceptionHResult = [int]$outputCapture.ExceptionHResult
                reason = 'gui_evidence_finalization_hard_deadline_exceeded'
            }
            Write-TunaGuiAtomicJson -Path $timeoutPath -Value $timeout

            $state = [ordered]@{
                schemaVersion = 1
                event = 'filetransfer_gui_evidence_finalization_state'
                acceptanceRunId = [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
                scenarioRunId = [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
                artifactDir = $ArtifactDir
                guiChildProcessId = $process.Id
                scenarioTimeoutSeconds = $TimeoutSeconds
                finalizationGraceSeconds = $finalizationGraceSeconds
                hardDeadlineSeconds = $hardDeadlineSeconds
                maximumHardDeadlineSeconds = $maximumHardDeadlineSeconds
                startedUtc = $startedUtc.ToString('O')
                scenarioDeadlineUtc = $scenarioDeadlineUtc.ToString('O')
                scenarioDeadlineRequested = $scenarioDeadlineRequested
                scenarioDeadlineRequestPath = $scenarioDeadlineRequestPath
                scenarioDeadlineRequestedUtc = $scenarioDeadlineRequestedUtc
                terminalPhaseDeadlinePath = $terminalPhaseDeadlinePath
                terminalPhaseDeadlineAdopted = $terminalPhaseDeadlineAdopted
                terminalPhaseDeadlineUtc = $terminalPhaseDeadlineUtc
                deadlineUtc = $deadlineUtc.ToString('O')
                endedUtc = $endedUtc.ToString('O')
                lastCompletedStage = $lastCompletedStage
                capturePath = $capturePath
                captureSha256 = $captureSha256
                captureLineCount = $captureLineCount
                timedOut = $true
                exitCode = 124
                exitCodeAvailable = $true
                exitCodeCaptureBranch = 'hard_timeout'
                processHasExited = $processHasExited
                outputCaptureCompleted = [bool]$outputCapture.Completed
                outputCaptureExceptionType = [string]$outputCapture.ExceptionType
                outputCaptureExceptionMessage = [string]$outputCapture.ExceptionMessage
                outputCaptureExceptionHResult = [int]$outputCapture.ExceptionHResult
                updatedUtc = $endedUtc.ToString('O')
            }
            Write-TunaGuiAtomicJson -Path $statePath -Value $state
            return [pscustomobject]@{
                ExitCode = 124
                ExitCodeAvailable = $true
                TimedOut = $true
            }
        }

        $process.WaitForExit()
        $outputCapture = Complete-TunaGuiChildOutputCapture `
            -StdoutTask $stdoutTask `
            -StderrTask $stderrTask `
            -StdoutPath $stdoutPath `
            -StderrPath $stderrPath
        $exitCode = -1
        $exitCodeAvailable = $false
        $exitCodeCaptureBranch = 'owned_process_exit_code'
        $exitCodeCaptureExceptionType = ''
        $exitCodeCaptureExceptionMessage = ''
        $exitCodeCaptureExceptionHResult = 0
        $processHasExited = $false
        try {
            $processHasExited = [bool]$process.HasExited
            if (-not $processHasExited) {
                throw 'The Tuna GUI smoke child did not report an exited state.'
            }
            $capturedExitCode = $process.ExitCode
            if ($null -eq $capturedExitCode) {
                throw 'The Tuna GUI smoke child exit code was unavailable.'
            }
            $exitCode = [int]$capturedExitCode
            $exitCodeAvailable = $true
        }
        catch {
            $exitCode = -1
            $exitCodeAvailable = $false
            $exitCodeCaptureBranch = 'owned_process_exit_code_failed'
            $exitCodeCaptureExceptionType = $_.Exception.GetType().FullName
            $exitCodeCaptureExceptionMessage = $_.Exception.Message
            $exitCodeCaptureExceptionHResult = $_.Exception.HResult
        }
        $endedUtc = [DateTimeOffset]::UtcNow
        $capturePath = ''
        $captureSha256 = ''
        $captureLineCount = 0L
        $childLastCompletedStage = ''
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            try {
                $currentState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                if ($currentState.PSObject.Properties.Name -contains 'lastCompletedStage') {
                    $childLastCompletedStage = [string]$currentState.lastCompletedStage
                }
                if ($currentState.PSObject.Properties.Name -contains 'capturePath') {
                    $capturePath = [string]$currentState.capturePath
                }
                if ($currentState.PSObject.Properties.Name -contains 'captureSha256') {
                    $captureSha256 = [string]$currentState.captureSha256
                }
                if ($currentState.PSObject.Properties.Name -contains 'captureLineCount') {
                    $captureLineCount = [long]$currentState.captureLineCount
                }
            }
            catch {
            }
        }
        $state = [ordered]@{
            schemaVersion = 1
            event = 'filetransfer_gui_evidence_finalization_state'
            acceptanceRunId = [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
            scenarioRunId = [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
            artifactDir = $ArtifactDir
            guiChildProcessId = $process.Id
            scenarioTimeoutSeconds = $TimeoutSeconds
            finalizationGraceSeconds = $finalizationGraceSeconds
            hardDeadlineSeconds = $hardDeadlineSeconds
            maximumHardDeadlineSeconds = $maximumHardDeadlineSeconds
            startedUtc = $startedUtc.ToString('O')
            scenarioDeadlineUtc = $scenarioDeadlineUtc.ToString('O')
            scenarioDeadlineRequested = $scenarioDeadlineRequested
            scenarioDeadlineRequestPath = $scenarioDeadlineRequestPath
            scenarioDeadlineRequestedUtc = $scenarioDeadlineRequestedUtc
            terminalPhaseDeadlinePath = $terminalPhaseDeadlinePath
            terminalPhaseDeadlineAdopted = $terminalPhaseDeadlineAdopted
            terminalPhaseDeadlineUtc = $terminalPhaseDeadlineUtc
            deadlineUtc = $deadlineUtc.ToString('O')
            endedUtc = $endedUtc.ToString('O')
            lastCompletedStage = 'gui_child_exited'
            childLastCompletedStage = $childLastCompletedStage
            capturePath = $capturePath
            captureSha256 = $captureSha256
            captureLineCount = $captureLineCount
            timedOut = $false
            exitCode = $exitCode
            exitCodeAvailable = $exitCodeAvailable
            exitCodeCaptureBranch = $exitCodeCaptureBranch
            processHasExited = $processHasExited
            exitCodeCaptureExceptionType = $exitCodeCaptureExceptionType
            exitCodeCaptureExceptionMessage = $exitCodeCaptureExceptionMessage
            exitCodeCaptureExceptionHResult = $exitCodeCaptureExceptionHResult
            outputCaptureCompleted = [bool]$outputCapture.Completed
            outputCaptureExceptionType = [string]$outputCapture.ExceptionType
            outputCaptureExceptionMessage = [string]$outputCapture.ExceptionMessage
            outputCaptureExceptionHResult = [int]$outputCapture.ExceptionHResult
            updatedUtc = $endedUtc.ToString('O')
        }
        Write-TunaGuiAtomicJson -Path $statePath -Value $state
        return [pscustomobject]@{
            ExitCode = $exitCode
            ExitCodeAvailable = $exitCodeAvailable
            TimedOut = $false
        }
    }
    finally {
        if ($null -eq $oldStatePath) {
            Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH = $oldStatePath
        }
        if ($null -eq $oldScenarioDeadlineRequestPath) {
            Remove-Item Env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH = $oldScenarioDeadlineRequestPath
        }
        if ($null -eq $oldTerminalPhaseDeadlinePath) {
            Remove-Item Env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH = $oldTerminalPhaseDeadlinePath
        }

        if ($process -is [System.IDisposable]) {
            $process.Dispose()
        }
    }
}

function Test-TunaGuiLateSetupCleanupLine {
    param([AllowEmptyString()][string]$Line)

    if ([string]::IsNullOrEmpty($Line)) {
        return $false
    }

    if ($Line.IndexOf('event=filetransfer_v4_feedback_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $Line.IndexOf('frame_type=filetransfer.cancel.v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $Line.IndexOf('OperationCanceledException', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    if ($Line.IndexOf('event=filetransfer_lifecycle_priority_send_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $Line.IndexOf('kind=cancel', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $Line.IndexOf('source=terminal_redundant_retry', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    return $false
}

function Select-TunaGuiControlledRestartLogSlices {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        return [pscustomobject]@{
            SetupPath = ''
            MeasuredPath = ''
            FilteredSetupCleanupLineCount = 0
        }
    }

    $lines = @(Get-Content -LiteralPath $logPath)
    $setupStartIndex = -1
    $firstFallbackIndex = -1
    $setupCanceledTerminalIndex = -1
    $startIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($setupStartIndex -lt 0 -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $setupStartIndex = $i
        }

        if ($firstFallbackIndex -lt 0 -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $firstFallbackIndex = $i
        }

        if ($firstFallbackIndex -ge 0 -and
            $setupCanceledTerminalIndex -lt 0 -and
            $lines[$i].IndexOf('event=file_transfer_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('state=Canceled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $setupCanceledTerminalIndex = $i
        }

        if ($setupCanceledTerminalIndex -ge 0 -and
            $i -gt $setupCanceledTerminalIndex -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $startIndex = $i
            break
        }
    }

    if ($startIndex -lt 0) {
        $startIndex = $firstFallbackIndex
    }

    $fullPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log'
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Copy-Item -LiteralPath $logPath -Destination $fullPath -Force
    }

    $setupPath = Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log'
    if ($setupStartIndex -ge 0) {
        $setupEndIndex = if ($startIndex -gt $setupStartIndex) { $startIndex - 1 } else { $lines.Count - 1 }
        $setupLines = @(
            '[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started'
            $lines[$setupStartIndex..$setupEndIndex]
        )
        if ($startIndex -gt $setupStartIndex) {
            $setupLines += '[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed'
        }

        $setupLines | Set-Content -LiteralPath $setupPath -Encoding UTF8
    }
    else {
        '[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started; missing_setup_slice=1' |
            Set-Content -LiteralPath $setupPath -Encoding UTF8
    }

    if ($startIndex -lt 0) {
        return [pscustomobject]@{
            SetupPath = $setupPath
            MeasuredPath = ''
            FilteredSetupCleanupLineCount = 0
        }
    }

    $filteredSetupCleanupLineCount = 0
    $measuredLines = New-Object System.Collections.Generic.List[string]
    $measuredLines.Add('[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=measured_post_tuna_fallback_v6_started') | Out-Null
    foreach ($line in @($lines[$startIndex..($lines.Count - 1)])) {
        if (Test-TunaGuiLateSetupCleanupLine -Line ([string]$line)) {
            $filteredSetupCleanupLineCount++
            continue
        }

        $measuredLines.Add([string]$line) | Out-Null
    }
    $measuredLines.Add('[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=measured_post_tuna_fallback_v6_terminal') | Out-Null

    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    $measuredLines.ToArray() | Set-Content -LiteralPath $measuredPath -Encoding UTF8
    return [pscustomobject]@{
        SetupPath = $setupPath
        MeasuredPath = $measuredPath
        FilteredSetupCleanupLineCount = $filteredSetupCleanupLineCount
    }
}

function Write-TunaGuiRetainedAnalysisTimeoutArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$AnalysisDir,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [int]$ProcessId = 0,
        [string]$StartedUtc = '',
        [string]$LiveRouteProofMode = 'None'
    )

    New-Item -ItemType Directory -Force -Path $AnalysisDir | Out-Null

    $artifactDir = Split-Path -Parent $LogPath
    $guiSummaryPath = Join-Path $artifactDir 'filetransfer-tuna-gui-summary.json'
    $guiSummaryPresent = Test-Path -LiteralPath $guiSummaryPath -PathType Leaf
    $guiCompleted = ''
    $guiIntegrityOk = ''
    $guiInboundTerminalState = ''
    $guiOutboundTerminalState = ''
    if ($guiSummaryPresent) {
        try {
            $summary = Get-Content -LiteralPath $guiSummaryPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            $guiCompleted = [string]$summary.completed
            $guiIntegrityOk = [string]$summary.integrityOk
            $guiInboundTerminalState = [string]$summary.inboundTerminalState
            $guiOutboundTerminalState = [string]$summary.outboundTerminalState
        }
        catch {
            $guiCompleted = 'unreadable'
            $guiIntegrityOk = 'unreadable'
        }
    }

    $routeSummaryPath = Join-Path $AnalysisDir 'filetransfer-route-consistency-summary.txt'
    $routeSummaryPresent = Test-Path -LiteralPath $routeSummaryPath -PathType Leaf
    $routeConsistencyVerdict = ''
    $liveRouteEpochProofVerdict = ''
    $fallbackLegAuthorityProofVerdict = ''
    $cleanFallbackLegProofVerdict = ''
    $bridgeLivenessIntegrationVerdict = ''
    $controlPlaneIsolationVerdict = ''
    if ($routeSummaryPresent) {
        $routeConsistencyVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'route_consistency_verdict' -DefaultValue '(missing)'
        $liveRouteEpochProofVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'live_route_epoch_proof_verdict' -DefaultValue '(missing)'
        $fallbackLegAuthorityProofVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'fallback_leg_authority_proof_verdict' -DefaultValue '(missing)'
        $cleanFallbackLegProofVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'clean_fallback_leg_proof_verdict' -DefaultValue $fallbackLegAuthorityProofVerdict
        $bridgeLivenessIntegrationVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'bridge_liveness_integration_verdict' -DefaultValue '(missing)'
        $controlPlaneIsolationVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'control_plane_isolation_verdict' -DefaultValue '(missing)'
    }

    $requiresLiveProof = -not [string]::Equals($LiveRouteProofMode, 'None', [System.StringComparison]::OrdinalIgnoreCase)
    $hasFailedRouteProof = [string]::Equals($liveRouteEpochProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($fallbackLegAuthorityProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($cleanFallbackLegProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($bridgeLivenessIntegrationVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($controlPlaneIsolationVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase)
    $routeProofOk = $routeSummaryPresent -and
        [string]::Equals($routeConsistencyVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -and
        (-not $hasFailedRouteProof) -and
        (-not $requiresLiveProof -or [string]::Equals($liveRouteEpochProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($fallbackLegAuthorityProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($cleanFallbackLegProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($bridgeLivenessIntegrationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($controlPlaneIsolationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase))

    $terminalSuccess = [string]::Equals($guiCompleted, 'True', [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($guiIntegrityOk, 'True', [System.StringComparison]::OrdinalIgnoreCase)
    $terminalSuccessWithProof = $terminalSuccess -and $routeProofOk
    $failureClass = if ($terminalSuccessWithProof) { 'retained_analysis_timeout_after_terminal_success' } else { 'retained_analysis_timeout' }
    $acceptanceFailureClass = 'incomplete_proof'
    $operatorVerdict = 'INCONCLUSIVE'
    $warningKinds = if ($terminalSuccessWithProof) { 'retained_analysis_timeout_after_terminal_success' } else { '(none)' }

    $dto = [ordered]@{
        failure_class = $failureClass
        acceptance_failure_class = $acceptanceFailureClass
        semantic_failure_domain = 'unavailable'
        retained_analysis_completed = 0
        retained_analysis_status = 'timed_out'
        timed_out = 1
        timeout_seconds = $TimeoutSeconds
        process_id = $ProcessId
        started_utc = $StartedUtc
        ended_utc = [DateTimeOffset]::UtcNow.ToString('O')
        artifact_dir = $AnalysisDir
        log_path = $LogPath
        live_route_proof_mode = $LiveRouteProofMode
        gui_summary_present = if ($guiSummaryPresent) { 1 } else { 0 }
        gui_completed = $guiCompleted
        gui_integrity_ok = $guiIntegrityOk
        gui_inbound_terminal_state = $guiInboundTerminalState
        gui_outbound_terminal_state = $guiOutboundTerminalState
        terminal_success_timeout = if ($terminalSuccessWithProof) { 1 } else { 0 }
        terminal_success_without_retained_route_proof = if ($terminalSuccess -and -not $routeProofOk) { 1 } else { 0 }
        route_summary_present = if ($routeSummaryPresent) { 1 } else { 0 }
        route_consistency_verdict = $routeConsistencyVerdict
        live_route_epoch_proof_verdict = $liveRouteEpochProofVerdict
        fallback_leg_authority_proof_verdict = $fallbackLegAuthorityProofVerdict
        clean_fallback_leg_proof_verdict = $cleanFallbackLegProofVerdict
        bridge_liveness_integration_verdict = $bridgeLivenessIntegrationVerdict
        control_plane_isolation_verdict = $controlPlaneIsolationVerdict
        warning_kinds = $warningKinds
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in @($dto.Keys)) {
        $lines.Add(('{0}={1}' -f $key, $dto[$key])) | Out-Null
    }

    Set-Content -LiteralPath (Join-Path $AnalysisDir 'filetransfer-retained-analysis-timeout-summary.txt') -Value $lines.ToArray() -Encoding UTF8
    $dto | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $AnalysisDir 'filetransfer-retained-analysis-timeout-summary.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $AnalysisDir 'filetransfer-operator-verdict.txt') -Value @(
        ('verdict={0}' -f $operatorVerdict),
        ('acceptance_failure_class={0}' -f $acceptanceFailureClass),
        'semantic_failure_domain=unavailable',
        'retained_analysis_completed=0',
        'retained_analysis_status=timed_out',
        ('failure_class={0}' -f $failureClass),
        'hard_failure_count=1',
        ('warning_kinds={0}' -f $warningKinds),
        ('timeout_seconds={0}' -f $TimeoutSeconds),
        ('log_path={0}' -f $LogPath)
    ) -Encoding UTF8

    return $false
}

function Test-TunaGuiRetainedAnalysisCompletedProofArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$AnalysisDir,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [string]$LiveRouteProofMode = 'None'
    )

    $artifactDir = Split-Path -Parent $LogPath
    $guiSummaryPath = Join-Path $artifactDir 'filetransfer-tuna-gui-summary.json'
    $routeSummaryPath = Join-Path $AnalysisDir 'filetransfer-route-consistency-summary.txt'
    $operatorVerdictPath = Join-Path $AnalysisDir 'filetransfer-operator-verdict.txt'
    $stabilitySummaryPath = Join-Path $AnalysisDir 'stability-gates-summary.txt'
    if (-not (Test-Path -LiteralPath $guiSummaryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $routeSummaryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $operatorVerdictPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $stabilitySummaryPath -PathType Leaf)) {
        return $false
    }

    try {
        $summary = Get-Content -LiteralPath $guiSummaryPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if (-not [string]::Equals([string]$summary.completed, 'True', [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$summary.integrityOk, 'True', [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    catch {
        return $false
    }

    $operatorVerdict = Read-TunaGuiReportValue -ReportPath $operatorVerdictPath -Key 'verdict' -DefaultValue '(missing)'
    $operatorHardFailureCountText = Read-TunaGuiReportValue -ReportPath $operatorVerdictPath -Key 'hard_failure_count' -DefaultValue '(missing)'
    $operatorHardFailureCount = 0
    if (-not [int]::TryParse($operatorHardFailureCountText, [ref]$operatorHardFailureCount) -or
        $operatorHardFailureCount -ne 0 -or
        [string]::Equals($operatorVerdict, '(missing)', [System.StringComparison]::OrdinalIgnoreCase) -or
        $operatorVerdict.StartsWith('FAIL', [System.StringComparison]::OrdinalIgnoreCase) -or
        $operatorVerdict.StartsWith('INVALID', [System.StringComparison]::OrdinalIgnoreCase) -or
        $operatorVerdict.StartsWith('INCONCLUSIVE', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $stabilityGateStatus = Read-TunaGuiReportValue -ReportPath $stabilitySummaryPath -Key 'gate_status' -DefaultValue '(missing)'
    $stabilityHardFailureCountText = Read-TunaGuiReportValue -ReportPath $stabilitySummaryPath -Key 'hard_failure_count' -DefaultValue '0'
    $stabilityHardFailureCount = 0
    if (-not [string]::Equals($stabilityGateStatus, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [int]::TryParse($stabilityHardFailureCountText, [ref]$stabilityHardFailureCount) -or
        $stabilityHardFailureCount -ne 0) {
        return $false
    }

    $routeConsistencyVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'route_consistency_verdict' -DefaultValue '(missing)'
    $liveRouteEpochProofVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'live_route_epoch_proof_verdict' -DefaultValue '(missing)'
    $fallbackLegAuthorityProofVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'fallback_leg_authority_proof_verdict' -DefaultValue '(missing)'
    $cleanFallbackLegProofVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'clean_fallback_leg_proof_verdict' -DefaultValue $fallbackLegAuthorityProofVerdict
    $bridgeLivenessIntegrationVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'bridge_liveness_integration_verdict' -DefaultValue '(missing)'
    $controlPlaneIsolationVerdict = Read-TunaGuiReportValue -ReportPath $routeSummaryPath -Key 'control_plane_isolation_verdict' -DefaultValue '(missing)'
    $requiresLiveProof = -not [string]::Equals($LiveRouteProofMode, 'None', [System.StringComparison]::OrdinalIgnoreCase)
    $hasFailedRouteProof = [string]::Equals($liveRouteEpochProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($fallbackLegAuthorityProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($cleanFallbackLegProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($bridgeLivenessIntegrationVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($controlPlaneIsolationVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase)

    return [string]::Equals($routeConsistencyVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -and
        (-not $hasFailedRouteProof) -and
        (-not $requiresLiveProof -or [string]::Equals($liveRouteEpochProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($fallbackLegAuthorityProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($cleanFallbackLegProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($bridgeLivenessIntegrationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($controlPlaneIsolationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase))
}

function Stop-TunaGuiRetainedAnalysisProcess {
    param([Parameter(Mandatory = $true)]$Process)

    try {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    } catch {
    }

    try {
        $Process.WaitForExit(5000) | Out-Null
    } catch {
    }
}

function Invoke-TunaGuiRetainedAnalysis {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$AnalysisDir,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    if ([string]::IsNullOrWhiteSpace($LogPath) -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        throw "Retained log slice was not available: $LogPath"
    }

    New-Item -ItemType Directory -Force -Path $AnalysisDir | Out-Null

    $stdoutPath = Join-Path $AnalysisDir 'filetransfer-retained-analysis.stdout.txt'
    $stderrPath = Join-Path $AnalysisDir 'filetransfer-retained-analysis.stderr.txt'
    $diagnosticsPath = Join-Path $AnalysisDir 'filetransfer-retained-analysis-diagnostics.json'
    $analysisTimeoutSeconds = 1200
    $startedUtc = [DateTimeOffset]::UtcNow
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $RepoRoot 'tools\FileTransfer-Ops.ps1'),
        '-Mode', 'AnalyzeRetained',
        '-LogPath', $LogPath,
        '-ArtifactDir', $AnalysisDir,
        '-TailMinutes', '0',
        '-LiveRouteProofMode', $LiveRouteProofMode)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powershell'
    $startInfo.Arguments = ($arguments | ForEach-Object {
            $argument = [string]$_
            if ($argument.Length -eq 0) {
                '""'
            }
            elseif ($argument.IndexOfAny([char[]]@(' ', "`t", '"')) -lt 0) {
                $argument
            }
            else {
                '"' + ($argument -replace '"', '\"') + '"'
            }
        }) -join ' '
    $startInfo.WorkingDirectory = $RepoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Failed to start retained analysis. Artifacts: $AnalysisDir"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $deadlineUtc = $startedUtc.AddSeconds($analysisTimeoutSeconds)
        while (-not $process.WaitForExit(5000)) {
            if (Test-TunaGuiRetainedAnalysisCompletedProofArtifacts -AnalysisDir $AnalysisDir -LogPath $LogPath -LiveRouteProofMode $LiveRouteProofMode) {
                Stop-TunaGuiRetainedAnalysisProcess -Process $process
                $diagnostics = [ordered]@{
                    completed_artifacts_observed_while_process_alive = 1
                    timed_out = 0
                    process_id = $process.Id
                    started_utc = $startedUtc.ToString('O')
                    ended_utc = [DateTimeOffset]::UtcNow.ToString('O')
                    artifact_dir = $AnalysisDir
                    log_path = $LogPath
                    live_route_proof_mode = $LiveRouteProofMode
                }
                $diagnostics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $AnalysisDir 'filetransfer-retained-analysis-completed-process-reaped.json') -Encoding UTF8
                return
            }

            if ([DateTimeOffset]::UtcNow -lt $deadlineUtc) {
                continue
            }

            Stop-TunaGuiRetainedAnalysisProcess -Process $process
            $diagnostics = [ordered]@{
                timed_out = 1
                timeout_seconds = $analysisTimeoutSeconds
                process_id = $process.Id
                started_utc = $startedUtc.ToString('O')
                ended_utc = [DateTimeOffset]::UtcNow.ToString('O')
                artifact_dir = $AnalysisDir
                log_path = $LogPath
                live_route_proof_mode = $LiveRouteProofMode
            }
            $diagnostics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $diagnosticsPath -Encoding UTF8
            Write-TunaGuiRetainedAnalysisTimeoutArtifacts `
                -AnalysisDir $AnalysisDir `
                -LogPath $LogPath `
                -TimeoutSeconds $analysisTimeoutSeconds `
                -ProcessId $process.Id `
                -StartedUtc $startedUtc.ToString('O') `
                -LiveRouteProofMode $LiveRouteProofMode | Out-Null

            throw "Retained analysis timed out after ${analysisTimeoutSeconds}s. Artifacts: $AnalysisDir"
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        Set-Content -LiteralPath $stdoutPath -Value $stdout -Encoding UTF8
        Set-Content -LiteralPath $stderrPath -Value $stderr -Encoding UTF8

        $diagnostics = [ordered]@{
            timed_out = 0
            exit_code = $process.ExitCode
            timeout_seconds = $analysisTimeoutSeconds
            process_id = $process.Id
            started_utc = $startedUtc.ToString('O')
            ended_utc = [DateTimeOffset]::UtcNow.ToString('O')
            artifact_dir = $AnalysisDir
            log_path = $LogPath
            live_route_proof_mode = $LiveRouteProofMode
        }
        $diagnostics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $diagnosticsPath -Encoding UTF8

        if ($process.ExitCode -ne 0) {
            throw "Retained analysis failed with exit code $($process.ExitCode). Artifacts: $AnalysisDir"
        }
    }
    finally {
        if ($process -is [System.IDisposable]) {
            $process.Dispose()
        }
    }
}

function Invoke-TunaGuiRetainedAnalysisBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$AnalysisDir,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    try {
        Invoke-TunaGuiRetainedAnalysis -RepoRoot $RepoRoot -AnalysisDir $AnalysisDir -LogPath $LogPath -LiveRouteProofMode $LiveRouteProofMode
    }
    catch {
        Write-Warning ("Retained analysis could not be completed for {0}: {1}" -f $LogPath, $_.Exception.Message)
    }
}

function Get-TunaGuiRetainedLogLineSortKey {
    param(
        [Parameter(Mandatory = $true)][string]$Line,
        [Parameter(Mandatory = $true)][long]$FallbackTicks
    )

    $match = [regex]::Match($Line, '^\[(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z)\]')
    if ($match.Success) {
        try {
            return [DateTimeOffset]::ParseExact(
                $match.Groups['timestamp'].Value,
                'yyyy-MM-dd HH:mm:ss\Z',
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::AssumeUniversal).UtcTicks
        }
        catch {
        }
    }

    return $FallbackTicks
}

function Sort-TunaGuiRetainedLogLinesChronologically {
    param([AllowEmptyCollection()][object[]]$Lines)

    $records = New-Object System.Collections.Generic.List[object]
    $ordinal = 0L
    foreach ($line in @($Lines)) {
        $text = [string]$line
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        $records.Add([pscustomobject]@{
            Line = $text
            SortTicks = Get-TunaGuiRetainedLogLineSortKey -Line $text -FallbackTicks ([long]::MaxValue)
            Ordinal = $ordinal
        }) | Out-Null
        $ordinal++
    }

    return @($records | Sort-Object -Property SortTicks, Ordinal | ForEach-Object { [string]$_.Line })
}

function Get-TunaGuiStrictUtf8LineCount {
    param([Parameter(Mandatory = $true)][string]$Path)

    $reader = [System.IO.StreamReader]::new(
        $Path,
        [System.Text.UTF8Encoding]::new($false, $true),
        $true,
        1MB)
    try {
        $count = 0L
        while ($null -ne $reader.ReadLine()) {
            $count++
        }

        return $count
    }
    finally {
        $reader.Dispose()
    }
}

function Write-TunaGuiDerivedAnalyzerInputReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$AnalyzerInputPath,
        [Parameter(Mandatory = $true)][string]$MilestonePath
    )

    $ledgerPath = Join-Path $ArtifactDir 'filetransfer-gui-log-ledger.json'
    if (-not (Test-Path -LiteralPath $ledgerPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $AnalyzerInputPath -PathType Leaf)) {
        return
    }

    $ledger = Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json
    if ($null -eq $ledger -or $null -eq $ledger.analyzerInput) {
        return
    }

    $milestoneExists = Test-Path -LiteralPath $MilestonePath -PathType Leaf
    $receipt = [ordered]@{
        schemaVersion = 1
        event = 'filetransfer_tuna_gui_derived_analyzer_input_receipt'
        sourceAnalyzerInputPath = [string]$ledger.analyzerInput.path
        sourceAnalyzerInputSha256 = [string]$ledger.analyzerInput.captureSha256
        sourceAnalyzerInputLineCount = [long]$ledger.analyzerInput.captureLineCount
        milestonePath = [System.IO.Path]::GetFullPath($MilestonePath)
        milestoneSha256 = $(if ($milestoneExists) { (Get-FileHash -LiteralPath $MilestonePath -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' })
        derivedAnalyzerInputPath = [System.IO.Path]::GetFullPath($AnalyzerInputPath)
        derivedAnalyzerInputSha256 = (Get-FileHash -LiteralPath $AnalyzerInputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        derivedAnalyzerInputLineCount = Get-TunaGuiStrictUtf8LineCount -Path $AnalyzerInputPath
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-TunaGuiAtomicJson `
        -Path (Join-Path $ArtifactDir 'filetransfer-retained-log-slice-derived-receipt.json') `
        -Value $receipt
}

function Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$FileName = 'filetransfer-retained-log-slice.log'
    )

    $milestonePath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-milestone-evidence.log'
    $retainedPath = Join-Path $ArtifactDir $FileName
    try {
        if (-not (Test-Path -LiteralPath $milestonePath -PathType Leaf)) {
            return
        }

        $milestoneLines = @(
            Get-Content -LiteralPath $milestonePath -ErrorAction SilentlyContinue |
                Where-Object {
                    $line = [string]$_
                    $line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    $line.IndexOf('event=filetransfer_live_route_epoch_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    $line.IndexOf('event=filetransfer_live_route_epoch_recovered', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                }
        )
        if ($milestoneLines.Count -eq 0) {
            return
        }

        $existingText = ''
        $existingLines = @()
        if (Test-Path -LiteralPath $retainedPath -PathType Leaf) {
            $existingText = Get-Content -LiteralPath $retainedPath -Raw -ErrorAction SilentlyContinue
            if (-not [string]::IsNullOrEmpty($existingText)) {
                $existingLines = @($existingText -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
            }
        }

        $missingLines = New-Object System.Collections.Generic.List[string]
        foreach ($line in @($milestoneLines)) {
            $text = [string]$line
            if ([string]::IsNullOrWhiteSpace($text)) {
                continue
            }

            if ($existingText.IndexOf($text, [System.StringComparison]::Ordinal) -lt 0) {
                $missingLines.Add($text) | Out-Null
            }
        }

        if ($missingLines.Count -eq 0) {
            return
        }

        $combined = Sort-TunaGuiRetainedLogLinesChronologically -Lines (@($missingLines.ToArray()) + @($existingLines))

        [System.IO.File]::WriteAllText($retainedPath, ($combined -join [Environment]::NewLine), [System.Text.Encoding]::UTF8)
    }
    finally {
        Write-TunaGuiDerivedAnalyzerInputReceipt `
            -ArtifactDir $ArtifactDir `
            -AnalyzerInputPath $retainedPath `
            -MilestonePath $milestonePath
    }
}

function Ensure-TunaGuiSecondTransferRetainedLogSlice {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    $secondRetainedPath = Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
    if (Test-Path -LiteralPath $secondRetainedPath -PathType Leaf) {
        return $secondRetainedPath
    }

    $retainedPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    $sourcePath = Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice-source.txt'
    if (Test-Path -LiteralPath $retainedPath -PathType Leaf) {
        $retainedLines = @(Get-Content -LiteralPath $retainedPath -ErrorAction SilentlyContinue)
        $startIndex = -1
        for ($i = $retainedLines.Count - 1; $i -ge 0; $i--) {
            $line = [string]$retainedLines[$i]
            if ($line.IndexOf('event=filetransfer_second_transfer_status', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=filetransfer_tuna_gui_second_transfer_fresh_readiness_required', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $startIndex = $i
                break
            }
        }

        if ($startIndex -ge 0) {
            $prefixLine = '[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=second_transfer_after_reactivation_slice_reconstructed'
            $sliceLines = @($prefixLine) + @($retainedLines[$startIndex..($retainedLines.Count - 1)])
            [System.IO.File]::WriteAllLines($secondRetainedPath, $sliceLines, [System.Text.Encoding]::UTF8)
            $sourceLines = @(
                'source=reconstructed_from_retained_log_slice',
                'reason=second_transfer_retained_slice_reconstructed',
                ('retained_log_slice={0}' -f $retainedPath),
                'retained_log_slice_exists=1',
                ('second_transfer_retained_log_slice={0}' -f $secondRetainedPath),
                'second_transfer_retained_log_slice_exists=1',
                ('start_index={0}' -f $startIndex)
            )
            [System.IO.File]::WriteAllLines($sourcePath, $sourceLines, [System.Text.Encoding]::UTF8)
            return $secondRetainedPath
        }
    }

    $sourceLines = @(
        'source=missing',
        'reason=second_transfer_retained_slice_missing',
        ('retained_log_slice={0}' -f $retainedPath),
        ('retained_log_slice_exists={0}' -f ($(if (Test-Path -LiteralPath $retainedPath -PathType Leaf) { '1' } else { '0' }))),
        ('second_transfer_retained_log_slice={0}' -f $secondRetainedPath),
        'second_transfer_retained_log_slice_exists=0'
    )
    [System.IO.File]::WriteAllLines($sourcePath, $sourceLines, [System.Text.Encoding]::UTF8)
    return $null
}

function Invoke-TunaGuiLiveRetainedAnalysisBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$RouteMode
    )

    if ($RouteMode -ne 'preactivated' -and
        $RouteMode -ne 'live-v4-switch-off' -and
        $RouteMode -ne 'live-multi-toggle' -and
        $RouteMode -ne 'live-reactivation-second-transfer' -and
        $RouteMode -ne 'live-regular-activation-cycle') {
        return
    }

    $retainedPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    $liveRouteProofMode = if ($RouteMode -eq 'live-v4-switch-off') { 'SwitchOff' } elseif ($RouteMode -eq 'live-multi-toggle') { 'MultiToggle' } elseif ($RouteMode -eq 'live-regular-activation-cycle') { 'RegularActivationCycle' } else { 'None' }
    Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice -ArtifactDir $ArtifactDir
    Invoke-TunaGuiRetainedAnalysisBestEffort -RepoRoot $RepoRoot -AnalysisDir $ArtifactDir -LogPath $retainedPath -LiveRouteProofMode $liveRouteProofMode

    if ($RouteMode -eq 'live-reactivation-second-transfer') {
        $secondRetainedPath = Ensure-TunaGuiSecondTransferRetainedLogSlice -ArtifactDir $ArtifactDir
        if (-not [string]::IsNullOrWhiteSpace([string]$secondRetainedPath)) {
            Invoke-TunaGuiRetainedAnalysisBestEffort -RepoRoot $RepoRoot -AnalysisDir (Join-Path $ArtifactDir 'second-transfer-analysis') -LogPath $secondRetainedPath
        }
        else {
            Write-Warning ("Second-transfer retained analysis could not be produced because retained evidence is missing. Artifact: {0}" -f (Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice-source.txt'))
        }
    }
}

function Invoke-TunaGuiMeasuredFallbackRetainedAnalysis {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    Invoke-TunaGuiRetainedAnalysis -RepoRoot $RepoRoot -AnalysisDir (Join-Path $ArtifactDir 'measured-fallback-analysis') -LogPath $LogPath
}

function Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    try {
        $slices = Select-TunaGuiControlledRestartLogSlices -ArtifactDir $ArtifactDir
        if (-not [string]::IsNullOrWhiteSpace([string]$slices.SetupPath) -and
            (Test-Path -LiteralPath ([string]$slices.SetupPath) -PathType Leaf)) {
            Invoke-TunaGuiRetainedAnalysisBestEffort -RepoRoot $RepoRoot -AnalysisDir (Join-Path $ArtifactDir 'setup-analysis') -LogPath $slices.SetupPath
        }

        if (-not [string]::IsNullOrWhiteSpace([string]$slices.MeasuredPath) -and
            (Test-Path -LiteralPath ([string]$slices.MeasuredPath) -PathType Leaf)) {
            Invoke-TunaGuiMeasuredFallbackRetainedAnalysis -RepoRoot $RepoRoot -ArtifactDir $ArtifactDir -LogPath $slices.MeasuredPath
        }
    }
    catch {
        Write-Warning ("Measured fallback retained analysis could not be completed: {0}" -f $_.Exception.Message)
    }
}

function Read-TunaGuiReportValue {
    param(
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$DefaultValue = ''
    )

    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        return $DefaultValue
    }

    foreach ($line in @(Get-Content -LiteralPath $ReportPath)) {
        $text = [string]$line
        if ($text.StartsWith(($Key + '='), [System.StringComparison]::Ordinal)) {
            return $text.Substring($Key.Length + 1)
        }
    }

    return $DefaultValue
}

function Count-TunaGuiReportMatchingLines {
    param(
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        return 0
    }

    return @((Get-Content -LiteralPath $ReportPath) | Select-String -Pattern $Pattern).Count
}

function Split-TunaGuiTokenList {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq '(none)') {
        return @()
    }

    return @(
        $Value.Split([char]',', [System.StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' }
    )
}

function Get-TunaGuiLogEventCount {
    param(
        [string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$EventName
    )

    return @($Lines | Where-Object { ([string]$_).IndexOf(('event=' + $EventName), [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count
}

function Get-TunaGuiMeasuredFallbackDiagnostics {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    $lines = @()
    if (Test-Path -LiteralPath $measuredPath -PathType Leaf) {
        $lines = @(Get-Content -LiteralPath $measuredPath)
    }

    $lastCommittedChunk = -1
    $highestObservedChunk = -1
    $finalTerminalState = '(none)'
    $bridgeQueueClearCount = 0
    $payloadBytes = 0
    foreach ($line in $lines) {
        $text = [string]$line
        if ($text -match 'file_size_bytes=([0-9]+)') {
            $payloadBytes = [Math]::Max($payloadBytes, [long]$Matches[1])
        }

        if ($text -match 'next_chunk_index=([0-9]+)') {
            $lastCommittedChunk = [Math]::Max($lastCommittedChunk, [int]$Matches[1])
        }

        if ($text -match 'highest_received_chunk_index=([0-9]+)') {
            $highestObservedChunk = [Math]::Max($highestObservedChunk, [int]$Matches[1])
        }

        if ($text.IndexOf('event=file_transfer_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $text.IndexOf('_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $text -match 'state=([^; ]+)') {
            $finalTerminalState = [string]$Matches[1]
        }

        if ($text.IndexOf('event=nkn_bridge_bulk_send_summary', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $text -match 'queue_clears=([0-9]+)') {
            $bridgeQueueClearCount += [int]$Matches[1]
        }

        if ($text.IndexOf('event=nkn_bridge_bulk_queue_state', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $text -match 'cleared_since_last=([0-9]+)') {
            $bridgeQueueClearCount += [int]$Matches[1]
        }
    }

    $frontierRequestCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_frontier_request_sent'
    $v6ChunkSendTimeoutCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_chunk_batch_send_timeout'
    $payloadMiB = if ($payloadBytes -gt 0) { [double]$payloadBytes / 1048576.0 } else { 0.0 }
    $operatorWarningKinds = @()
    $operatorVerdictPath = Join-Path $ArtifactDir 'measured-fallback-analysis\filetransfer-operator-verdict.txt'
    if (Test-Path -LiteralPath $operatorVerdictPath -PathType Leaf) {
        $operatorWarningKinds = @(Split-TunaGuiTokenList -Value (Read-TunaGuiReportValue -ReportPath $operatorVerdictPath -Key 'warning_kinds' -DefaultValue '(none)'))
    }

    return [ordered]@{
        measuredSlicePresent = (Test-Path -LiteralPath $measuredPath -PathType Leaf)
        lastCommittedChunk = $lastCommittedChunk
        highestObservedChunk = $highestObservedChunk
        frontierRequestCount = $frontierRequestCount
        duplicateFrontierRequestCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_frontier_request_duplicate_ignored'
        receiverStateDeferredCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_receiver_state_deferred'
        receiverStateCoalescedCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_receiver_state_coalesced'
        v6ChunkSendTimeoutCount = $v6ChunkSendTimeoutCount
        bridgeQueueClearCount = $bridgeQueueClearCount
        fallbackWarningKinds = @($operatorWarningKinds)
        sendTimeoutsPerMiB = if ($payloadMiB -gt 0.0) { [Math]::Round([double]$v6ChunkSendTimeoutCount / $payloadMiB, 3) } else { 0.0 }
        frontierRequestsPerMiB = if ($payloadMiB -gt 0.0) { [Math]::Round([double]$frontierRequestCount / $payloadMiB, 3) } else { 0.0 }
        fallbackRescueFreezeCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_post_tuna_fallback_normal_send_ahead_freeze_started'
        fallbackRescueWidenCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_post_tuna_fallback_frontier_rescue_widened'
        finalTerminalState = $finalTerminalState
    }
}

function Resolve-TunaGuiControlledRestartFailurePhase {
    param(
        [AllowNull()]$Summary,
        [AllowNull()]$ErrorSummary,
        [object]$FallbackDiagnostics
    )

    $errorText = ''
    if ($null -ne $ErrorSummary -and $ErrorSummary.PSObject.Properties.Name -contains 'error') {
        $errorText = [string]$ErrorSummary.error
    }

    if (-not [bool]$FallbackDiagnostics['measuredSlicePresent']) {
        return 'measured_not_started'
    }

    if ($errorText.IndexOf('progress', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $errorText.IndexOf('timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'measured_progress_timeout'
    }

    if ($null -ne $Summary -and
        ($Summary.PSObject.Properties.Name -contains 'completed') -and
        ($Summary.PSObject.Properties.Name -contains 'integrityOk') -and
        [bool]$Summary.completed -and
        [bool]$Summary.integrityOk) {
        return 'measured_completed_with_warnings'
    }

    if ([string]::Equals([string]$FallbackDiagnostics['finalTerminalState'], 'Completed', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'measured_completed_with_warnings'
    }

    return 'measured_terminal_failure'
}

function Test-TunaGuiControlledSetupCancelAccepted {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [AllowNull()]$Summary
    )

    if ($null -eq $Summary -or
        -not ($Summary.PSObject.Properties.Name -contains 'setupPhase') -or
        $null -eq $Summary.setupPhase) {
        return $false
    }

    $setup = $Summary.setupPhase
    $route = if ($setup.PSObject.Properties.Name -contains 'route') { [string]$setup.route } else { '' }
    $protocol = if ($setup.PSObject.Properties.Name -contains 'protocolVersion') { [int]$setup.protocolVersion } else { 0 }
    $inboundState = if ($setup.PSObject.Properties.Name -contains 'inboundState') { [string]$setup.inboundState } else { '' }
    $outboundState = if ($setup.PSObject.Properties.Name -contains 'outboundState') { [string]$setup.outboundState } else { '' }
    $inboundError = if ($setup.PSObject.Properties.Name -contains 'inboundErrorCode') { [string]$setup.inboundErrorCode } else { '' }
    $outboundError = if ($setup.PSObject.Properties.Name -contains 'outboundErrorCode') { [string]$setup.outboundErrorCode } else { '' }
    $setupRouteAccepted = ($route -eq 'file_tuna_v4' -and $protocol -eq 4) -or
        ($route -eq 'post_tuna_fallback_v6' -and $protocol -eq 6)
    if (-not $setupRouteAccepted -or
        $inboundState -ne 'Canceled' -or
        $outboundState -ne 'Canceled' -or
        $inboundError -ne 'canceled_remote' -or
        $outboundError -ne 'canceled_local') {
        return $false
    }

    $setupPath = Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log'
    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $measuredPath -PathType Leaf)) {
        return $false
    }

    $setupClosed = @(Get-Content -LiteralPath $setupPath | Where-Object { ([string]$_).IndexOf('phase=setup_file_tuna_v4_cleanup_closed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
    $measuredStarted = @(Get-Content -LiteralPath $measuredPath | Where-Object { ([string]$_).IndexOf('phase=measured_post_tuna_fallback_v6_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
    return $setupClosed -and $measuredStarted
}

function Write-TunaGuiControlledRestartFailureSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$RouteMode = 'v4-restart-v6-fallback'
    )

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        return
    }

    $errorPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json'
    $errorSummary = $null
    if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
        try {
            $errorSummary = Get-Content -LiteralPath $errorPath -Raw | ConvertFrom-Json
        }
        catch {
            $errorSummary = $null
        }
    }

    $diagnostics = Get-TunaGuiMeasuredFallbackDiagnostics -ArtifactDir $ArtifactDir
    $failurePhase = Resolve-TunaGuiControlledRestartFailurePhase -Summary $null -ErrorSummary $errorSummary -FallbackDiagnostics $diagnostics
    $summary = [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_summary'
        routeMode = $RouteMode
        completed = $false
        integrityOk = $false
        fallbackFailurePhase = $failurePhase
        fallbackFailureReason = if ($null -ne $errorSummary -and $errorSummary.PSObject.Properties.Name -contains 'error') { [string]$errorSummary.error } else { '(missing_summary)' }
        fallbackDiagnostics = $diagnostics
    }

    Write-TunaGuiAtomicJson -Path $summaryPath -Value $summary
}

function Write-TunaGuiFailureSummaryIfMissing {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$RouteMode = 'v4-restart-v6-fallback',
        [string]$SetupFailurePhase = 'progress_failure',
        [string]$SetupFailureReason = 'gui_smoke_failed'
    )

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        return
    }

    $errorPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json'
    $errorSummary = $null
    if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
        try {
            $errorSummary = Get-Content -LiteralPath $errorPath -Raw | ConvertFrom-Json
        }
        catch {
            $errorSummary = $null
        }
    }

    if ($null -ne $errorSummary) {
        if ($errorSummary.PSObject.Properties.Name -contains 'setupFailurePhase' -and
            -not [string]::IsNullOrWhiteSpace([string]$errorSummary.setupFailurePhase) -and
            -not [string]::Equals([string]$errorSummary.setupFailurePhase, 'unknown', [System.StringComparison]::OrdinalIgnoreCase)) {
            $SetupFailurePhase = [string]$errorSummary.setupFailurePhase
        }
        elseif ($errorSummary.PSObject.Properties.Name -contains 'failurePhase' -and
            -not [string]::IsNullOrWhiteSpace([string]$errorSummary.failurePhase) -and
            -not [string]::Equals([string]$errorSummary.failurePhase, 'unknown', [System.StringComparison]::OrdinalIgnoreCase)) {
            $SetupFailurePhase = [string]$errorSummary.failurePhase
        }

        if ($errorSummary.PSObject.Properties.Name -contains 'setupFailureReason' -and
            -not [string]::IsNullOrWhiteSpace([string]$errorSummary.setupFailureReason) -and
            -not [string]::Equals([string]$errorSummary.setupFailureReason, 'unknown', [System.StringComparison]::OrdinalIgnoreCase)) {
            $SetupFailureReason = [string]$errorSummary.setupFailureReason
        }
        elseif ($errorSummary.PSObject.Properties.Name -contains 'failureReason' -and
            -not [string]::IsNullOrWhiteSpace([string]$errorSummary.failureReason) -and
            -not [string]::Equals([string]$errorSummary.failureReason, 'unknown', [System.StringComparison]::OrdinalIgnoreCase)) {
            $SetupFailureReason = [string]$errorSummary.failureReason
        }
        elseif ($errorSummary.PSObject.Properties.Name -contains 'error' -and
            -not [string]::IsNullOrWhiteSpace([string]$errorSummary.error)) {
            $SetupFailureReason = [string]$errorSummary.error
        }
    }

    $faultWindow = [ordered]@{
        terminalCompletedBeforeFault = $false
        faultInjected = $false
        faultActionClaimed = $false
        faultActionInvoked = $false
        faultRuntimeEvidenceObserved = $false
        faultInjectedBeforeTerminal = $false
    }
    $milestonePath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-milestone-evidence.log'
    if (Test-Path -LiteralPath $milestonePath -PathType Leaf) {
        $faultLine = Get-Content -LiteralPath $milestonePath |
            Where-Object { ([string]$_).IndexOf('event=filetransfer_tuna_gui_fault_window', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } |
            Select-Object -Last 1
        if (-not [string]::IsNullOrWhiteSpace([string]$faultLine)) {
            foreach ($entry in @(
                    @{ Property = 'terminalCompletedBeforeFault'; Field = 'terminal_before_click' },
                    @{ Property = 'faultActionClaimed'; Field = 'fault_action_claimed' },
                    @{ Property = 'faultActionInvoked'; Field = 'fault_action_invoked' },
                    @{ Property = 'faultRuntimeEvidenceObserved'; Field = 'fault_runtime_evidence_observed' },
                    @{ Property = 'faultInjectedBeforeTerminal'; Field = 'fault_injected_before_terminal' })) {
                $match = [regex]::Match([string]$faultLine, ('(?:^|;\s*){0}=([^;]*)' -f [regex]::Escape([string]$entry.Field)), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if ($match.Success) {
                    $faultWindow[[string]$entry.Property] = [string]::Equals($match.Groups[1].Value.Trim(), '1', [System.StringComparison]::Ordinal)
                }
            }
            $faultWindow.faultInjected = [bool]$faultWindow.faultInjectedBeforeTerminal
            if ([bool]$faultWindow.faultActionInvoked -and -not [bool]$faultWindow.faultInjectedBeforeTerminal) {
                $faultWindow.terminalCompletedBeforeFault = $true
            }
        }
    }

    $summary = [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_summary'
        routeMode = $RouteMode
        completed = $false
        integrityOk = $false
        setupFailurePhase = $SetupFailurePhase
        setupFailureReason = $SetupFailureReason
        inboundState = '(unknown)'
        outboundState = '(unknown)'
        inboundErrorCode = '(unknown)'
        outboundErrorCode = '(unknown)'
        terminalCompletedBeforeFault = [bool]$faultWindow.terminalCompletedBeforeFault
        faultInjected = [bool]$faultWindow.faultInjected
        faultActionClaimed = [bool]$faultWindow.faultActionClaimed
        faultActionInvoked = [bool]$faultWindow.faultActionInvoked
        faultRuntimeEvidenceObserved = [bool]$faultWindow.faultRuntimeEvidenceObserved
        faultInjectedBeforeTerminal = [bool]$faultWindow.faultInjectedBeforeTerminal
    }

    Write-TunaGuiAtomicJson -Path $summaryPath -Value $summary
}

function Test-TunaGuiCurrentSummary {
    param(
        $Summary,
        [Parameter(Mandatory = $true)][string]$RouteMode
    )

    return $null -ne $Summary -and
        $Summary.PSObject.Properties.Name -contains 'event' -and
        [string]::Equals(
            [string]$Summary.event,
            'filetransfer_tuna_gui_handoff_fallback_summary',
            [System.StringComparison]::Ordinal) -and
        $Summary.PSObject.Properties.Name -contains 'routeMode' -and
        [string]::Equals([string]$Summary.routeMode, $RouteMode, [System.StringComparison]::Ordinal) -and
        $Summary.PSObject.Properties.Name -contains 'completed' -and
        $Summary.completed -is [bool] -and
        [bool]$Summary.completed -and
        $Summary.PSObject.Properties.Name -contains 'integrityOk' -and
        $Summary.integrityOk -is [bool] -and
        [bool]$Summary.integrityOk
}

function Update-TunaGuiControlledRestartSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$FilteredSetupCleanupLineCount
    )

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $setupVerdictPath = Join-Path $ArtifactDir 'setup-analysis\filetransfer-operator-verdict.txt'
    $measuredVerdictPath = Join-Path $ArtifactDir 'measured-fallback-analysis\filetransfer-operator-verdict.txt'
    $measuredRoutePath = Join-Path $ArtifactDir 'measured-fallback-analysis\filetransfer-route-consistency-summary.txt'
    $measuredStabilityPath = Join-Path $ArtifactDir 'measured-fallback-analysis\stability-gates-summary.txt'
    $fallbackDiagnostics = Get-TunaGuiMeasuredFallbackDiagnostics -ArtifactDir $ArtifactDir
    $fallbackFailurePhase = Resolve-TunaGuiControlledRestartFailurePhase -Summary $summary -ErrorSummary $null -FallbackDiagnostics $fallbackDiagnostics
    $setupRawOperatorVerdict = Read-TunaGuiReportValue -ReportPath $setupVerdictPath -Key 'verdict' -DefaultValue '(missing)'
    $setupControlledCancelAccepted = Test-TunaGuiControlledSetupCancelAccepted -ArtifactDir $ArtifactDir -Summary $summary
    $setupNormalizedVerdict = if ($setupControlledCancelAccepted) { 'expected_controlled_setup_cancel' } else { $setupRawOperatorVerdict }
    $fallbackDiagnostics['setupNormalizedVerdict'] = $setupNormalizedVerdict
    $summary | Add-Member -NotePropertyName controlledRestartAnalysis -NotePropertyValue ([ordered]@{
        setupVerdict = $setupRawOperatorVerdict
        setupRawOperatorVerdict = $setupRawOperatorVerdict
        setupControlledCancelAccepted = $setupControlledCancelAccepted
        setupNormalizedVerdict = $setupNormalizedVerdict
        measuredRouteVerdict = Read-TunaGuiReportValue -ReportPath $measuredRoutePath -Key 'route_consistency_verdict' -DefaultValue '(missing)'
        measuredOperatorVerdict = Read-TunaGuiReportValue -ReportPath $measuredVerdictPath -Key 'verdict' -DefaultValue '(missing)'
        setupCleanupWarningCount = $FilteredSetupCleanupLineCount
        fallbackBridgeRecoveryWarningCount = Count-TunaGuiReportMatchingLines -ReportPath $measuredStabilityPath -Pattern 'recovered post-Tuna fallback bridge queue clear'
    }) -Force
    $summary | Add-Member -NotePropertyName setupNormalizedVerdict -NotePropertyValue $setupNormalizedVerdict -Force
    $summary | Add-Member -NotePropertyName fallbackFailurePhase -NotePropertyValue $fallbackFailurePhase -Force
    $summary | Add-Member -NotePropertyName fallbackFailureReason -NotePropertyValue '(none)' -Force
    $summary | Add-Member -NotePropertyName fallbackDiagnostics -NotePropertyValue $fallbackDiagnostics -Force
    Write-TunaGuiAtomicJson -Path $summaryPath -Value $summary
}

function Write-TunaGuiDirectEvidenceManifest {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [AllowEmptyString()][string]$ExePath = '',
        [int]$GuiExitCode = -1,
        [bool]$GuiExitCodeAvailable = $false
    )

    if ([string]::IsNullOrWhiteSpace($ArtifactDir) -or -not (Test-Path -LiteralPath $ArtifactDir -PathType Container)) {
        return
    }

    $statePath = Join-Path $ArtifactDir 'filetransfer-gui-evidence-finalization-state.json'
    $ledgerPath = Join-Path $ArtifactDir 'filetransfer-gui-log-ledger.json'
    $capturePath = Join-Path $ArtifactDir 'filetransfer-gui-log-ledger-slice.log'
    $analyzerInputPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    $derivedAnalyzerInputReceiptPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice-derived-receipt.json'
    $milestonePath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-milestone-evidence.log'
    $state = if (Test-Path -LiteralPath $statePath -PathType Leaf) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } else { $null }
    $ledger = if (Test-Path -LiteralPath $ledgerPath -PathType Leaf) { Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json } else { $null }
    $captureSha = if (Test-Path -LiteralPath $capturePath -PathType Leaf) { (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    $analyzerInputSha = if (Test-Path -LiteralPath $analyzerInputPath -PathType Leaf) { (Get-FileHash -LiteralPath $analyzerInputPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    $derivedAnalyzerInputReceipt = if (Test-Path -LiteralPath $derivedAnalyzerInputReceiptPath -PathType Leaf) { Get-Content -LiteralPath $derivedAnalyzerInputReceiptPath -Raw | ConvertFrom-Json } else { $null }
    $ledgerSha = if (Test-Path -LiteralPath $ledgerPath -PathType Leaf) { (Get-FileHash -LiteralPath $ledgerPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    $writerEvidenceSlice = if ($null -ne $ledger -and $ledger.PSObject.Properties.Name -contains 'writerEvidenceSlice') {
        $ledger.writerEvidenceSlice
    }
    else {
        $null
    }
    $stateCaptureSha = if ($null -ne $state -and $state.PSObject.Properties.Name -contains 'captureSha256') { [string]$state.captureSha256 } else { '' }
    $ledgerCaptureSha = if ($null -ne $ledger -and $null -ne $ledger.retainedSlice) { [string]$ledger.retainedSlice.sha256 } else { '' }
    $ledgerAnalyzerInputSha = if ($null -ne $ledger -and $null -ne $ledger.analyzerInput) { [string]$ledger.analyzerInput.captureSha256 } else { '' }
    $captureMatches = -not [string]::IsNullOrWhiteSpace($captureSha) -and
        [string]::Equals($captureSha, $ledgerCaptureSha, [System.StringComparison]::OrdinalIgnoreCase) -and
        ([string]::IsNullOrWhiteSpace($stateCaptureSha) -or [string]::Equals($captureSha, $stateCaptureSha, [System.StringComparison]::OrdinalIgnoreCase))
    $analyzerInputMatches = -not [string]::IsNullOrWhiteSpace($analyzerInputSha) -and
        [string]::Equals($analyzerInputSha, $ledgerAnalyzerInputSha, [System.StringComparison]::OrdinalIgnoreCase)
    if ($null -ne $derivedAnalyzerInputReceipt) {
        $milestoneSha = if (Test-Path -LiteralPath $milestonePath -PathType Leaf) { (Get-FileHash -LiteralPath $milestonePath -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
        $analyzerInputLineCount = if (Test-Path -LiteralPath $analyzerInputPath -PathType Leaf) { Get-TunaGuiStrictUtf8LineCount -Path $analyzerInputPath } else { -1L }
        $analyzerInputMatches =
            [int]$derivedAnalyzerInputReceipt.schemaVersion -eq 1 -and
            [string]::Equals([string]$derivedAnalyzerInputReceipt.event, 'filetransfer_tuna_gui_derived_analyzer_input_receipt', [System.StringComparison]::Ordinal) -and
            [string]::Equals([string]$derivedAnalyzerInputReceipt.sourceAnalyzerInputSha256, $ledgerAnalyzerInputSha, [System.StringComparison]::OrdinalIgnoreCase) -and
            [long]$derivedAnalyzerInputReceipt.sourceAnalyzerInputLineCount -eq [long]$ledger.analyzerInput.captureLineCount -and
            [string]::Equals([string]$derivedAnalyzerInputReceipt.derivedAnalyzerInputPath, [System.IO.Path]::GetFullPath($analyzerInputPath), [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals([string]$derivedAnalyzerInputReceipt.derivedAnalyzerInputSha256, $analyzerInputSha, [System.StringComparison]::OrdinalIgnoreCase) -and
            [long]$derivedAnalyzerInputReceipt.derivedAnalyzerInputLineCount -eq $analyzerInputLineCount -and
            [string]::Equals([string]$derivedAnalyzerInputReceipt.milestonePath, [System.IO.Path]::GetFullPath($milestonePath), [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals([string]$derivedAnalyzerInputReceipt.milestoneSha256, $milestoneSha, [System.StringComparison]::OrdinalIgnoreCase)
    }
    $artifactFinalized = $null -ne $state -and
        -not ([bool]$state.timedOut) -and
        [string]$state.lastCompletedStage -eq 'gui_child_exited' -and
        $null -ne $ledger -and
        $captureMatches -and
        $analyzerInputMatches
    $cursorPass = $null -ne $ledger -and [string]$ledger.cursorIntegrityVerdict -eq 'pass'
    $writerPass = $null -ne $ledger -and [int]$ledger.writerEvidenceVersion -eq 3 -and [string]$ledger.writerIntegrityVerdict -eq 'pass'
    $analysisDirs = if ($RouteMode -eq 'v4-restart-v6-fallback') {
        @((Join-Path $ArtifactDir 'measured-fallback-analysis'))
    }
    elseif ($RouteMode -eq 'live-reactivation-second-transfer') {
        @($ArtifactDir, (Join-Path $ArtifactDir 'second-transfer-analysis'))
    }
    else {
        @($ArtifactDir)
    }
    $analysisStatuses = @(
        foreach ($analysisDir in $analysisDirs) {
            $timeoutSummaryPath = Join-Path $analysisDir 'filetransfer-retained-analysis-timeout-summary.json'
            $completedReapedPath = Join-Path $analysisDir 'filetransfer-retained-analysis-completed-process-reaped.json'
            $diagnosticsPath = Join-Path $analysisDir 'filetransfer-retained-analysis-diagnostics.json'
            if (Test-Path -LiteralPath $timeoutSummaryPath -PathType Leaf) {
                'timed_out'
                continue
            }
            if (Test-Path -LiteralPath $completedReapedPath -PathType Leaf) {
                'completed'
                continue
            }
            if (-not (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf)) {
                'not_started'
                continue
            }

            try {
                $analysisDiagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw | ConvertFrom-Json
                if ($analysisDiagnostics.PSObject.Properties.Name -contains 'timed_out' -and
                    [int]$analysisDiagnostics.timed_out -ne 0) {
                    'timed_out'
                }
                elseif ($analysisDiagnostics.PSObject.Properties.Name -contains 'exit_code' -and
                    [int]$analysisDiagnostics.exit_code -eq 0) {
                    'completed'
                }
                elseif ($analysisDiagnostics.PSObject.Properties.Name -contains 'exit_code') {
                    'failed'
                }
                else {
                    'in_progress'
                }
            }
            catch {
                'failed'
            }
        }
    )
    $retainedAnalysisStatus = if ($analysisStatuses -contains 'timed_out') {
        'timed_out'
    }
    elseif ($analysisStatuses -contains 'failed') {
        'failed'
    }
    elseif ($analysisStatuses.Count -gt 0 -and @($analysisStatuses | Where-Object { $_ -ne 'completed' }).Count -eq 0) {
        'completed'
    }
    elseif ($analysisStatuses -contains 'completed' -or $analysisStatuses -contains 'in_progress') {
        'in_progress'
    }
    else {
        'not_started'
    }
    $retainedAnalysisCompleted = $retainedAnalysisStatus -eq 'completed'
    $sourceCommit = ''
    $sourceDirty = $true
    try {
        $sourceCommit = [string](& git -C $RepoRoot rev-parse HEAD 2>$null)
        $sourceDirty = @(& git -C $RepoRoot status --porcelain 2>$null).Count -gt 0
    }
    catch {}
    $currentRuntimeProof = $artifactFinalized -and
        $cursorPass -and
        $writerPass -and
        $retainedAnalysisCompleted -and
        -not $sourceDirty

    $hashFile = {
        param([string]$Path)
        if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        event = 'filetransfer_tuna_gui_direct_evidence_manifest'
        acceptanceRunId = [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
        scenarioRunId = [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
        sourceCommit = $sourceCommit.Trim()
        sourceDirty = $sourceDirty
        guiExitCode = $GuiExitCode
        guiExitCodeAvailable = $GuiExitCodeAvailable
        routeMode = $RouteMode
        payloadSize = $PayloadSize
        direction = $Direction
        payerMode = $PayerMode
        fault = $Fault
        liveToggleSequence = $LiveToggleSequence
        executablePath = $ExePath
        executableSha256 = & $hashFile $ExePath
        harnessSha256 = & $hashFile (Join-Path $RepoRoot 'tools\GuiSmoke-Windows.ps1')
        wrapperSha256 = & $hashFile (Join-Path $RepoRoot 'tools\Run-FileTransferTunaGuiSmoke.ps1')
        finalizationStatePath = $statePath
        ledgerPath = $ledgerPath
        ledgerSha256 = $ledgerSha
        writerEvidenceSlicePath = $(if ($null -ne $writerEvidenceSlice) { [string]$writerEvidenceSlice.path } else { '' })
        writerEvidenceSliceSha256 = $(if ($null -ne $writerEvidenceSlice) { [string]$writerEvidenceSlice.sha256 } else { '' })
        writerEvidenceSliceLineCount = $(if ($null -ne $writerEvidenceSlice) { [long]$writerEvidenceSlice.lineCount } else { -1L })
        writerChronologyInversionCount = $(if ($null -ne $ledger) { [long]$ledger.writerChronologyInversionCount } else { -1L })
        writerChronologyCollisionCount = $(if ($null -ne $ledger) { [long]$ledger.writerChronologyCollisionCount } else { -1L })
        writerChronologyVerdict = $(if ($null -ne $ledger) { [string]$ledger.writerChronologyVerdict } else { '' })
        capturePath = $capturePath
        captureSha256 = $captureSha
        analyzerInputPath = $analyzerInputPath
        analyzerInputSha256 = $analyzerInputSha
        derivedAnalyzerInputReceiptPath = $derivedAnalyzerInputReceiptPath
        derivedAnalyzerInputReceiptSha256 = & $hashFile $derivedAnalyzerInputReceiptPath
        artifactFinalized = $artifactFinalized
        cursorIntegrityVerdict = $(if ($cursorPass) { 'pass' } else { 'fail' })
        writerEvidenceVersion = $(if ($null -ne $ledger) { [int]$ledger.writerEvidenceVersion } else { 0 })
        writerIntegrityVerdict = $(if ($writerPass) { 'pass' } else { 'fail' })
        currentRuntimeProof = $currentRuntimeProof
        retainedAnalysisCompleted = $retainedAnalysisCompleted
        retainedAnalysisStatus = $retainedAnalysisStatus
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-TunaGuiAtomicJson -Path (Join-Path $ArtifactDir 'filetransfer-tuna-gui-evidence-manifest.json') -Value $manifest
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$resolvedArtifactDir = ''
$resolvedExe = ''
$guiSmokeExitCode = -1
$guiSmokeExitCodeAvailable = $false
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($WalletPassword)) {
        $WalletPassword = [string]$env:NLINK_TUNA_TEST_WALLET_PASSWORD
    }

    if ([string]::IsNullOrWhiteSpace($WalletPassword)) {
        throw 'Provide -WalletPassword or set NLINK_TUNA_TEST_WALLET_PASSWORD.'
    }

    if ($Build) {
        & "$env:ProgramFiles\PowerShell\7\pwsh.exe" -NoProfile -ExecutionPolicy Bypass -File ".\installer\Build-Portable.ps1" -Runtime win-x64
    }

    $resolvedWallet = (Resolve-Path -LiteralPath $WalletPath).Path
    $resolvedSidecar = (Resolve-Path -LiteralPath $SidecarPath).Path
    $sidecarManifest = Join-Path ([System.IO.Path]::GetDirectoryName($resolvedSidecar)) 'tuna-sidecar-manifest.json'
    if (-not (Test-Path -LiteralPath $sidecarManifest)) {
        $packagedSidecar = Join-Path $repoRoot 'artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe'
        $packagedManifest = Join-Path ([System.IO.Path]::GetDirectoryName($packagedSidecar)) 'tuna-sidecar-manifest.json'
        if (-not (Test-Path -LiteralPath $packagedSidecar) -or -not (Test-Path -LiteralPath $packagedManifest)) {
            throw "Tuna sidecar manifest missing beside '$resolvedSidecar'. Build portable/installer first."
        }

        $resolvedSidecar = (Resolve-Path -LiteralPath $packagedSidecar).Path
        Write-Host "[Tuna GUI] Using packaged sidecar with manifest: $resolvedSidecar" -ForegroundColor DarkGray
    }
    $resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
    $exeDirectory = [System.IO.Path]::GetDirectoryName($resolvedExe)
    $bundledBridge = Join-Path $exeDirectory 'bridge\win-x64\index.js'
    if (-not (Test-Path -LiteralPath $bundledBridge)) {
        $repoBridgeDir = Join-Path $repoRoot 'artifacts\bridge\win-x64'
        $repoBridge = Join-Path $repoBridgeDir 'index.js'
        if (-not (Test-Path -LiteralPath $repoBridge)) {
            $repoBridgeDir = Join-Path $repoRoot 'artifacts\portable\nLink\win-x64\bridge\win-x64'
            $repoBridge = Join-Path $repoBridgeDir 'index.js'
        }

        if (-not (Test-Path -LiteralPath $repoBridge)) {
            throw "NKN bridge bundle not found beside ExePath or under repo artifacts. Build portable/installer first."
        }

        $repoNode = Join-Path $repoBridgeDir 'node.exe'
        if (-not (Test-Path -LiteralPath $repoNode)) {
            throw "NKN Node runtime not found beside repo bridge bundle. Build portable/installer first."
        }

        $env:NLINK_NKN_BRIDGE_PATH = (Resolve-Path -LiteralPath $repoBridge).Path
        $env:NLINK_NKN_NODE_PATH = (Resolve-Path -LiteralPath $repoNode).Path
        Write-Host "[Tuna GUI] Using bridge override: $($env:NLINK_NKN_BRIDGE_PATH)" -ForegroundColor DarkGray
        Write-Host "[Tuna GUI] Using node override: $($env:NLINK_NKN_NODE_PATH)" -ForegroundColor DarkGray
    }

    if ([string]::IsNullOrWhiteSpace($ArtifactDir)) {
        $timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
        $ArtifactDir = Join-Path $repoRoot "artifacts\gui-smoke\tuna-filetransfer-$timestamp"
    }

    $resolvedArtifactDir = [System.IO.Path]::GetFullPath($ArtifactDir)
    $repoArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    if (-not $resolvedArtifactDir.StartsWith($repoArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ArtifactDir must resolve under repo artifacts/: $resolvedArtifactDir"
    }

    New-Item -ItemType Directory -Force -Path $resolvedArtifactDir | Out-Null
    $receivedRoot = Join-Path $resolvedArtifactDir 'received'
    New-Item -ItemType Directory -Force -Path $receivedRoot | Out-Null

    if ([string]::IsNullOrWhiteSpace([string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID) -or
        [string]::Equals([string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
        $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'direct-tuna-' + [guid]::NewGuid().ToString('N')
    }
    if ([string]::IsNullOrWhiteSpace([string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID) -or
        [string]::Equals([string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
        $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'filetransfer-tuna-' + [guid]::NewGuid().ToString('N')
    }

    $env:NLINK_RUN_GUI_SMOKE = '1'
    $env:NLINK_RUN_TUNA_GUI_FILETRANSFER = '1'
    $env:NLINK_TRANSPORT = 'NKN'
    $env:NLINK_GUI_SMOKE_SCENARIOS = 'FILETRANSFER_TUNA_HANDOFF_FALLBACK'
    $env:NLINK_TUNA_GUI_WALLET_PATH = $resolvedWallet
    $env:NLINK_TUNA_TEST_WALLET_PASSWORD = $WalletPassword
    $env:NLINK_TUNA_GUI_SIDECAR_EXE = $resolvedSidecar
    $env:NLINK_TUNA_GUI_PAYER_MODE = $PayerMode
    $env:NLINK_TUNA_GUI_FAULT = $Fault
    $env:NLINK_TUNA_GUI_ROUTE_MODE = $RouteMode
    if (-not [string]::IsNullOrWhiteSpace($LiveToggleSequence)) {
        $env:NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE = $LiveToggleSequence
    }
    else {
        Remove-Item Env:NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE -ErrorAction SilentlyContinue
    }
    if ($Mixed) {
        $env:NLINK_TUNA_GUI_MIXED_SCREENSHARE = '1'
    }
    else {
        Remove-Item Env:NLINK_TUNA_GUI_MIXED_SCREENSHARE -ErrorAction SilentlyContinue
    }
    Remove-Item Env:NLINK_FILETRANSFER_DIAGNOSTIC_FILE_TUNA_V4 -ErrorAction SilentlyContinue
    if ($ExercisePause) {
        $env:NLINK_TUNA_GUI_EXERCISE_PAUSE = '1'
    }
    else {
        Remove-Item Env:NLINK_TUNA_GUI_EXERCISE_PAUSE -ErrorAction SilentlyContinue
    }
    $env:NLINK_FILETRANSFER_SOAK_DIRECTION = $Direction
    $env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES = $PayloadSize
    $env:NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS = [string]$TimeoutSeconds
    $env:NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS = [string]$ProgressTimeoutSeconds
    $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR = $resolvedArtifactDir
    $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT = $receivedRoot

    Write-Host "[Tuna GUI] Running file-transfer handoff/fallback GUI smoke." -ForegroundColor Cyan
    Write-Host "[Tuna GUI] Artifacts: $resolvedArtifactDir" -ForegroundColor DarkGray
    Write-Host "[Tuna GUI] Direction=$Direction Payer=$PayerMode Fault=$Fault RouteMode=$RouteMode Payload=$PayloadSize Mixed=$($Mixed.IsPresent)" -ForegroundColor DarkGray

    $guiSmokeResult = Invoke-TunaGuiSmokeWithHardDeadline `
        -RepoRoot $repoRoot `
        -ArtifactDir $resolvedArtifactDir `
        -ExePath $resolvedExe `
        -TimeoutSeconds $TimeoutSeconds
    $guiSmokeExitCodeAvailable = $null -ne $guiSmokeResult -and
        $guiSmokeResult.PSObject.Properties.Name -contains 'ExitCodeAvailable' -and
        [bool]$guiSmokeResult.ExitCodeAvailable
    $guiSmokeExitCode = if ($guiSmokeExitCodeAvailable -and $null -ne $guiSmokeResult.ExitCode) {
        [int]$guiSmokeResult.ExitCode
    }
    else {
        -1
    }
    $summaryPath = Join-Path $resolvedArtifactDir 'filetransfer-tuna-gui-summary.json'
    $summaryMissingFromChild = -not (Test-Path -LiteralPath $summaryPath -PathType Leaf)
    if ($summaryMissingFromChild) {
        if ($RouteMode -eq 'v4-restart-v6-fallback') {
            Write-TunaGuiControlledRestartFailureSummary -ArtifactDir $resolvedArtifactDir -RouteMode $RouteMode
        }
        else {
            $summaryFailurePhase = if ([bool]$guiSmokeResult.TimedOut) { 'evidence_finalization' } elseif ($guiSmokeExitCode -ne 0) { 'progress_failure' } else { 'summary_missing' }
            $summaryFailureReason = if ([bool]$guiSmokeResult.TimedOut) { 'gui_evidence_finalization_timeout' } elseif ($guiSmokeExitCode -ne 0) { 'gui_smoke_failed' } else { 'gui_summary_missing' }
            Write-TunaGuiFailureSummaryIfMissing `
                -ArtifactDir $resolvedArtifactDir `
                -RouteMode $RouteMode `
                -SetupFailurePhase $summaryFailurePhase `
                -SetupFailureReason $summaryFailureReason
        }
    }
    $summary = $null
    $summaryCurrentValid = $false
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        try {
            $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
            $summaryCurrentValid = Test-TunaGuiCurrentSummary -Summary $summary -RouteMode $RouteMode
        }
        catch {
            $summary = $null
            $summaryCurrentValid = $false
        }
    }
    Write-TunaGuiDirectEvidenceManifest `
        -RepoRoot $repoRoot `
        -ArtifactDir $resolvedArtifactDir `
        -ExePath $resolvedExe `
        -GuiExitCode $guiSmokeExitCode `
        -GuiExitCodeAvailable $guiSmokeExitCodeAvailable

    if ([bool]$guiSmokeResult.TimedOut) {
        Write-Error "GUI smoke evidence finalization timed out with exit code 124. Artifacts: $resolvedArtifactDir" -ErrorAction Continue
        exit 124
    }

    if ($guiSmokeExitCode -ne 0) {
        if ($RouteMode -eq 'v4-restart-v6-fallback') {
            Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir
        }
        else {
            Invoke-TunaGuiLiveRetainedAnalysisBestEffort -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir -RouteMode $RouteMode
        }

        throw "GUI smoke failed with exit code $guiSmokeExitCode. Artifacts: $resolvedArtifactDir"
    }

    if (-not $summaryCurrentValid) {
        if ($RouteMode -eq 'v4-restart-v6-fallback') {
            Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir
        }
        else {
            Invoke-TunaGuiLiveRetainedAnalysisBestEffort -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir -RouteMode $RouteMode
        }

        throw "GUI smoke did not write file-transfer Tuna summary. Artifacts: $resolvedArtifactDir"
    }

    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        $slices = Select-TunaGuiControlledRestartLogSlices -ArtifactDir $resolvedArtifactDir
        if (-not [string]::IsNullOrWhiteSpace([string]$slices.SetupPath)) {
            Invoke-TunaGuiRetainedAnalysisBestEffort -RepoRoot $repoRoot -AnalysisDir (Join-Path $resolvedArtifactDir 'setup-analysis') -LogPath $slices.SetupPath
        }

        Invoke-TunaGuiMeasuredFallbackRetainedAnalysis -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir -LogPath $slices.MeasuredPath
        Update-TunaGuiControlledRestartSummary -ArtifactDir $resolvedArtifactDir -FilteredSetupCleanupLineCount ([int]$slices.FilteredSetupCleanupLineCount)
    }
    elseif ($RouteMode -eq 'preactivated' -or $RouteMode -eq 'live-v4-switch-off' -or $RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer' -or $RouteMode -eq 'live-regular-activation-cycle') {
        $retainedPath = Join-Path $resolvedArtifactDir 'filetransfer-retained-log-slice.log'
        $liveRouteProofMode = if ($RouteMode -eq 'live-v4-switch-off') { 'SwitchOff' } elseif ($RouteMode -eq 'live-multi-toggle') { 'MultiToggle' } elseif ($RouteMode -eq 'live-regular-activation-cycle') { 'RegularActivationCycle' } else { 'None' }
        Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice -ArtifactDir $resolvedArtifactDir
        Invoke-TunaGuiRetainedAnalysis -RepoRoot $repoRoot -AnalysisDir $resolvedArtifactDir -LogPath $retainedPath -LiveRouteProofMode $liveRouteProofMode
        if ($RouteMode -eq 'live-reactivation-second-transfer') {
            $secondRetainedPath = Ensure-TunaGuiSecondTransferRetainedLogSlice -ArtifactDir $resolvedArtifactDir
            if ([string]::IsNullOrWhiteSpace([string]$secondRetainedPath)) {
                throw ("Second-transfer retained log slice is missing; refusing to analyze the full retained log as second-transfer proof. failure_class=second_transfer_retained_slice_missing Artifact: {0}" -f (Join-Path $resolvedArtifactDir 'filetransfer-second-transfer-retained-log-slice-source.txt'))
            }

            Invoke-TunaGuiRetainedAnalysis -RepoRoot $repoRoot -AnalysisDir (Join-Path $resolvedArtifactDir 'second-transfer-analysis') -LogPath $secondRetainedPath
        }
    }

    if (-not [bool]$summary.completed -or -not [bool]$summary.integrityOk) {
        throw ("GUI smoke summary reports incomplete transfer. completed={0}; integrity_ok={1}; inbound_state={2}; outbound_state={3}; inbound_error={4}; outbound_error={5}; artifacts={6}" -f `
            $summary.completed,
            $summary.integrityOk,
            $summary.inboundState,
            $summary.outboundState,
            $summary.inboundErrorCode,
            $summary.outboundErrorCode,
            $resolvedArtifactDir)
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($resolvedArtifactDir)) {
        try {
            Write-TunaGuiDirectEvidenceManifest `
                -RepoRoot $repoRoot `
                -ArtifactDir $resolvedArtifactDir `
                -ExePath $resolvedExe `
                -GuiExitCode $guiSmokeExitCode `
                -GuiExitCodeAvailable $guiSmokeExitCodeAvailable
        }
        catch {
            Write-Warning "Failed to write direct Tuna GUI evidence manifest: $($_.Exception.Message)"
        }
    }
    Pop-Location
}
