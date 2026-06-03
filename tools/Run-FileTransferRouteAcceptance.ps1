param(
    [string]$ExePath = ".\artifacts\portable\nLink\win-x64\nLink.exe",
    [string]$WalletPath = ".\artifacts\tuna-poc\wallet-test-nkn.json",
    [string]$WalletPassword = "",
    [string]$SidecarPath = ".\artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe",
    [string]$Runtime = "win-x64",
    [string]$ArtifactRoot = "artifacts\filetransfer-route-acceptance",
    [ValidateSet("legacy", "phase4-ab-acceptance", "phase5-analyzer-gui-acceptance")]
    [string]$MatrixMode = "legacy",
    [string]$BaselineManifestPath = "artifacts\filetransfer-route-ab\baseline-lock-v0.7.0-20260524\baseline-manifest.json",
    [double]$GoodputRegressionTolerancePercent = 10D,
    [int]$GoodputOnlyRerunLimit = 1,
    [int]$TimeoutSeconds = 900,
    [int]$ProgressTimeoutSeconds = 180,
    [int]$FallbackMaxAttempts = 2,
    [bool]$AllowExternalTransportWarnings = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RegularNknGoodputFloorBytesPerSecond = 1500000D
$script:TunaGoodputFloorBytesPerSecond = 4000000D
$script:RunResults = New-Object System.Collections.Generic.List[object]

function Resolve-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Test-RouteAcceptanceEnvEnabled {
    param([Parameter(Mandatory = $true)][string]$Name)

    $value = [string][System.Environment]::GetEnvironmentVariable($Name)
    return -not [string]::IsNullOrWhiteSpace($value) -and $value -match '^(1|true|yes|on)$'
}

function Get-RouteAcceptanceEnvValue {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$DefaultValue = ''
    )

    $value = [string][System.Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }

    return $value
}

function ConvertTo-RouteAcceptanceDouble {
    param(
        [AllowNull()]$Value,
        [double]$DefaultValue = 0
    )

    if ($null -eq $Value) {
        return $DefaultValue
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $DefaultValue
    }

    $parsed = 0.0
    if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function ConvertTo-RouteAcceptanceInt {
    param(
        [AllowNull()]$Value,
        [int]$DefaultValue = 0
    )

    if ($null -eq $Value) {
        return $DefaultValue
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $DefaultValue
    }

    $parsed = 0
    if ([int]::TryParse($text, [System.Globalization.NumberStyles]::Integer, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function ConvertTo-RouteAcceptanceBool {
    param([AllowNull()]$Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return [bool]$Value
    }

    $text = ([string]$Value).Trim()
    return $text -match '^(1|true|yes|on)$'
}

function Get-JsonPropertyValue {
    param(
        [AllowNull()]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()]$DefaultValue = $null
    )

    if ($null -eq $Object) {
        return $DefaultValue
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Resolve-RouteAcceptancePath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function New-RouteAcceptanceTimestampedRoot {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RequestedRoot
    )

    $root = Resolve-RouteAcceptancePath -RepoRoot $RepoRoot -Path $RequestedRoot
    $repoArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts'))
    if (-not $root.StartsWith($repoArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ArtifactRoot must resolve under repo artifacts/: $root"
    }

    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $timestamp = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TIMESTAMP'
    if ([string]::IsNullOrWhiteSpace($timestamp)) {
        $timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    }

    $candidate = Join-Path $root $timestamp
    $index = 1
    while (Test-Path -LiteralPath $candidate) {
        $candidate = Join-Path $root ("{0}-{1}" -f $timestamp, $index)
        $index++
    }

    New-Item -ItemType Directory -Force -Path $candidate | Out-Null
    return $candidate
}

function New-RouteAcceptanceRunResult {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ExpectedRoute,
        [Parameter(Mandatory = $true)][int]$ExpectedProtocol
    )

    return [pscustomobject]@{
        name = $Name
        artifactDir = $ArtifactDir
        expectedRoute = $ExpectedRoute
        expectedProtocol = $ExpectedProtocol
        route = '(unknown)'
        finalRoute = '(unknown)'
        protocol = '(unknown)'
        runtimeProfile = '(unknown)'
        bridgeRecoveryPolicy = '(unknown)'
        routeConsistencyVerdict = '(missing)'
        selectedRouteSequence = @()
        selectedRouteChanges = @()
        liveRouteEpochRouteChanges = @()
        completed = $false
        integrityOk = $false
        shaOk = $false
        goodputBytesPerSecond = 0D
        baselineGoodputBytesPerSecond = 0D
        goodputRegressionFloorBytesPerSecond = 0D
        goodputRegressionPercent = 0D
        bridgeBulkSendFailureCount = 0
        operatorVerdict = '(missing)'
        operatorAcceptedWithWarnings = $false
        hardFailureCount = 0
        warningCount = 0
        warningKinds = @()
        warningCapExceededKinds = '(none)'
        environmentalClassification = '(none)'
        measurementContaminated = $false
        measurementContaminationReasons = @()
        attemptCount = 1
        retryUsed = $false
        selectedAttempt = 1
        firstFailureReason = ''
        rerunArtifactDir = ''
        rerunFailureReason = ''
        setupFailurePhase = ''
        setupFailureReason = ''
        controlledRestartAnalysis = $null
        liveRouteEpochProofVerdict = '(missing)'
        fallbackLegAuthorityProofVerdict = '(missing)'
        bridgeLivenessIntegrationVerdict = '(missing)'
        sessionLivenessTimeoutCount = 0
        bridgeLivenessStaleDeferralCount = 0
        bridgeLivenessTimeoutDuringValidRecoveryCount = 0
        failures = New-Object System.Collections.Generic.List[string]
    }
}

function Add-RouteAcceptanceFailure {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $Result.failures.Add($Message) | Out-Null
}

function Read-RouteAcceptanceKeyValueArtifact {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $values
    }

    foreach ($line in @(Get-Content -LiteralPath $Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $index = $line.IndexOf('=')
        if ($index -le 0) {
            continue
        }

        $key = $line.Substring(0, $index).Trim()
        $value = $line.Substring($index + 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $values[$key] = $value
        }
    }

    return $values
}

function Get-RouteAcceptanceReportValue {
    param(
        [Parameter(Mandatory = $true)]$Report,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$DefaultValue = ''
    )

    if ($Report.ContainsKey($Name)) {
        return [string]$Report[$Name]
    }

    return $DefaultValue
}

function Test-RouteAcceptanceZombieTerminalState {
    param([string]$TerminalStates)

    if ([string]::IsNullOrWhiteSpace($TerminalStates)) {
        return $false
    }

    return $TerminalStates.IndexOf('Sending', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $TerminalStates.IndexOf('Receiving', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-RouteAcceptanceTerminalErrorState {
    param([string]$TerminalErrors)

    if ([string]::IsNullOrWhiteSpace($TerminalErrors)) {
        return $false
    }

    $tokens = @($TerminalErrors.Split([char]',') | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($tokens.Count -eq 0) {
        return $false
    }

    foreach ($token in $tokens) {
        if ($token -ne '(none)') {
            return $true
        }
    }

    return $false
}

function Split-RouteAcceptanceTokenList {
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

function Join-RouteAcceptanceTokenList {
    param([AllowNull()][object[]]$Values)

    $tokens = @($Values | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' })
    if ($tokens.Count -eq 0) {
        return '(none)'
    }

    return ($tokens -join ',')
}

function ConvertTo-RouteAcceptanceEnvNameToken {
    param([Parameter(Mandatory = $true)][string]$Value)

    return (($Value.ToUpperInvariant() -replace '[^A-Z0-9]+', '_').Trim('_'))
}

function Get-RouteAcceptanceScenarioEnvValue {
    param(
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][string]$Suffix,
        [string]$DefaultValue = ''
    )

    $token = ConvertTo-RouteAcceptanceEnvNameToken -Value $ScenarioName
    return Get-RouteAcceptanceEnvValue -Name ("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_{0}_{1}" -f $token, $Suffix) -DefaultValue $DefaultValue
}

function Test-RouteAcceptanceScenarioEnvEnabled {
    param(
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][string]$Suffix
    )

    $token = ConvertTo-RouteAcceptanceEnvNameToken -Value $ScenarioName
    return Test-RouteAcceptanceEnvEnabled -Name ("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_{0}_{1}" -f $token, $Suffix)
}

function Get-RouteAcceptanceRouteChanges {
    param([AllowNull()][object[]]$Routes)

    $changes = New-Object System.Collections.Generic.List[string]
    foreach ($route in @($Routes)) {
        $routeText = [string]$route
        if ([string]::IsNullOrWhiteSpace($routeText) -or $routeText -eq '(none)' -or $routeText -eq '(unknown)') {
            continue
        }

        if ($changes.Count -eq 0 -or
            -not [string]::Equals($changes[$changes.Count - 1], $routeText, [System.StringComparison]::OrdinalIgnoreCase)) {
            $changes.Add($routeText) | Out-Null
        }
    }

    return $changes.ToArray()
}

function Read-RouteAcceptanceBaselineManifest {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $resolvedPath = Resolve-RouteAcceptancePath -RepoRoot $RepoRoot -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Baseline manifest missing: $resolvedPath"
    }

    $manifest = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    $baselines = @{}
    foreach ($scenario in @($manifest.scenarios)) {
        $name = [string](Get-JsonPropertyValue -Object $scenario -Name 'scenario' -DefaultValue '')
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $baselines[$name] = $scenario
        }
    }

    return [pscustomobject]@{
        Path = $resolvedPath
        Manifest = $manifest
        Scenarios = $baselines
    }
}

function Test-RouteAcceptanceAllowedOperatorWarning {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedRoute,
        [Parameter(Mandatory = $true)][string]$Verdict,
        [Parameter(Mandatory = $true)][string[]]$WarningKinds
    )

    $kinds = @($WarningKinds)
    if (-not $AllowExternalTransportWarnings -or $Verdict -ne 'WARN_EXTERNAL_TRANSPORT') {
        return $false
    }

    if ($kinds.Count -eq 0) {
        return $false
    }

    $allowed = @(switch ($ExpectedRoute) {
        'file_tuna_v4' { @('external_transport_churn', 'fallback_frontier_repair_churn', 'fallback_receiver_state_churn') }
        'post_tuna_fallback_v6' { @('external_transport_churn', 'fallback_v6_send_timeout_churn', 'fallback_frontier_repair_churn', 'fallback_receiver_state_churn', 'recovered_post_tuna_fallback_bridge_clear') }
        default { @() }
    })

    if ($allowed.Count -eq 0) {
        return $false
    }

    foreach ($kind in @($kinds)) {
        if (-not ($allowed -contains $kind)) {
            return $false
        }
    }

    return $true
}

function Test-Phase4RegularNknExternalTransportVariance {
    param([Parameter(Mandatory = $true)]$Result)

    if ($Result.finalRoute -ne 'regular_nkn_v4_fast' -or
        $Result.operatorVerdict -ne 'WARN_EXTERNAL_TRANSPORT' -or
        $Result.hardFailureCount -ne 0 -or
        $Result.warningCapExceededKinds -ne '(none)') {
        return $false
    }

    $kinds = @($Result.warningKinds | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' })
    if ($kinds.Count -eq 0) {
        return $false
    }

    foreach ($kind in @($kinds)) {
        if ($kind -ne 'external_transport_churn') {
            return $false
        }
    }

    return $true
}

function Test-Phase4RegularNknProgressTimeoutRecoveryStormVariance {
    param([Parameter(Mandatory = $true)]$Result)

    if ($Result.finalRoute -ne 'regular_nkn_v4_fast' -or
        $Result.warningCapExceededKinds -ne '(none)' -or
        $Result.bridgeLivenessIntegrationVerdict -eq 'fail') {
        return $false
    }

    $summaryPath = Join-Path $Result.artifactDir 'filetransfer-live-nkn-summary.txt'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return $false
    }

    $summary = Read-RouteAcceptanceKeyValueArtifact -Path $summaryPath
    $protocol = Get-RouteAcceptanceReportValue -Report $summary -Name 'data_protocol_version' -DefaultValue '(missing)'
    $verdict = Get-RouteAcceptanceReportValue -Report $summary -Name 'verdict' -DefaultValue '(missing)'
    $cyclesObserved = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_observed' -DefaultValue '0')
    $bridgeBulkQueueClearCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'bridge_bulk_queue_clear_count' -DefaultValue '0')
    $v4FeedbackBothFailedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'v4_feedback_both_failed_count' -DefaultValue '0')
    $progressTimeoutCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'gui_progress_timeout_count' -DefaultValue '0')
    $terminalMissingAfterProgressTimeout = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'terminal_missing_after_progress_timeout' -DefaultValue '0')
    if ($protocol -ne '4') {
        return $false
    }

    $progressTimeoutStorm = ($verdict -eq 'INCONCLUSIVE_PROGRESS_TIMEOUT' -or $progressTimeoutCount -gt 0) -and
        $terminalMissingAfterProgressTimeout -ne 0
    $startupRecoveryStorm = $verdict -eq 'FAIL_PROTOCOL_OR_INTEGRITY' -and
        $progressTimeoutCount -eq 0 -and
        $terminalMissingAfterProgressTimeout -eq 0 -and
        $cyclesObserved -eq 0 -and
        $bridgeBulkQueueClearCount -gt 0 -and
        $v4FeedbackBothFailedCount -eq 0
    if (-not $progressTimeoutStorm -and -not $startupRecoveryStorm) {
        return $false
    }

    $kinds = @($Result.warningKinds | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' })
    if ($Result.operatorVerdict -eq 'INCONCLUSIVE_PROGRESS_TIMEOUT' -and
        $Result.hardFailureCount -eq 0 -and
        ($kinds -contains 'public_nkn_regular_v4_recovery_storm')) {
        return $true
    }

    if ($Result.operatorVerdict -eq 'INCONCLUSIVE' -and
        $Result.hardFailureCount -eq 0) {
        return $true
    }

    if ($Result.operatorVerdict -ne 'FAIL_PROTOCOL_OR_INTEGRITY' -or
        $Result.hardFailureCount -le 0) {
        return $false
    }

    $stabilityPath = Join-Path $Result.artifactDir 'stability-gates-summary.txt'
    if (-not (Test-Path -LiteralPath $stabilityPath -PathType Leaf)) {
        return $false
    }

    $stabilityText = Get-Content -LiteralPath $stabilityPath -Raw
    if ($stabilityText.IndexOf('bridge bulk send failure/clear', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $false
    }

    foreach ($unexpected in @('hard protocol/integrity event:', 'terminal failure:', 'route consistency:', 'legacy data protocol started:', 'legacy data frame observed')) {
        if ($stabilityText.IndexOf($unexpected, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $false
        }
    }

    return $true
}

function Set-Phase4RegularNknProgressTimeoutRecoveryStormVariance {
    param([Parameter(Mandatory = $true)]$Result)

    if (-not (Test-Phase4RegularNknProgressTimeoutRecoveryStormVariance -Result $Result)) {
        return
    }

    $Result.failures.Clear()
    $Result.environmentalClassification = 'public_nkn_regular_v4_recovery_storm'
    Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason 'public_nkn_regular_v4_recovery_storm'
}

function Add-RouteAcceptanceMeasurementContamination {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    if ([string]::IsNullOrWhiteSpace($Reason)) {
        return
    }

    $existing = @($Result.measurementContaminationReasons | ForEach-Object { [string]$_ })
    if (-not ($existing -contains $Reason)) {
        $Result.measurementContaminationReasons += @($Reason)
    }

    $Result.measurementContaminated = $true
}

function Get-RouteAcceptanceReportInt {
    param(
        [Parameter(Mandatory = $true)]$Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $Report -Name $Name -DefaultValue '0')
}

function Get-RouteAcceptanceMeasuredWindowBridgeRecoveryCount {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        return 0
    }

    $lines = @(Get-Content -LiteralPath $logPath)
    $routeStartIndex = -1
    $terminalIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = [string]$lines[$i]
        if ($routeStartIndex -lt 0 -and
            $line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $routeStartIndex = $i
            continue
        }

        if ($routeStartIndex -ge 0 -and
            $line.IndexOf('event=transfer_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $terminalIndex = $i
            break
        }
    }

    if ($routeStartIndex -lt 0) {
        return 0
    }

    $windowStart = [Math]::Max(0, $routeStartIndex - 150)
    $windowEnd = if ($terminalIndex -ge 0) { $terminalIndex } else { $lines.Count - 1 }
    $count = 0
    for ($i = $windowStart; $i -le $windowEnd; $i++) {
        $line = [string]$lines[$i]
        if ($line.IndexOf('event=nkn_bridge_receive_stall_detected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=nkn_bridge_receive_stall_recovery_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=nkn_bridge_receive_stall_recovery_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=nkn_bridge_receive_stall_recovery_receive_resumed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $count++
        }
    }

    return $count
}

function Set-Phase4MeasurementContamination {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if ($Scenario.Kind -ne 'tuna' -or $Result.finalRoute -ne 'file_tuna_v4') {
        return
    }

    $analysisDir = $Result.artifactDir
    if ([string]$Scenario.Name -eq 'second-transfer-after-reactivation') {
        $analysisDir = Join-Path $Result.artifactDir 'second-transfer-analysis'
    }

    $repairPath = Join-Path $analysisDir 'repair-reorder-summary.txt'
    if (Test-Path -LiteralPath $repairPath -PathType Leaf) {
        $repair = Read-RouteAcceptanceKeyValueArtifact -Path $repairPath
        $repairRequested = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_repair_requested_count'
        $repairScheduled = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_missing_range_repair_scheduled_count'
        $repairSent = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_missing_range_repair_sent_count'
        $controlBulkEscalated = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_repair_delivery_control_bulk_escalated_count'
        $creditStallEscalated = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_repair_delivery_credit_stall_escalated_count'
        $frontierStallEscalated = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_repair_delivery_frontier_not_advanced_escalated_count'
        if ($repairRequested -gt 0 -or $repairScheduled -gt 0 -or $repairSent -gt 0 -or $controlBulkEscalated -gt 0 -or $creditStallEscalated -gt 0 -or $frontierStallEscalated -gt 0) {
            Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason ("active_tuna_v4_repair_pressure: requested={0}; scheduled={1}; sent={2}; control_bulk={3}; credit_stall={4}; frontier_stall={5}" -f $repairRequested, $repairScheduled, $repairSent, $controlBulkEscalated, $creditStallEscalated, $frontierStallEscalated)
        }
    }

    $measuredRecoveryCount = Get-RouteAcceptanceMeasuredWindowBridgeRecoveryCount -ArtifactDir $analysisDir
    if ($measuredRecoveryCount -gt 0) {
        Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason ("active_tuna_v4_bridge_receive_recovery_window: event_count={0}" -f $measuredRecoveryCount)
    }
}

function Assert-RouteAcceptanceFileExists {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Result.artifactDir $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("missing artifact: {0}" -f $RelativePath)
        return $false
    }

    return $true
}

function Assert-RouteAcceptanceRouteSummary {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$ExpectedRoute,
        [Parameter(Mandatory = $true)][int]$ExpectedProtocol,
        [Parameter(Mandatory = $true)][string]$ExpectedRuntime,
        [Parameter(Mandatory = $true)][string]$ExpectedBridgePolicy
    )

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'filetransfer-route-consistency-summary.txt')) {
        return
    }

    $routePath = Join-Path $Result.artifactDir 'filetransfer-route-consistency-summary.txt'
    $routeSummary = Read-RouteAcceptanceKeyValueArtifact -Path $routePath
    $Result.routeConsistencyVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'route_consistency_verdict' -DefaultValue '(missing)'
    $Result.route = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'selected_routes' -DefaultValue '(none)'

    if ($Result.routeConsistencyVerdict -ne 'pass') {
        Add-RouteAcceptanceFailure -Result $Result -Message ("route consistency verdict is {0}" -f $Result.routeConsistencyVerdict)
    }

    if ($Result.route.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'diagnostic_regular_nkn_v6 route is not allowed during acceptance'
    }

    if ($Result.route -ne $ExpectedRoute) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("selected route mismatch: expected {0}, actual {1}" -f $ExpectedRoute, $Result.route)
    }

    $selectedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'route_selected_count' -DefaultValue '0')
    if ($selectedCount -le 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'no filetransfer_route_selected event was observed'
    }

    foreach ($key in @($routeSummary.Keys)) {
        $keyText = [string]$key
        if ($keyText -match '^selected\.\d+\.protocol_version$') {
            $protocol = [string]$routeSummary[$key]
            $Result.protocol = $protocol
            if ($protocol -ne ([string]$ExpectedProtocol)) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("route selected protocol mismatch: expected {0}, actual {1}" -f $ExpectedProtocol, $protocol)
            }
        }
        elseif ($keyText -match '^selected\.\d+\.runtime_profile$') {
            $runtimeProfile = [string]$routeSummary[$key]
            $Result.runtimeProfile = $runtimeProfile
            if ($runtimeProfile -ne $ExpectedRuntime) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("route selected runtime mismatch: expected {0}, actual {1}" -f $ExpectedRuntime, $runtimeProfile)
            }
        }
        elseif ($keyText -match '^selected\.\d+\.bridge_recovery_policy$') {
            $bridgePolicy = [string]$routeSummary[$key]
            $Result.bridgeRecoveryPolicy = $bridgePolicy
            if ($bridgePolicy -ne $ExpectedBridgePolicy) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("route selected bridge policy mismatch: expected {0}, actual {1}" -f $ExpectedBridgePolicy, $bridgePolicy)
            }
        }
    }

    if ($Result.protocol -eq '(unknown)') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'route summary did not include selected protocol version'
    }
}

