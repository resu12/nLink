param(
    [string]$ExePath = ".\artifacts\portable\nLink\win-x64\nLink.exe",
    [string]$WalletPath = ".\artifacts\tuna-poc\wallet-test-nkn.json",
    [string]$WalletPassword = "",
    [string]$SidecarPath = ".\artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe",
    [string]$Runtime = "win-x64",
    [string]$ArtifactRoot = "artifacts\filetransfer-route-acceptance",
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
        protocol = '(unknown)'
        runtimeProfile = '(unknown)'
        bridgeRecoveryPolicy = '(unknown)'
        routeConsistencyVerdict = '(missing)'
        completed = $false
        integrityOk = $false
        goodputBytesPerSecond = 0D
        bridgeBulkSendFailureCount = 0
        operatorVerdict = '(missing)'
        operatorAcceptedWithWarnings = $false
        warningKinds = @()
        attemptCount = 1
        retryUsed = $false
        selectedAttempt = 1
        firstFailureReason = ''
        controlledRestartAnalysis = $null
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
        'file_tuna_v4' { @('external_transport_churn') }
        'post_tuna_fallback_v6' { @('external_transport_churn', 'recovered_post_tuna_fallback_bridge_clear') }
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
        if ($result.goodputBytesPerSecond -lt $script:RegularNknGoodputFloorBytesPerSecond) {
            Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN goodput below floor: actual={0}; required>={1}" -f $result.goodputBytesPerSecond, $script:RegularNknGoodputFloorBytesPerSecond)
        }

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
            $setupPhase = Get-JsonPropertyValue -Object $summary -Name 'setupPhase' -DefaultValue $null
            if ($null -eq $setupPhase) {
                Add-RouteAcceptanceFailure -Result $result -Message 'Tuna fallback summary missing setupPhase evidence'
            }
            else {
                $setupRoute = [string](Get-JsonPropertyValue -Object $setupPhase -Name 'route' -DefaultValue '')
                $setupProtocol = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $setupPhase -Name 'protocolVersion' -DefaultValue 0)
                if ($setupRoute -ne 'file_tuna_v4' -or $setupProtocol -ne 4) {
                    Add-RouteAcceptanceFailure -Result $result -Message ("Tuna fallback setup phase mismatch: expected=file_tuna_v4/4; actual={0}/{1}" -f $setupRoute, $setupProtocol)
                }
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

        if ($ExpectedRoute -eq 'file_tuna_v4' -and
            $result.goodputBytesPerSecond -le $script:TunaGoodputFloorBytesPerSecond) {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna goodput must be > {0} B/s, actual {1}" -f $script:TunaGoodputFloorBytesPerSecond, $result.goodputBytesPerSecond)
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

function Get-RouteAcceptanceRouteMetadata {
    param([Parameter(Mandatory = $true)][string]$Route)

    switch ($Route) {
        'regular_nkn_v4_fast' {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'regular_nkn_v4_fast'; Bridge = 'regular_nkn_v4_fast'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 0 }
        }
        'file_tuna_v4' {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'file_tuna_v4_fast'; Bridge = 'tuna_strict'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 1; Fallback = 0; Diagnostic = 0 }
        }
        'file_tuna_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'default_v6'; Bridge = 'tuna_strict'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 1; Fallback = 0; Diagnostic = 0 }
        }
        'post_tuna_fallback_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'default_v6'; Bridge = 'post_tuna_fallback_strict'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 0; Fallback = 1; Diagnostic = 0 }
        }
        'diagnostic_regular_nkn_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'primary_regular_nkn_bulk_v6'; Bridge = 'primary_regular_nkn_quiet'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 1 }
        }
        default {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'regular_nkn_v4_fast'; Bridge = 'regular_nkn_v4_fast'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 0 }
        }
    }
}

