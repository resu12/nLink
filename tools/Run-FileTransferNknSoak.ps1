param(
    [ValidateSet("nkn-fast", "nkn-mixed")]
    [string]$Mode = "nkn-fast",
    [string]$ExePath = "",
    [string]$PayloadSizes = "",
    [int]$Cycles = 0,
    [int]$Seed = 1313625684,
    [ValidateSet("alternate", "helper-to-helpee", "helpee-to-helper")]
    [string]$Direction = "alternate",
    [string]$ArtifactDir = "",
    [int]$CycleTimeoutSeconds = 120,
    [int]$ProgressTimeoutSeconds = 120,
    [int]$TimeoutSeconds = 600,
    [ValidateSet("Default", "PinnedMainnetRpc", "PinnedSeedHttps", "MediaFanout8", "MediaFanout12", "BulkSingle1", "BulkFanout8", "BulkFanout12", "DefaultKeepAlive")]
    [string]$ExternalTopologyProfile = "Default",
    [ValidateSet("Auto", "Current", "Packed3x20KiB", "Packed3x21KiB", "LargeSingle48KiB")]
    [string]$PayloadEfficiencyProfile = "Auto",
    [switch]$Build,
    [string]$SafeBaselineArtifactDir = "",
    [string]$StrongBaselineArtifactDir = "",
    [switch]$IncludeRawSlices,
    [switch]$FailOnGate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ScreenShareSoak\ProcessAndBridge.ps1')
. (Join-Path $PSScriptRoot 'FileTransferOps\AnalyzerOrchestration.ps1')
. (Join-Path $PSScriptRoot 'FileTransferSoak\BaselineComparison.ps1')

function Set-ProcessEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()][string]$Value
    )

    if ($null -eq $Value) {
        Remove-Item -LiteralPath ("Env:{0}" -f $Name) -ErrorAction SilentlyContinue
        return
    }

    Set-Item -LiteralPath ("Env:{0}" -f $Name) -Value $Value
}

function Set-FileTransferExternalTopologyProfileEnvironment {
    param([Parameter(Mandatory = $true)][string]$Profile)

    $keys = @(
        "NLINK_NKN_SEED_RPC",
        "NLINK_NKN_NUM_SUBCLIENTS",
        "NLINK_NKN_MEDIA_NUM_SUBCLIENTS",
        "NLINK_NKN_BULK_NUM_SUBCLIENTS",
        "NLINK_NKN_BULK_SEND_CONCURRENCY",
        "NLINK_BRIDGE_REUSE_MODE",
        "NLINK_SCREENSHARE_EXTERNAL_TOPOLOGY_PROFILE",
        "NLINK_FILETRANSFER_EXTERNAL_TOPOLOGY_PROFILE"
    )

    $restore = @{}
    foreach ($key in $keys) {
        $restore[$key] = [pscustomobject]@{
            HadValue = Test-Path -LiteralPath ("Env:{0}" -f $key)
            Value = [System.Environment]::GetEnvironmentVariable($key)
        }
    }

    foreach ($key in $keys) {
        Set-ProcessEnvironmentValue -Name $key -Value $null
    }

    Set-ProcessEnvironmentValue -Name "NLINK_SCREENSHARE_EXTERNAL_TOPOLOGY_PROFILE" -Value $Profile
    Set-ProcessEnvironmentValue -Name "NLINK_FILETRANSFER_EXTERNAL_TOPOLOGY_PROFILE" -Value $Profile

    switch ($Profile) {
        "PinnedMainnetRpc" {
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_SEED_RPC" -Value "https://mainnet-rpc-node-0001.nkn.org/mainnet/api/wallet"
        }
        "PinnedSeedHttps" {
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_SEED_RPC" -Value "https://seed.nkn.org:30003"
        }
        "MediaFanout8" {
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_NUM_SUBCLIENTS" -Value "4"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_MEDIA_NUM_SUBCLIENTS" -Value "8"
        }
        "MediaFanout12" {
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_NUM_SUBCLIENTS" -Value "4"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_MEDIA_NUM_SUBCLIENTS" -Value "12"
        }
        "BulkSingle1" {
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_NUM_SUBCLIENTS" -Value "4"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_MEDIA_NUM_SUBCLIENTS" -Value "8"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_BULK_NUM_SUBCLIENTS" -Value "1"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_BULK_SEND_CONCURRENCY" -Value "4"
        }
        "BulkFanout8" {
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_NUM_SUBCLIENTS" -Value "4"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_MEDIA_NUM_SUBCLIENTS" -Value "8"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_BULK_NUM_SUBCLIENTS" -Value "8"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_BULK_SEND_CONCURRENCY" -Value "6"
        }
        "BulkFanout12" {
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_NUM_SUBCLIENTS" -Value "4"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_MEDIA_NUM_SUBCLIENTS" -Value "8"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_BULK_NUM_SUBCLIENTS" -Value "12"
            Set-ProcessEnvironmentValue -Name "NLINK_NKN_BULK_SEND_CONCURRENCY" -Value "8"
        }
        "DefaultKeepAlive" {
            Set-ProcessEnvironmentValue -Name "NLINK_BRIDGE_REUSE_MODE" -Value "KeepAlive"
        }
    }

    return $restore
}

function Restore-FileTransferExternalTopologyProfileEnvironment {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Restore)

    foreach ($key in $Restore.Keys) {
        $entry = $Restore[$key]
        if ($entry.HadValue) {
            Set-ProcessEnvironmentValue -Name $key -Value $entry.Value
        }
        else {
            Set-ProcessEnvironmentValue -Name $key -Value $null
        }
    }
}

function Test-UnsafeMixedPayloadEfficiencyProfileAllowed {
    $value = [System.Environment]::GetEnvironmentVariable("NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE")
    return -not [string]::IsNullOrWhiteSpace($value) -and $value -match '^(1|true|yes|on)$'
}

function Assert-PayloadEfficiencyProfileIsSafeForMode {
    if ($Mode -eq "nkn-mixed" -and
        $PayloadEfficiencyProfile -ne "Auto" -and
        $PayloadEfficiencyProfile -ne "Current" -and
        -not (Test-UnsafeMixedPayloadEfficiencyProfileAllowed)) {
        throw ("Payload efficiency profile '{0}' is not supported for nkn-mixed by default. Public NKN bridge-only probes reproduced receive stalls when screen-share-sized media was mixed with near-budget bulk payloads. Use nkn-fast for candidate profiles, or set NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE=1 only for controlled stall reproduction." -f $PayloadEfficiencyProfile)
    }
}