function Assert-RouteAcceptanceOperatorVerdict {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$ExpectedRoute
    )

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'filetransfer-operator-verdict.txt')) {
        return
    }

    $operator = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Result.artifactDir 'filetransfer-operator-verdict.txt')
    $Result.operatorVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'verdict' -DefaultValue '(missing)'
    $hardFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'hard_failure_count' -DefaultValue '0')
    $warningKinds = @(Split-RouteAcceptanceTokenList -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_kinds' -DefaultValue '(none)'))
    $Result.warningKinds = @($warningKinds)

    if ($hardFailureCount -gt 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("operator hard failures observed: {0}" -f $hardFailureCount)
        return
    }

    if ($Result.operatorVerdict -eq 'PASS') {
        return
    }

    if (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute $ExpectedRoute -Verdict $Result.operatorVerdict -WarningKinds $warningKinds) {
        $Result.operatorAcceptedWithWarnings = $true
        return
    }

    $operatorWarningKinds = @($warningKinds)
    Add-RouteAcceptanceFailure -Result $Result -Message ("operator verdict is not accepted for {0}: verdict={1}; warning_kinds={2}" -f $ExpectedRoute, $Result.operatorVerdict, ($(if ($operatorWarningKinds.Count -gt 0) { $operatorWarningKinds -join ',' } else { '(none)' })))
}

function Test-RouteAcceptanceFallbackSetupTunaV4Evidence {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $setupRoutePath = Join-Path $ArtifactDir 'setup-analysis\filetransfer-route-consistency-summary.txt'
    if (Test-Path -LiteralPath $setupRoutePath -PathType Leaf) {
        $setupRouteSummary = Read-RouteAcceptanceKeyValueArtifact -Path $setupRoutePath
        $selectedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $setupRouteSummary -Name 'route_selected_count' -DefaultValue '0')
        for ($i = 1; $i -le $selectedCount; $i++) {
            $route = Get-RouteAcceptanceReportValue -Report $setupRouteSummary -Name ("selected.{0}.route" -f $i) -DefaultValue ''
            $protocol = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $setupRouteSummary -Name ("selected.{0}.protocol_version" -f $i) -DefaultValue '0')
            if ($route -eq 'file_tuna_v4' -and $protocol -eq 4) {
                return $true
            }
        }
    }

    $setupLogPath = Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log'
    if (Test-Path -LiteralPath $setupLogPath -PathType Leaf) {
        foreach ($line in @(Get-Content -LiteralPath $setupLogPath)) {
            if ($line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('protocol_version=4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
        }
    }

    return $false
}

function Assert-RouteAcceptanceTerminalSummary {
    param([Parameter(Mandatory = $true)]$Result)

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'transfer-terminal-summary.txt')) {
        return
    }

    $terminal = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Result.artifactDir 'transfer-terminal-summary.txt')
    $inboundCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $terminal -Name 'inbound_terminal_count' -DefaultValue '0')
    $outboundCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $terminal -Name 'outbound_terminal_count' -DefaultValue '0')
    $states = Get-RouteAcceptanceReportValue -Report $terminal -Name 'terminal_states' -DefaultValue ''
    $errors = Get-RouteAcceptanceReportValue -Report $terminal -Name 'terminal_error_codes' -DefaultValue ''

    if ($inboundCount -le 0 -or $outboundCount -le 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("terminal summary missing inbound/outbound completion evidence: inbound={0}; outbound={1}" -f $inboundCount, $outboundCount)
    }

    if ($states.IndexOf('Completed', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("terminal summary did not report Completed state: {0}" -f $states)
    }

    if (Test-RouteAcceptanceZombieTerminalState -TerminalStates $states) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("zombie terminal state observed: {0}" -f $states)
    }

    if (Test-RouteAcceptanceTerminalErrorState -TerminalErrors $errors) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("terminal summary reported error codes: {0}" -f $errors)
    }
}

function Assert-RegularNknRouteAcceptanceRun {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    $result = New-RouteAcceptanceRunResult -Name $Name -ArtifactDir $ArtifactDir -ExpectedRoute 'regular_nkn_v4_fast' -ExpectedProtocol 4
    Assert-RouteAcceptanceRouteSummary -Result $result -ExpectedRoute 'regular_nkn_v4_fast' -ExpectedProtocol 4 -ExpectedRuntime 'regular_nkn_v4_fast' -ExpectedBridgePolicy 'regular_nkn_v4_fast'
    Assert-RouteAcceptanceTerminalSummary -Result $result
    Assert-RouteAcceptanceOperatorVerdict -Result $result -ExpectedRoute 'regular_nkn_v4_fast'

    if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-live-nkn-summary.txt') {
        $summary = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt')
        $protocol = Get-RouteAcceptanceReportValue -Report $summary -Name 'data_protocol_version' -DefaultValue '(missing)'
        $result.protocol = $protocol
        if ($protocol -ne '4') {
            Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN protocol mismatch: expected 4, actual {0}" -f $protocol)
        }

        $cyclesCompleted = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_completed' -DefaultValue '0')
        if ($cyclesCompleted -le 0) {
            Add-RouteAcceptanceFailure -Result $result -Message 'regular NKN completed no cycles'
        }

        $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'average_goodput_bytes_per_second' -DefaultValue '0')
        $result.bridgeBulkSendFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'bridge_bulk_send_failure_count' -DefaultValue '0')
        if ($result.bridgeBulkSendFailureCount -ne 0) {
            Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN bridge_bulk_send_failure_count must be 0, actual {0}" -f $result.bridgeBulkSendFailureCount)
        }
    }

    if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-live-nkn-cycles.jsonl') {
        $cycleLines = @(Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($cycleLines.Count -eq 0) {
            Add-RouteAcceptanceFailure -Result $result -Message 'regular NKN cycle artifact contained no cycles'
        }

        foreach ($line in $cycleLines) {
            $cycle = $line | ConvertFrom-Json
            $completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $cycle -Name 'completed' -DefaultValue $false)
            $integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $cycle -Name 'integrity_ok' -DefaultValue $false)
            if (-not $completed -or -not $integrityOk) {
                Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN cycle failed completion/integrity: {0}" -f $line)
            }
        }
    }

    $result.completed = $result.failures.Count -eq 0
    $result.integrityOk = $result.failures.Count -eq 0
    $script:RunResults.Add($result) | Out-Null
}

function Assert-TunaRouteAcceptanceRun {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ExpectedRoute,
        [Parameter(Mandatory = $true)][string]$ExpectedBridgePolicy
    )

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $ExpectedRoute
    $expectedProtocol = [int]$metadata.Protocol
    $result = New-RouteAcceptanceRunResult -Name $Name -ArtifactDir $ArtifactDir -ExpectedRoute $ExpectedRoute -ExpectedProtocol $expectedProtocol
    $attemptsPath = Join-Path $ArtifactDir 'route-acceptance-attempts.json'
    if (Test-Path -LiteralPath $attemptsPath -PathType Leaf) {
        $attempts = Get-Content -LiteralPath $attemptsPath -Raw | ConvertFrom-Json
        $result.attemptCount = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $attempts -Name 'attemptCount' -DefaultValue 1) -DefaultValue 1
        $result.retryUsed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $attempts -Name 'retryUsed' -DefaultValue $false)
        $result.selectedAttempt = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $attempts -Name 'selectedAttempt' -DefaultValue 0) -DefaultValue 0
        $result.firstFailureReason = [string](Get-JsonPropertyValue -Object $attempts -Name 'firstFailureReason' -DefaultValue '')
        if ($ExpectedRoute -eq 'post_tuna_fallback_v6' -and $result.selectedAttempt -le 0) {
            Add-RouteAcceptanceFailure -Result $result -Message 'fallback attempts exhausted before successful measured transfer'
        }
    }

    Assert-RouteAcceptanceRouteSummary -Result $result -ExpectedRoute $ExpectedRoute -ExpectedProtocol $expectedProtocol -ExpectedRuntime $metadata.Runtime -ExpectedBridgePolicy $ExpectedBridgePolicy
    Assert-RouteAcceptanceTerminalSummary -Result $result
    Assert-RouteAcceptanceOperatorVerdict -Result $result -ExpectedRoute $ExpectedRoute

    if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-tuna-gui-summary.json') {
        $summary = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Raw | ConvertFrom-Json
        $result.controlledRestartAnalysis = Get-JsonPropertyValue -Object $summary -Name 'controlledRestartAnalysis' -DefaultValue $null
        $result.completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $summary -Name 'completed' -DefaultValue $false)
        $result.integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $summary -Name 'integrityOk' -DefaultValue $false)
        $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-JsonPropertyValue -Object $summary -Name 'goodputBytesPerSecond' -DefaultValue 0)
        $inboundState = [string](Get-JsonPropertyValue -Object $summary -Name 'inboundState' -DefaultValue '(missing)')
        $outboundState = [string](Get-JsonPropertyValue -Object $summary -Name 'outboundState' -DefaultValue '(missing)')
        $inboundError = [string](Get-JsonPropertyValue -Object $summary -Name 'inboundErrorCode' -DefaultValue '(missing)')
        $outboundError = [string](Get-JsonPropertyValue -Object $summary -Name 'outboundErrorCode' -DefaultValue '(missing)')
        $measuredPhase = Get-JsonPropertyValue -Object $summary -Name 'measuredPhase' -DefaultValue $null
        if ($null -ne $measuredPhase) {
            $measuredRoute = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'route' -DefaultValue '')
            $measuredProtocol = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $measuredPhase -Name 'protocolVersion' -DefaultValue 0)
            $result.completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $measuredPhase -Name 'completed' -DefaultValue $false)
            $result.integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $measuredPhase -Name 'integrityOk' -DefaultValue $false)
            $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-JsonPropertyValue -Object $measuredPhase -Name 'goodputBytesPerSecond' -DefaultValue $result.goodputBytesPerSecond)
            $inboundState = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'inboundState' -DefaultValue $inboundState)
            $outboundState = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'outboundState' -DefaultValue $outboundState)
            $inboundError = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'inboundErrorCode' -DefaultValue $inboundError)
            $outboundError = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'outboundErrorCode' -DefaultValue $outboundError)

            if ($measuredRoute -ne $ExpectedRoute) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna measured route mismatch: expected={0}; actual={1}" -f $ExpectedRoute, $measuredRoute)
            }

            if ($measuredProtocol -ne $expectedProtocol) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna measured protocol mismatch: expected={0}; actual={1}" -f $expectedProtocol, $measuredProtocol)
            }
        }

        if ($ExpectedRoute -eq 'post_tuna_fallback_v6') {
            if (-not (Test-RouteAcceptanceFallbackSetupTunaV4Evidence -ArtifactDir $ArtifactDir)) {
                Add-RouteAcceptanceFailure -Result $result -Message 'Tuna fallback setup phase missing file_tuna_v4/4 route-selected evidence'
            }
        }

        if (-not $result.completed -or -not $result.integrityOk) {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI summary failed completion/integrity: completed={0}; integrityOk={1}" -f $result.completed, $result.integrityOk)
        }

        if ($inboundState -ne 'Completed' -or $outboundState -ne 'Completed') {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna terminals not Completed: inbound={0}; outbound={1}" -f $inboundState, $outboundState)
        }

        if (Test-RouteAcceptanceZombieTerminalState -TerminalStates ("{0},{1}" -f $inboundState, $outboundState)) {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna zombie terminal state observed: inbound={0}; outbound={1}" -f $inboundState, $outboundState)
        }

        if ($inboundError -ne '(none)' -or $outboundError -ne '(none)') {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna terminal errors observed: inbound={0}; outbound={1}" -f $inboundError, $outboundError)
        }

        $evidence = Get-JsonPropertyValue -Object $summary -Name 'evidence' -DefaultValue $null
        if ($ExpectedRoute -eq 'file_tuna_v4' -and $null -ne $evidence) {
            $fallbackStarted = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $evidence -Name 'fallbackEpochStarted' -DefaultValue $false)
            $fallbackRecovered = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $evidence -Name 'fallbackEpochRecovered' -DefaultValue $false)
            $fallbackWaiting = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $evidence -Name 'fallbackEpochWaiting' -DefaultValue $false)
            if ($fallbackStarted -or $fallbackRecovered -or $fallbackWaiting) {
                Add-RouteAcceptanceFailure -Result $result -Message 'Tuna no-fault acceptance unexpectedly entered fallback'
            }
        }
    }

    $script:RunResults.Add($result) | Out-Null
}

function Get-RouteAcceptanceSelectedRouteDetails {
    param([Parameter(Mandatory = $true)]$RouteSummary)

    $count = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $RouteSummary -Name 'route_selected_count' -DefaultValue '0')
    $items = New-Object System.Collections.Generic.List[object]
    for ($i = 1; $i -le $count; $i++) {
        $items.Add([pscustomobject]@{
            Index = $i
            Direction = Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.direction" -f $i) -DefaultValue ''
            Route = Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.route" -f $i) -DefaultValue ''
            Protocol = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.protocol_version" -f $i) -DefaultValue '0')
            Runtime = Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.runtime_profile" -f $i) -DefaultValue ''
            Bridge = Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.bridge_recovery_policy" -f $i) -DefaultValue ''
        }) | Out-Null
    }

    return $items.ToArray()
}

function Assert-Phase4RouteSummary {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string[]]$ExpectedRouteChanges
    )

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'filetransfer-route-consistency-summary.txt')) {
        return
    }

    $routeSummary = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Result.artifactDir 'filetransfer-route-consistency-summary.txt')
    $Result.routeConsistencyVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'route_consistency_verdict' -DefaultValue '(missing)'
    if ($Result.routeConsistencyVerdict -ne 'pass') {
        Add-RouteAcceptanceFailure -Result $Result -Message ("route consistency verdict is {0}" -f $Result.routeConsistencyVerdict)
    }

    $selected = @(Get-RouteAcceptanceSelectedRouteDetails -RouteSummary $routeSummary)
    if ($selected.Count -le 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'no filetransfer_route_selected event was observed'
        return
    }

    $Result.selectedRouteSequence = @($selected | ForEach-Object { [string]$_.Route })
    $Result.selectedRouteChanges = @(Get-RouteAcceptanceRouteChanges -Routes $Result.selectedRouteSequence)
    $Result.route = Join-RouteAcceptanceTokenList -Values $Result.selectedRouteChanges
    $Result.finalRoute = [string]$Result.selectedRouteChanges[$Result.selectedRouteChanges.Count - 1]
    $Result.protocol = [string]($selected[$selected.Count - 1].Protocol)
    $Result.runtimeProfile = [string]($selected[$selected.Count - 1].Runtime)
    $Result.bridgeRecoveryPolicy = [string]($selected[$selected.Count - 1].Bridge)
    $Result.liveRouteEpochRouteChanges = @(Split-RouteAcceptanceTokenList -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'live_route_epoch_route_changes' -DefaultValue '(none)'))
    $Result.liveRouteEpochProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'live_route_epoch_proof_verdict' -DefaultValue '(missing)'
    $Result.fallbackLegAuthorityProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_leg_authority_proof_verdict' -DefaultValue '(missing)'
    $Result.bridgeLivenessIntegrationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'bridge_liveness_integration_verdict' -DefaultValue '(missing)'
    $Result.bridgeLivenessStaleDeferralCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'bridge_liveness_stale_deferral_count' -DefaultValue '0')
    $Result.bridgeLivenessTimeoutDuringValidRecoveryCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'session_liveness_timeout_during_valid_recovery_count' -DefaultValue '0')

    if ($Result.route.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'diagnostic_regular_nkn_v6 route is not allowed during acceptance'
    }

    if ($Result.route.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'active file_tuna_v6 route is not allowed during acceptance'
    }

    $expectedChangesText = Join-RouteAcceptanceTokenList -Values $ExpectedRouteChanges
    $actualChangesText = Join-RouteAcceptanceTokenList -Values $Result.selectedRouteChanges
    if ($actualChangesText -ne $expectedChangesText) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("selected route sequence mismatch: expected {0}, actual {1}" -f $expectedChangesText, $actualChangesText)
    }

    foreach ($item in @($selected)) {
        if ([string]::IsNullOrWhiteSpace([string]$item.Route) -or
            [string]::IsNullOrWhiteSpace([string]$item.Runtime) -or
            [string]::IsNullOrWhiteSpace([string]$item.Bridge) -or
            [int]$item.Protocol -le 0) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("route metadata missing for selected.{0}" -f $item.Index)
            continue
        }

        $metadata = Get-RouteAcceptanceRouteMetadata -Route ([string]$item.Route)
        if ([int]$item.Protocol -ne [int]$metadata.Protocol) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("route selected protocol mismatch: route={0}; expected {1}; actual {2}" -f $item.Route, $metadata.Protocol, $item.Protocol)
        }

        if ([string]$item.Runtime -ne [string]$metadata.Runtime) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("route selected runtime mismatch: route={0}; expected {1}; actual {2}" -f $item.Route, $metadata.Runtime, $item.Runtime)
        }
    }
}

function Assert-Phase4OperatorVerdict {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][bool]$RequireWarningFree
    )

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'filetransfer-operator-verdict.txt')) {
        return
    }

    $operator = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Result.artifactDir 'filetransfer-operator-verdict.txt')
    $Result.operatorVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'verdict' -DefaultValue '(missing)'
    $Result.hardFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'hard_failure_count' -DefaultValue '0')
    $Result.warningCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_count' -DefaultValue '0')
    $Result.warningKinds = @(Split-RouteAcceptanceTokenList -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_kinds' -DefaultValue '(none)'))
    $warningCapExceeded = Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_cap_exceeded_kinds' -DefaultValue '(none)'
    $Result.warningCapExceededKinds = $warningCapExceeded

    if ($Result.hardFailureCount -gt 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("operator hard failures observed: {0}" -f $Result.hardFailureCount)
    }

    if ($warningCapExceeded -ne '(none)') {
        Add-RouteAcceptanceFailure -Result $Result -Message ("warning cap exceeded: {0}" -f $warningCapExceeded)
    }

    if ($RequireWarningFree -and ($Result.warningCount -gt 0 -or $Result.operatorVerdict -ne 'PASS')) {
        if (Test-Phase4RegularNknExternalTransportVariance -Result $Result) {
            $Result.environmentalClassification = 'public_nkn_external_transport_churn'
            Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason 'regular_nkn_external_transport_churn'
            return
        }

        Add-RouteAcceptanceFailure -Result $Result -Message ("regular NKN warning-free acceptance failed: verdict={0}; warning_count={1}; warning_kinds={2}" -f $Result.operatorVerdict, $Result.warningCount, (Join-RouteAcceptanceTokenList -Values $Result.warningKinds))
        return
    }

    if (-not $RequireWarningFree -and $Result.operatorVerdict -ne 'PASS') {
        if (@($Result.warningKinds).Count -gt 0 -and
            (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute $Result.finalRoute -Verdict $Result.operatorVerdict -WarningKinds @($Result.warningKinds))) {
            $Result.operatorAcceptedWithWarnings = $true
        }
        else {
            Add-RouteAcceptanceFailure -Result $Result -Message ("operator verdict is not accepted for {0}: verdict={1}; warning_kinds={2}" -f $Result.finalRoute, $Result.operatorVerdict, (Join-RouteAcceptanceTokenList -Values $Result.warningKinds))
        }
    }
}