function New-RouteAcceptanceFakeLogLine {
    param([Parameter(Mandatory = $true)][string]$Message)

    $timestamp = [datetime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
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
        (New-RouteAcceptanceFakeLogLine -Message ("event=file_transfer_inbound_terminal; role=helper; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; saved_path=(none); integrity_ok={5}" -f $sessionId, $TransferId, $TerminalState, $terminalError, $rawBytes, ($(if ($TerminalState -eq 'Completed') { 1 } else { 0 }))))
        (New-RouteAcceptanceFakeLogLine -Message ("event=file_transfer_outbound_terminal; role=helpee; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; integrity_ok={5}" -f $sessionId, $TransferId, $TerminalState, $terminalError, $rawBytes, ($(if ($TerminalState -eq 'Completed') { 1 } else { 0 }))))
        (New-RouteAcceptanceFakeLogLine -Message ("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=1; send_failures={0}; queue_clears=0; payload_bytes_sent={1}; payload_bytes_per_second=6000000; send_p95_ms=1; configured_concurrency=4; effective_concurrency=4" -f $BridgeBulkSendFailures, $rawBytes))
    )

    $lines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
}

function Invoke-RouteAcceptanceRetainedAnalysis {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "Retained log slice missing: $logPath"
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\FileTransfer-Ops.ps1" -Mode AnalyzeRetained -LogPath $logPath -ArtifactDir $ArtifactDir -TailMinutes 0
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

    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    if (Test-Path -LiteralPath $measuredPath -PathType Leaf) {
        Copy-Item -LiteralPath $measuredPath -Destination $logPath -Force
        return
    }

    $lines = @(Get-Content -LiteralPath $logPath)
    $setupStartIndex = -1
    $startIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($setupStartIndex -lt 0 -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $setupStartIndex = $i
        }

        if ($lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $startIndex = $i
            break
        }
    }

    if ($startIndex -lt 0) {
        return
    }

    if ($setupStartIndex -ge 0 -and $startIndex -gt $setupStartIndex) {
        $lines[$setupStartIndex..($startIndex - 1)] | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8
    }

    $lines[$startIndex..($lines.Count - 1)] | Set-Content -LiteralPath $logPath -Encoding UTF8
}