function Set-FileTransferPayloadEfficiencyProfileEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$Profile,
        [Parameter(Mandatory = $true)][string]$Mode
    )

    $keys = @(
        "NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_PROFILE",
        "NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_ALLOW_SCREENSHARE"
    )

    $restore = @{}
    foreach ($key in $keys) {
        $restore[$key] = [pscustomobject]@{
            HadValue = Test-Path -LiteralPath ("Env:{0}" -f $key)
            Value = [System.Environment]::GetEnvironmentVariable($key)
        }
    }

    if ($Profile -eq "Auto") {
        Set-ProcessEnvironmentValue -Name "NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_PROFILE" -Value $null
    }
    else {
        Set-ProcessEnvironmentValue -Name "NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_PROFILE" -Value $Profile
    }

    if ($Mode -eq "nkn-mixed" -and $Profile -ne "Auto" -and $Profile -ne "Current" -and (Test-UnsafeMixedPayloadEfficiencyProfileAllowed)) {
        Set-ProcessEnvironmentValue -Name "NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_ALLOW_SCREENSHARE" -Value "1"
    }
    else {
        Set-ProcessEnvironmentValue -Name "NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_ALLOW_SCREENSHARE" -Value $null
    }

    return $restore
}

function Restore-FileTransferPayloadEfficiencyProfileEnvironment {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Restore)

    foreach ($key in $Restore.Keys) {
        $entry = $Restore[$key]
        if ($entry.HadValue) {
            Set-ProcessEnvironmentValue -Name $key -Value $entry.Value
        }
        else {
            Set-ProcessEnvironmentValue -Name $key -Value $null
        }
    }
}

function Stop-FileTransferProcessTree {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    $children = @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ParentProcessId -eq $ProcessId }
    )
    foreach ($child in $children) {
        Stop-FileTransferProcessTree -ProcessId ([int]$child.ProcessId)
    }

    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
    catch {}
}

function Invoke-FileTransferGuiSmokeWithTimeout {
    param(
        [Parameter(Mandatory = $true)][string]$GuiSmokeScript,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $stdoutPath = Join-Path $ArtifactDir 'gui-smoke-stdout.log'
    $stderrPath = Join-Path $ArtifactDir 'gui-smoke-stderr.log'
    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

    $args = @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $GuiSmokeScript,
        '-ExePath',
        $ResolvedExePath,
        '-TimeoutSeconds',
        ([string]$TimeoutSeconds)
    )
    $argString = ($args | ForEach-Object {
            $value = [string]$_
            if ($value.IndexOfAny([char[]]@(' ', "`t", '"')) -ge 0) {
                '"' + ($value -replace '"', '\"') + '"'
            }
            else {
                $value
            }
        }) -join ' '

    $process = Start-Process `
        -FilePath 'powershell' `
        -ArgumentList $argString `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -NoNewWindow `
        -PassThru

    $startedUtc = [datetime]::UtcNow
    $lastStatusUtc = $startedUtc
    while (-not $process.HasExited) {
        $elapsed = [datetime]::UtcNow - $startedUtc
        if ($elapsed.TotalSeconds -ge $TimeoutSeconds) {
            $message = "GUI smoke harness exceeded hard timeout (${TimeoutSeconds}s); killing process tree rooted at pid=$($process.Id)."
            Write-Warning $message
            Add-Content -LiteralPath $stderrPath -Value $message -Encoding UTF8
            Stop-FileTransferProcessTree -ProcessId $process.Id
            Start-Sleep -Milliseconds 500
            if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) { Get-Content -LiteralPath $stdoutPath | ForEach-Object { Write-Host $_ } }
            if (Test-Path -LiteralPath $stderrPath -PathType Leaf) { Get-Content -LiteralPath $stderrPath | ForEach-Object { Write-Error $_ -ErrorAction Continue } }
            return 124
        }

        if ((([datetime]::UtcNow - $lastStatusUtc).TotalSeconds) -ge 30) {
            Write-Host ("[FileTransfer NKN Soak] GUI smoke still running ({0:N0}s/{1}s)..." -f $elapsed.TotalSeconds, $TimeoutSeconds) -ForegroundColor DarkGray
            $lastStatusUtc = [datetime]::UtcNow
        }

        Start-Sleep -Milliseconds 500
        try { $process.Refresh() } catch {}
    }

    if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) { Get-Content -LiteralPath $stdoutPath | ForEach-Object { Write-Host $_ } }
    if (Test-Path -LiteralPath $stderrPath -PathType Leaf) { Get-Content -LiteralPath $stderrPath | ForEach-Object { Write-Error $_ -ErrorAction Continue } }
    return [int]$process.ExitCode
}

function Resolve-FileTransferArtifactDir {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$RequestedArtifactDir = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedArtifactDir)) {
        if ([System.IO.Path]::IsPathRooted($RequestedArtifactDir)) {
            return [System.IO.Path]::GetFullPath($RequestedArtifactDir)
        }

        return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $RequestedArtifactDir))
    }

    return (Join-Path (Join-Path $RepoRoot 'artifacts\filetransfer-soak') (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

function Resolve-FileTransferLiveExePath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$RequestedPath = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return (Resolve-ExePath -RepoRoot $RepoRoot -RequestedPath $RequestedPath)
    }

    $portableCandidates = @(
        (Join-Path $RepoRoot 'artifacts\portable\nLink\win-x64\nLink.exe'),
        (Join-Path $RepoRoot 'artifacts\portable\nLink\win-x64\Link.exe'),
        (Join-Path $RepoRoot 'artifacts\portable\nLink\win-x64\nlink.exe')
    )

    foreach ($candidate in $portableCandidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $portableCandidates[0]
}

function Build-FileTransferPortableIfNeeded {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath,
        [bool]$ForceBuild
    )

    if (-not $ForceBuild -and (Test-Path -LiteralPath $ResolvedExePath -PathType Leaf)) {
        return
    }

    $portableScript = Join-Path $RepoRoot 'installer\Build-Portable.ps1'
    if (-not (Test-Path -LiteralPath $portableScript -PathType Leaf)) {
        throw "Portable build script not found: $portableScript"
    }

    Write-Host "Building portable nLink app for live file-transfer NKN soak..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $portableScript -Runtime 'win-x64'
    if ($LASTEXITCODE -ne 0) {
        throw "Build-Portable.ps1 failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $ResolvedExePath -PathType Leaf)) {
        throw "Portable build completed but executable was not found at $ResolvedExePath."
    }
}

function Get-FileTransferSummaryValue {
    param(
        $Values,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Default = '0'
    )

    if ($null -ne $Values -and $Values.ContainsKey($Name)) {
        return [string]$Values[$Name]
    }

    return $Default
}

function Get-FileTransferEventCountFromSummary {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)][string]$EventName
    )

    return @($Summary.TransferEvents | Where-Object { $_.EventName -eq $EventName }).Count
}

function Get-FileTransferGlobalEventCountFromSummary {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)][string]$EventName
    )

    return @($Summary.GlobalEvents | Where-Object { $_.EventName -eq $EventName }).Count
}

function Get-FileTransferGlobalSumField {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)][string]$EventName,
        [Parameter(Mandatory = $true)][string]$FieldName
    )

    $total = 0L
    foreach ($event in @($Summary.GlobalEvents | Where-Object { $_.EventName -eq $EventName })) {
        $total += Get-FileTransferEventInt64Field -Event $event -Name $FieldName -Default 0
    }

    return $total
}

function Read-FileTransferLiveCycles {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $path = Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return @()
    }

    return @(
        [System.IO.File]::ReadLines($path) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
}

function Get-FileTransferGuiProgressTimeoutEvidence {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    return Get-FileTransferLiveProgressTimeoutEvidence -ArtifactDir $ArtifactDir
}

function Add-FileTransferLiveNknSyntheticEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [int]$CyclesRequested = 0
    )

    $logSlicePath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logSlicePath -PathType Leaf)) {
        return
    }

    $progressTimeout = Get-FileTransferGuiProgressTimeoutEvidence -ArtifactDir $ArtifactDir
    $liveCycles = @(Read-FileTransferLiveCycles -ArtifactDir $ArtifactDir)
    $completedCycles = @($liveCycles | Where-Object { $_.completed -eq $true -and $_.integrity_ok -eq $true })
    $effectiveCyclesRequested = if ($CyclesRequested -gt 0) { $CyclesRequested } else { $liveCycles.Count }
    $requestedMatrixIncomplete = $effectiveCyclesRequested -gt 0 -and $completedCycles.Count -lt $effectiveCyclesRequested
    $existingText = Get-Content -LiteralPath $logSlicePath -Raw -ErrorAction SilentlyContinue
    $timestamp = [datetime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    $linesToAppend = New-Object System.Collections.Generic.List[string]
    $startReason = if ($progressTimeout.Count -gt 0) { 'live_soak_failure_context' } else { 'live_soak_retained_slice' }
    $endReason = if ($progressTimeout.Count -gt 0) { 'gui_progress_timeout' } else { 'soak_completed_or_harness_exit' }

    $hasAnySliceSummary = -not [string]::IsNullOrWhiteSpace($existingText) -and
        $existingText.IndexOf('event=filetransfer_artifact_slice_summary', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    $hasProgressTimeoutSliceSummary = -not [string]::IsNullOrWhiteSpace($existingText) -and
        $existingText.IndexOf('artifact_slice_end_reason=gui_progress_timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $hasAnySliceSummary -or ($progressTimeout.Count -gt 0 -and -not $hasProgressTimeoutSliceSummary)) {
        $linesToAppend.Add(('[{0}] [INFO] [FileTransferNknSoak] event=filetransfer_artifact_slice_summary; transfer_id=(all); artifact_slice_start_reason={1}; artifact_slice_end_reason={2}' -f $timestamp, $startReason, $endReason)) | Out-Null
    }

    if ($progressTimeout.Count -gt 0 -and
        ([string]::IsNullOrWhiteSpace($existingText) -or $existingText.IndexOf('event=filetransfer_live_progress_timeout', [System.StringComparison]::OrdinalIgnoreCase) -lt 0)) {
        $reason = if ([string]::IsNullOrWhiteSpace($progressTimeout.Reason)) { 'progress_timeout' } else { ($progressTimeout.Reason -replace ';', ',') }
        $linesToAppend.Add(('[{0}] [WARN] [FileTransferNknSoak] event=filetransfer_live_progress_timeout; transfer_id=(all); reason={1}; total_wait_s={2}; progress_timeout_seconds={3}; receiver_next_chunk={4}; receiver_highest_chunk={5}; progress_events={6}; requested_matrix_incomplete={7}; cycles_requested={8}; cycles_completed={9}' -f $timestamp, $reason, $progressTimeout.TotalWaitSeconds, $progressTimeout.ProgressTimeoutSeconds, $progressTimeout.ReceiverNextChunk, $progressTimeout.ReceiverHighestChunk, $progressTimeout.ProgressEventCount, ($(if ($requestedMatrixIncomplete) { 1 } else { 0 })), $effectiveCyclesRequested, $completedCycles.Count)) | Out-Null
    }

    if ($progressTimeout.Count -gt 0 -and $requestedMatrixIncomplete -and
        ([string]::IsNullOrWhiteSpace($existingText) -or $existingText.IndexOf('event=filetransfer_live_matrix_incomplete', [System.StringComparison]::OrdinalIgnoreCase) -lt 0)) {
        $linesToAppend.Add(('[{0}] [WARN] [FileTransferNknSoak] event=filetransfer_live_matrix_incomplete; transfer_id=(all); reason=progress_timeout_incomplete_matrix; cycles_requested={1}; cycles_completed={2}; cycles_observed={3}; gui_progress_timeout=1' -f $timestamp, $effectiveCyclesRequested, $completedCycles.Count, $liveCycles.Count)) | Out-Null
    }

    if ($linesToAppend.Count -gt 0) {
        Add-Content -LiteralPath $logSlicePath -Value $linesToAppend.ToArray() -Encoding UTF8
    }
}

function Write-FileTransferLiveNknSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Analysis,
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$ExternalTopologyProfile,
        [Parameter(Mandatory = $true)][string]$PayloadEfficiencyProfile,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath,
        [Parameter(Mandatory = $true)][int]$GuiHarnessExitCode,
        [int]$CyclesRequested = 0
    )

    $liveCycles = @(Read-FileTransferLiveCycles -ArtifactDir $ArtifactDir)
    $progressTimeout = Get-FileTransferGuiProgressTimeoutEvidence -ArtifactDir $ArtifactDir
    $completedCycles = @($liveCycles | Where-Object { $_.completed -eq $true -and $_.integrity_ok -eq $true })
    $effectiveCyclesRequested = if ($CyclesRequested -gt 0) { $CyclesRequested } else { $liveCycles.Count }
    $requestedMatrixIncomplete = $effectiveCyclesRequested -gt 0 -and $completedCycles.Count -lt $effectiveCyclesRequested
    $goodputs = @($completedCycles | ForEach-Object { [double]$_.goodput_bytes_per_second })
    $averageGoodput = if ($goodputs.Count -gt 0) { ($goodputs | Measure-Object -Average).Average } else { 0.0 }
    $minimumGoodput = if ($goodputs.Count -gt 0) { ($goodputs | Measure-Object -Minimum).Minimum } else { 0.0 }
    $totalPayloadBytes = 0L
    foreach ($cycle in @($completedCycles)) {
        $totalPayloadBytes += [int64]$cycle.payload_bytes
    }

    $batchAsBatch = [int64]$Analysis.Summary.BatchSentAsBatchCount
    $batchSplit = [int64]$Analysis.Summary.BatchSplitCount
    $batchDenominator = $batchAsBatch + $batchSplit
    $v3BatchRatio = if ($batchDenominator -gt 0) { $batchAsBatch / [double]$batchDenominator } else { 0.0 }
    $v4BatchEvents = @($Analysis.Summary.TransferEvents | Where-Object {
            $_.EventName -eq 'filetransfer_chunk_batch_sent_as_batch' -and
            ((Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -eq 'filetransfer.chunk_batch.v4' -or
             (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -eq 'v4_default_21k')
        })
    $v4SplitEvents = @($Analysis.Summary.TransferEvents | Where-Object {
            $_.EventName -eq 'filetransfer_chunk_batch_split_for_transport' -and
            (Get-FileTransferEventField -Event $_ -Name 'original_frame_type' -Default '') -eq 'filetransfer.chunk_batch.v4'
        })
    $v4PayloadShapeEvents = @($Analysis.Summary.TransferEvents | Where-Object {
            ($_.EventName -eq 'filetransfer_chunk_batch_sent_as_batch' -or $_.EventName -eq 'filetransfer_transport_payload_budget') -and
            ((Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -eq 'filetransfer.chunk_batch.v4' -or
             (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -eq 'v4_default_21k')
        })
    $v4BatchDenominator = $v4BatchEvents.Count + $v4SplitEvents.Count
    $v4BatchRatio = if ($v4BatchDenominator -gt 0) { $v4BatchEvents.Count / [double]$v4BatchDenominator } else { 0.0 }
    $dataProtocolVersion = if (
        (Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_negotiated') -gt 0 -or
        (Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_sender_started') -gt 0 -or
        (Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_receiver_started') -gt 0 -or
        $v4BatchEvents.Count -gt 0) {
        4
    }
    else {
        3
    }
    if ($dataProtocolVersion -eq 4) {
        $v3BatchRatio = 0.0
    }
    $v4FillValues = @(
        foreach ($event in @($v4PayloadShapeEvents)) {
            $value = Get-FileTransferEventDoubleField -Event $event -Name 'bridge_payload_fill_percent' -Default -1
            if ($value -ge 0) {
                $value
            }
        }
    )
    $v4AverageBridgePayloadFillPercent = if ($v4FillValues.Count -gt 0) { ($v4FillValues | Measure-Object -Average).Average } else { 0.0 }
    $legacyDataProtocolStartedCount = @($Analysis.Summary.TransferEvents | Where-Object {
            $_.EventName -eq 'filetransfer_session_opened' -and
            -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default '')) -and
            (Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default '') -ne '3' -and
            (Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default '') -ne '4'
        }).Count
    $unexpectedLegacyDataFrameDuringV4Count = 0
    if ($dataProtocolVersion -eq 4) {
        $unexpectedLegacyDataFrameDuringV4Count = @($Analysis.Summary.TransferEvents | Where-Object {
                ($_.EventName -eq 'filetransfer_binary_frame_sent' -or
                 $_.EventName -eq 'filetransfer_binary_frame_received' -or
                 $_.EventName -eq 'filetransfer_data_frame_dispatched') -and
                (
                    (Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -like 'filetransfer.*.v2' -or
                    (Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -like 'filetransfer.*.v3'
                )
            }).Count
    }
    $resolvedPayloadEfficiencyProfile = if ($dataProtocolVersion -eq 4 -and $PayloadEfficiencyProfile -eq 'Auto') {
        'v4_default_21k'
    }
    else {
        $PayloadEfficiencyProfile
    }

    $bridgeBulkSendFailures = Get-FileTransferGlobalSumField -Summary $Analysis.Summary -EventName 'nkn_bridge_bulk_send_summary' -FieldName 'send_failures'
    $bridgeBulkQueueClears = (Get-FileTransferGlobalSumField -Summary $Analysis.Summary -EventName 'nkn_bridge_bulk_send_summary' -FieldName 'queue_clears') +
        (Get-FileTransferGlobalSumField -Summary $Analysis.Summary -EventName 'nkn_bridge_bulk_queue_state' -FieldName 'cleared_since_last')
    $bridgeBulkSevereCount = @($Analysis.Summary.GlobalEvents | Where-Object {
            $_.EventName -eq 'nkn_bridge_bulk_queue_state' -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'severe' -Default 0) -gt 0
        }).Count
    $mediaQueueDropCount = Get-FileTransferGlobalSumField -Summary $Analysis.Summary -EventName 'screenshare_bridge_media_send_summary' -FieldName 'queue_drops'
    $mediaSendFailureCount = Get-FileTransferGlobalSumField -Summary $Analysis.Summary -EventName 'screenshare_bridge_media_send_summary' -FieldName 'send_failures'
    $mediaSevereCount = @($Analysis.Summary.GlobalEvents | Where-Object {
            ($_.EventName -eq 'screenshare_bridge_queue_state' -or $_.EventName -eq 'screenshare_bridge_media_send_summary') -and
            ((Get-FileTransferEventInt64Field -Event $_ -Name 'severe' -Default 0) -gt 0 -or
             (Get-FileTransferEventField -Event $_ -Name 'queue_mode' -Default 'normal') -eq 'severe')
        }).Count

    $summary = [ordered]@{
        artifact_kind = 'live-nkn'
        mode = $Mode
        verdict = $Analysis.GateResult.Verdict
        gate_status = $Analysis.GateResult.GateStatus
        external_topology_profile = $ExternalTopologyProfile
        payload_efficiency_profile = $resolvedPayloadEfficiencyProfile
        app_version = ($(try { (Get-Content -Path (Join-Path (Resolve-RepoRoot) 'VERSION') -TotalCount 1).Trim() } catch { '(unknown)' }))
        exe_path = $ResolvedExePath
        gui_harness_exit_code = $GuiHarnessExitCode
        cycles_requested = $effectiveCyclesRequested
        cycles_observed = $liveCycles.Count
        cycles_completed = $completedCycles.Count
        total_payload_bytes = $totalPayloadBytes
        average_goodput_bytes_per_second = ('{0:F3}' -f $averageGoodput)
        min_goodput_bytes_per_second = ('{0:F3}' -f $minimumGoodput)
        data_protocol_version = $dataProtocolVersion
        v3_batch_ratio = ('{0:F6}' -f $v3BatchRatio)
        v4_batch_ratio = ('{0:F6}' -f $v4BatchRatio)
        v4_average_bridge_payload_fill_percent = ('{0:F3}' -f $v4AverageBridgePayloadFillPercent)
        v4_state_feedback_count = Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_state_sent'
        v4_feedback_redundant_success_count = Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_feedback_first_success'
        v4_feedback_both_failed_count = Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_feedback_both_failed'
        v4_sender_failed_count = Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_sender_failed'
        v4_receiver_failed_count = Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_receiver_failed'
        v4_runtime_not_implemented_count = Get-FileTransferEventCount -Events $Analysis.Summary.TransferEvents -Name 'filetransfer_v4_runtime_not_implemented'
        legacy_data_protocol_started_count = $legacyDataProtocolStartedCount
        unexpected_legacy_data_frame_during_v4_count = $unexpectedLegacyDataFrameDuringV4Count
        chunk_batch_sent_as_batch_count = $batchAsBatch
        chunk_batch_split_count = $batchSplit
        reorder_event_count = $Analysis.Summary.ReorderEventCount
        request_timeout_count = $Analysis.Summary.RequestTimeoutCount
        retry_requested_count = $Analysis.Summary.RetryRequestedCount
        payload_rejected_count = $Analysis.Summary.PayloadRejectedCount
        decode_failure_count = $Analysis.Summary.DataFrameDecodeFailedCount
        message_rejected_count = $Analysis.Summary.MessageRejectedCount
        bridge_bulk_send_failure_count = $bridgeBulkSendFailures
        bridge_bulk_queue_clear_count = $bridgeBulkQueueClears
        bridge_bulk_queue_waiting_count = Get-FileTransferGlobalEventCountFromSummary -Summary $Analysis.Summary -EventName 'nkn_bridge_bulk_queue_waiting'
        bridge_bulk_queue_severe_count = $bridgeBulkSevereCount
        media_queue_drop_count = $mediaQueueDropCount
        media_send_failure_count = $mediaSendFailureCount
        media_queue_severe_count = $mediaSevereCount
        gui_progress_timeout_count = $progressTimeout.Count
        gui_progress_timeout_reason = $progressTimeout.Reason
        last_receiver_next_chunk = $progressTimeout.ReceiverNextChunk
        last_receiver_highest_chunk = $progressTimeout.ReceiverHighestChunk
        last_progress_event_count = $progressTimeout.ProgressEventCount
        terminal_missing_after_progress_timeout = if ($progressTimeout.Count -gt 0 -and ($requestedMatrixIncomplete -or -not $Analysis.Summary.HasTerminalEvidence)) { 1 } else { 0 }
        observed_start_utc = $Analysis.Summary.FirstTimestamp
        observed_end_utc = $Analysis.Summary.LastTimestamp
    }

    $txtLines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $summary.Keys) {
        $txtLines.Add(("{0}={1}" -f $key, $summary[$key])) | Out-Null
    }

    $txtLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt') -Encoding UTF8
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.json') -Encoding UTF8
}

function Test-FileTransferNknSoakFakeGuiMode {
    $value = [System.Environment]::GetEnvironmentVariable('NLINK_FILETRANSFER_NKN_SOAK_FAKE_GUI')
    return -not [string]::IsNullOrWhiteSpace($value) -and $value -match '^(1|true|yes|on)$'
}

function Test-FileTransferNknSoakFakeProgressTimeout {
    $value = [System.Environment]::GetEnvironmentVariable('NLINK_FILETRANSFER_NKN_SOAK_FAKE_PROGRESS_TIMEOUT')
    return -not [string]::IsNullOrWhiteSpace($value) -and $value -match '^(1|true|yes|on)$'
}

function ConvertTo-FileTransferNknSoakByteCount {
    param(
        [string]$Value,
        [int64]$DefaultValue = 65536
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $DefaultValue
    }

    $match = [regex]::Match($Value.Trim(), '^(?<number>\d+(?:\.\d+)?)\s*(?<unit>B|KiB|MiB|GiB|KB|MB|GB)?$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return $DefaultValue
    }

    $number = 0.0
    if (-not [double]::TryParse(
            $match.Groups['number'].Value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$number)) {
        return $DefaultValue
    }

    $multiplier = 1.0
    switch ($match.Groups['unit'].Value.ToLowerInvariant()) {
        'kib' { $multiplier = 1024.0 }
        'kb' { $multiplier = 1000.0 }
        'mib' { $multiplier = 1024.0 * 1024.0 }
        'mb' { $multiplier = 1000.0 * 1000.0 }
        'gib' { $multiplier = 1024.0 * 1024.0 * 1024.0 }
        'gb' { $multiplier = 1000.0 * 1000.0 * 1000.0 }
    }

    return [int64][Math]::Max(1, [Math]::Round($number * $multiplier))
}

function Get-FileTransferNknSoakPayloadByteCounts {
    param([string]$PayloadSizes)

    $values = New-Object System.Collections.Generic.List[int64]
    foreach ($part in @($PayloadSizes -split ',')) {
        if ([string]::IsNullOrWhiteSpace($part)) {
            continue
        }

        $values.Add((ConvertTo-FileTransferNknSoakByteCount -Value $part)) | Out-Null
    }

    if ($values.Count -eq 0) {
        $values.Add(65536) | Out-Null
    }

    return $values.ToArray()
}

function Get-FileTransferNknFakeGoodputBytesPerSecond {
    $value = [System.Environment]::GetEnvironmentVariable('NLINK_FILETRANSFER_NKN_SOAK_FAKE_GOODPUT_BPS')
    $parsed = 0.0
    if (-not [string]::IsNullOrWhiteSpace($value) -and
        [double]::TryParse($value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -and
        $parsed -gt 0) {
        return $parsed
    }

    return 8388608.0
}

function Get-FileTransferNknEffectiveCycleCount {
    param(
        [string]$PayloadSizes = '',
        [int]$Cycles = 0
    )

    $payloadCount = @(Get-FileTransferNknSoakPayloadByteCounts -PayloadSizes $PayloadSizes).Count
    if ($payloadCount -le 0) {
        $payloadCount = 1
    }

    if ($Cycles -gt 0) {
        return ($Cycles * $payloadCount)
    }

    return 0
}

function Format-FileTransferNknFakeTimestamp {
    param([Parameter(Mandatory = $true)][datetime]$TimestampUtc)

    return $TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
}

function New-FileTransferNknFakeLogLine {
    param(
        [Parameter(Mandatory = $true)][datetime]$TimestampUtc,
        [Parameter(Mandatory = $true)][string]$Message
    )

    return ('[{0}] [INFO] [FileTransferNknFake] {1}' -f (Format-FileTransferNknFakeTimestamp -TimestampUtc $TimestampUtc), $Message)
}

function Write-FileTransferNknFakeArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$Scenario,
        [string]$PayloadSizes = '',
        [int]$Cycles = 0,
        [Parameter(Mandatory = $true)][string]$Direction,
        [int]$Seed = 0,
        [string]$PayloadEfficiencyProfile = 'Current'
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null

    $payloadByteCounts = @(Get-FileTransferNknSoakPayloadByteCounts -PayloadSizes $PayloadSizes)
    $cycleCount = Get-FileTransferNknEffectiveCycleCount -PayloadSizes $PayloadSizes -Cycles $Cycles
    if ($cycleCount -le 0) {
        $cycleCount = $payloadByteCounts.Count
    }
    $goodputBytesPerSecond = Get-FileTransferNknFakeGoodputBytesPerSecond
    $baseTimestamp = [datetime]::UtcNow
    $logLines = New-Object System.Collections.Generic.List[string]
    $cycleLines = New-Object System.Collections.Generic.List[string]
    $sessionId = 'fake-live-nkn-session'

    $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $baseTimestamp -Message (
                'event=filetransfer_live_nkn_fake_soak_started; mode={0}; scenario={1}; seed={2}; direction={3}' -f $Mode, $Scenario, $Seed, $Direction))) | Out-Null

    if (Test-FileTransferNknSoakFakeProgressTimeout) {
        $transferId = 'fake-live-nkn-timeout'
        $timestamp = $baseTimestamp.AddSeconds(1)
        $stdoutPath = Join-Path $ArtifactDir 'gui-smoke-stdout.log'

        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp -Message (
                    'event=filetransfer_profile_selected; transport=nkn; transfer_id={0}; session_id={1}; protocol_version=3; profile=v3_live; target_window_bytes=16777216; granted_window_bytes=16777216' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(50) -Message (
                    'event=filetransfer_binary_frame_received; transfer_id={0}; session_id={1}; frame_type=filetransfer.chunk_batch.v3; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(100) -Message (
                    'event=filetransfer_receiver_sparse_mode_selected; transfer_id={0}; session_id={1}; reason=seekable_readwrite_destination; can_read=1; can_write=1; can_seek=1' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(1) -Message (
                    'event=filetransfer_v3_sender_throughput_summary; transfer_id={0}; session_id={1}; sample_window_ms=2000; raw_bytes_sent=516096; raw_bytes_per_second=258048; chunk_frames_sent=0; batch_frames_sent=8; chunk_count_sent=24; chunks_accepted_for_transport=2860; remote_next_expected_chunk_index=2507; remote_granted_until_chunk_index_exclusive=2890; remote_granted_window_bytes=8232960; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=12; repair_send_count=2' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(2) -Message (
                    'event=filetransfer_v3_receiver_throughput_summary; transfer_id={0}; session_id={1}; sample_window_ms=2000; raw_bytes_received=516096; raw_bytes_received_per_second=258048; contiguous_bytes_committed=516096; contiguous_bytes_committed_per_second=258048; pending_chunk_count=0; pending_bytes=0; next_chunk_index=2507; highest_received_chunk_index=2860; late_arrival_distance=353; oldest_gap_age_ms=42000; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; write_batch_count=8; write_batch_bytes=516096; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=258048; sparse_written_ahead_bytes=7587072; sparse_gap_count=1' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(3) -Message (
                    'event=filetransfer_v3_gap_stall_summary; transfer_id={0}; session_id={1}; sample_window_ms=2000; gap_start_chunk_index=2507; highest_received_chunk_index=2860; late_arrival_distance=353; stall_duration_ms=42294; pending_bytes=0; granted_window_bytes=8232960' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(4) -Message (
                    'event=filetransfer_frontier_gap_repair_requested; transfer_id={0}; session_id={1}; start_chunk_index=2507; requested_chunk_count=32; gap_stall_age_ms=12000; late_arrival_distance=353; highest_received_chunk_index=2860; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; reason=proactive_frontier_gap' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(5) -Message (
                    'event=filetransfer_frontier_gap_repair_requested; transfer_id={0}; session_id={1}; start_chunk_index=2507; requested_chunk_count=32; gap_stall_age_ms=18000; late_arrival_distance=353; highest_received_chunk_index=2860; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; reason=proactive_frontier_gap' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(6) -Message (
                    'event=filetransfer_v3_receiver_feedback_enqueued; transfer_id={0}; session_id={1}; mode=pump; frame_type=filetransfer.grant_window.v3; queue_depth=2; coalesced_count=1' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(7) -Message (
                    'event=filetransfer_v3_receiver_feedback_sent; transfer_id={0}; session_id={1}; mode=pump; frame_type=filetransfer.grant_window.v3; queue_depth=1; enqueue_to_send_age_ms=900; send_duration_ms=120' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(8) -Message 'event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=200; send_failures=0; queue_clears=0; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_p95_ms=4; configured_concurrency=4; effective_concurrency=4; in_flight_max=3')) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(9) -Message (
                    'event=filetransfer_live_progress_timeout; transfer_id=(all); reason=no useful data progress for 120s; total_wait_s=379; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=4729'))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddSeconds(10) -Message 'event=filetransfer_artifact_slice_summary; transfer_id=(all); artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout')) | Out-Null

        $logLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
        [System.IO.File]::WriteAllText((Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl'), '', [System.Text.UTF8Encoding]::new($false))
        ('[GUI Smoke] FAIL: Timed out waiting for live file-transfer progress: no useful data progress for 120s; total_wait_s=379; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=4729.') |
            Set-Content -LiteralPath $stdoutPath -Encoding UTF8
        return
    }

    for ($cycleIndex = 0; $cycleIndex -lt $cycleCount; $cycleIndex++) {
        $payloadBytes = [int64]$payloadByteCounts[$cycleIndex % $payloadByteCounts.Count]
        $chunkFrameCount = [Math]::Max(1, [int][Math]::Ceiling($payloadBytes / 21504.0))
        $batchChunkCount = [Math]::Min(3, $chunkFrameCount)
        $batchFinalChunkIndex = $batchChunkCount - 1
        $batchPayloadBytes = [Math]::Min($payloadBytes, [int64]64512)
        $transportPayloadBytes = $batchPayloadBytes + 401
        $transferId = 'fake-live-nkn-transfer-{0:D4}' -f ($cycleIndex + 1)
        $timestamp = $baseTimestamp.AddSeconds(1 + ($cycleIndex * 3))

        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp -Message (
                    'event=filetransfer_v4_negotiated; transfer_id={0}; session_id={1}; direction=outbound; negotiated_version=4' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(10) -Message (
                    'event=filetransfer_v4_sender_started; transfer_id={0}; session_id={1}; chunk_size_bytes=21504; chunk_count={2}; pipeline_depth=8; pending_bytes_limit=2097152' -f $transferId, $sessionId, $chunkFrameCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(20) -Message (
                    'event=filetransfer_v4_receiver_started; transfer_id={0}; session_id={1}; protocol_version=4; session_open_chunk_size_bytes=21504; session_open_pipeline_depth=8' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(40) -Message (
                    'event=filetransfer_v4_manifest_sent; transfer_id={0}; session_id={1}; file_size_bytes={2}; chunk_size_bytes=21504; chunk_count={3}' -f $transferId, $sessionId, $payloadBytes, $chunkFrameCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(60) -Message (
                    'event=filetransfer_v4_state_sent; transfer_id={0}; session_id={1}; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive={2}; missing_range_count=0; bytes_committed=0; terminal_ready=0' -f $transferId, $sessionId, $chunkFrameCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(80) -Message (
                    'event=filetransfer_v4_feedback_first_success; transport=nkn; transfer_id={0}; session_id={1}; frame_type=filetransfer.state.v4; lane=bulk; elapsed_ms=2; first_lane_failed=0' -f $transferId, $sessionId))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(100) -Message (
                    'event=filetransfer_v4_state_received; transfer_id={0}; session_id={1}; epoch=1; applied=1; stale=0; duplicate=0; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive={2}; effective_credit_until_chunk_index_exclusive={2}; available_credit_chunks={2}; missing_range_count=0; bytes_committed=0; terminal_ready=0' -f $transferId, $sessionId, $chunkFrameCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(105) -Message (
                    'event=filetransfer_v4_sender_pump_summary; transfer_id={0}; session_id={1}; sample_window_ms=1000; scheduled_frames=1; normal_scheduled_frames=1; repair_scheduled_frames=0; completed_frames=1; failed_frames=0; in_flight_frames=1; raw_bytes_sent={2}; repair_send_count=0; available_credit_bytes=1048576; credit_exhausted_time_ms=0; next_unsent_chunk_index=0; credit_ceiling_chunk_index={3}; remote_frontier_chunk_index=0; terminal_ready=0; pump_wake_reason=state_credit' -f $transferId, $sessionId, $payloadBytes, $chunkFrameCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(110) -Message (
                    'event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={0}; session_id={1}; frame_type=filetransfer.chunk_batch.v4; chunk_range=0-{2}; chunk_frame_count={3}; batch_chunk_count={3}; raw_bytes={4}; lane=bulk; batch_profile=v4_default_21k; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.10' -f $transferId, $sessionId, $batchFinalChunkIndex, $batchChunkCount, $batchPayloadBytes))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(120) -Message (
                    'event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={0}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; lane=bulk; serialized_payload_bytes={1}; secure_payload_bytes={2}; bridge_payload_bytes={3}; bridge_command_bytes={4}; max_allowed_bytes=65536; batch_profile=v4_default_21k; batch_chunk_count={5}; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.10' -f $transferId, $batchPayloadBytes, ($batchPayloadBytes + 225), ($batchPayloadBytes + 302), $transportPayloadBytes, $batchChunkCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(130) -Message (
                    'event=filetransfer_binary_frame_sent; transfer_id={0}; session_id={1}; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-{2}; payload_bytes={3}; serialized_payload_bytes={3}; raw_chunk_bytes={4}; chunk_count={5}' -f $transferId, $sessionId, $batchFinalChunkIndex, $batchPayloadBytes, $batchPayloadBytes, $batchChunkCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(140) -Message (
                    'event=filetransfer_binary_frame_received; transfer_id={0}; session_id={1}; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-{2}; raw_chunk_bytes={3}; chunk_count={4}' -f $transferId, $sessionId, $batchFinalChunkIndex, $batchPayloadBytes, $batchChunkCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(160) -Message (
                    'event=file_transfer_inbound_terminal; role=helper; session_id={0}; transfer_id={1}; state=Completed; error_code=(none); chunks_transferred={2}/{2}; reason=Transfer complete; saved_path=(none)' -f $sessionId, $transferId, $chunkFrameCount))) | Out-Null
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $timestamp.AddMilliseconds(180) -Message (
                    'event=file_transfer_outbound_terminal; role=helpee; session_id={0}; transfer_id={1}; state=Completed; error_code=(none); chunks_transferred={2}/{2}; reason=Transfer complete' -f $sessionId, $transferId, $chunkFrameCount))) | Out-Null

        $durationMs = [Math]::Max(1, [int][Math]::Round(($payloadBytes / $goodputBytesPerSecond) * 1000.0))
        $cycle = [ordered]@{
            cycle_index = $cycleIndex
            mode = $Mode
            scenario = $Scenario
            direction = $Direction
            transfer_id = $transferId
            payload_bytes = $payloadBytes
            duration_ms = $durationMs
            goodput_bytes_per_second = $goodputBytesPerSecond
            completed = $true
            integrity_ok = $true
        }
        $cycleLines.Add(($cycle | ConvertTo-Json -Compress -Depth 6)) | Out-Null
    }

    $summaryTimestamp = $baseTimestamp.AddSeconds(1 + ($cycleCount * 3))
    $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $summaryTimestamp -Message 'event=nkn_bridge_bulk_queue_state; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; congested=0; severe=0; cleared_since_last=0')) | Out-Null
    $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $summaryTimestamp.AddMilliseconds(20) -Message (
                'event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent={0}; send_failures=0; queue_clears=0; enqueue_to_send_p95_ms=0; dequeue_to_send_p95_ms=0' -f $cycleCount))) | Out-Null

    if ([string]::Equals($Mode, 'nkn-mixed', [System.StringComparison]::OrdinalIgnoreCase)) {
        $logLines.Add((New-FileTransferNknFakeLogLine -TimestampUtc $summaryTimestamp.AddMilliseconds(40) -Message 'event=screenshare_bridge_media_send_summary; frames_sent=30; queue_drops=0; send_failures=0; severe=0; queue_mode=normal; binary_send_frame_observed_to_queue_enqueue_p95_ms=0')) | Out-Null
    }

    $logLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
    $cycleLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') -Encoding UTF8
}

function Clear-FileTransferNknRunArtifacts {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    foreach ($name in @(
            'baseline-comparison.txt',
            'bridge-bulk-summary.txt',
            'coexistence-summary.txt',
            'external-transport-health-summary.txt',
            'filetransfer-live-autopick-payload.bin',
            'filetransfer-live-nkn-cycles.jsonl',
            'filetransfer-live-nkn-summary.json',
            'filetransfer-live-nkn-summary.txt',
            'filetransfer-operator-verdict.txt',
            'filetransfer-retained-log-slice.log',
            'gui-smoke-stderr.log',
            'gui-smoke-stdout.log',
            'payload-efficiency-summary.txt',
            'protocol-shape-summary.txt',
            'raw-log-slices.txt',
            'repair-reorder-summary.txt',
            'stability-gates-summary.txt',
            'throughput-decomposition-summary.txt',
            'throughput-summary.txt',
            'transfer-terminal-summary.txt',
            'transport-budget-summary.txt',
            'v4-promotion-decision.json',
            'v4-promotion-decision.txt')) {
        $path = Join-Path $ArtifactDir $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
}

$repoRoot = Resolve-RepoRoot
Assert-PayloadEfficiencyProfileIsSafeForMode
$resolvedArtifactDir = Resolve-FileTransferArtifactDir -RepoRoot $repoRoot -RequestedArtifactDir $ArtifactDir
New-Item -ItemType Directory -Force -Path $resolvedArtifactDir | Out-Null
Clear-FileTransferNknRunArtifacts -ArtifactDir $resolvedArtifactDir
$effectiveLiveCycles = Get-FileTransferNknEffectiveCycleCount -PayloadSizes $PayloadSizes -Cycles $Cycles

$guiSmokeScript = Join-Path $repoRoot 'tools\GuiSmoke-Windows.ps1'
if (-not (Test-Path -LiteralPath $guiSmokeScript -PathType Leaf)) {
    throw "GUI smoke harness not found: $guiSmokeScript"
}

$resolvedExePath = Resolve-FileTransferLiveExePath -RepoRoot $repoRoot -RequestedPath $ExePath
$autopickPath = Join-Path $resolvedArtifactDir 'filetransfer-live-autopick-payload.bin'
[System.IO.File]::WriteAllBytes($autopickPath, [byte[]]@())

$scenario = if ([string]::Equals($Mode, 'nkn-mixed', [System.StringComparison]::OrdinalIgnoreCase)) {
    'FILETRANSFER_NKN_MIXED_SOAK'
}
else {
    'FILETRANSFER_NKN_SOAK'
}

$previousValues = @{}
foreach ($key in @(
        'NLINK_GUI_SMOKE_SCENARIOS',
        'NLINK_TRANSPORT',
        'NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES',
        'NLINK_FILETRANSFER_SOAK_CYCLES',
        'NLINK_FILETRANSFER_SOAK_DIRECTION',
        'NLINK_FILETRANSFER_SOAK_SEED',
        'NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS',
        'NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR',
        'NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE',
        'NLINK_FILETRANSFER_SOAK_STARTUP_TIMEOUT_SECONDS',
        'NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS',
        'NLINK_FILETRANSFER_MIXED_SCREENSHARE_WARMUP_TIMEOUT_SECONDS')) {
    $previousValues[$key] = [pscustomobject]@{
        HadValue = Test-Path -LiteralPath ("Env:{0}" -f $key)
        Value = [System.Environment]::GetEnvironmentVariable($key)
    }
}

$topologyRestore = Set-FileTransferExternalTopologyProfileEnvironment -Profile $ExternalTopologyProfile
$payloadEfficiencyRestore = Set-FileTransferPayloadEfficiencyProfileEnvironment -Profile $PayloadEfficiencyProfile -Mode $Mode
$guiHarnessExitCode = 0
$analysis = $null
$fakeGuiMode = Test-FileTransferNknSoakFakeGuiMode

try {
    if (-not $fakeGuiMode) {
        Stop-NLinkProcesses -ResolvedExePath $resolvedExePath
        Build-FileTransferPortableIfNeeded -RepoRoot $repoRoot -ResolvedExePath $resolvedExePath -ForceBuild:$Build.IsPresent
        Ensure-NknBridgeRuntimeForExe -RepoRoot $repoRoot -ResolvedExePath $resolvedExePath
    }

    Set-ProcessEnvironmentValue -Name 'NLINK_GUI_SMOKE_SCENARIOS' -Value $scenario
    Set-ProcessEnvironmentValue -Name 'NLINK_TRANSPORT' -Value 'NKN'
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES' -Value $PayloadSizes
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_CYCLES' -Value ([string]$effectiveLiveCycles)
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_DIRECTION' -Value $Direction
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_SEED' -Value ([string]$Seed)
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS' -Value ([string]$CycleTimeoutSeconds)
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR' -Value $resolvedArtifactDir
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE' -Value $autopickPath
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_STARTUP_TIMEOUT_SECONDS' -Value ([string][Math]::Min([Math]::Max(30, $CycleTimeoutSeconds), 90))
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS' -Value ([string][Math]::Min([Math]::Max(30, $ProgressTimeoutSeconds), $CycleTimeoutSeconds))
    Set-ProcessEnvironmentValue -Name 'NLINK_FILETRANSFER_MIXED_SCREENSHARE_WARMUP_TIMEOUT_SECONDS' -Value ([string][Math]::Min([Math]::Max(30, $CycleTimeoutSeconds), 120))

    Write-Host "Running live NKN file-transfer soak..." -ForegroundColor Cyan
    Write-Host "  Mode: $Mode"
    Write-Host "  Scenario: $scenario"
    Write-Host "  ExePath: $resolvedExePath"
    Write-Host "  ArtifactDir: $resolvedArtifactDir"
    Write-Host "  ExternalTopologyProfile: $ExternalTopologyProfile"
    Write-Host "  PayloadEfficiencyProfile: $PayloadEfficiencyProfile"
    if ($fakeGuiMode) {
        Write-Host "  FakeGuiMode: enabled"
    }

    if ($fakeGuiMode) {
        Write-FileTransferNknFakeArtifacts `
            -ArtifactDir $resolvedArtifactDir `
            -Mode $Mode `
            -Scenario $scenario `
            -PayloadSizes $PayloadSizes `
            -Cycles $Cycles `
            -Direction $Direction `
            -Seed $Seed `
            -PayloadEfficiencyProfile $PayloadEfficiencyProfile
        $guiHarnessExitCode = if (Test-FileTransferNknSoakFakeProgressTimeout) { 1 } else { 0 }
    }
    else {
        $guiHarnessExitCode = Invoke-FileTransferGuiSmokeWithTimeout `
            -GuiSmokeScript $guiSmokeScript `
            -ResolvedExePath $resolvedExePath `
            -ArtifactDir $resolvedArtifactDir `
            -TimeoutSeconds $TimeoutSeconds
    }

    $logSlicePath = Join-Path $resolvedArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logSlicePath -PathType Leaf)) {
        $fallbackLog = Join-Path $env:LOCALAPPDATA 'nLink\logs\nlink.log'
        if (Test-Path -LiteralPath $fallbackLog -PathType Leaf) {
            Copy-Item -LiteralPath $fallbackLog -Destination $logSlicePath -Force
        }
    }

    Add-FileTransferLiveNknSyntheticEvidence -ArtifactDir $resolvedArtifactDir -CyclesRequested $effectiveLiveCycles

    if (Test-Path -LiteralPath $logSlicePath -PathType Leaf) {
        $analysis = Invoke-FileTransferRetainedAnalysis `
            -RepoRoot $repoRoot `
            -LogPath @($logSlicePath) `
            -ArtifactDir $resolvedArtifactDir `
            -TailMinutes 0 `
            -IncludeRawSlices:$IncludeRawSlices `
            -AllTransfers
    }
    else {
        Write-Warning "No retained log slice was produced for live NKN file-transfer soak."
        $analysis = Invoke-FileTransferRetainedAnalysis `
            -RepoRoot $repoRoot `
            -LogPath @() `
            -ArtifactDir $resolvedArtifactDir `
            -TailMinutes 0 `
            -IncludeRawSlices:$IncludeRawSlices `
            -AllTransfers
    }

    Write-FileTransferLiveNknSummary `
        -ArtifactDir $resolvedArtifactDir `
        -Analysis $analysis `
        -Mode $Mode `
        -ExternalTopologyProfile $ExternalTopologyProfile `
        -PayloadEfficiencyProfile $PayloadEfficiencyProfile `
        -ResolvedExePath $resolvedExePath `
        -GuiHarnessExitCode $guiHarnessExitCode `
        -CyclesRequested $effectiveLiveCycles

    $baseline = Write-FileTransferBaselineComparison `
        -ArtifactDir $resolvedArtifactDir `
        -SafeBaselineArtifactDir $SafeBaselineArtifactDir `
        -StrongBaselineArtifactDir $StrongBaselineArtifactDir

    if ($baseline.RegressionFailed) {
        Set-FileTransferRegressionVerdict `
            -ArtifactDir $resolvedArtifactDir `
            -TransferId '(live-nkn)' `
            -RegressionFindings $baseline.RegressionFindings
    }

    Write-Host ("[FileTransfer NKN Soak] artifact_dir={0}" -f $resolvedArtifactDir) -ForegroundColor Green
    Write-Host "[FileTransfer NKN Soak] first_read=filetransfer-operator-verdict.txt" -ForegroundColor Green
    Write-Host "[FileTransfer NKN Soak] baseline_artifact=baseline-comparison.txt" -ForegroundColor Green

    if ($guiHarnessExitCode -ne 0) {
        exit $guiHarnessExitCode
    }

    if ($FailOnGate -and (
            $analysis.GateResult.Verdict -eq 'FAIL_PROTOCOL_OR_INTEGRITY' -or
            $analysis.GateResult.Verdict -eq 'INCONCLUSIVE' -or
            $analysis.GateResult.Verdict -eq 'INCONCLUSIVE_PROGRESS_TIMEOUT' -or
            $analysis.GateResult.Verdict -eq 'INVALID_SETUP' -or
            $baseline.RegressionFailed)) {
        exit 1
    }

    exit 0
}
finally {
    if (-not $fakeGuiMode) {
        Stop-NLinkProcesses -ResolvedExePath $resolvedExePath
    }
    Restore-FileTransferExternalTopologyProfileEnvironment -Restore $topologyRestore
    Restore-FileTransferPayloadEfficiencyProfileEnvironment -Restore $payloadEfficiencyRestore
    foreach ($key in $previousValues.Keys) {
        $entry = $previousValues[$key]
        if ($entry.HadValue) {
            Set-ProcessEnvironmentValue -Name $key -Value $entry.Value
        }
        else {
            Set-ProcessEnvironmentValue -Name $key -Value $null
        }
    }
}