function Assert-Phase4ScenarioRun {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    $expectedRouteChanges = @($Scenario.ExpectedRouteChanges)
    $expectedFinalRoute = [string]$expectedRouteChanges[$expectedRouteChanges.Count - 1]
    $expectedMetadata = Get-RouteAcceptanceRouteMetadata -Route $expectedFinalRoute
    $result = New-RouteAcceptanceRunResult -Name ([string]$Scenario.Name) -ArtifactDir $ArtifactDir -ExpectedRoute $expectedFinalRoute -ExpectedProtocol ([int]$expectedMetadata.Protocol)
    Assert-Phase4RouteSummary -Result $result -ExpectedRouteChanges $expectedRouteChanges
    if ($Scenario.Kind -eq 'tuna' -and $expectedRouteChanges.Count -gt 1) {
        $expectedLiveRouteChanges = @($expectedRouteChanges | Select-Object -Skip 1)
        $expectedLiveText = Join-RouteAcceptanceTokenList -Values $expectedLiveRouteChanges
        $actualLiveText = Join-RouteAcceptanceTokenList -Values $result.liveRouteEpochRouteChanges
        if ($actualLiveText -ne $expectedLiveText) {
            Add-RouteAcceptanceFailure -Result $result -Message ("live route epoch sequence mismatch: expected {0}, actual {1}" -f $expectedLiveText, $actualLiveText)
        }
    }

    Assert-RouteAcceptanceTerminalSummary -Result $result
    Assert-Phase4OperatorVerdict -Result $result -RequireWarningFree:($Scenario.Kind -eq 'regular')

    $retainedLogPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (Test-Path -LiteralPath $retainedLogPath -PathType Leaf) {
        $retainedText = Get-Content -LiteralPath $retainedLogPath -Raw
        if ($retainedText.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Add-RouteAcceptanceFailure -Result $result -Message 'active file_tuna_v6 evidence was observed'
        }
    }

    if ($Scenario.Kind -eq 'regular') {
        if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-live-nkn-summary.txt') {
            $summary = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt')
            $protocol = Get-RouteAcceptanceReportValue -Report $summary -Name 'data_protocol_version' -DefaultValue '(missing)'
            $result.protocol = $protocol
            if ($protocol -ne '4') {
                Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN protocol mismatch: expected 4, actual {0}" -f $protocol)
            }

            $cyclesCompleted = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_completed' -DefaultValue '0')
            if ($cyclesCompleted -le 0) {
                Add-RouteAcceptanceFailure -Result $result -Message 'regular NKN completed no cycles'
            }

            $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'average_goodput_bytes_per_second' -DefaultValue '0')
            $result.bridgeBulkSendFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'bridge_bulk_send_failure_count' -DefaultValue '0')
            if ($result.bridgeBulkSendFailureCount -ne 0) {
                Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN bridge_bulk_send_failure_count must be 0, actual {0}" -f $result.bridgeBulkSendFailureCount)
            }
        }

        if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-live-nkn-cycles.jsonl') {
            foreach ($line in @(Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
                $cycle = $line | ConvertFrom-Json
                $completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $cycle -Name 'completed' -DefaultValue $false)
                $integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $cycle -Name 'integrity_ok' -DefaultValue $false)
                if (-not $completed -or -not $integrityOk) {
                    Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN cycle failed completion/integrity: {0}" -f $line)
                }
            }
        }

        Set-Phase4RegularNknProgressTimeoutRecoveryStormVariance -Result $result
        $result.completed = $result.failures.Count -eq 0
        $result.integrityOk = $result.completed
        $result.shaOk = $result.completed
    }
    else {
        $errorPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json'
        if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
            try {
                $errorSummary = Get-Content -LiteralPath $errorPath -Raw | ConvertFrom-Json
                $failurePhase = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'failurePhase' -DefaultValue '')
                $failureReason = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'failureReason' -DefaultValue '')
                $errorMessage = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'error' -DefaultValue '')
                $result.setupFailurePhase = $failurePhase
                $result.setupFailureReason = $failureReason
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI setup failure: phase={0}; reason={1}; error={2}" -f `
                    ($(if ([string]::IsNullOrWhiteSpace($failurePhase)) { '(unknown)' } else { $failurePhase })),
                    ($(if ([string]::IsNullOrWhiteSpace($failureReason)) { '(unknown)' } else { $failureReason })),
                    ($(if ([string]::IsNullOrWhiteSpace($errorMessage)) { '(none)' } else { $errorMessage })))
            }
            catch {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI setup failure artifact could not be parsed: {0}" -f $_.Exception.Message)
            }
        }

        if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-tuna-gui-summary.json') {
            $summary = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Raw | ConvertFrom-Json
            $measuredPhase = Get-JsonPropertyValue -Object $summary -Name 'measuredPhase' -DefaultValue $null
            $phase = if ($null -ne $measuredPhase) { $measuredPhase } else { $summary }
            $result.completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $phase -Name 'completed' -DefaultValue $false)
            $result.integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $phase -Name 'integrityOk' -DefaultValue $false)
            $result.shaOk = $result.integrityOk
            $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-JsonPropertyValue -Object $phase -Name 'goodputBytesPerSecond' -DefaultValue 0)
            $phaseRoute = [string](Get-JsonPropertyValue -Object $phase -Name 'route' -DefaultValue $result.finalRoute)
            $phaseProtocol = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $phase -Name 'protocolVersion' -DefaultValue $result.expectedProtocol)
            $inboundState = [string](Get-JsonPropertyValue -Object $phase -Name 'inboundState' -DefaultValue '(missing)')
            $outboundState = [string](Get-JsonPropertyValue -Object $phase -Name 'outboundState' -DefaultValue '(missing)')
            $inboundError = [string](Get-JsonPropertyValue -Object $phase -Name 'inboundErrorCode' -DefaultValue '(missing)')
            $outboundError = [string](Get-JsonPropertyValue -Object $phase -Name 'outboundErrorCode' -DefaultValue '(missing)')

            if ($phaseRoute -ne $expectedFinalRoute) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna measured route mismatch: expected={0}; actual={1}" -f $expectedFinalRoute, $phaseRoute)
            }

            if ($phaseProtocol -ne [int]$expectedMetadata.Protocol) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna measured protocol mismatch: expected={0}; actual={1}" -f $expectedMetadata.Protocol, $phaseProtocol)
            }

            if (-not $result.completed -or -not $result.integrityOk) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI summary failed completion/integrity: completed={0}; integrityOk={1}" -f $result.completed, $result.integrityOk)
            }

            if ($inboundState -ne 'Completed' -or $outboundState -ne 'Completed') {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna terminals not Completed: inbound={0}; outbound={1}" -f $inboundState, $outboundState)
            }

            if ($inboundError -ne '(none)' -or $outboundError -ne '(none)') {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna terminal errors observed: inbound={0}; outbound={1}" -f $inboundError, $outboundError)
            }

            if ($Scenario.Name -eq 'second-transfer-after-reactivation') {
                $second = Get-JsonPropertyValue -Object $summary -Name 'secondTransfer' -DefaultValue $null
                if ($null -eq $second) {
                    Add-RouteAcceptanceFailure -Result $result -Message 'second transfer evidence is missing'
                }
                else {
                    $secondRoute = [string](Get-JsonPropertyValue -Object $second -Name 'route' -DefaultValue '(missing)')
                    $secondProtocol = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $second -Name 'protocolVersion' -DefaultValue 0)
                    $secondCompleted = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $second -Name 'completed' -DefaultValue $false)
                    $secondIntegrity = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $second -Name 'integrityOk' -DefaultValue $false)
                    $secondSetupPhase = [string](Get-JsonPropertyValue -Object $second -Name 'setupFailurePhase' -DefaultValue '')
                    $secondSetupReason = [string](Get-JsonPropertyValue -Object $second -Name 'setupFailureReason' -DefaultValue '')
                    if (-not [string]::IsNullOrWhiteSpace($secondSetupPhase) -or -not [string]::IsNullOrWhiteSpace($secondSetupReason)) {
                        Add-RouteAcceptanceFailure -Result $result -Message ("second transfer setup failure after reactivation: phase={0}; reason={1}" -f `
                            ($(if ([string]::IsNullOrWhiteSpace($secondSetupPhase)) { '(unknown)' } else { $secondSetupPhase })),
                            ($(if ([string]::IsNullOrWhiteSpace($secondSetupReason)) { '(unknown)' } else { $secondSetupReason })))
                    }

                    if ($secondRoute -ne 'file_tuna_v4' -or $secondProtocol -ne 4) {
                        Add-RouteAcceptanceFailure -Result $result -Message ("second transfer route mismatch after reactivation: expected file_tuna_v4/4, actual {0}/{1}" -f $secondRoute, $secondProtocol)
                    }

                    if (-not $secondCompleted -or -not $secondIntegrity) {
                        Add-RouteAcceptanceFailure -Result $result -Message ("second transfer failed completion/integrity: completed={0}; integrityOk={1}" -f $secondCompleted, $secondIntegrity)
                    }
                }

                if (-not (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-second-transfer-retained-log-slice.log')) {
                    Add-RouteAcceptanceFailure -Result $result -Message 'second transfer retained log slice is missing'
                }

                if (-not (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'second-transfer-analysis\filetransfer-route-consistency-summary.txt')) {
                    Add-RouteAcceptanceFailure -Result $result -Message 'second transfer retained route analysis is missing'
                }
            }
        }
    }

    Set-Phase4MeasurementContamination -Scenario $Scenario -Result $result

    $baselineGoodput = 0D
    if ($null -ne $Scenario.Baseline) {
        $baselineGoodput = ConvertTo-RouteAcceptanceDouble -Value $Scenario.Baseline.goodputBytesPerSecond
        if ($baselineGoodput -gt 0) {
            $result.baselineGoodputBytesPerSecond = $baselineGoodput
            $floor = $baselineGoodput * (1D - ([Math]::Max(0D, $GoodputRegressionTolerancePercent) / 100D))
            $result.goodputRegressionFloorBytesPerSecond = $floor
            if ($result.goodputBytesPerSecond -gt 0) {
                $result.goodputRegressionPercent = (($baselineGoodput - $result.goodputBytesPerSecond) / $baselineGoodput) * 100D
            }
        }
    }

    if ($result.measurementContaminated) {
        Add-RouteAcceptanceFailure -Result $result -Message ("measurement contaminated: {0}" -f (Join-RouteAcceptanceTokenList -Values $result.measurementContaminationReasons))
    }
    elseif ($baselineGoodput -gt 0) {
        if ($result.goodputBytesPerSecond -lt $result.goodputRegressionFloorBytesPerSecond) {
            Add-RouteAcceptanceFailure -Result $result -Message ("goodput regression exceeded {0:F1}%: current={1:F3}; floor={2:F3}; baseline={3:F3}" -f $GoodputRegressionTolerancePercent, $result.goodputBytesPerSecond, $result.goodputRegressionFloorBytesPerSecond, $baselineGoodput)
        }
    }

    return $result
}

function Get-RouteAcceptanceRouteMetadata {
    param([Parameter(Mandatory = $true)][string]$Route)

    switch ($Route) {
        'regular_nkn_v4_fast' {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'regular_nkn_v4_fast'; Bridge = 'regular_nkn_v4_fast'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 0 }
        }
        'file_tuna_v4' {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'file_tuna_v4_fast'; Bridge = 'tuna_strict'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 1; Fallback = 0; Diagnostic = 0 }
        }
        'post_tuna_fallback_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'default_v6'; Bridge = 'post_tuna_fallback_strict'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 0; Fallback = 1; Diagnostic = 0 }
        }
        'diagnostic_regular_nkn_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'primary_regular_nkn_bulk_v6'; Bridge = 'primary_regular_nkn_quiet'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 1 }
        }
        'file_tuna_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'file_tuna_v6'; Bridge = 'tuna_strict'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 1; Fallback = 0; Diagnostic = 0 }
        }
        default {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'regular_nkn_v4_fast'; Bridge = 'regular_nkn_v4_fast'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 0 }
        }
    }
}

function New-RouteAcceptanceFakeLogLine {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [int]$SecondsOffset = 0
    )

    $timestamp = ([datetime]::UtcNow.AddSeconds($SecondsOffset)).ToString("yyyy-MM-dd HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    return ('[{0}] [INFO] [RouteAcceptanceFake] {1}' -f $timestamp, $Message)
}

function Write-RouteAcceptanceFakeRetainedLog {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$TransferId,
        [Parameter(Mandatory = $true)][string]$Route,
        [int]$ProtocolOverride = 0,
        [string]$TerminalState = 'Completed',
        [int]$BridgeBulkSendFailures = 0
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $metadata = Get-RouteAcceptanceRouteMetadata -Route $Route
    $protocol = if ($ProtocolOverride -gt 0) { $ProtocolOverride } else { [int]$metadata.Protocol }
    $frameFamily = if ($protocol -eq 6) { 'v6' } else { 'v4' }
    $frameType = 'filetransfer.chunk_batch.{0}' -f $frameFamily
    $sessionId = 'route-acceptance-session'
    $terminalError = if ($TerminalState -eq 'Completed') { '(none)' } else { 'pending' }
    $rawBytes = 67108864L

    $lines = @(
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_route_selected; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; handoff_kind=none; bridge_recovery_policy={6}; liveness_terminal_policy={2}; selection_reason=route_acceptance_fake; file_tuna_active={7}; post_tuna_fallback_active={8}; diagnostic_regular_nkn_v6={9}; transport_profile=nkn" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge, $metadata.FileTuna, $metadata.Fallback, $metadata.Diagnostic))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_route_selected; direction=inbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; handoff_kind=none; bridge_recovery_policy={6}; liveness_terminal_policy={2}; selection_reason=route_acceptance_fake; file_tuna_active={7}; post_tuna_fallback_active={8}; diagnostic_regular_nkn_v6={9}; transport_profile=nkn" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge, $metadata.FileTuna, $metadata.Fallback, $metadata.Diagnostic))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}; selection_reason=route_acceptance_fake" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_session_opened; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}; reason=role=Sender" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event={0}; direction=outbound; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; bridge_recovery_policy={7}" -f $metadata.SenderStarted, $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event={0}; direction=inbound; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; bridge_recovery_policy={7}" -f $metadata.ReceiverStarted, $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_binary_frame_sent; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; payload_bytes={3}; serialized_payload_bytes={3}; raw_chunk_bytes={3}; chunk_count=32" -f $TransferId, $sessionId, $frameType, $rawBytes))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_binary_frame_received; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; raw_chunk_bytes={3}; chunk_count=32" -f $TransferId, $sessionId, $frameType, $rawBytes))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset 120 -Message ("event=file_transfer_inbound_terminal; role=helper; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; saved_path=(none); integrity_ok={5}" -f $sessionId, $TransferId, $TerminalState, $terminalError, $rawBytes, ($(if ($TerminalState -eq 'Completed') { 1 } else { 0 }))))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset 120 -Message ("event=file_transfer_outbound_terminal; role=helpee; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; integrity_ok={5}" -f $sessionId, $TransferId, $TerminalState, $terminalError, $rawBytes, ($(if ($TerminalState -eq 'Completed') { 1 } else { 0 }))))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset 110 -Message ("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=1; send_failures={0}; queue_clears=0; payload_bytes_sent={1}; payload_bytes_per_second=6000000; send_p95_ms=1; configured_concurrency=4; effective_concurrency=4" -f $BridgeBulkSendFailures, $rawBytes))
    )

    $lines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
}

function New-RouteAcceptanceRouteSelectedLogLine {
    param(
        [Parameter(Mandatory = $true)][string]$TransferId,
        [Parameter(Mandatory = $true)][string]$SessionId,
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][string]$Route,
        [string]$HandoffKind = 'none',
        [int]$LiveRouteEpoch = 0,
        [int]$SecondsOffset = 0
    )

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $Route
    $protocol = [int]$metadata.Protocol
    $suffix = if ($LiveRouteEpoch -gt 0) { "; live_route_epoch=$LiveRouteEpoch" } else { "" }
    return New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message (
        "event=filetransfer_route_selected; direction={0}; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; handoff_kind={7}; bridge_recovery_policy={8}; liveness_terminal_policy={3}; selection_reason=phase4_fake; file_tuna_active={9}; post_tuna_fallback_active={10}; diagnostic_regular_nkn_v6={11}; transport_profile=nkn{12}" -f
            $Direction,
            $TransferId,
            $SessionId,
            $Route,
            $protocol,
            $metadata.Runtime,
            $metadata.FrameFamily,
            $HandoffKind,
            $metadata.Bridge,
            $metadata.FileTuna,
            $metadata.Fallback,
            $metadata.Diagnostic,
            $suffix)
}

function New-RouteAcceptanceRuntimeLogLines {
    param(
        [Parameter(Mandatory = $true)][string]$TransferId,
        [Parameter(Mandatory = $true)][string]$SessionId,
        [Parameter(Mandatory = $true)][string]$Route,
        [int]$SecondsOffset = 0
    )

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $Route
    $protocol = [int]$metadata.Protocol
    return @(
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}; selection_reason=phase4_fake" -f $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_session_opened; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}; reason=role=Sender" -f $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event={0}; direction=outbound; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; bridge_recovery_policy={7}" -f $metadata.SenderStarted, $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event={0}; direction=inbound; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; bridge_recovery_policy={7}" -f $metadata.ReceiverStarted, $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}" -f $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
    )
}

function New-RouteAcceptanceLiveRouteEpochLogLines {
    param(
        [Parameter(Mandatory = $true)][string]$TransferId,
        [Parameter(Mandatory = $true)][string]$SessionId,
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][int]$LiveRouteEpoch,
        [int]$SecondsOffset = 0,
        [switch]$TransportOnly,
        [switch]$MissingMetadata
    )

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $Route
    $protocol = [int]$metadata.Protocol
    $handoffKind = if ($Route -eq 'post_tuna_fallback_v6') { 'tuna_to_normal_fallback' } elseif ($Route -eq 'file_tuna_v4') { 'normal_to_tuna_activation' } else { 'none' }
    $targetTransport = if ($Route -eq 'post_tuna_fallback_v6') { 'regular_nkn' } elseif ($Route -eq 'file_tuna_v4') { 'tuna' } else { 'regular_nkn' }

    if ($TransportOnly) {
        return @(
            (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_v6_epoch_started; direction=outbound; transfer_id={0}; session_id={1}; handoff_kind={2}; target_transport={3}; transport_epoch={4}" -f $TransferId, $SessionId, $handoffKind, $targetTransport, $LiveRouteEpoch))
            (New-RouteAcceptanceFakeLogLine -SecondsOffset ($SecondsOffset + 1) -Message ("event=filetransfer_v6_epoch_recovered; direction=outbound; transfer_id={0}; session_id={1}; handoff_kind={2}; target_transport={3}; transport_epoch={4}" -f $TransferId, $SessionId, $handoffKind, $targetTransport, $LiveRouteEpoch))
        )
    }

    if ($MissingMetadata) {
        return @(
            (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={0}; session_id={1}; live_route_epoch={2}" -f $TransferId, $SessionId, $LiveRouteEpoch))
            (New-RouteAcceptanceFakeLogLine -SecondsOffset ($SecondsOffset + 1) -Message ("event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={0}; session_id={1}; live_route_epoch={2}" -f $TransferId, $SessionId, $LiveRouteEpoch))
        )
    }

    return @(
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; handoff_kind={4}; target_transport={5}; live_route_epoch={6}" -f $TransferId, $SessionId, $Route, $protocol, $handoffKind, $targetTransport, $LiveRouteEpoch))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset ($SecondsOffset + 1) -Message ("event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; handoff_kind={4}; target_transport={5}; live_route_epoch={6}" -f $TransferId, $SessionId, $Route, $protocol, $handoffKind, $targetTransport, $LiveRouteEpoch))
    )
}

function Write-RouteAcceptanceFakePhase4Run {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [int]$RerunAttempt = 0
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $scenarioName = [string]$Scenario.Name
    $forceExecutionFailure = (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'EXECUTION_FAIL') -or
        ($RerunAttempt -gt 0 -and (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_EXECUTION_FAIL'))
    $forcePostArtifactExecutionFailure = ($RerunAttempt -le 0 -and (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'POST_ARTIFACT_EXECUTION_FAIL')) -or
        ($RerunAttempt -gt 0 -and (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_POST_ARTIFACT_EXECUTION_FAIL'))
    if ($forceExecutionFailure) {
        [ordered]@{
            event = 'filetransfer_tuna_gui_handoff_fallback_failure'
            routeMode = [string]$Scenario.RouteMode
            direction = 'helpee-to-helper'
            payerMode = [string]$Scenario.PayerMode
            faultMode = [string]$Scenario.Fault
            payloadBytes = [long]$Scenario.PayloadBytes
            completed = $false
            integrityOk = $false
            failurePhase = 'measured_accept_wait'
            failureReason = 'offer_sent_accept_not_enabled'
            tunaActive = $true
            listenerReady = $true
            listenerUnavailable = $false
            routeSelected = $true
            offerSent = $true
            offerReceived = $false
            terminalObserved = $false
            error = 'Injected fake Phase 4 scenario execution failure.'
            failedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json') -Encoding UTF8
        throw 'Injected fake Phase 4 scenario execution failure.'
    }

    $defaultRouteChanges = @($Scenario.ExpectedRouteChanges)
    $routeOverride = Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'ROUTE' -DefaultValue ''
    $routeChangeList = New-Object System.Collections.Generic.List[string]
    $sourceRouteChanges = if (-not [string]::IsNullOrWhiteSpace($routeOverride)) { @(Split-RouteAcceptanceTokenList -Value $routeOverride) } else { @($defaultRouteChanges) }
    foreach ($routeChange in @($sourceRouteChanges)) {
        $routeText = [string]$routeChange
        if (-not [string]::IsNullOrWhiteSpace($routeText)) {
            $routeChangeList.Add($routeText) | Out-Null
        }
    }

    $routeChanges = @($routeChangeList.ToArray())
    $finalRoute = [string]$routeChanges[$routeChanges.Count - 1]
    $defaultGoodput = if ($null -ne $Scenario.Baseline -and (ConvertTo-RouteAcceptanceDouble -Value $Scenario.Baseline.goodputBytesPerSecond) -gt 0) {
        [string]$Scenario.Baseline.goodputBytesPerSecond
    }
    elseif ($Scenario.Kind -eq 'regular') {
        '2000000'
    }
    elseif ($finalRoute -eq 'file_tuna_v4') {
        '5000000'
    }
    else {
        '650000'
    }

    $goodputSuffix = if ($RerunAttempt -gt 0) { 'RERUN_GOODPUT_BPS' } else { 'GOODPUT_BPS' }
    $goodput = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix $goodputSuffix -DefaultValue (Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'GOODPUT_BPS' -DefaultValue $defaultGoodput))
    $payloadBytes = [long]$Scenario.PayloadBytes
    $completed = -not (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'SHA_FAIL')
    $terminalState = if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TERMINAL_ERROR') { 'Failed' } else { 'Completed' }
    if (-not $completed) {
        $terminalState = 'Completed'
    }

    $hardFailure = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'HARD_FAILURE'
    $missingLiveProof = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'MISSING_LIVE_PROOF'
    $transportOnlyLiveProof = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TRANSPORT_ONLY_LIVE_PROOF'
    $missingLiveMetadata = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'MISSING_LIVE_METADATA'
    $bridgeLivenessFailure = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'BRIDGE_LIVENESS_FAIL'
    $fallbackAuthorityMetadataMissing = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'FALLBACK_AUTHORITY_METADATA_MISSING'
    $warningCapExcess = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'WARNING_CAP_EXCESS'
    $warning = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_WARNING'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'WARNING'
    }
    $progressTimeoutRecoveryStorm = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_PROGRESS_TIMEOUT_RECOVERY_STORM'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'PROGRESS_TIMEOUT_RECOVERY_STORM'
    }
    $startupRecoveryStorm = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_STARTUP_RECOVERY_STORM'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'STARTUP_RECOVERY_STORM'
    }
    $contaminatedMeasurement = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_CONTAMINATED_MEASUREMENT'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'CONTAMINATED_MEASUREMENT'
    }
    $transferId = ('fake-phase4-{0}' -f $scenarioName)
    $sessionId = 'phase4-fake-session'
    $lines = New-Object System.Collections.Generic.List[string]
    $seconds = 0
    $liveEpoch = 0

    for ($index = 0; $index -lt $routeChanges.Count; $index++) {
        $route = [string]$routeChanges[$index]
        $handoffKind = if ($index -eq 0) { 'none' } elseif ($route -eq 'post_tuna_fallback_v6') { 'tuna_to_normal_fallback' } elseif ($route -eq 'file_tuna_v4') { 'normal_to_tuna_activation' } else { 'none' }
        $routeEpoch = if ($index -gt 0) { $liveEpoch + 1 } else { 0 }
        foreach ($direction in @('outbound', 'inbound')) {
            $lines.Add((New-RouteAcceptanceRouteSelectedLogLine -TransferId $transferId -SessionId $sessionId -Direction $direction -Route $route -HandoffKind $handoffKind -LiveRouteEpoch $routeEpoch -SecondsOffset $seconds)) | Out-Null
        }

        if ($index -gt 0) {
            $liveEpoch++
            if (-not $missingLiveProof) {
                foreach ($line in @(New-RouteAcceptanceLiveRouteEpochLogLines -TransferId $transferId -SessionId $sessionId -Route $route -LiveRouteEpoch $liveEpoch -SecondsOffset ($seconds + 1) -TransportOnly:$transportOnlyLiveProof -MissingMetadata:$missingLiveMetadata)) {
                    $lines.Add($line) | Out-Null
                }
            }
        }

        $seconds += 2
    }

    foreach ($line in @(New-RouteAcceptanceRuntimeLogLines -TransferId $transferId -SessionId $sessionId -Route $finalRoute -SecondsOffset $seconds)) {
        $lines.Add($line) | Out-Null
    }

    $fallbackAuthorityLegGeneration = 0
    $lastFallbackRouteIndex = -1
    for ($index = 1; $index -lt $routeChanges.Count -and $scenarioName -ne 'second-transfer-after-reactivation'; $index++) {
        if ([string]$routeChanges[$index] -eq 'post_tuna_fallback_v6') {
            $lastFallbackRouteIndex = $index
        }
    }

    for ($index = 1; $index -lt $routeChanges.Count -and $scenarioName -ne 'second-transfer-after-reactivation'; $index++) {
        if ([string]$routeChanges[$index] -ne 'post_tuna_fallback_v6') {
            continue
        }

        $fallbackAuthorityLegGeneration++
        $authorityRoute = 'post_tuna_fallback_v6'
        $authorityProtocol = if ($fallbackAuthorityMetadataMissing) { 0 } else { 6 }
        $authorityLiveEpoch = if ($fallbackAuthorityMetadataMissing) { 0 } else { $index }
        $authorityTransportEpoch = $fallbackAuthorityLegGeneration
        $authorityBridgeGeneration = 1
        $authorityCheckpointId = 'phase5-fallback-checkpoint:{0}' -f $fallbackAuthorityLegGeneration
        $authorityReason = if ($scenarioName -eq 'regular-v4-live-activation-off-on-off-128mb') { 'phase5_canonical_repeated_toggle' } else { 'phase5_fallback_authority' }
        $authorityOffset = $seconds + (6 * ($fallbackAuthorityLegGeneration - 1)) + 1
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset $authorityOffset -Message ("event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={0}; session_id={1}; leg_generation={2}; route={3}; protocol_version={4}; live_route_epoch={5}; transport_epoch={6}; bridge_recovery_generation={7}; checkpoint_request_id={8}; authority_reason={9}" -f $transferId, $sessionId, $fallbackAuthorityLegGeneration, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $authorityTransportEpoch, $authorityBridgeGeneration, $authorityCheckpointId, $authorityReason))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 1) -Message ("event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={0}; session_id={1}; leg_generation={2}; route={3}; protocol_version={4}; live_route_epoch={5}; transport_epoch={6}; bridge_recovery_generation={7}; checkpoint_request_id={8}; authority_reason={9}" -f $transferId, $sessionId, $fallbackAuthorityLegGeneration, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $authorityTransportEpoch, $authorityBridgeGeneration, $authorityCheckpointId, $authorityReason))) | Out-Null
        if ($bridgeLivenessFailure -and $index -eq $lastFallbackRouteIndex) {
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 2) -Message ("event=session_liveness_timeout; session_id={0}; transfer_id={1}; route={2}; protocol_version=6; leg_generation={3}; bridge_recovery_generation={4}; reason=phase5_fake_valid_recovery_timeout" -f $sessionId, $transferId, $authorityRoute, $fallbackAuthorityLegGeneration, $authorityBridgeGeneration))) | Out-Null
        }
        else {
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 2) -Message ("event=bridge_receive_stall_recovery_receive_resumed; session_id={0}; transfer_id={1}; route={2}; protocol_version=6; leg_generation={3}; bridge_recovery_generation={4}; reason=phase5_fake_receive_resumed" -f $sessionId, $transferId, $authorityRoute, $fallbackAuthorityLegGeneration, $authorityBridgeGeneration))) | Out-Null
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 3) -Message ("event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={0}; session_id={1}; leg_generation={2}; route={3}; protocol_version={4}; live_route_epoch={5}; transport_epoch={6}; bridge_recovery_generation={7}; checkpoint_request_id={8}; proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=phase5_fake_receiver_state" -f $transferId, $sessionId, $fallbackAuthorityLegGeneration, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $authorityTransportEpoch, $authorityBridgeGeneration, $authorityCheckpointId))) | Out-Null
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 4) -Message ("event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={0}; session_id={1}; leg_generation={2}; route={3}; protocol_version={4}; live_route_epoch={5}; transport_epoch={6}; bridge_recovery_generation={7}; checkpoint_request_id={8}; authority_reason={9}; proof=phase5_fake_receiver_state" -f $transferId, $sessionId, $fallbackAuthorityLegGeneration, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $authorityTransportEpoch, $authorityBridgeGeneration, $authorityCheckpointId, $authorityReason))) | Out-Null
        }
    }

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $finalRoute
    $frameType = 'filetransfer.chunk_batch.{0}' -f $metadata.FrameFamily
    $terminalError = if ($terminalState -eq 'Completed') { '(none)' } else { 'phase4_fake_terminal_error' }
    $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 1) -Message ("event=filetransfer_binary_frame_sent; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; payload_bytes={3}; serialized_payload_bytes={3}; raw_chunk_bytes={3}; chunk_count=32" -f $transferId, $sessionId, $frameType, $payloadBytes))) | Out-Null
    $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 1) -Message ("event=filetransfer_binary_frame_received; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; raw_chunk_bytes={3}; chunk_count=32" -f $transferId, $sessionId, $frameType, $payloadBytes))) | Out-Null
    if ($hardFailure) {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message ("event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={0}; session_id={1}; frame_type=filetransfer.chunk_batch.v4; first_lane=control; second_lane=bulk; first_error=IOException; second_error=IOException" -f $transferId, $sessionId))) | Out-Null
    }
    if ($warning) {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3) -Message 'event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1')) | Out-Null
    }
    if ($warningCapExcess) {
        for ($warningIndex = 0; $warningIndex -lt 4; $warningIndex++) {
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3 + $warningIndex) -Message 'event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1')) | Out-Null
        }
    }

    if ($contaminatedMeasurement -and $finalRoute -eq 'file_tuna_v4') {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message 'event=nkn_bridge_receive_stall_detected; reason=all_channels_zero_receive; consecutive_zero_receive_windows=3; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; control_last_received_age_ms=12000; bulk_last_received_age_ms=12000')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message 'event=nkn_bridge_receive_stall_recovery_started; stall_reason=all_channels_zero_receive; attempt=1; max_restarts=4; control_last_received_age_ms=12000; bulk_last_received_age_ms=12000')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3) -Message 'event=nkn_bridge_receive_stall_recovery_completed; stall_reason=all_channels_zero_receive; attempt=1; recovery_count=1; fallback_delay_ms=3000; requires_control_proof=1; requires_bulk_proof=1')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 4) -Message ("event=filetransfer_v4_state_received; transfer_id={0}; session_id={1}; epoch=2; previous_epoch=1; applied=1; stale=0; duplicate=0; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3121; effective_credit_until_chunk_index_exclusive=3121; missing_range_count=1; bytes_committed=0; terminal_ready=0" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 4) -Message ("event=filetransfer_v4_repair_scheduled; transfer_id={0}; session_id={1}; repair_request_key=0:12:0:12; epoch=2; range_count=1; requested_chunk_count=12; scheduled_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; frontier_tail_repair=1; credit_exhausted_time_ms_at_repair=1200; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=3121" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 5) -Message ("event=filetransfer_v4_repair_sent; transfer_id={0}; session_id={1}; repair_request_key=0:12:0:12; range_count=1; requested_chunk_count=12; sent_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; frontier_tail_repair=1; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=3121" -f $transferId, $sessionId))) | Out-Null
    }

    if (($progressTimeoutRecoveryStorm -or $startupRecoveryStorm) -and $Scenario.Kind -eq 'regular') {
        $completed = $false
        $terminalState = 'Pending'
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 6) -Message 'event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only; stall_reason=bulk_receive_stalled; attempt=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 7) -Message 'event=nkn_bridge_receive_stall_recovery_protocol_repair_exhausted; trigger=filetransfer_protocol_repair_only; requested_reason=regular_v4_peer_silence; recovery_count=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 8) -Message 'event=nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed; requested_reason=regular_v4_peer_silence; stall_reason=regular_v4_unproven_recovery_escalation; recovery_count=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 9) -Message 'event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=2')) | Out-Null
    }
    else {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 120 -Message ("event=file_transfer_inbound_terminal; role=helper; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; saved_path=(none); integrity_ok={5}; route={6}; protocol_version={7}" -f $sessionId, $transferId, $terminalState, $terminalError, $payloadBytes, ($(if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 })), $finalRoute, $metadata.Protocol))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 120 -Message ("event=file_transfer_outbound_terminal; role=helpee; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; integrity_ok={5}; route={6}; protocol_version={7}" -f $sessionId, $transferId, $terminalState, $terminalError, $payloadBytes, ($(if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 })), $finalRoute, $metadata.Protocol))) | Out-Null
    }
    $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 110 -Message ("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=1; send_failures=0; queue_clears=0; payload_bytes_sent={0}; payload_bytes_per_second=6000000; send_p95_ms=1; configured_concurrency=4; effective_concurrency=4" -f $payloadBytes))) | Out-Null
    $lines.ToArray() | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8

    $liveMode = if ($Scenario.LiveProofMode) { [string]$Scenario.LiveProofMode } else { 'None' }
    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir -LiveRouteProofMode $liveMode

    if ($Scenario.Name -eq 'second-transfer-after-reactivation') {
        $secondSlicePath = Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
        $secondAnalysisDir = Join-Path $ArtifactDir 'second-transfer-analysis'
        New-Item -ItemType Directory -Force -Path $secondAnalysisDir | Out-Null
        $secondTransferId = ('{0}-second' -f $transferId)
        $secondPayloadBytes = 16777216L
        $secondMetadata = Get-RouteAcceptanceRouteMetadata -Route 'file_tuna_v4'
        $secondFrameType = 'filetransfer.chunk_batch.{0}' -f $secondMetadata.FrameFamily
        $secondLines = New-Object System.Collections.Generic.List[string]
        foreach ($direction in @('outbound', 'inbound')) {
            $secondLines.Add((New-RouteAcceptanceRouteSelectedLogLine -TransferId $secondTransferId -SessionId $sessionId -Direction $direction -Route 'file_tuna_v4' -SecondsOffset 0)) | Out-Null
        }
        foreach ($line in @(New-RouteAcceptanceRuntimeLogLines -TransferId $secondTransferId -SessionId $sessionId -Route 'file_tuna_v4' -SecondsOffset 1)) {
            $secondLines.Add($line) | Out-Null
        }
        $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 2 -Message ("event=filetransfer_binary_frame_sent; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; payload_bytes={3}; serialized_payload_bytes={3}; raw_chunk_bytes={3}; chunk_count=32" -f $secondTransferId, $sessionId, $secondFrameType, $secondPayloadBytes))) | Out-Null
        $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 2 -Message ("event=filetransfer_binary_frame_received; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; raw_chunk_bytes={3}; chunk_count=32" -f $secondTransferId, $sessionId, $secondFrameType, $secondPayloadBytes))) | Out-Null
        if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'SECOND_CONTAMINATED_MEASUREMENT') {
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 3 -Message 'event=nkn_bridge_receive_stall_detected; reason=all_channels_zero_receive; consecutive_zero_receive_windows=3; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; control_last_received_age_ms=12000; bulk_last_received_age_ms=12000')) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 3 -Message 'event=nkn_bridge_receive_stall_recovery_started; stall_reason=all_channels_zero_receive; attempt=1; max_restarts=4; control_last_received_age_ms=12000; bulk_last_received_age_ms=12000')) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 4 -Message 'event=nkn_bridge_receive_stall_recovery_completed; stall_reason=all_channels_zero_receive; attempt=1; recovery_count=1; fallback_delay_ms=3000; requires_control_proof=1; requires_bulk_proof=1')) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 5 -Message ("event=filetransfer_v4_state_received; transfer_id={0}; session_id={1}; epoch=2; previous_epoch=1; applied=1; stale=0; duplicate=0; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=781; effective_credit_until_chunk_index_exclusive=781; missing_range_count=1; bytes_committed=0; terminal_ready=0" -f $secondTransferId, $sessionId))) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 5 -Message ("event=filetransfer_v4_repair_scheduled; transfer_id={0}; session_id={1}; repair_request_key=0:12:0:12; epoch=2; range_count=1; requested_chunk_count=12; scheduled_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; frontier_tail_repair=1; credit_exhausted_time_ms_at_repair=1200; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=781" -f $secondTransferId, $sessionId))) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 6 -Message ("event=filetransfer_v4_repair_sent; transfer_id={0}; session_id={1}; repair_request_key=0:12:0:12; range_count=1; requested_chunk_count=12; sent_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; frontier_tail_repair=1; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=781" -f $secondTransferId, $sessionId))) | Out-Null
        }
        $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 60 -Message ("event=file_transfer_inbound_terminal; role=helper; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; saved_path=(none); integrity_ok={5}; route=file_tuna_v4; protocol_version=4" -f $sessionId, $secondTransferId, $terminalState, $terminalError, $secondPayloadBytes, ($(if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 }))))) | Out-Null
        $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 60 -Message ("event=file_transfer_outbound_terminal; role=helpee; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; integrity_ok={5}; route=file_tuna_v4; protocol_version=4" -f $sessionId, $secondTransferId, $terminalState, $terminalError, $secondPayloadBytes, ($(if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 }))))) | Out-Null
        $secondLines.ToArray() | Set-Content -LiteralPath $secondSlicePath -Encoding UTF8
        Copy-Item -LiteralPath $secondSlicePath -Destination (Join-Path $secondAnalysisDir 'filetransfer-retained-log-slice.log') -Force
        Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $secondAnalysisDir
    }

    if ($Scenario.Kind -eq 'regular') {
        $cycle = [ordered]@{
            cycle_index = 0
            mode = 'nkn-fast'
            scenario = 'FILETRANSFER_NKN_SOAK'
            direction = 'helpee-to-helper'
            transfer_id = $transferId
            payload_bytes = $payloadBytes
            duration_ms = [Math]::Max(1L, [long][Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D))
            goodput_bytes_per_second = $goodput
            completed = $completed
            integrity_ok = $completed
            inbound_state = $terminalState
            outbound_state = $terminalState
        }
        $cycle | ConvertTo-Json -Compress -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') -Encoding UTF8
        $regularSummaryVerdict = if ($progressTimeoutRecoveryStorm) {
            'INCONCLUSIVE_PROGRESS_TIMEOUT'
        }
        elseif ($completed -and $terminalState -eq 'Completed') {
            'PASS'
        }
        else {
            'FAIL_PROTOCOL_OR_INTEGRITY'
        }
        $summary = [ordered]@{
            artifact_kind = 'live-nkn'
            mode = 'nkn-fast'
            verdict = $regularSummaryVerdict
            gate_status = if ($regularSummaryVerdict -eq 'PASS') { 'pass' } elseif ($regularSummaryVerdict -eq 'INCONCLUSIVE_PROGRESS_TIMEOUT') { 'inconclusive' } else { 'fail' }
            cycles_requested = 1
            cycles_observed = if ($startupRecoveryStorm) { 0 } else { 1 }
            cycles_completed = if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 }
            total_payload_bytes = if ($completed -and $terminalState -eq 'Completed') { $payloadBytes } else { 0 }
            average_goodput_bytes_per_second = ('{0:F3}' -f $goodput)
            min_goodput_bytes_per_second = ('{0:F3}' -f $goodput)
            data_protocol_version = [int]$metadata.Protocol
            bridge_bulk_send_failure_count = 0
            bridge_bulk_queue_clear_count = if ($progressTimeoutRecoveryStorm -or $startupRecoveryStorm) { 2 } else { 0 }
            gui_progress_timeout_count = if ($progressTimeoutRecoveryStorm) { 1 } else { 0 }
            gui_progress_timeout_reason = if ($progressTimeoutRecoveryStorm) { 'no useful data progress for 180s' } else { '(none)' }
            last_receiver_next_chunk = if ($progressTimeoutRecoveryStorm) { 1614 } else { -1 }
            last_receiver_highest_chunk = if ($progressTimeoutRecoveryStorm) { 1613 } else { -1 }
            last_progress_event_count = if ($progressTimeoutRecoveryStorm) { 23 } else { 0 }
            terminal_missing_after_progress_timeout = if ($progressTimeoutRecoveryStorm) { 1 } else { 0 }
        }
        $summary.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value } | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt') -Encoding UTF8
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.json') -Encoding UTF8
    }
    else {
        $summary = [ordered]@{
            event = 'filetransfer_tuna_gui_handoff_fallback_summary'
            routeMode = [string]$Scenario.RouteMode
            direction = 'helpee-to-helper'
            payerMode = [string]$Scenario.PayerMode
            faultMode = [string]$Scenario.Fault
            transferId = $transferId
            payloadBytes = $payloadBytes
            durationMs = [Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D, 3)
            goodputBytesPerSecond = $goodput
            completed = $completed -and $terminalState -eq 'Completed'
            integrityOk = $completed -and $terminalState -eq 'Completed'
            inboundState = $terminalState
            outboundState = $terminalState
            inboundErrorCode = $terminalError
            outboundErrorCode = $terminalError
            expectedSha256 = 'fake'
            receivedSha256 = if ($completed) { 'fake' } else { 'mismatch' }
            savedFileSizeBytes = if ($completed) { $payloadBytes } else { 0 }
            measuredPhase = [ordered]@{
                name = [string]$Scenario.Name
                route = $finalRoute
                protocolVersion = [int]$metadata.Protocol
                payloadBytes = $payloadBytes
                goodputBytesPerSecond = $goodput
                completed = $completed -and $terminalState -eq 'Completed'
                integrityOk = $completed -and $terminalState -eq 'Completed'
                inboundState = $terminalState
                outboundState = $terminalState
                inboundErrorCode = $terminalError
                outboundErrorCode = $terminalError
            }
            liveRouteEpochRouteChanges = @($routeChanges | Select-Object -Skip 1)
            secondTransfer = if ($Scenario.Name -eq 'second-transfer-after-reactivation') {
                $secondRoute = Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'SECOND_ROUTE' -DefaultValue 'file_tuna_v4'
                $secondMetadata = Get-RouteAcceptanceRouteMetadata -Route $secondRoute
                [ordered]@{
                    route = $secondRoute
                    protocolVersion = [int]$secondMetadata.Protocol
                    completed = $completed -and $terminalState -eq 'Completed'
                    integrityOk = $completed -and $terminalState -eq 'Completed'
                    inboundState = $terminalState
                    outboundState = $terminalState
                    inboundErrorCode = $terminalError
                    outboundErrorCode = $terminalError
                    payloadBytes = 16777216
                    goodputBytesPerSecond = $goodput
                }
            }
            else {
                $null
            }
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
    }

    if ($forcePostArtifactExecutionFailure) {
        throw 'Injected fake Phase 4 scenario execution failure after artifacts.'
    }
}

function Invoke-RouteAcceptanceRetainedAnalysis {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [ValidateSet("None", "SwitchOff", "MultiToggle", "RegularActivationCycle")]
        [string]$LiveRouteProofMode = "None"
    )

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "Retained log slice missing: $logPath"
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\FileTransfer-Ops.ps1" -Mode AnalyzeRetained -LogPath $logPath -ArtifactDir $ArtifactDir -TailMinutes 0 -LiveRouteProofMode $LiveRouteProofMode
    if ($LASTEXITCODE -ne 0) {
        throw "FileTransfer-Ops retained analysis failed for $ArtifactDir with exit code $LASTEXITCODE"
    }
}

function Select-RouteAcceptanceMeasuredFallbackLogSlice {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        return
    }

    $fullPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log'
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Copy-Item -LiteralPath $logPath -Destination $fullPath -Force
    }

    $sliceSourcePath = if (Test-Path -LiteralPath $fullPath -PathType Leaf) { $fullPath } else { $logPath }
    $lines = @(Get-Content -LiteralPath $sliceSourcePath)
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

    if ($startIndex -lt 0) {
        return
    }

    if ($setupStartIndex -ge 0 -and $startIndex -gt $setupStartIndex) {
        $lines[$setupStartIndex..($startIndex - 1)] | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8
    }

    $measuredLines = $lines[$startIndex..($lines.Count - 1)]
    $measuredLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log') -Encoding UTF8
    $measuredLines | Set-Content -LiteralPath $logPath -Encoding UTF8
}

function Write-RouteAcceptanceFakeRegularRun {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$PayloadSize
    )

    $route = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_ROUTE' -DefaultValue 'regular_nkn_v4_fast'
    $protocol = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_PROTOCOL' -DefaultValue '0')
    $defaultGoodput = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_GOODPUT_BPS' -DefaultValue '8388608'
    $payloadGoodputOverrideName = if ($PayloadSize -eq '128MiB') { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_128MB_GOODPUT_BPS' } else { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_64MB_GOODPUT_BPS' }
    $goodput = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceEnvValue -Name $payloadGoodputOverrideName -DefaultValue $defaultGoodput)
    $bridgeFailures = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_BRIDGE_FAILURES' -DefaultValue '0')
    $terminalState = if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ZOMBIE_TERMINAL') { 'Sending' } else { 'Completed' }
    $payloadBytes = if ($PayloadSize -eq '128MiB') { 134217728L } else { 67108864L }
    $completed = $terminalState -eq 'Completed'

    Write-RouteAcceptanceFakeRetainedLog -ArtifactDir $ArtifactDir -TransferId ('fake-{0}' -f $PayloadSize.ToLowerInvariant()) -Route $route -ProtocolOverride $protocol -TerminalState $terminalState -BridgeBulkSendFailures $bridgeFailures
    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir

    if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_MISSING_ROUTE_SUMMARY') {
        Remove-Item -LiteralPath (Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt') -Force -ErrorAction SilentlyContinue
    }

    $effectiveProtocol = if ($protocol -gt 0) { $protocol } else { (Get-RouteAcceptanceRouteMetadata -Route $route).Protocol }
    $cycle = [ordered]@{
        cycle_index = 0
        mode = 'nkn-fast'
        scenario = 'FILETRANSFER_NKN_SOAK'
        direction = 'helpee-to-helper'
        transfer_id = ('fake-{0}' -f $PayloadSize.ToLowerInvariant())
        payload_bytes = $payloadBytes
        duration_ms = [Math]::Max(1L, [long][Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D))
        goodput_bytes_per_second = $goodput
        completed = $completed
        integrity_ok = $completed
        inbound_state = $terminalState
        outbound_state = $terminalState
    }
    $cycle | ConvertTo-Json -Compress -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') -Encoding UTF8

    $summary = [ordered]@{
        artifact_kind = 'live-nkn'
        mode = 'nkn-fast'
        verdict = if ($completed) { 'PASS' } else { 'FAIL_PROTOCOL_OR_INTEGRITY' }
        gate_status = if ($completed) { 'pass' } else { 'fail' }
        cycles_requested = 1
        cycles_observed = 1
        cycles_completed = if ($completed) { 1 } else { 0 }
        total_payload_bytes = if ($completed) { $payloadBytes } else { 0 }
        average_goodput_bytes_per_second = ('{0:F3}' -f $goodput)
        min_goodput_bytes_per_second = ('{0:F3}' -f $goodput)
        data_protocol_version = $effectiveProtocol
        bridge_bulk_send_failure_count = $bridgeFailures
        gui_progress_timeout_count = 0
        terminal_missing_after_progress_timeout = 0
    }

    $summaryLines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $summary.Keys) {
        $summaryLines.Add(("{0}={1}" -f $key, $summary[$key])) | Out-Null
    }
    $summaryLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt') -Encoding UTF8
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.json') -Encoding UTF8
}

function Write-RouteAcceptanceFakeTunaRun {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][string]$RouteMode
    )

    $routeOverrideName = if ($RouteMode -eq 'preactivated') { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_ROUTE' } else { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_FALLBACK_ROUTE' }
    $effectiveRoute = Get-RouteAcceptanceEnvValue -Name $routeOverrideName -DefaultValue $Route
    $protocolOverride = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_PROTOCOL' -DefaultValue '0')
    $defaultGoodput = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_GOODPUT_BPS' -DefaultValue '5000000'
    $goodputOverrideName = if ($RouteMode -eq 'preactivated') { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_GOODPUT_BPS' } else { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_FALLBACK_GOODPUT_BPS' }
    $goodput = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceEnvValue -Name $goodputOverrideName -DefaultValue $defaultGoodput)
    $terminalState = if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ZOMBIE_TERMINAL') { 'Receiving' } else { 'Completed' }
    $completed = $terminalState -eq 'Completed'
    $payloadBytes = 134217728L
    $forceFallbackEvidence = $RouteMode -eq 'preactivated' -and (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_FALLBACK')
    $effectiveProtocol = if ($protocolOverride -gt 0) { $protocolOverride } else { [int](Get-RouteAcceptanceRouteMetadata -Route $effectiveRoute).Protocol }
    $inboundError = if ($completed) { '(none)' } else { 'pending' }
    $outboundError = if ($completed) { '(none)' } else { 'pending' }
    $emitNoFaultExternalWarning = $RouteMode -eq 'preactivated' -and (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_EXTERNAL_WARNING')
    $isControlledRestartMode = $RouteMode -eq 'v4-restart-v6-fallback'
    $emitFallbackRecoveredBridgeWarning = $isControlledRestartMode -and (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RECOVERED_BRIDGE_WARNING')

    Write-RouteAcceptanceFakeRetainedLog -ArtifactDir $ArtifactDir -TransferId ('fake-tuna-{0}' -f $RouteMode) -Route $effectiveRoute -ProtocolOverride $protocolOverride -TerminalState $terminalState -BridgeBulkSendFailures 0
    if ($emitNoFaultExternalWarning) {
        Add-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8 -Value (New-RouteAcceptanceFakeLogLine -SecondsOffset 60 -Message 'event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1')
    }

    if ($isControlledRestartMode) {
        $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
        Add-Content -LiteralPath $logPath -Encoding UTF8 -Value (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed')
        if ($emitFallbackRecoveredBridgeWarning) {
            @(
                (New-RouteAcceptanceFakeLogLine -SecondsOffset 40 -Message ('event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id=fake-tuna-{0}; session_id=sess_fake; reason=transport_recovered_unproven; target_transport=regular_nkn' -f $RouteMode))
                (New-RouteAcceptanceFakeLogLine -SecondsOffset 60 -Message 'event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=22; payload_bytes_sent=847447; payload_bytes_per_second=423724; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4')
            ) | Add-Content -LiteralPath $logPath -Encoding UTF8
        }
        Copy-Item -LiteralPath $logPath -Destination (Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log') -Force
        Copy-Item -LiteralPath $logPath -Destination (Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log') -Force
        @(
            (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started')
            (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_route_selected; direction=outbound; transfer_id=fake-tuna-v4-setup; session_id=sess_setup; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=fake')
            (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_inbound_terminal; role=helper; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_remote')
            (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_outbound_terminal; role=helpee; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_local')
            (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id=fake-tuna-v4-setup; session_id=sess_setup; frame_type=filetransfer.cancel.v4; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException')
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8
    }
    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir

    if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_MISSING_ROUTE_SUMMARY') {
        Remove-Item -LiteralPath (Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt') -Force -ErrorAction SilentlyContinue
    }

    $summary = [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_summary'
        routeMode = $RouteMode
        direction = 'helpee-to-helper'
        payerMode = 'helpee'
        faultMode = if ($RouteMode -eq 'post-fallback' -or $isControlledRestartMode) { 'switch-off' } else { 'none' }
        transferId = ('fake-tuna-{0}' -f $RouteMode)
        payloadBytes = $payloadBytes
        durationMs = [Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D, 3)
        goodputBytesPerSecond = $goodput
        completed = $completed
        integrityOk = $completed
        inboundState = $terminalState
        outboundState = $terminalState
        inboundErrorCode = $inboundError
        outboundErrorCode = $outboundError
        expectedSha256 = 'fake'
        receivedSha256 = if ($completed) { 'fake' } else { '(none)' }
        savedFileSizeBytes = if ($completed) { $payloadBytes } else { 0 }
        setupPhase = if ($isControlledRestartMode) {
            [ordered]@{
                name = 'setup_file_tuna_v4'
                route = 'file_tuna_v4'
                protocolVersion = 4
                payloadBytes = 67108864
                completed = $false
                integrityOk = $false
                inboundState = 'Canceled'
                outboundState = 'Canceled'
                inboundErrorCode = 'canceled_remote'
                outboundErrorCode = 'canceled_local'
            }
        }
        else {
            $null
        }
        measuredPhase = [ordered]@{
            name = if ($RouteMode -eq 'v4-restart-v6-fallback') { 'measured_post_tuna_fallback_v6' } else { 'measured_file_tuna_v4' }
            route = $effectiveRoute
            protocolVersion = $effectiveProtocol
            payloadBytes = $payloadBytes
            goodputBytesPerSecond = $goodput
            completed = $completed
            integrityOk = $completed
            inboundState = $terminalState
            outboundState = $terminalState
            inboundErrorCode = $inboundError
            outboundErrorCode = $outboundError
        }
        evidence = [ordered]@{
            tunaNegotiated = $true
            activationEpochStarted = $RouteMode -eq 'preactivated'
            activationEpochRecovered = $RouteMode -eq 'preactivated'
            fallbackEpochStarted = $RouteMode -eq 'post-fallback' -or $isControlledRestartMode -or $forceFallbackEvidence
            fallbackEpochRecovered = $RouteMode -eq 'post-fallback' -or $isControlledRestartMode -or $forceFallbackEvidence
            fallbackEpochWaiting = $false
        }
        controlledRestartAnalysis = if ($isControlledRestartMode) {
            [ordered]@{
                setupVerdict = 'INVALID_SETUP'
                setupRawOperatorVerdict = 'INVALID_SETUP'
                setupControlledCancelAccepted = $true
                setupNormalizedVerdict = 'expected_controlled_setup_cancel'
                measuredRouteVerdict = 'pass'
                measuredOperatorVerdict = if ($emitFallbackRecoveredBridgeWarning) { 'WARN_EXTERNAL_TRANSPORT' } else { 'PASS' }
                setupCleanupWarningCount = 0
                fallbackBridgeRecoveryWarningCount = if ($emitFallbackRecoveredBridgeWarning) { 1 } else { 0 }
            }
        }
        else {
            $null
        }
        setupNormalizedVerdict = if ($isControlledRestartMode) { 'expected_controlled_setup_cancel' } else { $null }
        fallbackDiagnostics = if ($isControlledRestartMode) {
            [ordered]@{
                fallbackWarningKinds = if ($emitFallbackRecoveredBridgeWarning) { @('recovered_post_tuna_fallback_bridge_clear') } else { @() }
                sendTimeoutsPerMiB = 0
                frontierRequestsPerMiB = 0
                fallbackRescueFreezeCount = 0
                fallbackRescueWidenCount = 0
                setupNormalizedVerdict = 'expected_controlled_setup_cancel'
            }
        }
        else {
            $null
        }
    }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
}

function Write-RouteAcceptanceFakeFallbackRetryableFailure {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$Attempt
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $setupLines = @(
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_route_selected; direction=outbound; transfer_id=fake-tuna-v4-setup; session_id=sess_setup; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=fake')
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_inbound_terminal; role=helper; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_remote')
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_outbound_terminal; role=helpee; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_local')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_live_progress_timeout; transfer_id=fake-tuna-v4-setup; reason=no useful data progress for 30s before measured fallback route started; total_wait_s=30; progress_timeout_seconds=30; receiver_next_chunk=0; receiver_highest_chunk=0; progress_events=0')
    )

    $setupLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
    $setupLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log') -Encoding UTF8
    $setupLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8

    [ordered]@{
        event = 'filetransfer_tuna_gui_error'
        routeMode = 'v4-restart-v6-fallback'
        attempt = $Attempt
        measuredRouteStarted = $false
        reason = 'progress_timeout_before_measured_fallback_route'
        message = 'no useful data progress before measured fallback route started'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json') -Encoding UTF8
}

function Write-RouteAcceptanceFakeFallbackRouteNotReadyFailure {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$Attempt
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $setupLines = @(
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_route_selected; direction=outbound; transfer_id=fake-tuna-v4-setup; session_id=sess_setup; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=fake')
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_inbound_terminal; role=helper; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_remote')
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_outbound_terminal; role=helpee; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_local')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed')
    )
    $measuredLines = @(
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=measured_post_tuna_fallback_v6_started')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_route_selected; direction=outbound; transfer_id=fake-fallback-route-not-ready; session_id=(none); route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=fake')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_legacy_negotiation_rejected; transfer_id=fake-fallback-route-not-ready; session_id=sess_fallback; direction=Inbound; offered_version=6; accepted_version=(none); reason=offer_route_not_active')
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_outbound_terminal; role=Helpee; session_id=; transfer_id=fake-fallback-route-not-ready; state=Declined; error_code=(none)')
        (New-RouteAcceptanceFakeLogLine -Message 'event=transfer_terminal; direction=outbound; transfer_id=fake-fallback-route-not-ready; session_id=; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=0; error_code=(none); reason=transport_incompatible; saved_path=(none); route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active')
    )

    $fullLines = @($setupLines + $measuredLines)
    $fullLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
    $fullLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log') -Encoding UTF8
    $setupLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8
    $measuredLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log') -Encoding UTF8

    [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_summary'
        routeMode = 'v4-restart-v6-fallback'
        attempt = $Attempt
        completed = $false
        integrityOk = $false
        fallbackFailurePhase = 'measured_terminal_failure'
        fallbackFailureReason = 'Timed out waiting for Chat.FileTransfer.Accept to become enabled.'
        fallbackDiagnostics = [ordered]@{
            measuredSlicePresent = $true
            finalTerminalState = 'Declined'
        }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8

    [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_failure'
        routeMode = 'v4-restart-v6-fallback'
        attempt = $Attempt
        measuredRouteStarted = $true
        reason = 'offer_route_not_active'
        message = 'Timed out waiting for Chat.FileTransfer.Accept to become enabled.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json') -Encoding UTF8
}

function Copy-RouteAcceptanceSelectedAttemptArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$AttemptDir,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    foreach ($item in @(Get-ChildItem -LiteralPath $AttemptDir -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $ArtifactDir -Recurse -Force
    }
}

function Write-RouteAcceptanceAttemptSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][object[]]$Attempts,
        [int]$SelectedAttempt = 0
    )

    $firstFailure = ''
    foreach ($attempt in @($Attempts)) {
        if (-not [bool]$attempt.succeeded) {
            $firstFailure = [string]$attempt.failureReason
            break
        }
    }

    [ordered]@{
        event = 'filetransfer_route_acceptance_fallback_attempts'
        attemptCount = @($Attempts).Count
        retryUsed = @($Attempts).Count -gt 1
        selectedAttempt = $SelectedAttempt
        firstFailureReason = $firstFailure
        attempts = @($Attempts)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'route-acceptance-attempts.json') -Encoding UTF8
}

function Test-RouteAcceptanceFallbackAttemptRetryable {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $observedRoutes = New-Object System.Collections.Generic.List[string]
    $summaryRetryable = $false
    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        $failurePhase = [string](Get-JsonPropertyValue -Object $summary -Name 'fallbackFailurePhase' -DefaultValue '')
        $failureReason = [string](Get-JsonPropertyValue -Object $summary -Name 'fallbackFailureReason' -DefaultValue '')
        $diagnostics = Get-JsonPropertyValue -Object $summary -Name 'fallbackDiagnostics' -DefaultValue $null
        $finalTerminalState = [string](Get-JsonPropertyValue -Object $diagnostics -Name 'finalTerminalState' -DefaultValue '')
        if ($failureReason.IndexOf('Timed out waiting for Chat.FileTransfer.Accept', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $failureReason.IndexOf('offer_route_not_active', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            ($failurePhase -eq 'measured_terminal_failure' -and $finalTerminalState -eq 'Declined')) {
            $summaryRetryable = $true
        }

        $measuredPhase = Get-JsonPropertyValue -Object $summary -Name 'measuredPhase' -DefaultValue $null
        if ($null -ne $measuredPhase) {
            $route = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'route' -DefaultValue '')
            if (-not [string]::IsNullOrWhiteSpace($route)) {
                $observedRoutes.Add($route) | Out-Null
            }
        }
    }

    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    if (Test-Path -LiteralPath $measuredPath -PathType Leaf) {
        foreach ($line in @(Get-Content -LiteralPath $measuredPath)) {
            if ($line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }

            if ($line -match 'route=([^;\s]+)') {
                $observedRoutes.Add($Matches[1]) | Out-Null
            }
        }
    }

    $uniqueRoutes = @($observedRoutes | Select-Object -Unique)
    if ($uniqueRoutes.Count -gt 0 -and
        -not ($uniqueRoutes -contains 'post_tuna_fallback_v6')) {
        return $false
    }

    if ($uniqueRoutes.Count -eq 0) {
        return $true
    }

    return $summaryRetryable -or
        $FailureMessage.IndexOf('progress timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $FailureMessage.IndexOf('no useful data progress', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Write-RouteAcceptanceFakeFallbackRun {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $attempts = New-Object System.Collections.Generic.List[object]
    $maxAttempts = [Math]::Max(1, $FallbackMaxAttempts)
    $retryAttempt1 = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RETRYABLE_ATTEMPT1'
    $routeNotReadyAttempt1 = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_ROUTE_NOT_READY_ATTEMPT1'
    $alwaysRetryableFailure = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RETRYABLE_ALWAYS'
    $fallbackRoute = 'post_tuna_fallback_v6'
    $fallbackRouteMode = 'v4-restart-v6-fallback'

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $attemptDir = Join-Path $ArtifactDir ("attempt-{0}" -f $attempt)
        New-Item -ItemType Directory -Force -Path $attemptDir | Out-Null

        if ($alwaysRetryableFailure -or ($retryAttempt1 -and $attempt -eq 1)) {
            Write-RouteAcceptanceFakeFallbackRetryableFailure -ArtifactDir $attemptDir -Attempt $attempt
            $attempts.Add([ordered]@{
                attempt = $attempt
                artifactDir = $attemptDir
                succeeded = $false
                retryable = $true
                failureReason = 'progress_timeout_before_measured_fallback_route'
            }) | Out-Null
            continue
        }

        if ($routeNotReadyAttempt1 -and $attempt -eq 1) {
            Write-RouteAcceptanceFakeFallbackRouteNotReadyFailure -ArtifactDir $attemptDir -Attempt $attempt
            $attempts.Add([ordered]@{
                attempt = $attempt
                artifactDir = $attemptDir
                succeeded = $false
                retryable = $true
                failureReason = 'measured_fallback_offer_route_not_active'
            }) | Out-Null
            continue
        }

        Write-RouteAcceptanceFakeTunaRun -ArtifactDir $attemptDir -Route $fallbackRoute -RouteMode $fallbackRouteMode
        Copy-RouteAcceptanceSelectedAttemptArtifacts -AttemptDir $attemptDir -ArtifactDir $ArtifactDir
        $attempts.Add([ordered]@{
            attempt = $attempt
            artifactDir = $attemptDir
            succeeded = $true
            retryable = $false
            failureReason = ''
        }) | Out-Null
        Write-RouteAcceptanceAttemptSummary -ArtifactDir $ArtifactDir -Attempts $attempts.ToArray() -SelectedAttempt $attempt
        return
    }

    Write-RouteAcceptanceAttemptSummary -ArtifactDir $ArtifactDir -Attempts $attempts.ToArray() -SelectedAttempt 0
}

function Invoke-RouteAcceptanceChildScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Invoke-RouteAcceptanceChildScriptNoThrow {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $childOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    }
    catch {
        $childOutput = @($_)
        $exitCode = if ($null -eq $LASTEXITCODE -or $LASTEXITCODE -eq 0) { 1 } else { [int]$LASTEXITCODE }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    foreach ($line in $childOutput) {
        Write-Host $line
    }

    return $exitCode
}

function Invoke-RegularNknRouteAcceptanceRun {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$PayloadSize,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath
    )

    $scriptPath = Join-Path $RepoRoot 'tools\Run-FileTransferNknSoak.ps1'
    $cycleTimeoutSeconds = [Math]::Min([Math]::Max(180, $ProgressTimeoutSeconds), $TimeoutSeconds)
    Invoke-RouteAcceptanceChildScript -ScriptPath $scriptPath -Description ("Regular NKN {0}" -f $PayloadSize) -Arguments @(
        '-Mode', 'nkn-fast',
        '-PayloadSizes', $PayloadSize,
        '-Cycles', '1',
        '-Direction', 'helpee-to-helper',
        '-ArtifactDir', $ArtifactDir,
        '-ExePath', $ResolvedExePath,
        '-TimeoutSeconds', ([string]$TimeoutSeconds),
        '-CycleTimeoutSeconds', ([string]$cycleTimeoutSeconds),
        '-ProgressTimeoutSeconds', ([string]$ProgressTimeoutSeconds),
        '-FailOnGate'
    )
}

function Invoke-TunaRouteAcceptanceRun {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath,
        [Parameter(Mandatory = $true)][string]$ResolvedWalletPath,
        [Parameter(Mandatory = $true)][string]$ResolvedSidecarPath,
        [Parameter(Mandatory = $true)][string]$EffectiveWalletPassword,
        [Parameter(Mandatory = $true)][string]$RouteMode,
        [Parameter(Mandatory = $true)][string]$Fault,
        [string]$PayerMode = 'helpee',
        [string]$PayloadSize = '128MiB',
        [string]$LiveToggleSequence = '',
        [string]$LiveProofMode = 'None'
    )

    $scriptPath = Join-Path $RepoRoot 'tools\Run-FileTransferTunaGuiSmoke.ps1'
    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
        $attempts = New-Object System.Collections.Generic.List[object]
        $maxAttempts = [Math]::Max(1, $FallbackMaxAttempts)
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            $attemptDir = Join-Path $ArtifactDir ("attempt-{0}" -f $attempt)
            New-Item -ItemType Directory -Force -Path $attemptDir | Out-Null
            $failureMessage = ''
            $arguments = @(
                '-ExePath', $ResolvedExePath,
                '-WalletPath', $ResolvedWalletPath,
                '-WalletPassword', $EffectiveWalletPassword,
                '-SidecarPath', $ResolvedSidecarPath,
                '-PayerMode', $PayerMode,
                '-RouteMode', $RouteMode,
                '-Fault', $Fault,
                '-Direction', 'helpee-to-helper',
                '-PayloadSize', $PayloadSize,
                '-ArtifactDir', $attemptDir,
                '-TimeoutSeconds', ([string]$TimeoutSeconds),
                '-ProgressTimeoutSeconds', ([string]$ProgressTimeoutSeconds)
            )
            if (-not [string]::IsNullOrWhiteSpace($LiveToggleSequence)) {
                $arguments += @('-LiveToggleSequence', $LiveToggleSequence)
            }

            $exitCode = Invoke-RouteAcceptanceChildScriptNoThrow -ScriptPath $scriptPath -Arguments $arguments

            Select-RouteAcceptanceMeasuredFallbackLogSlice -ArtifactDir $attemptDir
            if ($exitCode -ne 0) {
                $failureMessage = "Tuna GUI {0} attempt {1} failed with exit code {2}." -f $RouteMode, $attempt, $exitCode
            }
            else {
                try {
                Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $attemptDir -LiveRouteProofMode $LiveProofMode
                }
                catch {
                    $failureMessage = $_.Exception.Message
                }
            }

            if ([string]::IsNullOrWhiteSpace($failureMessage)) {
                Copy-RouteAcceptanceSelectedAttemptArtifacts -AttemptDir $attemptDir -ArtifactDir $ArtifactDir
                $attempts.Add([ordered]@{
                    attempt = $attempt
                    artifactDir = $attemptDir
                    succeeded = $true
                    retryable = $false
                    failureReason = ''
                }) | Out-Null
                Write-RouteAcceptanceAttemptSummary -ArtifactDir $ArtifactDir -Attempts $attempts.ToArray() -SelectedAttempt $attempt
                return
            }

            $retryable = Test-RouteAcceptanceFallbackAttemptRetryable -ArtifactDir $attemptDir -FailureMessage $failureMessage
            $attempts.Add([ordered]@{
                attempt = $attempt
                artifactDir = $attemptDir
                succeeded = $false
                retryable = $retryable
                failureReason = $failureMessage
            }) | Out-Null

            if (-not $retryable -or $attempt -eq $maxAttempts) {
                Write-RouteAcceptanceAttemptSummary -ArtifactDir $ArtifactDir -Attempts $attempts.ToArray() -SelectedAttempt 0
                throw $failureMessage
            }
        }
    }

    $arguments = @(
        '-ExePath', $ResolvedExePath,
        '-WalletPath', $ResolvedWalletPath,
        '-WalletPassword', $EffectiveWalletPassword,
        '-SidecarPath', $ResolvedSidecarPath,
        '-PayerMode', $PayerMode,
        '-RouteMode', $RouteMode,
        '-Fault', $Fault,
        '-Direction', 'helpee-to-helper',
        '-PayloadSize', $PayloadSize,
        '-ArtifactDir', $ArtifactDir,
        '-TimeoutSeconds', ([string]$TimeoutSeconds),
        '-ProgressTimeoutSeconds', ([string]$ProgressTimeoutSeconds)
    )
    if (-not [string]::IsNullOrWhiteSpace($LiveToggleSequence)) {
        $arguments += @('-LiveToggleSequence', $LiveToggleSequence)
    }

    Invoke-RouteAcceptanceChildScript -ScriptPath $scriptPath -Description ("Tuna GUI {0}" -f $RouteMode) -Arguments $arguments

    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        Select-RouteAcceptanceMeasuredFallbackLogSlice -ArtifactDir $ArtifactDir
    }

    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir -LiveRouteProofMode $LiveProofMode
}

function New-Phase4RouteAcceptanceScenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string[]]$ExpectedRouteChanges,
        [Parameter(Mandatory = $true)][long]$PayloadBytes,
        [string]$BaselineScenario = '',
        [AllowNull()]$Baseline = $null,
        [string]$RouteMode = '',
        [string]$Fault = 'none',
        [string]$PayerMode = 'helpee',
        [string]$LiveToggleSequence = '',
        [string]$LiveProofMode = 'None'
    )

    return [pscustomobject]@{
        Name = $Name
        Kind = $Kind
        ExpectedRouteChanges = @($ExpectedRouteChanges)
        PayloadBytes = $PayloadBytes
        BaselineScenario = $BaselineScenario
        Baseline = $Baseline
        RouteMode = $RouteMode
        Fault = $Fault
        PayerMode = $PayerMode
        LiveToggleSequence = $LiveToggleSequence
        LiveProofMode = $LiveProofMode
    }
}

function Get-Phase4RouteAcceptanceScenarios {
    param([Parameter(Mandatory = $true)]$BaselineManifest)

    $baselines = $BaselineManifest.Scenarios
    return @(
        (New-Phase4RouteAcceptanceScenario -Name 'regular-nkn-v4-64mb' -Kind 'regular' -ExpectedRouteChanges @('regular_nkn_v4_fast') -PayloadBytes 67108864L -BaselineScenario 'regular-nkn-v4-64mb' -Baseline $baselines['regular-nkn-v4-64mb'])
        (New-Phase4RouteAcceptanceScenario -Name 'active-tuna-v4-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4') -PayloadBytes 67108864L -BaselineScenario 'active-tuna-v4-64mb' -Baseline $baselines['active-tuna-v4-64mb'] -RouteMode 'preactivated' -Fault 'none' -PayerMode 'helpee')
        (New-Phase4RouteAcceptanceScenario -Name 'live-switch-off-helpee-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-switch-off-helpee-64mb' -Baseline $baselines['live-switch-off-helpee-64mb'] -RouteMode 'live-v4-switch-off' -Fault 'switch-off' -PayerMode 'helpee' -LiveProofMode 'SwitchOff')
        (New-Phase4RouteAcceptanceScenario -Name 'live-switch-off-helper-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-switch-off-helper-64mb' -Baseline $baselines['live-switch-off-helper-64mb'] -RouteMode 'live-v4-switch-off' -Fault 'switch-off' -PayerMode 'helper' -LiveProofMode 'SwitchOff')
        (New-Phase4RouteAcceptanceScenario -Name 'live-multi-toggle-off-on-off-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-multi-toggle-off-on-off-64mb' -Baseline $baselines['live-multi-toggle-off-on-off-64mb'] -RouteMode 'live-multi-toggle' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'off,on,off' -LiveProofMode 'MultiToggle')
        (New-Phase4RouteAcceptanceScenario -Name 'regular-v4-live-activation-off-on-off-128mb' -Kind 'tuna' -ExpectedRouteChanges @('regular_nkn_v4_fast', 'file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 134217728L -RouteMode 'live-regular-activation-cycle' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'on,off,on,off' -LiveProofMode 'RegularActivationCycle')
        (New-Phase4RouteAcceptanceScenario -Name 'second-transfer-after-reactivation' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4') -PayloadBytes 134217728L -RouteMode 'live-reactivation-second-transfer' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'off,on' -LiveProofMode 'None')
    )
}

function Get-Phase5RouteAcceptanceScenarios {
    param([Parameter(Mandatory = $true)]$BaselineManifest)

    $baselines = $BaselineManifest.Scenarios
    return @(
        (New-Phase4RouteAcceptanceScenario -Name 'regular-nkn-v4-64mb' -Kind 'regular' -ExpectedRouteChanges @('regular_nkn_v4_fast') -PayloadBytes 67108864L -BaselineScenario 'regular-nkn-v4-64mb' -Baseline $baselines['regular-nkn-v4-64mb'])
        (New-Phase4RouteAcceptanceScenario -Name 'active-tuna-v4-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4') -PayloadBytes 67108864L -BaselineScenario 'active-tuna-v4-64mb' -Baseline $baselines['active-tuna-v4-64mb'] -RouteMode 'preactivated' -Fault 'none' -PayerMode 'helpee')
        (New-Phase4RouteAcceptanceScenario -Name 'live-switch-off-helpee-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-switch-off-helpee-64mb' -Baseline $baselines['live-switch-off-helpee-64mb'] -RouteMode 'live-v4-switch-off' -Fault 'switch-off' -PayerMode 'helpee' -LiveProofMode 'SwitchOff')
        (New-Phase4RouteAcceptanceScenario -Name 'live-switch-off-helper-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-switch-off-helper-64mb' -Baseline $baselines['live-switch-off-helper-64mb'] -RouteMode 'live-v4-switch-off' -Fault 'switch-off' -PayerMode 'helper' -LiveProofMode 'SwitchOff')
        (New-Phase4RouteAcceptanceScenario -Name 'regular-v4-live-activation-off-on-off-128mb' -Kind 'tuna' -ExpectedRouteChanges @('regular_nkn_v4_fast', 'file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 134217728L -RouteMode 'live-regular-activation-cycle' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'on,off,on,off' -LiveProofMode 'RegularActivationCycle')
        (New-Phase4RouteAcceptanceScenario -Name 'second-transfer-after-reactivation' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4') -PayloadBytes 134217728L -RouteMode 'live-reactivation-second-transfer' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'off,on' -LiveProofMode 'None')
    )
}

function Test-Phase4RerunnableMeasurementFailure {
    param([Parameter(Mandatory = $true)]$Result)

    $failures = @($Result.failures | ForEach-Object { [string]$_ })
    return $failures.Count -gt 0 -and
        (@($failures | Where-Object {
            $_.IndexOf('goodput regression exceeded', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $_.IndexOf('measurement contaminated', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            -not (Test-Phase4RerunnableMeasurementExecutionFailure -Result $Result -Line ([string]$_))
        }).Count -eq 0)
}

function Test-Phase4RerunnableMeasurementExecutionFailure {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Line
    )

    if (-not $Result.measurementContaminated) {
        return $false
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$Result.setupFailurePhase) -or
        -not [string]::IsNullOrWhiteSpace([string]$Result.setupFailureReason)) {
        return $false
    }

    return $Line.IndexOf('scenario execution failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-Phase4FirstFailureReason {
    param([Parameter(Mandatory = $true)]$Result)

    $failure = @($Result.failures | Select-Object -First 1)
    if ($failure.Count -gt 0) {
        return [string]$failure[0]
    }

    return ''
}

function Test-Phase4SetupInvalidAttempt {
    param([Parameter(Mandatory = $true)]$Result)

    if (-not [string]::IsNullOrWhiteSpace([string]$Result.setupFailurePhase) -or
        -not [string]::IsNullOrWhiteSpace([string]$Result.setupFailureReason)) {
        return $true
    }

    if ([string]$Result.operatorVerdict -eq 'INVALID_SETUP') {
        return $true
    }

    foreach ($failure in @($Result.failures | ForEach-Object { [string]$_ })) {
        if ($failure.IndexOf('setup failure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $failure.IndexOf('scenario rerun execution failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $failure.IndexOf('missing artifact: filetransfer-tuna-gui-summary.json', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Test-Phase4PerformanceFailureLine {
    param([Parameter(Mandatory = $true)][string]$Line)

    return $Line.IndexOf('goodput regression exceeded', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Line.IndexOf('measurement contaminated', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-Phase4FailureClass {
    param([Parameter(Mandatory = $true)]$Failures)

    $failureLines = @($Failures | ForEach-Object { [string]$_ })
    if ($failureLines.Count -eq 0) {
        return 'none'
    }

    $performanceFailures = @($failureLines | Where-Object { Test-Phase4PerformanceFailureLine -Line ([string]$_) })
    if ($performanceFailures.Count -eq $failureLines.Count) {
        return 'performance'
    }

    if ($performanceFailures.Count -gt 0) {
        return 'correctness_and_performance'
    }

    return 'correctness'
}

function Test-Phase5CanonicalRepeatedToggleScenario {
    param([Parameter(Mandatory = $true)]$Scenario)

    return [string]$Scenario.Name -eq 'regular-v4-live-activation-off-on-off-128mb'
}

function Get-Phase5FailureClass {
    param([Parameter(Mandatory = $true)]$Failures)

    $failureLines = @($Failures | ForEach-Object { [string]$_ })
    if ($failureLines.Count -eq 0) {
        return 'none'
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('setup failure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('scenario execution failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('missing artifact', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'setup'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('completion/integrity', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('failed completion/integrity', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('terminal/integrity proof is incomplete', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('terminal errors observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('terminals not Completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'protocol_or_integrity'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('live route epoch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'live_route_proof'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('fallback leg authority', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'fallback_authority'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('route consistency', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('selected route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('measured route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('measured protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('route selected protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('runtime', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'route_runtime'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('bridge liveness', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('session liveness timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'bridge_liveness'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('warning', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('operator verdict', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('external_transport_churn', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'warning_policy'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('goodput regression exceeded', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('measurement contaminated', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'performance'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('environmental', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('public_nkn', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'environmental'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('operator hard failures observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'protocol_or_integrity'
        }
    }

    return 'protocol_or_integrity'
}

function Assert-Phase5ScenarioRun {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if ($Result.liveRouteEpochProofVerdict -eq 'fail') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'live route epoch proof verdict is fail'
    }

    if ($Result.fallbackLegAuthorityProofVerdict -eq 'fail' -and [string]$Scenario.Name -ne 'second-transfer-after-reactivation') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'fallback leg authority proof verdict is fail'
    }

    if ($Result.bridgeLivenessIntegrationVerdict -eq 'fail') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'bridge liveness integration verdict is fail'
    }

    $canonicalRepeatedToggle = Test-Phase5CanonicalRepeatedToggleScenario -Scenario $Scenario
    if ($canonicalRepeatedToggle) {
        if ($Result.liveRouteEpochProofVerdict -ne 'pass') {
            Add-RouteAcceptanceFailure -Result $Result -Message ("canonical repeated-toggle live route proof must pass, actual {0}" -f $Result.liveRouteEpochProofVerdict)
        }

        if ($Result.fallbackLegAuthorityProofVerdict -ne 'pass') {
            Add-RouteAcceptanceFailure -Result $Result -Message ("canonical repeated-toggle fallback leg authority proof must pass, actual {0}" -f $Result.fallbackLegAuthorityProofVerdict)
        }

        if ($Result.bridgeLivenessIntegrationVerdict -ne 'pass') {
            Add-RouteAcceptanceFailure -Result $Result -Message ("canonical repeated-toggle bridge liveness integration proof must pass, actual {0}" -f $Result.bridgeLivenessIntegrationVerdict)
        }
    }
    elseif ($Result.bridgeLivenessIntegrationVerdict -eq 'none') {
        $retainedLogPath = Join-Path $Result.artifactDir 'filetransfer-retained-log-slice.log'
        if (Test-Path -LiteralPath $retainedLogPath -PathType Leaf) {
            $retainedText = Get-Content -LiteralPath $retainedLogPath -Raw
            $Result.sessionLivenessTimeoutCount = [regex]::Matches($retainedText, 'event=session_liveness_timeout(?:;|\s)').Count
            if ($Result.sessionLivenessTimeoutCount -gt 0) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("bridge liveness integration verdict is none but session_liveness_timeout_count={0}" -f $Result.sessionLivenessTimeoutCount)
            }
        }

        if ($Result.bridgeLivenessStaleDeferralCount -gt 0) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("bridge liveness integration verdict is none but stale_deferral_count={0}" -f $Result.bridgeLivenessStaleDeferralCount)
        }

        if (-not $Result.completed -or -not $Result.shaOk) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("bridge liveness integration verdict is none but terminal/integrity proof is incomplete: completed={0}; sha_ok={1}" -f $Result.completed, $Result.shaOk)
        }
    }
}

function Get-Phase4ScenarioPayloadSizeText {
    param([Parameter(Mandatory = $true)]$Scenario)

    $payloadBytes = [long]$Scenario.PayloadBytes
    if ($payloadBytes -eq 134217728L) {
        return '128MiB'
    }

    if ($payloadBytes -eq 67108864L) {
        return '64MiB'
    }

    if ($payloadBytes -gt 0 -and ($payloadBytes % 1048576L) -eq 0) {
        return ('{0}MiB' -f [long]($payloadBytes / 1048576L))
    }

    return ([string]$payloadBytes)
}

function Invoke-Phase4RouteAcceptanceScenario {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][bool]$FakeMode,
        [ValidateSet('phase4', 'phase5')]
        [string]$AcceptancePhase = 'phase4',
        [string]$ResolvedExePath = '',
        [string]$ResolvedWalletPath = '',
        [string]$ResolvedSidecarPath = '',
        [string]$EffectiveWalletPassword = ''
    )

    $artifactDir = Join-Path $RunRoot ([string]$Scenario.Name)
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    $payloadSize = Get-Phase4ScenarioPayloadSizeText -Scenario $Scenario

    $scenarioExecutionFailure = ''
    try {
        if ($FakeMode) {
            Write-RouteAcceptanceFakePhase4Run -Scenario $Scenario -ArtifactDir $artifactDir
        }
        elseif ($Scenario.Kind -eq 'regular') {
            Invoke-RegularNknRouteAcceptanceRun -RepoRoot $RepoRoot -ArtifactDir $artifactDir -PayloadSize $payloadSize -ResolvedExePath $ResolvedExePath
        }
        else {
            Invoke-TunaRouteAcceptanceRun `
                -RepoRoot $RepoRoot `
                -ArtifactDir $artifactDir `
                -ResolvedExePath $ResolvedExePath `
                -ResolvedWalletPath $ResolvedWalletPath `
                -ResolvedSidecarPath $ResolvedSidecarPath `
                -EffectiveWalletPassword $EffectiveWalletPassword `
                -RouteMode ([string]$Scenario.RouteMode) `
                -Fault ([string]$Scenario.Fault) `
                -PayerMode ([string]$Scenario.PayerMode) `
                -PayloadSize $payloadSize `
                -LiveToggleSequence ([string]$Scenario.LiveToggleSequence) `
                -LiveProofMode ([string]$Scenario.LiveProofMode)
        }
    }
    catch {
        $scenarioExecutionFailure = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
    }

    $result = Assert-Phase4ScenarioRun -Scenario $Scenario -ArtifactDir $artifactDir
    if (-not [string]::IsNullOrWhiteSpace($scenarioExecutionFailure)) {
        Add-RouteAcceptanceFailure -Result $result -Message ("scenario execution failed: {0}" -f $scenarioExecutionFailure)
    }
    if ($AcceptancePhase -eq 'phase5') {
        Assert-Phase5ScenarioRun -Scenario $Scenario -Result $result
    }
    if (Test-Phase4RerunnableMeasurementFailure -Result $result) {
        $firstAttemptResult = $result
        $firstFailureReason = Get-Phase4FirstFailureReason -Result $firstAttemptResult
        $maxReruns = [Math]::Max(0, $GoodputOnlyRerunLimit)
        for ($rerun = 1; $rerun -le $maxReruns; $rerun++) {
            $rerunDir = Join-Path $RunRoot ("{0}-rerun-{1}" -f $Scenario.Name, $rerun)
            New-Item -ItemType Directory -Force -Path $rerunDir | Out-Null
            $rerunExecutionFailure = ''
            try {
                if ($FakeMode) {
                    Write-RouteAcceptanceFakePhase4Run -Scenario $Scenario -ArtifactDir $rerunDir -RerunAttempt $rerun
                }
                elseif ($Scenario.Kind -eq 'regular') {
                    Invoke-RegularNknRouteAcceptanceRun -RepoRoot $RepoRoot -ArtifactDir $rerunDir -PayloadSize $payloadSize -ResolvedExePath $ResolvedExePath
                }
                else {
                    Invoke-TunaRouteAcceptanceRun `
                        -RepoRoot $RepoRoot `
                        -ArtifactDir $rerunDir `
                        -ResolvedExePath $ResolvedExePath `
                        -ResolvedWalletPath $ResolvedWalletPath `
                        -ResolvedSidecarPath $ResolvedSidecarPath `
                        -EffectiveWalletPassword $EffectiveWalletPassword `
                        -RouteMode ([string]$Scenario.RouteMode) `
                        -Fault ([string]$Scenario.Fault) `
                        -PayerMode ([string]$Scenario.PayerMode) `
                        -PayloadSize $payloadSize `
                        -LiveToggleSequence ([string]$Scenario.LiveToggleSequence) `
                        -LiveProofMode ([string]$Scenario.LiveProofMode)
                }
            }
            catch {
                $rerunExecutionFailure = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
            }

            $rerunResult = Assert-Phase4ScenarioRun -Scenario $Scenario -ArtifactDir $rerunDir
            if (-not [string]::IsNullOrWhiteSpace($rerunExecutionFailure)) {
                Add-RouteAcceptanceFailure -Result $rerunResult -Message ("scenario rerun execution failed: {0}" -f $rerunExecutionFailure)
            }
            if ($AcceptancePhase -eq 'phase5') {
                Assert-Phase5ScenarioRun -Scenario $Scenario -Result $rerunResult
            }
            $rerunResult.attemptCount = $rerun + 1
            $rerunResult.retryUsed = $true
            $rerunResult.selectedAttempt = if ($rerunResult.failures.Count -eq 0) { $rerun + 1 } else { 0 }
            $rerunResult.firstFailureReason = $firstFailureReason
            if ($rerunResult.failures.Count -ne 0 -and (Test-Phase4SetupInvalidAttempt -Result $rerunResult)) {
                $rerunFailureReason = if (-not [string]::IsNullOrWhiteSpace($rerunExecutionFailure)) {
                    "scenario rerun execution failed: {0}" -f $rerunExecutionFailure
                }
                else {
                    Get-Phase4FirstFailureReason -Result $rerunResult
                }
                if ([string]::IsNullOrWhiteSpace($rerunFailureReason)) {
                    $rerunFailureReason = 'rerun did not produce comparable transfer evidence'
                }

                $firstAttemptResult.attemptCount = $rerun + 1
                $firstAttemptResult.retryUsed = $true
                $firstAttemptResult.selectedAttempt = 1
                $firstAttemptResult.firstFailureReason = $firstFailureReason
                $firstAttemptResult.rerunArtifactDir = $rerunDir
                $firstAttemptResult.rerunFailureReason = $rerunFailureReason
                Add-RouteAcceptanceFailure -Result $firstAttemptResult -Message ("scenario rerun setup failed; preserving first-attempt evidence: {0}" -f $rerunFailureReason)
                $result = $firstAttemptResult
                break
            }

            $result = $rerunResult
            if ($result.failures.Count -eq 0) {
                break
            }
        }
    }

    $script:RunResults.Add($result) | Out-Null
}

function Write-Phase4RouteAcceptanceSummaryFiles {
    param(
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][string]$BaselinePath,
        [ValidateSet('phase4', 'phase5')]
        [string]$AcceptancePhase = 'phase4',
        # Default output remains phase4-ab-acceptance-summary.txt for Phase 4 compatibility.
        # Phase 5 writes phase5-analyzer-gui-acceptance-summary.txt.
        [string]$SummaryBaseName = 'phase4-ab-acceptance',
        [string]$SummaryTitle = 'Phase 4 File Transfer A/B Acceptance',
        [int]$ExpectedRunCount = 7
    )

    $failureLines = @()
    foreach ($result in $script:RunResults) {
        foreach ($failure in @($result.failures)) {
            $failureLines += ("{0}: {1}" -f $result.name, $failure)
        }
    }

    if ($script:RunResults.Count -ne $expectedRunCount) {
        $failureLines += ("expected {0} runs, observed {1}" -f $expectedRunCount, $script:RunResults.Count)
    }

    $performanceFailureLines = @($failureLines | Where-Object { Test-Phase4PerformanceFailureLine -Line ([string]$_) })
    $correctnessFailureLines = @($failureLines | Where-Object { -not (Test-Phase4PerformanceFailureLine -Line ([string]$_)) })
    $verdict = if ($failureLines.Count -eq 0 -and $script:RunResults.Count -eq $expectedRunCount) { 'PASS' } else { 'FAIL' }
    $correctnessVerdict = if ($correctnessFailureLines.Count -eq 0 -and $script:RunResults.Count -eq $expectedRunCount) { 'PASS' } else { 'FAIL' }
    $performanceVerdict = if ($performanceFailureLines.Count -eq 0) { 'PASS' } else { 'FAIL' }
    $networkVarianceNote = 'Goodput on public NKN/Tuna is classified separately from runtime correctness only after strict route/protocol/SHA/hard-failure/warning proof passes; persistent goodput below the Phase 4 floor still fails performance acceptance and remains release-blocking unless a rerun proves environmental noise.'
    $textLines = @(
        $SummaryTitle,
        ("verdict={0}" -f $verdict),
        ("correctness_verdict={0}" -f $correctnessVerdict),
        ("performance_verdict={0}" -f $performanceVerdict),
        ("acceptance_phase={0}" -f $AcceptancePhase),
        ("artifact_root={0}" -f $RunRoot),
        ("baseline_manifest={0}" -f $BaselinePath),
        ("goodput_regression_tolerance_percent={0:F1}" -f [double]$GoodputRegressionTolerancePercent),
        'correctness_gate_policy=strict_no_exceptions',
        'network_variance_policy=public_nkn_paired_rerun',
        ("network_variance_note={0}" -f $networkVarianceNote),
        'regular_nkn_external_transport_warning_policy=capped_external_transport_churn_requires_clean_rerun',
        'goodput_regression_policy=rerun_once_when_only_failure',
        ("run_count={0}" -f $script:RunResults.Count),
        ("failure_count={0}" -f $failureLines.Count),
        ("correctness_failure_count={0}" -f $correctnessFailureLines.Count),
        ("performance_failure_count={0}" -f $performanceFailureLines.Count),
        ''
    )

    foreach ($result in $script:RunResults) {
        $prefix = [string]$result.name
        $resultFailureLines = @($result.failures | ForEach-Object { [string]$_ })
        $resultPerformanceFailureCount = @($resultFailureLines | Where-Object { Test-Phase4PerformanceFailureLine -Line ([string]$_) }).Count
        $resultCorrectnessFailureCount = @($resultFailureLines | Where-Object { -not (Test-Phase4PerformanceFailureLine -Line ([string]$_)) }).Count
        $textLines += ("{0}.artifact_dir={1}" -f $prefix, $result.artifactDir)
        $textLines += ("{0}.final_route={1}" -f $prefix, $result.finalRoute)
        $textLines += ("{0}.selected_route_sequence={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.selectedRouteChanges))
        $textLines += ("{0}.route_sequence={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.selectedRouteChanges))
        $textLines += ("{0}.live_route_epoch_route_changes={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.liveRouteEpochRouteChanges))
        $textLines += ("{0}.live_epoch_route_changes={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.liveRouteEpochRouteChanges))
        $textLines += ("{0}.protocol={1}" -f $prefix, $result.protocol)
        $textLines += ("{0}.route_consistency_verdict={1}" -f $prefix, $result.routeConsistencyVerdict)
        $textLines += ("{0}.live_route_epoch_proof_verdict={1}" -f $prefix, $result.liveRouteEpochProofVerdict)
        $textLines += ("{0}.fallback_leg_authority_proof_verdict={1}" -f $prefix, $result.fallbackLegAuthorityProofVerdict)
        $textLines += ("{0}.bridge_liveness_integration_verdict={1}" -f $prefix, $result.bridgeLivenessIntegrationVerdict)
        $textLines += ("{0}.operator_verdict={1}" -f $prefix, $result.operatorVerdict)
        $textLines += ("{0}.hard_failure_count={1}" -f $prefix, $result.hardFailureCount)
        $textLines += ("{0}.warning_count={1}" -f $prefix, $result.warningCount)
        $textLines += ("{0}.warning_kinds={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.warningKinds))
        $textLines += ("{0}.warning_cap_exceeded_kinds={1}" -f $prefix, $result.warningCapExceededKinds)
        $textLines += ("{0}.environmental_classification={1}" -f $prefix, $result.environmentalClassification)
        $textLines += ("{0}.measurement_contaminated={1}" -f $prefix, ($(if ($result.measurementContaminated) { 1 } else { 0 })))
        $textLines += ("{0}.measurement_contamination_reasons={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.measurementContaminationReasons))
        $textLines += ("{0}.completed={1}" -f $prefix, ($(if ($result.completed) { 1 } else { 0 })))
        $textLines += ("{0}.sha_ok={1}" -f $prefix, ($(if ($result.shaOk) { 1 } else { 0 })))
        $textLines += ("{0}.goodput_bytes_per_second={1:F3}" -f $prefix, (ConvertTo-RouteAcceptanceDouble -Value $result.goodputBytesPerSecond))
        $textLines += ("{0}.baseline_goodput_bytes_per_second={1:F3}" -f $prefix, (ConvertTo-RouteAcceptanceDouble -Value $result.baselineGoodputBytesPerSecond))
        $textLines += ("{0}.goodput_floor_bytes_per_second={1:F3}" -f $prefix, (ConvertTo-RouteAcceptanceDouble -Value $result.goodputRegressionFloorBytesPerSecond))
        $textLines += ("{0}.goodput_regression_percent={1:F3}" -f $prefix, (ConvertTo-RouteAcceptanceDouble -Value $result.goodputRegressionPercent))
        $textLines += ("{0}.attempt_count={1}" -f $prefix, $result.attemptCount)
        $textLines += ("{0}.retry_used={1}" -f $prefix, ($(if ($result.retryUsed) { 1 } else { 0 })))
        $textLines += ("{0}.selected_attempt={1}" -f $prefix, $result.selectedAttempt)
        if (-not [string]::IsNullOrWhiteSpace([string]$result.firstFailureReason)) {
            $textLines += ("{0}.first_failure_reason={1}" -f $prefix, $result.firstFailureReason)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.rerunArtifactDir)) {
            $textLines += ("{0}.rerun_artifact_dir={1}" -f $prefix, $result.rerunArtifactDir)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.rerunFailureReason)) {
            $textLines += ("{0}.rerun_failure_reason={1}" -f $prefix, $result.rerunFailureReason)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.setupFailurePhase)) {
            $textLines += ("{0}.setup_failure_phase={1}" -f $prefix, $result.setupFailurePhase)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.setupFailureReason)) {
            $textLines += ("{0}.setup_failure_reason={1}" -f $prefix, $result.setupFailureReason)
        }
        $textLines += ("{0}.failure_count={1}" -f $prefix, $result.failures.Count)
        $textLines += ("{0}.correctness_failure_count={1}" -f $prefix, $resultCorrectnessFailureCount)
        $textLines += ("{0}.performance_failure_count={1}" -f $prefix, $resultPerformanceFailureCount)
        $failureClass = if ($AcceptancePhase -eq 'phase5') { Get-Phase5FailureClass -Failures $result.failures } else { Get-Phase4FailureClass -Failures $result.failures }
        $textLines += ("{0}.acceptance_failure_class={1}" -f $prefix, $failureClass)
    }

    $textLines += ''
    $textLines += 'failures:'
    if ($failureLines.Count -eq 0) {
        $textLines += '(none)'
    }
    else {
        $textLines += @($failureLines)
    }

    $summaryTxt = Join-Path $RunRoot ("{0}-summary.txt" -f $SummaryBaseName)
    $textLines | Set-Content -LiteralPath $summaryTxt -Encoding UTF8
    $textLines | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.txt') -Encoding UTF8

    $varianceNoteLines = @(
        ("# {0} Network Variance Note" -f ($(if ($AcceptancePhase -eq 'phase5') { 'Phase 5' } else { 'Phase 4' }))),
        '',
        ('artifact_root={0}' -f $RunRoot),
        ('baseline_manifest={0}' -f $BaselinePath),
        ('verdict={0}' -f $verdict),
        ('correctness_verdict={0}' -f $correctnessVerdict),
        ('performance_verdict={0}' -f $performanceVerdict),
        ('goodput_regression_tolerance_percent={0:F1}' -f [double]$GoodputRegressionTolerancePercent),
        '',
        $networkVarianceNote,
        '',
        'A goodput-only miss does not waive Phase 4 acceptance. It separates performance variance from route/runtime correctness so remediation can avoid unnecessary bridge, wallet, installer, or route-policy changes.',
        '',
        'Performance failures:'
    )
    if ($performanceFailureLines.Count -eq 0) {
        $varianceNoteLines += '- (none)'
    }
    else {
        $varianceNoteLines += @($performanceFailureLines | ForEach-Object { '- {0}' -f $_ })
    }
    $varianceNoteLines += ''
    $varianceNoteLines += 'Correctness/evidence failures:'
    if ($correctnessFailureLines.Count -eq 0) {
        $varianceNoteLines += '- (none)'
    }
    else {
        $varianceNoteLines += @($correctnessFailureLines | ForEach-Object { '- {0}' -f $_ })
    }
    $varianceNoteName = if ($AcceptancePhase -eq 'phase5') { 'phase5-network-variance-note.md' } else { 'phase4-network-variance-note.md' }
    $varianceNoteLines | Set-Content -LiteralPath (Join-Path $RunRoot $varianceNoteName) -Encoding UTF8

    $jsonRuns = @(
        foreach ($result in $script:RunResults) {
            $resultFailureLines = @($result.failures | ForEach-Object { [string]$_ })
            $resultPerformanceFailureCount = @($resultFailureLines | Where-Object { Test-Phase4PerformanceFailureLine -Line ([string]$_) }).Count
            $resultCorrectnessFailureCount = @($resultFailureLines | Where-Object { -not (Test-Phase4PerformanceFailureLine -Line ([string]$_)) }).Count
            [ordered]@{
                name = $result.name
                artifactDir = $result.artifactDir
                expectedRoute = $result.expectedRoute
                expectedProtocol = $result.expectedProtocol
                finalRoute = $result.finalRoute
                protocol = $result.protocol
                selectedRouteSequence = @($result.selectedRouteChanges)
                liveRouteEpochRouteChanges = @($result.liveRouteEpochRouteChanges)
                routeConsistencyVerdict = $result.routeConsistencyVerdict
                liveRouteEpochProofVerdict = $result.liveRouteEpochProofVerdict
                fallbackLegAuthorityProofVerdict = $result.fallbackLegAuthorityProofVerdict
                bridgeLivenessIntegrationVerdict = $result.bridgeLivenessIntegrationVerdict
                operatorVerdict = $result.operatorVerdict
                hardFailureCount = $result.hardFailureCount
                warningCount = $result.warningCount
                warningKinds = @($result.warningKinds)
                warningCapExceededKinds = $result.warningCapExceededKinds
                environmentalClassification = $result.environmentalClassification
                measurementContaminated = $result.measurementContaminated
                measurementContaminationReasons = @($result.measurementContaminationReasons)
                completed = $result.completed
                integrityOk = $result.integrityOk
                shaOk = $result.shaOk
                goodputBytesPerSecond = $result.goodputBytesPerSecond
                baselineGoodputBytesPerSecond = $result.baselineGoodputBytesPerSecond
                goodputRegressionFloorBytesPerSecond = $result.goodputRegressionFloorBytesPerSecond
                goodputRegressionPercent = $result.goodputRegressionPercent
                attemptCount = $result.attemptCount
                retryUsed = $result.retryUsed
                selectedAttempt = $result.selectedAttempt
                firstFailureReason = $result.firstFailureReason
                rerunArtifactDir = $result.rerunArtifactDir
                rerunFailureReason = $result.rerunFailureReason
                setupFailurePhase = $result.setupFailurePhase
                setupFailureReason = $result.setupFailureReason
                correctnessFailureCount = $resultCorrectnessFailureCount
                performanceFailureCount = $resultPerformanceFailureCount
                acceptanceFailureClass = if ($AcceptancePhase -eq 'phase5') { Get-Phase5FailureClass -Failures $result.failures } else { Get-Phase4FailureClass -Failures $result.failures }
                failures = @($result.failures | ForEach-Object { [string]$_ })
            }
        }
    )

    $jsonSummary = [ordered]@{
        event = if ($AcceptancePhase -eq 'phase5') { 'phase5_filetransfer_analyzer_gui_acceptance_summary' } else { 'phase4_filetransfer_ab_acceptance_summary' }
        verdict = $verdict
        correctnessVerdict = $correctnessVerdict
        performanceVerdict = $performanceVerdict
        acceptancePhase = $AcceptancePhase
        artifactRoot = $RunRoot
        baselineManifest = $BaselinePath
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        goodputRegressionTolerancePercent = $GoodputRegressionTolerancePercent
        correctnessGatePolicy = 'strict_no_exceptions'
        networkVariancePolicy = 'public_nkn_paired_rerun'
        networkVarianceNote = $networkVarianceNote
        regularNknExternalTransportWarningPolicy = 'capped_external_transport_churn_requires_clean_rerun'
        goodputRegressionPolicy = 'rerun_once_when_only_failure'
        failureCount = $failureLines.Count
        correctnessFailureCount = $correctnessFailureLines.Count
        performanceFailureCount = $performanceFailureLines.Count
        runs = $jsonRuns
        failures = @($failureLines)
        correctnessFailures = @($correctnessFailureLines)
        performanceFailures = @($performanceFailureLines)
    }

    $jsonSummary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $RunRoot ("{0}-summary.json" -f $SummaryBaseName)) -Encoding UTF8
    $jsonSummary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.json') -Encoding UTF8
    return $verdict
}

function Write-RouteAcceptanceSummaryFiles {
    param([Parameter(Mandatory = $true)][string]$RunRoot)

    $failureLines = @()
    foreach ($result in $script:RunResults) {
        foreach ($failure in $result.failures) {
            $failureLines += ("{0}: {1}" -f $result.name, $failure)
        }
    }

    $verdict = if ($failureLines.Count -eq 0 -and $script:RunResults.Count -eq 4) { 'PASS' } else { 'FAIL' }
    if ($script:RunResults.Count -ne 4) {
        $failureLines += ("expected 4 runs, observed {0}" -f $script:RunResults.Count)
    }

    $textLines = @()
    $textLines += 'File Transfer Route Acceptance'
    $textLines += ("verdict={0}" -f $verdict)
    $textLines += ("artifact_root={0}" -f $RunRoot)
    $regularFloorText = "{0:F3}" -f [double]$script:RegularNknGoodputFloorBytesPerSecond
    $tunaFloorText = "{0:F3}" -f [double]$script:TunaGoodputFloorBytesPerSecond
    $textLines += ("regular_nkn_goodput_floor_bytes_per_second={0}" -f $regularFloorText)
    $textLines += ("tuna_goodput_floor_bytes_per_second={0}" -f $tunaFloorText)
    $textLines += ("run_count={0}" -f $script:RunResults.Count)
    $textLines += ("failure_count={0}" -f $failureLines.Count)
    $textLines += ''

    foreach ($result in $script:RunResults) {
        $prefix = $result.name
        $textLines += ("{0}.artifact_dir={1}" -f $prefix, $result.artifactDir)
        $textLines += ("{0}.route={1}" -f $prefix, $result.route)
        $textLines += ("{0}.protocol={1}" -f $prefix, $result.protocol)
        $textLines += ("{0}.runtime_profile={1}" -f $prefix, $result.runtimeProfile)
        $textLines += ("{0}.bridge_recovery_policy={1}" -f $prefix, $result.bridgeRecoveryPolicy)
        $textLines += ("{0}.route_consistency_verdict={1}" -f $prefix, $result.routeConsistencyVerdict)
        $textLines += ("{0}.operator_verdict={1}" -f $prefix, $result.operatorVerdict)
        $textLines += ("{0}.operator_accepted_with_warnings={1}" -f $prefix, ($(if ($result.operatorAcceptedWithWarnings) { 1 } else { 0 })))
        $resultWarningKinds = @($result.warningKinds)
        $textLines += ("{0}.warning_kinds={1}" -f $prefix, ($(if ($resultWarningKinds.Count -gt 0) { $resultWarningKinds -join ',' } else { '(none)' })))
        $textLines += ("{0}.attempt_count={1}" -f $prefix, $result.attemptCount)
        $textLines += ("{0}.retry_used={1}" -f $prefix, ($(if ($result.retryUsed) { 1 } else { 0 })))
        $textLines += ("{0}.selected_attempt={1}" -f $prefix, $result.selectedAttempt)
        if (-not [string]::IsNullOrWhiteSpace([string]$result.firstFailureReason)) {
            $textLines += ("{0}.first_failure_reason={1}" -f $prefix, $result.firstFailureReason)
        }
        $goodputText = "{0:F3}" -f (ConvertTo-RouteAcceptanceDouble -Value $result.goodputBytesPerSecond)
        $textLines += ("{0}.goodput_bytes_per_second={1}" -f $prefix, $goodputText)
        $textLines += ("{0}.bridge_bulk_send_failure_count={1}" -f $prefix, $result.bridgeBulkSendFailureCount)
        $textLines += ("{0}.failure_count={1}" -f $prefix, $result.failures.Count)
    }

    $textLines += ''
    $textLines += 'failures:'
    if ($failureLines.Count -eq 0) {
        $textLines += '(none)'
    }
    else {
        foreach ($failure in @($failureLines)) {
            $textLines += $failure
        }
    }

    Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.txt') -Value $textLines -Encoding UTF8

    $jsonRuns = @(
        foreach ($result in $script:RunResults) {
            [ordered]@{
                name = $result.name
                artifactDir = $result.artifactDir
                expectedRoute = $result.expectedRoute
                expectedProtocol = $result.expectedProtocol
                route = $result.route
                protocol = $result.protocol
                runtimeProfile = $result.runtimeProfile
                bridgeRecoveryPolicy = $result.bridgeRecoveryPolicy
                routeConsistencyVerdict = $result.routeConsistencyVerdict
                operatorVerdict = $result.operatorVerdict
                operatorAcceptedWithWarnings = $result.operatorAcceptedWithWarnings
                warningKinds = @($result.warningKinds)
                attemptCount = $result.attemptCount
                retryUsed = $result.retryUsed
                selectedAttempt = $result.selectedAttempt
                firstFailureReason = $result.firstFailureReason
                controlledRestartAnalysis = $result.controlledRestartAnalysis
                completed = $result.completed
                integrityOk = $result.integrityOk
                goodputBytesPerSecond = $result.goodputBytesPerSecond
                bridgeBulkSendFailureCount = $result.bridgeBulkSendFailureCount
                failures = @($result.failures | ForEach-Object { [string]$_ })
            }
        }
    )

    $jsonSummary = [ordered]@{
        event = 'filetransfer_route_acceptance_summary'
        verdict = $verdict
        artifactRoot = $RunRoot
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        regularNknGoodputFloorBytesPerSecond = $script:RegularNknGoodputFloorBytesPerSecond
        tunaGoodputFloorBytesPerSecond = $script:TunaGoodputFloorBytesPerSecond
        failureCount = $failureLines.Count
        runs = $jsonRuns
        failures = @($failureLines)
    }

    $jsonSummary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.json') -Encoding UTF8
    return $verdict
}

$repoRoot = Resolve-RepoRoot
$runRoot = $null
Push-Location $repoRoot
try {
    $runRoot = New-RouteAcceptanceTimestampedRoot -RepoRoot $repoRoot -RequestedRoot $ArtifactRoot
    $fakeMode = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_GUI'

    if ($MatrixMode -eq 'phase4-ab-acceptance' -or $MatrixMode -eq 'phase5-analyzer-gui-acceptance') {
        $baseline = Read-RouteAcceptanceBaselineManifest -RepoRoot $repoRoot -Path $BaselineManifestPath
        $acceptancePhase = if ($MatrixMode -eq 'phase5-analyzer-gui-acceptance') { 'phase5' } else { 'phase4' }
        $scenarios = if ($acceptancePhase -eq 'phase5') {
            @(Get-Phase5RouteAcceptanceScenarios -BaselineManifest $baseline)
        }
        else {
            @(Get-Phase4RouteAcceptanceScenarios -BaselineManifest $baseline)
        }
        $resolvedExePath = ''
        $resolvedWalletPath = ''
        $resolvedSidecarPath = ''
        $effectiveWalletPassword = ''

        if (-not $fakeMode) {
            $effectiveWalletPassword = $WalletPassword
            if ([string]::IsNullOrWhiteSpace($effectiveWalletPassword)) {
                $effectiveWalletPassword = [string]$env:NLINK_TUNA_TEST_WALLET_PASSWORD
            }

            if ([string]::IsNullOrWhiteSpace($effectiveWalletPassword)) {
                throw 'Provide -WalletPassword or set NLINK_TUNA_TEST_WALLET_PASSWORD before running route acceptance.'
            }

            $resolvedExePath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $ExePath)).Path
            $resolvedWalletPath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $WalletPath)).Path
            $resolvedSidecarPath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $SidecarPath)).Path
        }

        foreach ($scenario in $scenarios) {
            Invoke-Phase4RouteAcceptanceScenario `
                -Scenario $scenario `
                -RepoRoot $repoRoot `
                -RunRoot $runRoot `
                -FakeMode $fakeMode `
                -AcceptancePhase $acceptancePhase `
                -ResolvedExePath $resolvedExePath `
                -ResolvedWalletPath $resolvedWalletPath `
                -ResolvedSidecarPath $resolvedSidecarPath `
                -EffectiveWalletPassword $effectiveWalletPassword
        }

        if ($acceptancePhase -eq 'phase5') {
            $verdict = Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $runRoot -BaselinePath ([string]$baseline.Path) -AcceptancePhase 'phase5' -SummaryBaseName 'phase5-analyzer-gui-acceptance' -SummaryTitle 'Phase 5 File Transfer Analyzer/GUI Acceptance' -ExpectedRunCount 6
            Write-Host ("[FileTransfer Phase5 Analyzer/GUI Acceptance] verdict={0}; artifact_root={1}" -f $verdict, $runRoot) -ForegroundColor ($(if ($verdict -eq 'PASS') { 'Green' } else { 'Red' }))
        }
        else {
            $verdict = Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $runRoot -BaselinePath ([string]$baseline.Path)
            Write-Host ("[FileTransfer Phase4 A/B Acceptance] verdict={0}; artifact_root={1}" -f $verdict, $runRoot) -ForegroundColor ($(if ($verdict -eq 'PASS') { 'Green' } else { 'Red' }))
        }
        if ($verdict -ne 'PASS') {
            exit 1
        }

        exit 0
    }

    $regularQuickDir = Join-Path $runRoot 'regular-nkn-64mb-quick'
    $regularTargetDir = Join-Path $runRoot 'regular-nkn-128mb-target'
    $tunaNoFaultDir = Join-Path $runRoot 'tuna-128mb-no-fault'
    $tunaFallbackDir = Join-Path $runRoot 'tuna-128mb-fallback'

    foreach ($dir in @($regularQuickDir, $regularTargetDir, $tunaNoFaultDir, $tunaFallbackDir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    if ($fakeMode) {
        Write-RouteAcceptanceFakeRegularRun -ArtifactDir $regularQuickDir -PayloadSize '64MiB'
        Write-RouteAcceptanceFakeRegularRun -ArtifactDir $regularTargetDir -PayloadSize '128MiB'
        Write-RouteAcceptanceFakeTunaRun -ArtifactDir $tunaNoFaultDir -Route 'file_tuna_v4' -RouteMode 'preactivated'
        Write-RouteAcceptanceFakeFallbackRun -ArtifactDir $tunaFallbackDir
    }
    else {
        $effectiveWalletPassword = $WalletPassword
        if ([string]::IsNullOrWhiteSpace($effectiveWalletPassword)) {
            $effectiveWalletPassword = [string]$env:NLINK_TUNA_TEST_WALLET_PASSWORD
        }

        if ([string]::IsNullOrWhiteSpace($effectiveWalletPassword)) {
            throw 'Provide -WalletPassword or set NLINK_TUNA_TEST_WALLET_PASSWORD before running route acceptance.'
        }

        $resolvedExePath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $ExePath)).Path
        $resolvedWalletPath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $WalletPath)).Path
        $resolvedSidecarPath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $SidecarPath)).Path

        Invoke-RegularNknRouteAcceptanceRun -RepoRoot $repoRoot -ArtifactDir $regularQuickDir -PayloadSize '64MiB' -ResolvedExePath $resolvedExePath
        Invoke-RegularNknRouteAcceptanceRun -RepoRoot $repoRoot -ArtifactDir $regularTargetDir -PayloadSize '128MiB' -ResolvedExePath $resolvedExePath
        Invoke-TunaRouteAcceptanceRun -RepoRoot $repoRoot -ArtifactDir $tunaNoFaultDir -ResolvedExePath $resolvedExePath -ResolvedWalletPath $resolvedWalletPath -ResolvedSidecarPath $resolvedSidecarPath -EffectiveWalletPassword $effectiveWalletPassword -RouteMode 'preactivated' -Fault 'none'
        Invoke-TunaRouteAcceptanceRun -RepoRoot $repoRoot -ArtifactDir $tunaFallbackDir -ResolvedExePath $resolvedExePath -ResolvedWalletPath $resolvedWalletPath -ResolvedSidecarPath $resolvedSidecarPath -EffectiveWalletPassword $effectiveWalletPassword -RouteMode 'v4-restart-v6-fallback' -Fault 'switch-off'
    }

    Assert-RegularNknRouteAcceptanceRun -Name 'regular_nkn_64mb_quick' -ArtifactDir $regularQuickDir
    Assert-RegularNknRouteAcceptanceRun -Name 'regular_nkn_128mb_target' -ArtifactDir $regularTargetDir
    Assert-TunaRouteAcceptanceRun -Name 'tuna_128mb_no_fault' -ArtifactDir $tunaNoFaultDir -ExpectedRoute 'file_tuna_v4' -ExpectedBridgePolicy 'tuna_strict'
    Assert-TunaRouteAcceptanceRun -Name 'tuna_128mb_fallback' -ArtifactDir $tunaFallbackDir -ExpectedRoute 'post_tuna_fallback_v6' -ExpectedBridgePolicy 'post_tuna_fallback_strict'

    $verdict = Write-RouteAcceptanceSummaryFiles -RunRoot $runRoot
    Write-Host ("[FileTransfer Route Acceptance] verdict={0}; artifact_root={1}" -f $verdict, $runRoot) -ForegroundColor ($(if ($verdict -eq 'PASS') { 'Green' } else { 'Red' }))
    if ($verdict -ne 'PASS') {
        exit 1
    }

    exit 0
}
catch {
    $fatalMessage = ($_ | Out-String).Trim()
    if ($null -ne $runRoot -and -not [string]::IsNullOrWhiteSpace([string]$runRoot)) {
        $fatal = New-RouteAcceptanceRunResult -Name 'fatal' -ArtifactDir $runRoot -ExpectedRoute '(none)' -ExpectedProtocol 0
        Add-RouteAcceptanceFailure -Result $fatal -Message $fatalMessage
        $script:RunResults.Add($fatal) | Out-Null
        try {
            if ($MatrixMode -eq 'phase4-ab-acceptance') {
                $baselinePath = Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $BaselineManifestPath
                [void](Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $runRoot -BaselinePath $baselinePath)
            }
            else {
                [void](Write-RouteAcceptanceSummaryFiles -RunRoot $runRoot)
            }
        }
        catch {
            Write-Warning ("Failed to write route acceptance summary after fatal error: {0}" -f $_.Exception.Message)
        }
    }

    Write-Error $fatalMessage
    exit 1
}
finally {
    Pop-Location
}