function Write-RouteAcceptanceFakeRegularRun {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$PayloadSize
    )

    $route = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_ROUTE' -DefaultValue 'regular_nkn_v4_fast'
    $protocol = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_PROTOCOL' -DefaultValue '0')
    $goodput = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_GOODPUT_BPS' -DefaultValue '8388608')
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
        duration_ms = [Math]::Max(1, [int][Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D))
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
    $emitFallbackRecoveredBridgeWarning = $RouteMode -eq 'v4-restart-v6-fallback' -and (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RECOVERED_BRIDGE_WARNING')

    Write-RouteAcceptanceFakeRetainedLog -ArtifactDir $ArtifactDir -TransferId ('fake-tuna-{0}' -f $RouteMode) -Route $effectiveRoute -ProtocolOverride $protocolOverride -TerminalState $terminalState -BridgeBulkSendFailures 0
    if ($emitNoFaultExternalWarning) {
        Add-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8 -Value (New-RouteAcceptanceFakeLogLine -Message 'event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1')
    }

    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
        Add-Content -LiteralPath $logPath -Encoding UTF8 -Value (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed')
        if ($emitFallbackRecoveredBridgeWarning) {
            @(
                (New-RouteAcceptanceFakeLogLine -Message ('event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id=fake-tuna-{0}; session_id=sess_fake; reason=transport_recovered_unproven; target_transport=regular_nkn' -f $RouteMode))
                (New-RouteAcceptanceFakeLogLine -Message 'event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=22; payload_bytes_sent=847447; payload_bytes_per_second=423724; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4')
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
        faultMode = if ($RouteMode -eq 'post-fallback' -or $RouteMode -eq 'v4-restart-v6-fallback') { 'switch-off' } else { 'none' }
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
        setupPhase = if ($RouteMode -eq 'v4-restart-v6-fallback') {
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
            fallbackEpochStarted = $RouteMode -eq 'post-fallback' -or $RouteMode -eq 'v4-restart-v6-fallback' -or $forceFallbackEvidence
            fallbackEpochRecovered = $RouteMode -eq 'post-fallback' -or $RouteMode -eq 'v4-restart-v6-fallback' -or $forceFallbackEvidence
            fallbackEpochWaiting = $false
        }
        controlledRestartAnalysis = if ($RouteMode -eq 'v4-restart-v6-fallback') {
            [ordered]@{
                setupVerdict = 'INVALID_SETUP'
                measuredRouteVerdict = 'pass'
                measuredOperatorVerdict = if ($emitFallbackRecoveredBridgeWarning) { 'WARN_EXTERNAL_TRANSPORT' } else { 'PASS' }
                setupCleanupWarningCount = 0
                fallbackBridgeRecoveryWarningCount = if ($emitFallbackRecoveredBridgeWarning) { 1 } else { 0 }
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
    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
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
    if ($uniqueRoutes.Count -gt 0 -and -not ($uniqueRoutes -contains 'post_tuna_fallback_v6')) {
        return $false
    }

    if ($uniqueRoutes.Count -eq 0) {
        return $true
    }

    return $FailureMessage.IndexOf('progress timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $FailureMessage.IndexOf('no useful data progress', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Write-RouteAcceptanceFakeFallbackRun {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $attempts = New-Object System.Collections.Generic.List[object]
    $maxAttempts = [Math]::Max(1, $FallbackMaxAttempts)
    $retryAttempt1 = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RETRYABLE_ATTEMPT1'
    $alwaysRetryableFailure = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RETRYABLE_ALWAYS'

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

        Write-RouteAcceptanceFakeTunaRun -ArtifactDir $attemptDir -Route 'post_tuna_fallback_v6' -RouteMode 'v4-restart-v6-fallback'
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

    & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    return $LASTEXITCODE
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
        [Parameter(Mandatory = $true)][string]$Fault
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
            $exitCode = Invoke-RouteAcceptanceChildScriptNoThrow -ScriptPath $scriptPath -Arguments @(
                '-ExePath', $ResolvedExePath,
                '-WalletPath', $ResolvedWalletPath,
                '-WalletPassword', $EffectiveWalletPassword,
                '-SidecarPath', $ResolvedSidecarPath,
                '-RouteMode', $RouteMode,
                '-Fault', $Fault,
                '-Direction', 'helpee-to-helper',
                '-PayloadSize', '128MiB',
                '-ArtifactDir', $attemptDir,
                '-TimeoutSeconds', ([string]$TimeoutSeconds),
                '-ProgressTimeoutSeconds', ([string]$ProgressTimeoutSeconds)
            )

            Select-RouteAcceptanceMeasuredFallbackLogSlice -ArtifactDir $attemptDir
            if ($exitCode -ne 0) {
                $failureMessage = "Tuna GUI {0} attempt {1} failed with exit code {2}." -f $RouteMode, $attempt, $exitCode
            }
            else {
                try {
                    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $attemptDir
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

    Invoke-RouteAcceptanceChildScript -ScriptPath $scriptPath -Description ("Tuna GUI {0}" -f $RouteMode) -Arguments @(
        '-ExePath', $ResolvedExePath,
        '-WalletPath', $ResolvedWalletPath,
        '-WalletPassword', $EffectiveWalletPassword,
        '-SidecarPath', $ResolvedSidecarPath,
        '-RouteMode', $RouteMode,
        '-Fault', $Fault,
        '-Direction', 'helpee-to-helper',
        '-PayloadSize', '128MiB',
        '-ArtifactDir', $ArtifactDir,
        '-TimeoutSeconds', ([string]$TimeoutSeconds),
        '-ProgressTimeoutSeconds', ([string]$ProgressTimeoutSeconds)
    )

    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        Select-RouteAcceptanceMeasuredFallbackLogSlice -ArtifactDir $ArtifactDir
    }

    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir
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
    $regularQuickDir = Join-Path $runRoot 'regular-nkn-64mb-quick'
    $regularTargetDir = Join-Path $runRoot 'regular-nkn-128mb-target'
    $tunaNoFaultDir = Join-Path $runRoot 'tuna-128mb-no-fault'
    $tunaFallbackDir = Join-Path $runRoot 'tuna-128mb-fallback'

    foreach ($dir in @($regularQuickDir, $regularTargetDir, $tunaNoFaultDir, $tunaFallbackDir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $fakeMode = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_GUI'
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
            [void](Write-RouteAcceptanceSummaryFiles -RunRoot $runRoot)
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
