param(
    [string]$ExePath = "",
    [int]$DurationSeconds = 30,
    [switch]$Build,
    [int]$TimeoutSeconds = 180,
    [string]$StrongBaselineArtifactDir = "",
    [string]$SafeBaselineArtifactDir = "",
    [switch]$SkipBehaviorFirstGate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Stop-NLinkProcesses {
    param([string]$ResolvedExePath = "")

    $targets = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -like 'nLink*' -or $_.ProcessName -eq 'dotnet'
    }

    if ($targets) {
        Write-Host "Stopping lingering nLink/dotnet processes before NKN soak..." -ForegroundColor DarkYellow
        $targets | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 750
    }

    Stop-BridgeNodeProcesses -ResolvedExePath $ResolvedExePath
}

function Stop-BridgeNodeProcesses {
    param([string]$ResolvedExePath = "")

    if ([string]::IsNullOrWhiteSpace($ResolvedExePath)) {
        return
    }

    $appDir = Split-Path -Parent $ResolvedExePath
    if ([string]::IsNullOrWhiteSpace($appDir)) {
        return
    }

    $expectedNodePath = Join-Path $appDir 'bridge\win-x64\node.exe'
    $expectedScriptPath = Join-Path $appDir 'bridge\win-x64\index.js'
    if (-not (Test-Path $expectedNodePath)) {
        return
    }

    $expectedNodePath = [System.IO.Path]::GetFullPath($expectedNodePath)
    $expectedScriptPath = [System.IO.Path]::GetFullPath($expectedScriptPath)

    $targets = @(
        Get-CimInstance Win32_Process -Filter "Name = 'node.exe' OR Name = 'node'" -ErrorAction SilentlyContinue |
            Where-Object {
                $exeMatches = $false
                $cmdMatches = $false

                if ($_.ExecutablePath) {
                    try {
                        $exeMatches = [string]::Equals(
                            [System.IO.Path]::GetFullPath($_.ExecutablePath),
                            $expectedNodePath,
                            [System.StringComparison]::OrdinalIgnoreCase)
                    }
                    catch {}
                }

                if ($_.CommandLine) {
                    try {
                        $cmdMatches = $_.CommandLine.IndexOf($expectedScriptPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                    }
                    catch {}
                }

                return $exeMatches -or $cmdMatches
            }
    )

    if ($targets.Count -eq 0) {
        return
    }

    Write-Host "Stopping lingering bridge node.exe processes for selected app..." -ForegroundColor DarkYellow
    foreach ($target in $targets) {
        try { Stop-Process -Id $target.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
    }
    Start-Sleep -Milliseconds 500
}

function Resolve-RepoRoot {
    $current = Split-Path -Parent $PSScriptRoot
    while ($null -ne $current -and $current -ne "") {
        if ((Test-Path (Join-Path $current "nLink.sln")) -and
            (Test-Path (Join-Path $current "VERSION"))) {
            return $current
        }

        $parent = Split-Path -Parent $current
        if ($parent -eq $current) {
            break
        }

        $current = $parent
    }

    throw "Could not locate repo root from $PSScriptRoot."
}

function Resolve-ExePath {
    param(
        [string]$RepoRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidate = $RequestedPath
        if (-not [System.IO.Path]::IsPathRooted($candidate)) {
            $candidate = Join-Path $RepoRoot $candidate
        }

        if (Test-Path $candidate) {
            return $candidate
        }

        $alternateCandidate = $candidate -replace '(?i)(^|\\)link\.exe$', '${1}nLink.exe'
        if ($alternateCandidate -ne $candidate -and (Test-Path $alternateCandidate)) {
            return $alternateCandidate
        }

        return $candidate
    }

    $candidates = @(
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\nlink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\nnLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\Link.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\win-x64\nnLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\win-x64\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\win-x64\Link.exe"),
        (Join-Path $RepoRoot "artifacts\portable\nLink\win-x64\nnLink.exe"),
        (Join-Path $RepoRoot "artifacts\portable\nLink\win-x64\nLink.exe"),
        (Join-Path $RepoRoot "artifacts\portable\nLink\win-x64\Link.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\nnLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\Link.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\win-x64\nnLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\win-x64\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\win-x64\Link.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $candidates[0]
}

function Build-LocalExeIfNeeded {
    param(
        [string]$RepoRoot,
        [string]$ResolvedExePath,
        [bool]$ForceBuild
    )

    if (-not $ForceBuild -and (Test-Path $ResolvedExePath)) {
        return
    }

    Write-Host "Building nLink executable for NKN soak..." -ForegroundColor Cyan
    dotnet build (Join-Path $RepoRoot "src\nLink.App\nLink.App.csproj") | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path $ResolvedExePath)) {
        $alternateResolvedExePath = $ResolvedExePath -replace '(?i)(^|\\)link\.exe$', '${1}nLink.exe'
        if ($alternateResolvedExePath -ne $ResolvedExePath -and (Test-Path $alternateResolvedExePath)) {
            return
        }

        throw "Build completed but executable was not found at $ResolvedExePath."
    }
}

function Test-IsPathWithinRoot {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$CandidatePath
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd('\') + '\'
    $normalizedCandidate = [System.IO.Path]::GetFullPath($CandidatePath)
    return $normalizedCandidate.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-BridgeRuntimePresent {
    param([Parameter(Mandatory = $true)][string]$AppDir)

    $bridgeDir = Join-Path $AppDir 'bridge\win-x64'
    return (Test-Path (Join-Path $bridgeDir 'index.js')) -and
           (Test-Path (Join-Path $bridgeDir 'node.exe')) -and
           (Test-Path (Join-Path $bridgeDir 'bridge-manifest.json'))
}

function Ensure-NknBridgeRuntimeForExe {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath
    )

    $appDir = Split-Path -Parent $ResolvedExePath
    if ([string]::IsNullOrWhiteSpace($appDir) -or -not (Test-Path $appDir)) {
        throw "Could not resolve app directory for executable: $ResolvedExePath"
    }

    $repoLocalBuild = Test-IsPathWithinRoot -RootPath $RepoRoot -CandidatePath $appDir
    if (-not $repoLocalBuild) {
        if (Test-BridgeRuntimePresent -AppDir $appDir) {
            return
        }

        throw "Selected executable is missing bridge\\win-x64 (index.js, node.exe, bridge-manifest.json): $ResolvedExePath"
    }

    $bridgeBundleDir = Join-Path $RepoRoot 'artifacts\bridge\win-x64'
    $bridgeBundleScript = Join-Path $RepoRoot 'installer\Build-BridgeBundle.ps1'
    $bridgeSourceScript = Join-Path $RepoRoot 'tools\nkn-bridge\index.js'
    $artifactIndexPath = Join-Path $bridgeBundleDir 'index.js'
    $artifactNodePath = Join-Path $bridgeBundleDir 'node.exe'
    $artifactManifestPath = Join-Path $bridgeBundleDir 'bridge-manifest.json'

    $needsBundleBuild =
        -not (Test-Path $artifactIndexPath) -or
        -not (Test-Path $artifactNodePath) -or
        -not (Test-Path $artifactManifestPath)

    if (-not $needsBundleBuild -and (Test-Path $bridgeSourceScript)) {
        $sourceWriteUtc = (Get-Item $bridgeSourceScript).LastWriteTimeUtc
        $artifactWriteUtc = (Get-Item $artifactIndexPath).LastWriteTimeUtc
        if ($artifactWriteUtc -lt $sourceWriteUtc) {
            $needsBundleBuild = $true
        }
    }

    if (-not (Test-Path $bridgeBundleScript)) {
        throw "Bridge bundle script not found: $bridgeBundleScript"
    }

    if ($needsBundleBuild) {
        Write-Host "Building bridge bundle for repo-local soak target..." -ForegroundColor Cyan
        & powershell -ExecutionPolicy Bypass -File $bridgeBundleScript -Runtime 'win-x64'
        if ($LASTEXITCODE -ne 0) {
            throw "Build-BridgeBundle.ps1 failed with exit code $LASTEXITCODE."
        }
    }

    $targetBridgeDir = Join-Path $appDir 'bridge\win-x64'
    New-Item -ItemType Directory -Force -Path $targetBridgeDir | Out-Null
    Copy-Item -Path (Join-Path $bridgeBundleDir '*') -Destination $targetBridgeDir -Recurse -Force

    if (-not (Test-BridgeRuntimePresent -AppDir $appDir)) {
        throw "Failed to stage bridge runtime into $targetBridgeDir"
    }

    Write-Host "Staged bridge runtime into repo-local soak target: $targetBridgeDir" -ForegroundColor DarkCyan
}

function Get-PercentileValue {
    param(
        [Parameter(Mandatory = $true)][int[]]$Values,
        [Parameter(Mandatory = $true)][double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $rank = [Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    if ($rank -lt 0) { $rank = 0 }
    if ($rank -ge $sorted.Count) { $rank = $sorted.Count - 1 }
    return [int]$sorted[$rank]
}

function Get-StructuredLogFieldPairs {
    param([string]$Line)

    $pairs = New-Object System.Collections.Generic.List[object]
    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $pairs
    }

    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($Line, '(?<key>[A-Za-z0-9_\[\]-]+)=(?<value>[^;]*)')) {
        $pairs.Add([pscustomobject]@{
            Key = [string]$match.Groups['key'].Value
            Value = [string]$match.Groups['value'].Value.Trim()
        })
    }

    return $pairs
}

function Get-StructuredLogFieldValue {
    param(
        [System.Collections.IList]$Pairs,
        [string]$Key
    )

    if ($null -eq $Pairs -or [string]::IsNullOrWhiteSpace($Key)) {
        return $null
    }

    foreach ($pair in $Pairs) {
        if ([string]::Equals([string]$pair.Key, $Key, [System.StringComparison]::OrdinalIgnoreCase)) {
            return [string]$pair.Value
        }
    }

    return $null
}

function Get-StructuredLogFieldValueAfter {
    param(
        [System.Collections.IList]$Pairs,
        [string]$AfterKey,
        [int]$Offset = 1
    )

    if ($null -eq $Pairs -or [string]::IsNullOrWhiteSpace($AfterKey) -or $Offset -lt 1) {
        return $null
    }

    for ($i = 0; $i -lt $Pairs.Count; $i++) {
        if ([string]::Equals([string]$Pairs[$i].Key, $AfterKey, [System.StringComparison]::OrdinalIgnoreCase)) {
            $targetIndex = $i + $Offset
            if ($targetIndex -lt $Pairs.Count) {
                return [string]$Pairs[$targetIndex].Value
            }

            break
        }
    }

    return $null
}

function Get-StructuredLogIntField {
    param(
        [System.Collections.IList]$Pairs,
        [string]$Key,
        [int]$DefaultValue = -1,
        [string]$FallbackAfterKey = '',
        [int]$FallbackOffset = 1
    )

    $value = Get-StructuredLogFieldValue -Pairs $Pairs -Key $Key
    if ([string]::IsNullOrWhiteSpace($value) -and -not [string]::IsNullOrWhiteSpace($FallbackAfterKey)) {
        $value = Get-StructuredLogFieldValueAfter -Pairs $Pairs -AfterKey $FallbackAfterKey -Offset $FallbackOffset
    }

    if (-not [string]::IsNullOrWhiteSpace($value) -and $value -match '^-?[0-9]+$') {
        return [int]$value
    }

    return $DefaultValue
}

function Get-StructuredLogFloatField {
    param(
        [System.Collections.IList]$Pairs,
        [string]$Key,
        [double]$DefaultValue = -1,
        [string]$FallbackAfterKey = '',
        [int]$FallbackOffset = 1
    )

    $value = Get-StructuredLogFieldValue -Pairs $Pairs -Key $Key
    if ([string]::IsNullOrWhiteSpace($value) -and -not [string]::IsNullOrWhiteSpace($FallbackAfterKey)) {
        $value = Get-StructuredLogFieldValueAfter -Pairs $Pairs -AfterKey $FallbackAfterKey -Offset $FallbackOffset
    }

    if (-not [string]::IsNullOrWhiteSpace($value)) {
        $parsed = 0.0
        if ([double]::TryParse($value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
            return $parsed
        }
    }

    return $DefaultValue
}

function Get-StructuredLogStringField {
    param(
        [System.Collections.IList]$Pairs,
        [string]$Key,
        [string]$DefaultValue = '',
        [string]$FallbackAfterKey = '',
        [int]$FallbackOffset = 1
    )

    $value = Get-StructuredLogFieldValue -Pairs $Pairs -Key $Key
    if ([string]::IsNullOrWhiteSpace($value) -and -not [string]::IsNullOrWhiteSpace($FallbackAfterKey)) {
        $value = Get-StructuredLogFieldValueAfter -Pairs $Pairs -AfterKey $FallbackAfterKey -Offset $FallbackOffset
    }

    if ($null -eq $value) {
        return $DefaultValue
    }

    return [string]$value
}

function Read-KeyValueSummaryFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    if (-not (Test-Path $Path)) {
        return $values
    }

    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -le 0) {
            continue
        }

        $key = $line.Substring(0, $separatorIndex).Trim()
        if ([string]::IsNullOrWhiteSpace($key)) {
            continue
        }

        $values[$key] = $line.Substring($separatorIndex + 1).Trim()
    }

    return $values
}

function Get-SummaryNumberValue {
    param(
        [hashtable]$Values,
        [string[]]$Keys
    )

    if ($null -eq $Values -or $null -eq $Keys) {
        return $null
    }

    foreach ($key in $Keys) {
        if ([string]::IsNullOrWhiteSpace($key) -or -not $Values.ContainsKey($key)) {
            continue
        }

        $rawValue = [string]$Values[$key]
        if ([string]::IsNullOrWhiteSpace($rawValue) -or
            [string]::Equals($rawValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $parsed = 0.0
        if ([double]::TryParse($rawValue, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
            return $parsed
        }
    }

    return $null
}

function Get-CurrentSoakComparisonMetrics {
    param([Parameter(Mandatory = $true)]$Summary)

    $latencyProxyName = 'helper_apply_ms_avg'
    $latencyProxyValue = if ($Summary.HelperApplyAvgMs -ge 0) { [double]$Summary.HelperApplyAvgMs } else { $null }
    if ($null -eq $latencyProxyValue -and $Summary.LatestHelperBaselineCaptureToRenderMs -ge 0) {
        $latencyProxyName = 'baseline_capture_to_render_ms'
        $latencyProxyValue = [double]$Summary.LatestHelperBaselineCaptureToRenderMs
    }

    return @{
        artifact_dir = ''
        visible_apply_ratio = if ($Summary.LatestHelperVisibleApplyRatio -ge 0) { [double]$Summary.LatestHelperVisibleApplyRatio } else { $null }
        helper_apply_ms_avg = if ($Summary.HelperApplyAvgMs -ge 0) { [double]$Summary.HelperApplyAvgMs } else { $null }
        helper_apply_ms_p95 = if ($Summary.HelperApplyP95Ms -ge 0) { [double]$Summary.HelperApplyP95Ms } else { $null }
        baseline_capture_to_render_ms = if ($Summary.LatestHelperBaselineCaptureToRenderMs -ge 0) { [double]$Summary.LatestHelperBaselineCaptureToRenderMs } else { $null }
        reassembler_loss_count = if ($Summary.LatestHelperReassemblerLossCount -ge 0) { [double]$Summary.LatestHelperReassemblerLossCount } else { $null }
        gap_count = if ($Summary.LatestHelperGapCount -ge 0) { [double]$Summary.LatestHelperGapCount } else { $null }
        resync_count = if ($Summary.LatestHelperResyncCount -ge 0) { [double]$Summary.LatestHelperResyncCount } else { $null }
        recovery_runway_overflow_reject_count = if ($Summary.LatestHelperRecoveryRunwayOverflowRejectCount -ge 0) { [double]$Summary.LatestHelperRecoveryRunwayOverflowRejectCount } else { $null }
        actionable_late_fragment_count = if ($Summary.LatestHelperActionableLateFragmentCount -ge 0) { [double]$Summary.LatestHelperActionableLateFragmentCount } else { $null }
        latency_proxy_name = $latencyProxyName
        latency_proxy_ms = $latencyProxyValue
    }
}

function Get-BaselineSoakComparisonMetrics {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    if (-not (Test-Path $ArtifactDir)) {
        return $null
    }

    $qualitySummary = Read-KeyValueSummaryFile -Path (Join-Path $ArtifactDir 'helper-quality-summary.txt')
    $frameLossSummary = Read-KeyValueSummaryFile -Path (Join-Path $ArtifactDir 'helper-frame-loss-epoch.txt')
    $rootCauseSummary = Read-KeyValueSummaryFile -Path (Join-Path $ArtifactDir 'helper-reassembler-root-cause-summary.txt')

    $latencyProxyName = $null
    $latencyProxyValue = Get-SummaryNumberValue -Values $qualitySummary -Keys @('helper_apply_ms_avg')
    if ($null -ne $latencyProxyValue) {
        $latencyProxyName = 'helper_apply_ms_avg'
    }
    else {
        $latencyProxyValue = Get-SummaryNumberValue -Values $qualitySummary -Keys @('avg_capture_to_render_ms', 'baseline_capture_to_render_ms')
        if ($null -ne $latencyProxyValue) {
            $latencyProxyName = if ($qualitySummary.ContainsKey('avg_capture_to_render_ms')) {
                'avg_capture_to_render_ms'
            }
            else {
                'baseline_capture_to_render_ms'
            }
        }
    }

    $reassemblerLossCount = Get-SummaryNumberValue -Values $qualitySummary -Keys @('reassembler_loss_count')
    if ($null -eq $reassemblerLossCount) {
        $reassemblerLossCount = Get-SummaryNumberValue -Values $frameLossSummary -Keys @('reassembler_loss_count')
    }
    if ($null -eq $reassemblerLossCount) {
        $reassemblerLossCount = Get-SummaryNumberValue -Values $rootCauseSummary -Keys @('reassembler_loss_count')
    }

    $actionableLateFragmentCount = Get-SummaryNumberValue -Values $qualitySummary -Keys @('actionable_late_fragment_count')
    if ($null -eq $actionableLateFragmentCount) {
        $actionableLateFragmentCount = Get-SummaryNumberValue -Values $rootCauseSummary -Keys @('actionable_late_fragment_count')
    }

    return @{
        artifact_dir = $ArtifactDir
        visible_apply_ratio = Get-SummaryNumberValue -Values $qualitySummary -Keys @('visible_apply_ratio')
        helper_apply_ms_avg = Get-SummaryNumberValue -Values $qualitySummary -Keys @('helper_apply_ms_avg')
        helper_apply_ms_p95 = Get-SummaryNumberValue -Values $qualitySummary -Keys @('helper_apply_ms_p95')
        reassembler_loss_count = $reassemblerLossCount
        gap_count = Get-SummaryNumberValue -Values $qualitySummary -Keys @('gap_count')
        resync_count = Get-SummaryNumberValue -Values $qualitySummary -Keys @('resync_count')
        recovery_runway_overflow_reject_count = Get-SummaryNumberValue -Values $qualitySummary -Keys @('recovery_runway_overflow_reject_count')
        actionable_late_fragment_count = $actionableLateFragmentCount
        latency_proxy_name = $latencyProxyName
        latency_proxy_ms = $latencyProxyValue
    }
}

function New-BaselineComparisonReport {
    param(
        [string]$Label,
        $CurrentMetrics,
        $BaselineMetrics
    )

    $lines = New-Object System.Collections.Generic.List[string]
    if ($null -eq $BaselineMetrics) {
        $lines.Add(("{0}_baseline_available=0" -f $Label))
        return $lines
    }

    $lines.Add(("{0}_baseline_available=1" -f $Label))
    $lines.Add(("{0}_baseline_artifact_dir={1}" -f $Label, $BaselineMetrics.artifact_dir))
    foreach ($metricName in @(
            'visible_apply_ratio',
            'helper_apply_ms_avg',
            'helper_apply_ms_p95',
            'reassembler_loss_count',
            'gap_count',
            'resync_count',
            'recovery_runway_overflow_reject_count',
            'actionable_late_fragment_count')) {
        $currentValue = $CurrentMetrics[$metricName]
        $baselineValue = $BaselineMetrics[$metricName]
        $lines.Add(("{0}_{1}_current={2}" -f $Label, $metricName, $(if ($null -ne $currentValue) { $currentValue.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })))
        $lines.Add(("{0}_{1}_baseline={2}" -f $Label, $metricName, $(if ($null -ne $baselineValue) { $baselineValue.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })))
        if ($null -ne $currentValue -and $null -ne $baselineValue) {
            $delta = [math]::Round(($currentValue - $baselineValue), 3)
            $lines.Add(("{0}_{1}_delta={2}" -f $Label, $metricName, $delta.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
        }
        else {
            $lines.Add(("{0}_{1}_delta=(none)" -f $Label, $metricName))
        }
    }

    $baselineLatencyMetricName = if ([string]::IsNullOrWhiteSpace($BaselineMetrics.latency_proxy_name)) { '(none)' } else { $BaselineMetrics.latency_proxy_name }
    $currentLatencyMetricKey = switch ($BaselineMetrics.latency_proxy_name) {
        'helper_apply_ms_avg' { 'helper_apply_ms_avg' }
        'avg_capture_to_render_ms' { 'baseline_capture_to_render_ms' }
        'baseline_capture_to_render_ms' { 'baseline_capture_to_render_ms' }
        default { 'latency_proxy_ms' }
    }
    $currentComparableLatency = if ([string]::Equals($currentLatencyMetricKey, 'latency_proxy_ms', [System.StringComparison]::OrdinalIgnoreCase)) {
        $CurrentMetrics.latency_proxy_ms
    }
    else {
        $CurrentMetrics[$currentLatencyMetricKey]
    }

    $lines.Add(("{0}_latency_proxy_name={1}" -f $Label, $baselineLatencyMetricName))
    $lines.Add(("{0}_latency_proxy_current_metric={1}" -f $Label, $currentLatencyMetricKey))
    $lines.Add(("{0}_latency_proxy_ms_baseline={1}" -f $Label, $(if ($null -ne $BaselineMetrics.latency_proxy_ms) { $BaselineMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })))
    $lines.Add(("{0}_latency_proxy_ms_current={1}" -f $Label, $(if ($null -ne $currentComparableLatency) { $currentComparableLatency.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })))
    if ($null -ne $currentComparableLatency -and $null -ne $BaselineMetrics.latency_proxy_ms) {
        $latencyDelta = [math]::Round(($currentComparableLatency - $BaselineMetrics.latency_proxy_ms), 3)
        $lines.Add(("{0}_latency_proxy_ms_delta={1}" -f $Label, $latencyDelta.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
    }
    else {
        $lines.Add(("{0}_latency_proxy_ms_delta=(none)" -f $Label))
    }

    return $lines
}

function Get-TopNamedCount {
    param(
        [Parameter(Mandatory = $true)][object[]]$Candidates,
        [string]$DefaultValue = 'none'
    )

    if ($Candidates.Count -eq 0) {
        return $DefaultValue
    }

    $best = $Candidates |
        Sort-Object -Property @{ Expression = { [int64]$_.Count }; Descending = $true }, @{ Expression = { [string]$_.Name }; Descending = $false } |
        Select-Object -First 1

    if ($null -eq $best -or [int64]$best.Count -le 0) {
        return $DefaultValue
    }

    return [string]$best.Name
}

function Get-SoakSummaryFromLog {
    $logPath = Join-Path $env:LOCALAPPDATA 'nLink\logs\nlink.log'
    if (-not (Test-Path $logPath)) {
        throw "App log not found after NKN soak: $logPath"
    }

    $captureToSend = New-Object System.Collections.Generic.List[int]
    $helperApply = New-Object System.Collections.Generic.List[int]
    $helperStaleDrops = 0
    $receiverSupersededFrames = 0
    $persistentSummaries = 0
    $sinkWriterSummaries = 0
    $normalModeSummaries = 0
    $reducedModeSummaries = 0
    $catchUpModeSummaries = 0
    $bridgeHealthAdvisorySummaries = 0
    $bridgeHealthActionableSummaries = 0
    $latestFramesQueued = -1
    $latestFramesDeferredToSendSlot = -1
    $latestFramesReplacedBeforeSendSlot = -1
    $latestFramesDroppedByQueueEvict = -1
    $latestSendSlotEmptyCount = -1
    $latestSlotCoalescingActive = -1
    $latestRawFramesDeferredToEncodeSlot = -1
    $latestRawFramesReplacedBeforeEncodeSlot = -1
    $latestRawEncodeSlotEmptyCount = -1
    $latestRawSlotCoalescingActive = -1
    $latestPromotionCaptureToSendBudgetMs = -1
    $latestSourceSupersededPendingFrames = -1
    $latestAvgFragmentsPerFrame = -1.0
    $latestAvgPayloadsPerFrame = -1.0
    $latestBatchPayloadCount = -1
    $latestLegacyPayloadCount = -1
    $latestOrdinaryNonKeyBatchedPayloadCount = -1
    $latestOrdinaryNonKeyLegacyPayloadCount = -1
    $latestKeyframeRecoveryBatchedPayloadCount = -1
    $latestEmittedDisplayableFrames = -1
    $latestEmittedNonDisplayableUnits = -1
    $latestEmittedIdrFrames = -1
    $latestEmittedPFrames = -1
    $latestDroppedBFrames = -1
    $latestDroppedMultiPictureUnits = -1
    $latestDisplayableFrameRatio = -1.0
    $latestIdrFrameRatio = -1.0
    $latestAverageEncodedFrameBytes = -1.0
    $latestTransportIpOnlyMode = -1
    $latestLastAccessUnitKind = ''
    $latestLowDelayConfigApplied = ''
    $latestHelperFramesCompleted = -1
    $latestHelperFramesEnqueuedForDecode = -1
    $latestHelperFramesDroppedBeforeDecode = -1
    $latestHelperFramesDecoded = -1
    $latestHelperFramesDroppedAfterDecode = -1
    $latestHelperFramesApplied = -1
    $latestHelperNeedMoreInputCount = -1
    $latestHelperCompletedWithoutPictureCount = -1
    $latestHelperDecodeDurationMs = -1.0
    $latestHelperApplyIntervalMs = -1.0
    $latestHelperMaxPendingEncodedDepth = -1
    $latestHelperMaxPendingDecodedDepth = -1
    $latestHelperAvgEnqueueToDecodeStartMs = -1.0
    $latestHelperAvgEnqueueToDropMs = -1.0
    $latestHelperDecodeWorkerDropQueueOverflowCount = -1
    $latestHelperDecodeWorkerDropAgeBudgetCount = -1
    $latestHelperDecodeWorkerDropGenerationCount = -1
    $latestHelperDecodeWorkerDropStoppedCount = -1
    $latestHelperReassemblerLossCount = -1
    $latestHelperEnqueueRejectCount = -1
    $latestHelperWaitingForRecoveryKeyframeRejectCount = -1
    $latestHelperRecoveryWaitRejectBeforeRunwayCount = -1
    $latestHelperRecoveryRunwayOverflowRejectCount = -1
    $latestHelperSuppressedEmitDuringRecoveryWaitCount = -1
    $latestHelperStaleSupersededRecoverySuppressedCount = -1
    $latestHelperSoftStaleCleanupCount = -1
    $latestHelperBlockedByReservedRecoveryFrameRejectCount = -1
    $latestHelperOlderEpochIgnoredDuringRecoveryLockCount = -1
    $latestHelperNewerEpochNonKeyIgnoredDuringLockCount = -1
    $latestHelperDeferredPostRecoveryCandidateReplaceCount = -1
    $latestHelperDecodeWorkerDropCount = -1
    $latestHelperPostDecodeDropCount = -1
    $latestHelperDecodeQueueOverflowCount = -1
    $latestHelperDecodeAgeBudgetCount = -1
    $latestHelperDecodeGenerationChangedCount = -1
    $latestHelperDecodeStoppedCount = -1
    $latestHelperDecodedApplyQueueOverflowCount = -1
    $latestHelperDecodedFrameReplacedBeforeApplyCount = -1
    $latestHelperStaleDroppedAfterDecodeCount = -1
    $latestHelperDroppedWaitingForRecoveryKeyframeCount = -1
    $latestHelperGapNonKeyPrunedCount = -1
    $latestHelperFutureTailQuarantinedDuringGapCount = -1
    $latestHelperFutureTailQuarantinedAfterGapCount = -1
    $latestHelperPreCandidateGapTailRejectedCount = -1
    $latestHelperRecoveryCandidatePresentCount = -1
    $latestHelperVisibleRecoveryFloorFrameId = -1
    $latestHelperStableVisibleHeadFrameId = -1
    $latestHelperAppliedHeadFrameId = -1
    $latestHelperOrderedEmitHeadFrameId = -1
    $latestHelperWinningRecoveryFrameId = -1
    $latestHelperVisibleHeadFrameId = -1
    $latestHelperSupersededRecoveryTailCleanupCount = -1
    $latestHelperLateSameEpochAfterHeadAdvancedDropCount = -1
    $latestHelperStaleRunwayWindowAbortCount = -1
    $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount = -1
    $latestHelperRunwayFollowersEmittedWithinActionableWindowCount = -1
    $latestHelperRecoveryOwnerReplacedCount = -1
    $latestHelperOlderEpochCleanupAfterEpochAdvanceCount = -1
    $latestHelperSteadyVisibleProgressActive = 0
    $latestHelperSteadyVisibleProgressActivationFrameId = -1
    $latestHelperFramesAppliedSinceLastGap = -1
    $latestRemoteHelperFactHealthyActive = 0
    $latestRemoteHelperFactHealthySource = ''
    $latestRemoteHelperFactProofFrameId = -1
    $latestRemoteHelperFactLastMessageAgeMs = -1
    $latestRemoteHelperFactHealthyClearCount = -1
    $latestRemoteHelperFactHealthyClearReason = ''
    $latestHelperLastSentStableVisibleHeadFrameId = -1
    $latestHelperPressureSendBypassedForVisibleProgressCount = -1
    $latestHelperProofKeepaliveSendCount = -1
    $latestHelperProofKeepaliveTimerDrivenSendCount = -1
    $latestHelperProofKeepaliveLastHeadFrameId = -1
    $latestHelperProofKeepaliveLastSendAgeMs = -1
    $latestHelperFirstVisibleApplyToSenderFactSendMs = -1
    $latestHelperSteadyVisibleProgressClearedCount = -1
    $latestHelperSteadyVisibleProgressClearedReason = ''
    $latestHelperLateFragmentAfterAppliedHeadCount = -1
    $latestHelperLateFragmentAfterOrderedHeadCount = -1
    $latestHelperLateFragmentAfterStableVisibleHeadCount = -1
    $latestHelperLateFragmentAfterVisibleRecoveryCount = -1
    $latestHelperPreCandidateGapTailEmittedToViewerCount = -1
    $latestHelperHighFrameAgeSuppressedDueToVisibleProgressCount = -1
    $latestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount = -1
    $latestHelperActionableHighFrameAgeCount = -1
    $latestHelperActionableLateFragmentCount = -1
    $latestRecoveryBurstActive = 0
    $latestRecoveryBurstPhase = 'idle'
    $latestRecoveryBurstStreamEpoch = -1
    $latestRecoveryOwnerFrameId = -1
    $latestRecoveryProtectedFollowerCount = -1
    $latestRecoveryGapCount = -1
    $latestRecoveryGapToKeyframeRequestMs = -1
    $latestRecoveryKeyframeRequestToOwnerEmitMs = -1
    $latestRecoveryOwnerEmitToAckMs = -1
    $latestRecoveryOwnerAckFrameId = -1
    $latestRecoveryAckSource = ''
    $latestRecoveryOwnerEmitToFirstVisibleApplyMs = -1
    $latestRecoveryBurstControlFallbackCount = -1
    $latestRecoveryBurstTimeoutCount = -1
    $latestRecoveryBurstCompletedCount = -1
    $latestRecoveryBurstRestartSuppressedCount = -1
    $latestRecoveryBurstEncoderRerequestCount = -1
    $latestRecoveryOwnerPendingForcedResetCount = -1
    $latestRecoveryKeyframeEmittedAfterForcedResetCount = -1
    $latestRecoveryBurstCompletedByHelperAckCount = -1
    $latestRecoveryBurstCompletedByAppliedHeadAckCount = -1
    $latestRecoveryBurstCompletedByLastVisibleApplyAckCount = -1
    $latestRecoveryBurstCompletedByVisibleRecoveryFloorCount = -1
    $latestRecoveryBurstCompletedByVisibleApplyFallbackCount = -1
    $latestRecoveryBurstCompletedByTimeoutCount = -1
    $latestRecoveryBurstCompletedByProtectedFramesCount = -1
    $latestRecoveryBurstProfileTransitionDeferredCount = -1
    $latestRecoveryBurstProfileTransitionTakeoverCount = -1
    $latestRecoveryBurstStaleRequestSuppressedCount = -1
    $latestRecoveryBurstRequestSuppressedDueToHelperAckCount = -1
    $latestRecoveryBurstStartedWhileHelperProofHealthyCount = -1
    $eventRecoveryBurstCompletedCount = 0
    $eventRecoveryBurstCompletedByHelperAckCount = 0
    $eventRecoveryBurstCompletedByAppliedHeadAckCount = 0
    $eventRecoveryBurstCompletedByLastVisibleApplyAckCount = 0
    $eventRecoveryBurstCompletedByVisibleRecoveryFloorCount = 0
    $eventRecoveryBurstCompletedByVisibleApplyFallbackCount = 0
    $eventRecoveryBurstCompletedByTimeoutCount = 0
    $eventRecoveryOwnerPendingForcedResetCount = 0
    $eventRecoveryKeyframeEmittedAfterForcedResetCount = 0
    $latestLastCompletedRecoveryEpoch = -1
    $latestLastCompletedRecoveryOwnerFrameId = -1
    $latestLastCompletedRecoveryAckFrameId = -1
    $latestLastCompletedRecoveryAckSource = ''
    $latestLastCompletedRecoveryOwnerEmitToAckMs = -1
    $latestLastCompletedRecoveryCompletionKind = ''
    $latestRecoveryCompletionAccountingMismatch = 0
    $latestRecoveryOwnerPendingNonKeyHeldCount = -1
    $latestRecoveryOwnerPendingNonKeyReplacedCount = -1
    $latestRecoveryOwnerUnackedNonKeyHeldCount = -1
    $latestRecoveryOwnerUnackedNonKeyReplacedCount = -1
    $latestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = -1
    $latestRecoveryOwnerReplacedBeforeAckCount = -1
    $latestRecoveryOwnerAckWindowMs = -1
    $latestHighFrameAgeSuppressedDuringOwnerAckCount = -1
    $latestRecoveryTimeoutWhileHelperHeadAdvancedCount = -1
    $latestSenderReceivedHelperProgressDuringContinuityLossCount = -1
    $latestHelperAckAfterFactSendMs = -1
    $latestPostAckModeGraceSuppressedHighFrameAgeCount = -1
    $latestBootstrapGraceSuppressedCatchUpCount = -1
    $latestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = -1
    $latestCatchUpExitWhileRemoteHighFrameAgePressureCount = -1
    $latestProtectedRecoveryFramesDispatchedCount = -1
    $latestRecoveryProtectedFrameBlockedByOrdinaryCount = -1
    $latestRecoveryPostAckHoldActive = 0
    $latestRecoveryPostAckHoldStartedCount = -1
    $latestRecoveryPostAckHoldExpiredCount = -1
    $latestRecoveryPostAckHoldSuppressedReopenCount = -1
    $latestLastAcknowledgedRecoveryOwnerFrameId = -1
    $latestLastAcknowledgedHelperHeadFrameId = -1
    $latestRemoteHelperVisibleHeadFrameId = -1
    $latestRemoteHelperVisibleRecoveryFloorFrameId = -1
    $latestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount = -1
    $latestLastAcknowledgedVisibleHelperHeadFrameId = -1
    $latestLastAcknowledgedHelperProofAgeMs = -1
    $latestPersistedReleaseFloorEpoch = -1
    $latestSatisfiedRecoveryFloorFrameId = -1
    $latestSatisfiedRecoveryFloorSource = ''
    $latestSatisfiedRecoveryFloorVisibleProofCount = -1
    $latestContinuitySignalIgnoredDueToSatisfiedFloorCount = -1
    $latestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount = -1
    $latestRecoveryLockClearedByAcknowledgedProofCount = -1
    $latestRecoveryLockClearedByVisibleProofCount = -1
    $latestRecoveryLockLastClearReason = ''
    $latestHelperProgressPastOwnerWithoutBurstAckCount = -1
    $latestPostRecoveryAgeGraceActive = 0
    $latestPostRecoveryAgeGraceSuppressedCount = -1
    $recoveryControlBootstrapRetrySkippedDueToBurstResolvedCount = 0
    $recoveryControlBootstrapRetryQueuedAfterBurstResolutionCount = 0
    $recoveryControlFallbackQueuedCount = 0
    $steadyStateControlFallbackQueuedCount = 0
    $latestBridgeMediaMessagesReceived = -1
    $bridgeMediaMessagesReceivedFromLogs = 0
    $latestMediaPlaneFramesSent = -1
    $latestMediaPlaneAttached = -1
    $recoveryBurstCompletedWithoutHelperAdvance = 0
    $recoveryAckMissedDespiteHelperProgress = 0
    $latestHelperRecoveryRunwayContiguousFollowerBufferCount = -1
    $latestHelperRecoveryRunwayContiguousFollowerApplyCount = -1
    $latestHelperRecoveryRunwayAbortCount = -1
    $latestHelperRecoveryKeyframeResyncCount = -1
    $latestHelperGapActive = -1
    $latestHelperGapExpectedFrameId = -1
    $latestHelperBufferedRecoveryKeyframeFrameId = -1
    $latestHelperFutureNonKeyBufferedCount = -1
    $latestHelperPostRecoveryVisibleGenerationResetCount = -1
    $latestHelperPostRecoveryPurgedPreRecoveryFollowerCount = -1
    $latestHelperPostRecoveryStaleDropBypassCount = -1
    $latestHelperLateFragmentAfterSuccessfulRecoveryCount = -1
    $latestHelperUnattributedLossCount = -1
    $latestHelperRecentLosses = ''
    $latestHelperVisibleApplyRatio = -1.0
    $latestHelperAvgDecodeCompleteToVisibleApplyMs = -1.0
    $latestHelperAvgUiPostApplyMs = -1.0
    $latestHelperAvgVisibleHeadLagFrames = -1.0
    $latestHelperAvgStableHeadLagFrames = -1.0
    $latestHelperLastReservedApplyHoldMs = -1
    $latestHelperLastRecoveryProgressCorridorHoldMs = -1
    $latestHelperLastRecoveryRunwayAbortHoldMs = -1
    $latestHelperLastRecoveryProgressCorridorAbortReason = 'none'
    $latestHelperGapCount = -1
    $latestHelperRecoveryKeyframeApplyCount = -1
    $latestHelperResyncCount = -1
    $latestHelperDominantReassemblerRootCause = ''
    $latestHelperDominantAdmissionRejectReason = ''
    $latestHelperPostRecoveryHighFrameAgeSuppressedTicks = -1
    $latestHelperPostRecoverySettleWindowCount = -1
    $latestHelperPostRecoverySettleWindowSuccessCount = -1
    $latestHelperPostRecoverySettleWindowTimeoutCount = -1
    $latestHelperVisibleAppliesDuringSettleCount = -1
    $latestHelperVisibleAppliesBeforePressureReenabled = -1
    $latestHelperRecoveryWindowActive = -1
    $latestHelperRecoveryWindowProgressed = -1
    $latestHelperRecoveryWindowSucceeded = -1
    $latestHelperRecoveryWindowProgressedCount = -1
    $latestHelperRecoveryWindowSuccessCount = -1
    $latestHelperActiveRecoveryWindowEpoch = -1
    $latestHelperActiveRecoveryWindowRecoveryFrameId = -1
    $latestHelperRecoveryWindowContiguousFollowerApplyCount = -1
    $latestHelperAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = -1
    $latestHelperBaselineEstablished = -1
    $latestHelperBaselineCaptureToRenderMs = -1
    $latestHelperAgeExcessMs = -1
    $latestHelperProgressStallMs = -1
    $latestHelperBaselineReseedInProgress = -1
    $latestHelperAgePressureConsecutiveCount = -1
    $latestHelperCadencePressureConsecutiveCount = -1
    $latestHelperCatchUpSuppressedDueToProgressCount = -1
    $latestHelperBaselineFrozenDueToStallCount = -1
    $latestHelperBaselineReseedAfterRecoveryCount = -1
    $latestHelperCadenceStallWindowCount = -1
    $latestHelperCadenceStallTriggerCount = -1
    $latestHelperBridgeHealthAdvisoryCount = -1
    $latestHelperBridgeHealthActionableCount = -1
    $latestHelperBridgeHealthQuarantineSuppressedCount = -1
    $latestHelperBridgeHealthActionableWithoutQueueOrDropCount = -1
    $latestHelperSessionId = ''
    $latestHelperRecoveryFollowerWindowBufferedCount = -1
    $latestHelperRecoveryFollowerWindowAppliedCount = -1
    $latestHelperRecoveryFollowerWindowTrimmedCount = -1
    $latestHelperProtectedRecoveryDeliveryCount = -1
    $latestHelperRecoveryProgressCorridorCount = -1
    $latestHelperRecoveryProgressCorridorSuccessCount = -1
    $latestHelperRecoveryProgressCorridorAbortCount = -1
    $latestHelperRecoveryProgressCorridorAppliedCount = -1
    $latestHelperRecoveryKeyframePendingVisibleApplyCount = -1
    $latestHelperStartupCorridorBufferedFollowerCount = -1
    $latestHelperStartupCorridorReleaseCount = -1
    $latestHelperStartupCorridorAbortCount = -1
    $latestHelperStartupCorridorAbortReason = ''
    $latestPromotionBlockerRateGateTicks = -1
    $latestPromotionBlockerHelperPressureTicks = -1
    $latestPromotionBlockerHelperWarmupTicks = -1
    $latestPromotionBlockerHelperApplyCountTicks = -1
    $latestPromotionBlockerBridgeHealthTicks = -1
    $latestPromotionBlockerRecoveryLockTicks = -1
    $latestPromotionBlockerQueueEvictTicks = -1
    $latestPromotionBlockerCaptureAgeTicks = -1
    $latestPromotionBlockerEncodeBudgetTicks = -1
    $latestPromotionBlockerTransitionGraceTicks = -1
    $latestPromotionEncodeSoftSpikeCount = -1
    $latestPromotionEncodeSoftSpikeResetSuppressedCount = -1
    $promotionBlockedByMissingHelperProofCount = 0
    $promotionBlockedByStaleHelperProofCount = 0
    $promotionBlockedByEncodeBudgetCount = 0
    $promotionBlockedByEncodeBudgetAloneCount = 0
    $latestHealthyTickResetReasonCounts = ''
    $latestReducedPromotionRecentEntries = ''
    $latestHelperRunId = ''
    $latestHelperListenerGeneration = -1
    $latestHealthSenderOperatingState = 'normal'
    $latestHealthSenderGuardState = 'none'
    $latestHealthHelperSessionPhase = 'no_visible_baseline'
    $latestHealthHelperRecoveryMechanism = 'none'
    $latestHealthDominantLossClass = 'benign_stale_cleanup'
    $latestHealthDominantPressureBlocker = 'none'
    $latestHealthDominantTroubleDomain = 'none'
    $latestHealthRecoveryActive = 0
    $latestHealthBaselineEstablished = 0
    $latestHealthSteadyVisibleProgressActive = 0
    $latestSummarySenderOperatingState = ''
    $latestSummarySenderGuardState = ''
    $latestSummaryDominantPressureBlocker = ''
    $latestSummaryHelperSessionPhase = ''
    $latestSummaryHelperRecoveryMechanism = ''
    $latestSummaryDominantLossClass = ''
    $latestHelperUpstreamCaptureToFrameReadyAvgMs = -1
    $latestHelperUpstreamCaptureToFrameReadyMedianMs = -1
    $latestHelperUpstreamCaptureToFrameReadyP95Ms = -1
    $latestHelperUpstreamCaptureToFrameReadyMaxMs = -1
    $latestHelperUpstreamFrameReadyToViewerAcceptAvgMs = -1
    $latestHelperUpstreamFrameReadyToViewerAcceptMedianMs = -1
    $latestHelperUpstreamFrameReadyToViewerAcceptP95Ms = -1
    $latestHelperUpstreamFrameReadyToViewerAcceptMaxMs = -1
    $latestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs = -1
    $latestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs = -1
    $latestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms = -1
    $latestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs = -1
    $latestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs = -1
    $latestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs = -1
    $latestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms = -1
    $latestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs = -1
    $latestHelperUpstreamCaptureToDecodeStartAvgMs = -1
    $latestHelperUpstreamCaptureToDecodeStartMedianMs = -1
    $latestHelperUpstreamCaptureToDecodeStartP95Ms = -1
    $latestHelperUpstreamCaptureToDecodeStartMaxMs = -1
    $latestHelperUpstreamWorstEpochByCaptureToDecodeStart = -1
    $latestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs = -1
    $latestHelperDominantUpstreamLatencyStage = 'none'
    $latestHelperReadyPathCaptureToFirstFragmentObservedAvgMs = -1
    $latestHelperReadyPathCaptureToFirstFragmentObservedMedianMs = -1
    $latestHelperReadyPathCaptureToFirstFragmentObservedP95Ms = -1
    $latestHelperReadyPathCaptureToFirstFragmentObservedMaxMs = -1
    $latestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs = -1
    $latestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs = -1
    $latestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms = -1
    $latestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs = -1
    $latestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs = -1
    $latestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs = -1
    $latestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms = -1
    $latestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs = -1
    $latestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs = -1
    $latestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs = -1
    $latestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms = -1
    $latestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs = -1
    $latestHelperDominantReadyPathStage = 'none'
    $latestHelperReceivePathCaptureToEnvelopeSendAvgMs = -1
    $latestHelperReceivePathCaptureToEnvelopeSendMedianMs = -1
    $latestHelperReceivePathCaptureToEnvelopeSendP95Ms = -1
    $latestHelperReceivePathCaptureToEnvelopeSendMaxMs = -1
    $latestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs = -1
    $latestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs = -1
    $latestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms = -1
    $latestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs = -1
    $latestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs = -1
    $latestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs = -1
    $latestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms = -1
    $latestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs = -1
    $latestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs = -1
    $latestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs = -1
    $latestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms = -1
    $latestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs = -1
    $latestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs = -1
    $latestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs = -1
    $latestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms = -1
    $latestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs = -1
    $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs = -1
    $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs = -1
    $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms = -1
    $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs = -1
    $latestHelperDominantReceivePathStage = 'none'
    $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs = -1
    $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs = -1
    $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms = -1
    $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs = -1
    $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs = -1
    $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs = -1
    $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms = -1
    $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs = -1
    $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs = -1
    $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs = -1
    $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms = -1
    $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs = -1
    $latestHelperDominantBridgeIngressStage = 'none'
    $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs = -1
    $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs = -1
    $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms = -1
    $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs = -1
    $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs = -1
    $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs = -1
    $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms = -1
    $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs = -1
    $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs = -1
    $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs = -1
    $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms = -1
    $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs = -1
    $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs = -1
    $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs = -1
    $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms = -1
    $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs = -1
    $latestHelperDominantNknReceiveStage = 'none'
    $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs = -1
    $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs = -1
    $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms = -1
    $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs = -1
    $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs = -1
    $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs = -1
    $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms = -1
    $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs = -1
    $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs = -1
    $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs = -1
    $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms = -1
    $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs = -1
    $latestHelperDominantWsReceiveStage = 'none'
    $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs = -1
    $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs = -1
    $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms = -1
    $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs = -1
    $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs = -1
    $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs = -1
    $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms = -1
    $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs = -1
    $latestHelperDominantSocketReceiveStage = 'none'
    $latestBridgeEventLoopP95Ms = -1
    $latestBridgeEventLoopMaxMs = -1
    $latestBridgeEventLoopMeanMs = -1
    $latestBridgeEventLoopSampleWindowMs = -1
    $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = -1
    $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = -1
    $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = -1
    $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = -1
    $latestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = -1
    $latestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = -1
    $latestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = -1
    $latestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = -1
    $latestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = -1
    $latestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = -1
    $latestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = -1
    $latestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = -1
    $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = -1
    $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = -1
    $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = -1
    $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = -1
    $latestBridgeMediaSendFramesSent = -1
    $latestBridgeMediaSendFailures = -1
    $latestBridgeMediaSendQueueDrops = -1
    $latestBridgeMediaSendQueueMode = 'normal'
    $latestBridgeMediaSendQueueDepth = -1
    $latestBridgeMediaSendOldestQueuedAgeMs = -1
    $latestBridgeMediaSendSampleWindowMs = -1
    $bestBridgeMediaSendFramesSent = -1
    $latestBridgeTransportHealthSelectedRpc = '(none)'
    $latestBridgeTransportHealthSelectedRpcKey = '(none)'
    $latestBridgeTransportHealthSelectedRpcStage = 'none'
    $latestBridgeTransportHealthConnectId = '(none)'
    $latestBridgeTransportHealthConnectKey = '(none)'
    $latestBridgeTransportHealthReadyEmitted = -1
    $latestBridgeTransportHealthClientReadyAgeMs = -1
    $latestBridgeTransportHealthDisconnectCountSinceLast = -1
    $latestBridgeTransportHealthConnectFailedCountSinceLast = -1
    $latestBridgeTransportHealthWsErrorCountSinceLast = -1
    $latestBridgeTransportHealthRpcFallbackAttemptCountSinceLast = -1
    $latestBridgeTransportHealthControlReady = -1
    $latestBridgeTransportHealthMediaReady = -1
    $latestBridgeTransportHealthBulkReady = -1
    $latestBridgeTransportHealthFramesSentSinceLast = -1
    $latestBridgeTransportHealthLatestDisconnectReason = '(none)'
    $latestBridgeTransportHealthSampleWindowMs = -1
    $latestBridgeTransportHealthUniqueSelectedRpcCount = 0
    $bestBridgeTransportHealthFramesSentSinceLast = -1
    $helperEpochLossLines = New-Object System.Collections.Generic.List[string]
    $helperQualitySummaryLines = New-Object System.Collections.Generic.List[string]
    $helperUpstreamLatencySummaryLines = New-Object System.Collections.Generic.List[string]
    $helperReadyPathSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperReceivePathSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperBridgeIngressSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperNknReceiveSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperWsReceiveSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperSocketReceiveSummaryLines = New-Object System.Collections.Generic.List[string]
    $bridgeEventLoopSummaryLines = New-Object System.Collections.Generic.List[string]
    $bridgeMediaSendSummaryLines = New-Object System.Collections.Generic.List[string]
    $bridgeTransportHealthSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperEpochTimelineLines = New-Object System.Collections.Generic.List[string]
    $helperReassemblerRootCauseSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperRecoveryEpochInvestigationLines = New-Object System.Collections.Generic.List[string]
    $helperReassemblerRecoveryOwnerTransitionLines = New-Object System.Collections.Generic.List[string]
    $helperReassemblerActionableLateFragmentLines = New-Object System.Collections.Generic.List[string]
    $helperReassemblerOlderEpochCleanupLines = New-Object System.Collections.Generic.List[string]
    $helperPressureSummaryLines = New-Object System.Collections.Generic.List[string]
    $healthSnapshotLines = New-Object System.Collections.Generic.List[string]
    $reducedPromotionSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperEpochVisibleRatioByEpoch = @{}
    $helperEpochRecoveryLockMsByEpoch = @{}
    $helperEpochRootCauseByEpoch = @{}
    $helperEpochPressureBlockerByEpoch = @{}
    $helperPressureSummaryByEpoch = @{}
    $helperRootCauseSummaryByEpoch = @{}
    $bridgeTransportHealthSelectedRpcKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($line in [System.IO.File]::ReadAllLines($logPath)) {
        if ($line -match 'event=screenshare_freshness_summary;.*capture_to_send_age_ms=([0-9-]+).*frames_queued=([0-9-]+).*emitted_displayable_frames=([0-9-]+).*emitted_non_displayable_units=([0-9-]+).*emitted_idr_frames=([0-9-]+).*emitted_p_frames=([0-9-]+).*dropped_b_frames=([0-9-]+).*dropped_multi_picture_units=([0-9-]+).*displayable_frame_ratio=([0-9.]+).*idr_frame_ratio=([0-9.]+).*avg_encoded_frame_bytes=([0-9.]+).*transport_ip_only_mode=([0-9-]+).*last_access_unit_kind=([^;]+).*low_delay_config_applied=([^;]+).*encoder_path=([a-z_]+).*sender_freshness_mode=([a-z_]+).*avg_transport_payloads_per_frame=([0-9.]+).*batched_payloads_sent=([0-9-]+).*legacy_fragment_payloads_sent=([0-9-]+).*bridge_health_kind=([a-z_]+)') {
            [void]$captureToSend.Add([int]$matches[1])
            $latestFramesQueued = [int]$matches[2]
            $latestEmittedDisplayableFrames = [int]$matches[3]
            $latestEmittedNonDisplayableUnits = [int]$matches[4]
            $latestEmittedIdrFrames = [int]$matches[5]
            $latestEmittedPFrames = [int]$matches[6]
            $latestDroppedBFrames = [int]$matches[7]
            $latestDroppedMultiPictureUnits = [int]$matches[8]
            $latestDisplayableFrameRatio = [double]$matches[9]
            $latestIdrFrameRatio = [double]$matches[10]
            $latestAverageEncodedFrameBytes = [double]$matches[11]
            $latestTransportIpOnlyMode = [int]$matches[12]
            $latestLastAccessUnitKind = [string]$matches[13]
            $latestLowDelayConfigApplied = [string]$matches[14]
            if ([string]::Equals($matches[15], 'persistent_transform', [System.StringComparison]::OrdinalIgnoreCase)) {
                $persistentSummaries++
            }
            elseif ([string]::Equals($matches[15], 'sink_writer_fallback', [System.StringComparison]::OrdinalIgnoreCase)) {
                $sinkWriterSummaries++
            }

            switch -Regex ($matches[16]) {
                '^normal$' { $normalModeSummaries++ }
                '^reduced$' { $reducedModeSummaries++ }
                '^catch_up$' { $catchUpModeSummaries++ }
            }

            $latestAvgPayloadsPerFrame = [double]$matches[17]
            $latestBatchPayloadCount = [int]$matches[18]
            $latestLegacyPayloadCount = [int]$matches[19]

            switch -Regex ($matches[20]) {
                '^advisory$' { $bridgeHealthAdvisorySummaries++ }
                '^actionable$' { $bridgeHealthActionableSummaries++ }
            }
        }

        if ($line -match 'Bridge screenshare (first inbound traffic|traffic) \(messages=([0-9]+),') {
            $bridgeMediaMessagesReceivedFromLogs += [int]$matches[2]
            if ($bridgeMediaMessagesReceivedFromLogs -gt $latestBridgeMediaMessagesReceived) {
                $latestBridgeMediaMessagesReceived = $bridgeMediaMessagesReceivedFromLogs
            }
        }

        if ($line -like '*event=screenshare_freshness_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestFramesDeferredToSendSlot = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_deferred_to_send_slot'
            $latestFramesReplacedBeforeSendSlot = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_replaced_before_send_slot'
            $latestFramesDroppedByQueueEvict = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_dropped_by_queue_evict' -DefaultValue $latestFramesDroppedByQueueEvict
            $latestSendSlotEmptyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'send_slot_empty_count'
            $latestSlotCoalescingActive = Get-StructuredLogIntField -Pairs $pairs -Key 'slot_coalescing_active'
            $latestRawFramesDeferredToEncodeSlot = Get-StructuredLogIntField -Pairs $pairs -Key 'raw_frames_deferred_to_encode_slot'
            $latestRawFramesReplacedBeforeEncodeSlot = Get-StructuredLogIntField -Pairs $pairs -Key 'raw_frames_replaced_before_encode_slot'
            $latestRawEncodeSlotEmptyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'raw_encode_slot_empty_count'
            $latestRawSlotCoalescingActive = Get-StructuredLogIntField -Pairs $pairs -Key 'raw_slot_coalescing_active'
            $latestSummarySenderOperatingState = Get-StructuredLogStringField -Pairs $pairs -Key 'sender_operating_state' -DefaultValue $latestSummarySenderOperatingState
            $latestSummarySenderGuardState = Get-StructuredLogStringField -Pairs $pairs -Key 'sender_guard_state' -DefaultValue $latestSummarySenderGuardState
            $latestSummaryDominantPressureBlocker = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_pressure_blocker' -DefaultValue $latestSummaryDominantPressureBlocker
            $latestPromotionCaptureToSendBudgetMs = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_capture_to_send_budget_ms'
            $latestSourceSupersededPendingFrames = Get-StructuredLogIntField -Pairs $pairs -Key 'source_superseded_pending_frames'
            $latestHelperSteadyVisibleProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_steady_visible_progress_active' -DefaultValue $latestHelperSteadyVisibleProgressActive
            $stableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_stable_visible_head_frame_id' -DefaultValue $latestHelperStableVisibleHeadFrameId
            if ($stableVisibleHeadValue -match '^-?[0-9]+$') {
                $latestHelperStableVisibleHeadFrameId = [int64]$stableVisibleHeadValue
            }
            $latestHelperFramesAppliedSinceLastGap = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_frames_applied_since_last_gap' -DefaultValue $latestHelperFramesAppliedSinceLastGap
            $latestRemoteHelperFactHealthyActive = Get-StructuredLogIntField -Pairs $pairs -Key 'remote_helper_fact_healthy_active' -DefaultValue $latestRemoteHelperFactHealthyActive
            $latestRemoteHelperFactHealthySource = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_fact_healthy_source' -DefaultValue $latestRemoteHelperFactHealthySource
            $remoteHelperFactProofValue = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_fact_proof_frame_id' -DefaultValue $latestRemoteHelperFactProofFrameId
            if ($remoteHelperFactProofValue -match '^-?[0-9]+$') {
                $latestRemoteHelperFactProofFrameId = [int64]$remoteHelperFactProofValue
            }
            $latestRemoteHelperFactLastMessageAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'remote_helper_fact_last_message_age_ms' -DefaultValue $latestRemoteHelperFactLastMessageAgeMs
            $latestRemoteHelperFactHealthyClearCount = Get-StructuredLogIntField -Pairs $pairs -Key 'remote_helper_fact_healthy_clear_count' -DefaultValue $latestRemoteHelperFactHealthyClearCount
            $latestRemoteHelperFactHealthyClearReason = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_fact_healthy_clear_reason' -DefaultValue $latestRemoteHelperFactHealthyClearReason
            $latestRecoveryBurstActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_active' -DefaultValue $latestRecoveryBurstActive
            $latestRecoveryBurstPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_burst_phase' -DefaultValue $latestRecoveryBurstPhase
            $latestRecoveryBurstStreamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_stream_epoch' -DefaultValue $latestRecoveryBurstStreamEpoch
            $recoveryOwnerFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_frame_id' -DefaultValue $latestRecoveryOwnerFrameId
            if ($recoveryOwnerFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerFrameId = [int64]$recoveryOwnerFrameValue
            }
            $latestRecoveryProtectedFollowerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_protected_follower_count' -DefaultValue $latestRecoveryProtectedFollowerCount
            $latestRecoveryGapCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_gap_count' -DefaultValue $latestRecoveryGapCount
            $latestRecoveryGapToKeyframeRequestMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_gap_to_keyframe_request_ms' -DefaultValue $latestRecoveryGapToKeyframeRequestMs
            $latestRecoveryKeyframeRequestToOwnerEmitMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_request_to_owner_emit_ms' -DefaultValue $latestRecoveryKeyframeRequestToOwnerEmitMs
            $latestRecoveryOwnerAckWindowMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_ack_window_ms' -DefaultValue $latestRecoveryOwnerAckWindowMs
            $latestRecoveryOwnerEmitToAckMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_emit_to_ack_ms' -DefaultValue $latestRecoveryOwnerEmitToAckMs
            $latestRecoveryPostAckHoldActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_post_ack_hold_active' -DefaultValue $latestRecoveryPostAckHoldActive
            $latestRecoveryPostAckHoldStartedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_post_ack_hold_started_count' -DefaultValue $latestRecoveryPostAckHoldStartedCount
            $latestRecoveryPostAckHoldExpiredCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_post_ack_hold_expired_count' -DefaultValue $latestRecoveryPostAckHoldExpiredCount
            $latestRecoveryPostAckHoldSuppressedReopenCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_post_ack_hold_suppressed_reopen_count' -DefaultValue $latestRecoveryPostAckHoldSuppressedReopenCount
            $recoveryOwnerAckFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_ack_frame_id' -DefaultValue $latestRecoveryOwnerAckFrameId
            if ($recoveryOwnerAckFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerAckFrameId = [int64]$recoveryOwnerAckFrameValue
            }

            $latestRecoveryAckSource = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_ack_source' -DefaultValue $latestRecoveryAckSource
            $latestRecoveryOwnerEmitToFirstVisibleApplyMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_emit_to_first_visible_apply_ms' -DefaultValue $latestRecoveryOwnerEmitToFirstVisibleApplyMs
            $latestRecoveryBurstControlFallbackCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_control_fallback_count' -DefaultValue $latestRecoveryBurstControlFallbackCount
            $latestBridgeMediaMessagesReceived = [Math]::Max(
                $latestBridgeMediaMessagesReceived,
                (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_media_messages_received' -DefaultValue $latestBridgeMediaMessagesReceived))
            $latestMediaPlaneFramesSent = Get-StructuredLogIntField -Pairs $pairs -Key 'media_plane_frames_sent' -DefaultValue $latestMediaPlaneFramesSent
            $latestMediaPlaneAttached = Get-StructuredLogIntField -Pairs $pairs -Key 'media_plane_attached' -DefaultValue $latestMediaPlaneAttached
            $latestRecoveryBurstTimeoutCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_timeout_count' -DefaultValue $latestRecoveryBurstTimeoutCount
            $latestRecoveryBurstCompletedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_count' -DefaultValue $latestRecoveryBurstCompletedCount
            $latestRecoveryBurstRestartSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_restart_suppressed_count' -DefaultValue $latestRecoveryBurstRestartSuppressedCount
            $latestRecoveryBurstEncoderRerequestCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_encoder_rerequest_count' -DefaultValue $latestRecoveryBurstEncoderRerequestCount
            $latestRecoveryOwnerPendingForcedResetCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_pending_forced_reset_count' -DefaultValue $latestRecoveryOwnerPendingForcedResetCount
            $latestRecoveryKeyframeEmittedAfterForcedResetCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_emitted_after_forced_reset_count' -DefaultValue $latestRecoveryKeyframeEmittedAfterForcedResetCount
            $latestRecoveryBurstCompletedByHelperAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_helper_ack_count' -DefaultValue $latestRecoveryBurstCompletedByHelperAckCount
            $latestRecoveryBurstCompletedByAppliedHeadAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_applied_head_ack_count' -DefaultValue $latestRecoveryBurstCompletedByAppliedHeadAckCount
            $latestRecoveryBurstCompletedByLastVisibleApplyAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_last_visible_apply_ack_count' -DefaultValue $latestRecoveryBurstCompletedByLastVisibleApplyAckCount
            $latestRecoveryBurstCompletedByTimeoutCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_timeout_count' -DefaultValue $latestRecoveryBurstCompletedByTimeoutCount
            $latestRecoveryBurstCompletedByProtectedFramesCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_protected_frames_count' -DefaultValue $latestRecoveryBurstCompletedByProtectedFramesCount
            $latestRecoveryBurstProfileTransitionDeferredCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_profile_transition_deferred_count' -DefaultValue $latestRecoveryBurstProfileTransitionDeferredCount
            $latestRecoveryBurstProfileTransitionTakeoverCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_profile_transition_takeover_count' -DefaultValue $latestRecoveryBurstProfileTransitionTakeoverCount
            $latestRecoveryBurstStaleRequestSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_stale_request_suppressed_count' -DefaultValue $latestRecoveryBurstStaleRequestSuppressedCount
            $latestRecoveryBurstRequestSuppressedDueToHelperAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_request_suppressed_due_to_helper_ack_count' -DefaultValue $latestRecoveryBurstRequestSuppressedDueToHelperAckCount
            $latestRecoveryBurstStartedWhileHelperProofHealthyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_started_while_helper_proof_healthy_count' -DefaultValue $latestRecoveryBurstStartedWhileHelperProofHealthyCount
            $lastCompletedRecoveryEpochValue = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_epoch'
            if ($null -ne $lastCompletedRecoveryEpochValue) {
                if ([string]::Equals($lastCompletedRecoveryEpochValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryEpoch = -1
                }
                elseif ($lastCompletedRecoveryEpochValue -match '^-?[0-9]+$') {
                    $latestLastCompletedRecoveryEpoch = [int64]$lastCompletedRecoveryEpochValue
                }
            }

            $lastCompletedRecoveryOwnerValue = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_owner_frame_id'
            if ($null -ne $lastCompletedRecoveryOwnerValue) {
                if ([string]::Equals($lastCompletedRecoveryOwnerValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryOwnerFrameId = -1
                }
                elseif ($lastCompletedRecoveryOwnerValue -match '^-?[0-9]+$') {
                    $latestLastCompletedRecoveryOwnerFrameId = [int64]$lastCompletedRecoveryOwnerValue
                }
            }

            $lastCompletedRecoveryAckValue = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_ack_frame_id'
            if ($null -ne $lastCompletedRecoveryAckValue) {
                if ([string]::Equals($lastCompletedRecoveryAckValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryAckFrameId = -1
                }
                elseif ($lastCompletedRecoveryAckValue -match '^-?[0-9]+$') {
                    $latestLastCompletedRecoveryAckFrameId = [int64]$lastCompletedRecoveryAckValue
                }
            }

            $parsedLastCompletedRecoveryAckSource = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_ack_source'
            if ($null -ne $parsedLastCompletedRecoveryAckSource) {
                if ([string]::IsNullOrWhiteSpace($parsedLastCompletedRecoveryAckSource) -or
                    [string]::Equals($parsedLastCompletedRecoveryAckSource, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryAckSource = ''
                }
                else {
                    $latestLastCompletedRecoveryAckSource = [string]$parsedLastCompletedRecoveryAckSource
                }
            }

            $parsedLastCompletedRecoveryOwnerEmitToAckMs = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_owner_emit_to_ack_ms'
            if ($null -ne $parsedLastCompletedRecoveryOwnerEmitToAckMs -and
                $parsedLastCompletedRecoveryOwnerEmitToAckMs -match '^-?[0-9]+$') {
                $latestLastCompletedRecoveryOwnerEmitToAckMs = [int64]$parsedLastCompletedRecoveryOwnerEmitToAckMs
            }

            $parsedLastCompletedRecoveryCompletionKind = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_completion_kind'
            if ($null -ne $parsedLastCompletedRecoveryCompletionKind) {
                if ([string]::IsNullOrWhiteSpace($parsedLastCompletedRecoveryCompletionKind) -or
                    [string]::Equals($parsedLastCompletedRecoveryCompletionKind, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryCompletionKind = ''
                }
                else {
                    $latestLastCompletedRecoveryCompletionKind = [string]$parsedLastCompletedRecoveryCompletionKind
                }
            }
            $latestRecoveryCompletionAccountingMismatch = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_completion_accounting_mismatch' -DefaultValue $latestRecoveryCompletionAccountingMismatch
            $latestRecoveryOwnerPendingNonKeyHeldCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_pending_non_key_held_count' -DefaultValue $latestRecoveryOwnerPendingNonKeyHeldCount
            $latestRecoveryOwnerPendingNonKeyReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_pending_non_key_replaced_count' -DefaultValue $latestRecoveryOwnerPendingNonKeyReplacedCount
            $latestRecoveryOwnerUnackedNonKeyHeldCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_unacked_non_key_held_count' -DefaultValue $latestRecoveryOwnerUnackedNonKeyHeldCount
            $latestRecoveryOwnerUnackedNonKeyReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_unacked_non_key_replaced_count' -DefaultValue $latestRecoveryOwnerUnackedNonKeyReplacedCount
            $latestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_same_epoch_keyframe_suppressed_while_owner_unacked_count' -DefaultValue $latestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount
            $latestRecoveryOwnerReplacedBeforeAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_replaced_before_ack_count' -DefaultValue $latestRecoveryOwnerReplacedBeforeAckCount
            $latestHighFrameAgeSuppressedDuringOwnerAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_during_owner_ack_count' -DefaultValue $latestHighFrameAgeSuppressedDuringOwnerAckCount
            $latestRecoveryTimeoutWhileHelperHeadAdvancedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_timeout_while_helper_head_advanced_count' -DefaultValue $latestRecoveryTimeoutWhileHelperHeadAdvancedCount
            $latestSenderReceivedHelperProgressDuringContinuityLossCount = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_received_helper_progress_during_continuity_loss_count' -DefaultValue $latestSenderReceivedHelperProgressDuringContinuityLossCount
            $helperAckAfterFactSendValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_ack_after_fact_send_ms' -DefaultValue ''
            if ($helperAckAfterFactSendValue -match '^-?[0-9]+$') {
            $latestHelperAckAfterFactSendMs = [int64]$helperAckAfterFactSendValue
        }
        $latestPostAckModeGraceSuppressedHighFrameAgeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_ack_mode_grace_suppressed_high_frame_age_count' -DefaultValue $latestPostAckModeGraceSuppressedHighFrameAgeCount
        $latestBootstrapGraceSuppressedCatchUpCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bootstrap_grace_suppressed_catch_up_count' -DefaultValue $latestBootstrapGraceSuppressedCatchUpCount
        $latestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'catch_up_recovery_suppressed_due_to_remote_high_frame_age_count' -DefaultValue $latestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount
        $latestCatchUpExitWhileRemoteHighFrameAgePressureCount = Get-StructuredLogIntField -Pairs $pairs -Key 'catch_up_exit_while_remote_high_frame_age_pressure_count' -DefaultValue $latestCatchUpExitWhileRemoteHighFrameAgePressureCount
        $latestProtectedRecoveryFramesDispatchedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'protected_recovery_frames_dispatched_count' -DefaultValue $latestProtectedRecoveryFramesDispatchedCount
            $latestRecoveryProtectedFrameBlockedByOrdinaryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_protected_frame_blocked_by_ordinary_count' -DefaultValue $latestRecoveryProtectedFrameBlockedByOrdinaryCount
            $lastAcknowledgedRecoveryOwnerValue = Get-StructuredLogStringField -Pairs $pairs -Key 'last_acknowledged_recovery_owner_frame_id' -DefaultValue $latestLastAcknowledgedRecoveryOwnerFrameId
            if ($lastAcknowledgedRecoveryOwnerValue -match '^-?[0-9]+$') {
                $latestLastAcknowledgedRecoveryOwnerFrameId = [int64]$lastAcknowledgedRecoveryOwnerValue
            }
            $lastAcknowledgedHelperHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'last_acknowledged_helper_head_frame_id' -DefaultValue $latestLastAcknowledgedHelperHeadFrameId
            if ($lastAcknowledgedHelperHeadValue -match '^-?[0-9]+$') {
                $latestLastAcknowledgedHelperHeadFrameId = [int64]$lastAcknowledgedHelperHeadValue
            }
            $latestLastAcknowledgedHelperProofAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_acknowledged_helper_proof_age_ms' -DefaultValue $latestLastAcknowledgedHelperProofAgeMs
            $latestPersistedReleaseFloorEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'persisted_release_floor_epoch' -DefaultValue $latestPersistedReleaseFloorEpoch
            $latestSatisfiedRecoveryFloorFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'satisfied_recovery_floor_frame_id' -DefaultValue $latestSatisfiedRecoveryFloorFrameId
            $latestSatisfiedRecoveryFloorSource = Get-StructuredLogStringField -Pairs $pairs -Key 'satisfied_recovery_floor_source' -DefaultValue $latestSatisfiedRecoveryFloorSource
            $latestContinuitySignalIgnoredDueToSatisfiedFloorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'continuity_signal_ignored_due_to_satisfied_floor_count' -DefaultValue $latestContinuitySignalIgnoredDueToSatisfiedFloorCount
            $latestRecoveryLockClearedByAcknowledgedProofCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_lock_cleared_by_acknowledged_proof_count' -DefaultValue $latestRecoveryLockClearedByAcknowledgedProofCount
            $latestRecoveryLockLastClearReason = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_lock_last_clear_reason' -DefaultValue $latestRecoveryLockLastClearReason
            $latestHelperProgressPastOwnerWithoutBurstAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_progress_past_owner_without_burst_ack_count' -DefaultValue $latestHelperProgressPastOwnerWithoutBurstAckCount
            $latestPostRecoveryAgeGraceActive = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_age_grace_active' -DefaultValue $latestPostRecoveryAgeGraceActive
            $latestPostRecoveryAgeGraceSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_age_grace_suppressed_count' -DefaultValue $latestPostRecoveryAgeGraceSuppressedCount
            if ($latestHelperProgressPastOwnerWithoutBurstAckCount -gt 0) {
                $recoveryAckMissedDespiteHelperProgress = 1
            }
        }

        if ($line -like '*event=screenshare_health_snapshot;*') {
            [void]$healthSnapshotLines.Add($line)
            while ($healthSnapshotLines.Count -gt 24) {
                $healthSnapshotLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHealthSenderOperatingState = Get-StructuredLogStringField -Pairs $pairs -Key 'sender_operating_state' -DefaultValue $latestHealthSenderOperatingState
            $latestHealthSenderGuardState = Get-StructuredLogStringField -Pairs $pairs -Key 'sender_guard_state' -DefaultValue $latestHealthSenderGuardState
            $latestHealthHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestHealthHelperSessionPhase
            $latestHealthHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestHealthHelperRecoveryMechanism
            $latestHealthDominantLossClass = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_loss_class' -DefaultValue $latestHealthDominantLossClass
            $latestHealthDominantPressureBlocker = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_pressure_blocker' -DefaultValue $latestHealthDominantPressureBlocker
            $latestHealthDominantTroubleDomain = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_trouble_domain' -DefaultValue $latestHealthDominantTroubleDomain
            $latestHealthRecoveryActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_active' -DefaultValue $latestHealthRecoveryActive
            $latestHealthBaselineEstablished = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_established' -DefaultValue $latestHealthBaselineEstablished
            $latestHealthSteadyVisibleProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_active' -DefaultValue $latestHealthSteadyVisibleProgressActive
        }

        if ($line -like '*event=screenshare_sender_recovery_burst_started;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestRecoveryBurstStreamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch' -DefaultValue $latestRecoveryBurstStreamEpoch
            $latestRecoveryBurstActive = 1
            $latestRecoveryBurstPhase = 'requested'
            $startedGapToRequestValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_gap_to_keyframe_request_ms' -DefaultValue $latestRecoveryGapToKeyframeRequestMs
            if ($startedGapToRequestValue -match '^-?[0-9]+$') {
                $latestRecoveryGapToKeyframeRequestMs = [int64]$startedGapToRequestValue
            }
        }

        if ($line -like '*event=screenshare_visible_proof_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $remoteHelperVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_visible_head_frame_id' -DefaultValue $latestRemoteHelperVisibleHeadFrameId
            if ($remoteHelperVisibleHeadValue -match '^-?[0-9]+$') {
                $latestRemoteHelperVisibleHeadFrameId = [int64]$remoteHelperVisibleHeadValue
            }

            $remoteHelperVisibleRecoveryFloorValue = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_visible_recovery_floor_frame_id' -DefaultValue $latestRemoteHelperVisibleRecoveryFloorFrameId
            if ($remoteHelperVisibleRecoveryFloorValue -match '^-?[0-9]+$') {
                $latestRemoteHelperVisibleRecoveryFloorFrameId = [int64]$remoteHelperVisibleRecoveryFloorValue
            }

            $latestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'remote_helper_current_epoch_recovery_keyframe_apply_count' -DefaultValue $latestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount

            $lastAcknowledgedVisibleHelperHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'last_acknowledged_visible_helper_head_frame_id' -DefaultValue $latestLastAcknowledgedVisibleHelperHeadFrameId
            if ($lastAcknowledgedVisibleHelperHeadValue -match '^-?[0-9]+$') {
                $latestLastAcknowledgedVisibleHelperHeadFrameId = [int64]$lastAcknowledgedVisibleHelperHeadValue
            }

            $latestPersistedReleaseFloorEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'persisted_release_floor_epoch' -DefaultValue $latestPersistedReleaseFloorEpoch
            $latestSatisfiedRecoveryFloorFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'satisfied_recovery_floor_frame_id' -DefaultValue $latestSatisfiedRecoveryFloorFrameId
            $latestSatisfiedRecoveryFloorSource = Get-StructuredLogStringField -Pairs $pairs -Key 'satisfied_recovery_floor_source' -DefaultValue $latestSatisfiedRecoveryFloorSource
            $latestSatisfiedRecoveryFloorVisibleProofCount = Get-StructuredLogIntField -Pairs $pairs -Key 'satisfied_recovery_floor_visible_proof_count' -DefaultValue $latestSatisfiedRecoveryFloorVisibleProofCount
            $latestRecoveryBurstCompletedByVisibleRecoveryFloorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_visible_recovery_floor_count' -DefaultValue $latestRecoveryBurstCompletedByVisibleRecoveryFloorCount
            $latestRecoveryBurstCompletedByVisibleApplyFallbackCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_visible_apply_fallback_count' -DefaultValue $latestRecoveryBurstCompletedByVisibleApplyFallbackCount
            $latestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'continuity_signal_ignored_due_to_visible_satisfied_floor_count' -DefaultValue $latestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount
            $latestRecoveryLockClearedByVisibleProofCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_lock_cleared_by_visible_proof_count' -DefaultValue $latestRecoveryLockClearedByVisibleProofCount
            $latestRecoveryLockLastClearReason = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_lock_last_clear_reason' -DefaultValue $latestRecoveryLockLastClearReason
        }

        if ($line -like '*event=screenshare_control_fallback_queued;*' -or
            $line -like '*event=screenshare_control_bootstrap_retry_queued;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $fallbackReason = Get-StructuredLogStringField -Pairs $pairs -Key 'reason' -DefaultValue ''
            if ($fallbackReason -like 'recovery_burst_*') {
                $recoveryControlFallbackQueuedCount++
            }
            elseif (-not [string]::IsNullOrWhiteSpace($fallbackReason)) {
                $steadyStateControlFallbackQueuedCount++
            }
        }

        if ($line -like '*event=screenshare_sender_recovery_burst_owner_emitted;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestRecoveryBurstStreamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch' -DefaultValue $latestRecoveryBurstStreamEpoch
            $ownerFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_frame_id' -DefaultValue $latestRecoveryOwnerFrameId
            if ($ownerFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerFrameId = [int64]$ownerFrameValue
            }

            $ownerEmitLatencyValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_keyframe_request_to_owner_emit_ms' -DefaultValue $latestRecoveryKeyframeRequestToOwnerEmitMs
            if ($ownerEmitLatencyValue -match '^-?[0-9]+$') {
                $latestRecoveryKeyframeRequestToOwnerEmitMs = [int64]$ownerEmitLatencyValue
            }

            $latestRecoveryBurstPhase = 'owner_emitted_awaiting_helper_ack'
        }

        if ($line -like '*event=screenshare_sender_recovery_burst_completed;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestRecoveryBurstStreamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch' -DefaultValue $latestRecoveryBurstStreamEpoch
            $latestRecoveryBurstActive = 0
            $eventRecoveryBurstCompletedCount++
            $completedOwnerFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_frame_id' -DefaultValue $latestRecoveryOwnerFrameId
            if ($completedOwnerFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerFrameId = [int64]$completedOwnerFrameValue
            }

            $ownerToVisibleApplyValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_emit_to_first_visible_apply_ms' -DefaultValue $latestRecoveryOwnerEmitToFirstVisibleApplyMs
            if ($ownerToVisibleApplyValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerEmitToFirstVisibleApplyMs = [int64]$ownerToVisibleApplyValue
            }

            $ownerToAckValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_emit_to_ack_ms' -DefaultValue $latestRecoveryOwnerEmitToAckMs
            if ($ownerToAckValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerEmitToAckMs = [int64]$ownerToAckValue
            }

            $ownerAckFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_head_frame_id' -DefaultValue $latestRecoveryOwnerAckFrameId
            if ($ownerAckFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerAckFrameId = [int64]$ownerAckFrameValue
            }

            $latestRecoveryAckSource = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_ack_source' -DefaultValue $latestRecoveryAckSource

            $completion = Get-StructuredLogStringField -Pairs $pairs -Key 'completion' -DefaultValue ''
            switch -Exact ($completion) {
                'helper_head_advance' {
                    $eventRecoveryBurstCompletedByHelperAckCount++
                    if ($latestRecoveryBurstStreamEpoch -gt 0) {
                        $latestLastCompletedRecoveryEpoch = $latestRecoveryBurstStreamEpoch
                    }

                    if ($latestRecoveryOwnerFrameId -ge 0) {
                        $latestLastCompletedRecoveryOwnerFrameId = $latestRecoveryOwnerFrameId
                    }

                    if ($latestRecoveryOwnerAckFrameId -ge 0) {
                        $latestLastCompletedRecoveryAckFrameId = $latestRecoveryOwnerAckFrameId
                    }

                    if (-not [string]::IsNullOrWhiteSpace($latestRecoveryAckSource) -and
                        -not [string]::Equals($latestRecoveryAckSource, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                        $latestLastCompletedRecoveryAckSource = $latestRecoveryAckSource
                    }

                    if ($latestRecoveryOwnerEmitToAckMs -ge 0) {
                        $latestLastCompletedRecoveryOwnerEmitToAckMs = $latestRecoveryOwnerEmitToAckMs
                    }

                    $latestLastCompletedRecoveryCompletionKind = 'helper_ack'

                    switch -Exact ($latestRecoveryAckSource) {
                        'applied_head' { $eventRecoveryBurstCompletedByAppliedHeadAckCount++ }
                        'last_visible_apply' { $eventRecoveryBurstCompletedByLastVisibleApplyAckCount++ }
                        'visible_recovery_floor' { $eventRecoveryBurstCompletedByVisibleRecoveryFloorCount++ }
                        'visible_apply_fallback' { $eventRecoveryBurstCompletedByVisibleApplyFallbackCount++ }
                        'helper_visible_receipt' { }
                    }

                    $latestRecoveryBurstPhase = 'completed'
                }
                'helper_visible_receipt' {
                    $eventRecoveryBurstCompletedByHelperAckCount++
                    if ($latestRecoveryBurstStreamEpoch -gt 0) {
                        $latestLastCompletedRecoveryEpoch = $latestRecoveryBurstStreamEpoch
                    }

                    if ($latestRecoveryOwnerFrameId -ge 0) {
                        $latestLastCompletedRecoveryOwnerFrameId = $latestRecoveryOwnerFrameId
                    }

                    if ($latestRecoveryOwnerAckFrameId -ge 0) {
                        $latestLastCompletedRecoveryAckFrameId = $latestRecoveryOwnerAckFrameId
                    }

                    if (-not [string]::IsNullOrWhiteSpace($latestRecoveryAckSource) -and
                        -not [string]::Equals($latestRecoveryAckSource, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                        $latestLastCompletedRecoveryAckSource = $latestRecoveryAckSource
                    }

                    if ($latestRecoveryOwnerEmitToAckMs -ge 0) {
                        $latestLastCompletedRecoveryOwnerEmitToAckMs = $latestRecoveryOwnerEmitToAckMs
                    }

                    $latestLastCompletedRecoveryCompletionKind = 'helper_ack'

                    switch -Exact ($latestRecoveryAckSource) {
                        'applied_head' { $eventRecoveryBurstCompletedByAppliedHeadAckCount++ }
                        'last_visible_apply' { $eventRecoveryBurstCompletedByLastVisibleApplyAckCount++ }
                        'visible_recovery_floor' { $eventRecoveryBurstCompletedByVisibleRecoveryFloorCount++ }
                        'visible_apply_fallback' { $eventRecoveryBurstCompletedByVisibleApplyFallbackCount++ }
                        'helper_visible_receipt' { }
                    }

                    $latestRecoveryBurstPhase = 'completed'
                }
                'timeout' {
                    $eventRecoveryBurstCompletedByTimeoutCount++
                    if ($latestRecoveryBurstStreamEpoch -gt 0) {
                        $latestLastCompletedRecoveryEpoch = $latestRecoveryBurstStreamEpoch
                    }
                    if ($latestRecoveryOwnerFrameId -ge 0) {
                        $latestLastCompletedRecoveryOwnerFrameId = $latestRecoveryOwnerFrameId
                    }
                    $latestLastCompletedRecoveryAckFrameId = -1
                    $latestLastCompletedRecoveryAckSource = ''
                    $latestLastCompletedRecoveryOwnerEmitToAckMs = -1
                    $latestLastCompletedRecoveryCompletionKind = 'timeout'
                    if ($latestHelperProgressPastOwnerWithoutBurstAckCount -gt 0) {
                        $recoveryAckMissedDespiteHelperProgress = 1
                    }
                    $latestRecoveryBurstPhase = 'timed_out'
                }
                default {
                    if (-not [string]::IsNullOrWhiteSpace($completion)) {
                        $recoveryBurstCompletedWithoutHelperAdvance = 1
                    }

                    $latestRecoveryBurstPhase = 'completed'
                }
            }
        }

        if ($line -like '*event=screenshare_sender_recovery_owner_pending_forced_reset;*') {
            $eventRecoveryOwnerPendingForcedResetCount++
        }

        if ($line -like '*event=screenshare_sender_recovery_keyframe_emitted_after_forced_reset;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $eventRecoveryKeyframeEmittedAfterForcedResetCount++
            $latestRecoveryKeyframeEmittedAfterForcedResetCount = [Math]::Max(
                $latestRecoveryKeyframeEmittedAfterForcedResetCount,
                $eventRecoveryKeyframeEmittedAfterForcedResetCount)
            $emittedLatencyMs = Get-StructuredLogIntField -Pairs $pairs -Key 'latency_ms' -DefaultValue $latestRecoveryKeyframeRequestToOwnerEmitMs
            if ($emittedLatencyMs -ge 0) {
                $latestRecoveryKeyframeRequestToOwnerEmitMs = $emittedLatencyMs
            }
        }

        if ($line -like '*event=screenshare_control_bootstrap_retry_skipped;*' -and $line -like '*skip_reason=recovery_burst_resolved*') {
            $recoveryControlBootstrapRetrySkippedDueToBurstResolvedCount++
        }

        if ($line -like '*event=screenshare_control_bootstrap_retry_queued_after_burst_resolution;*') {
            $recoveryControlBootstrapRetryQueuedAfterBurstResolutionCount++
        }

        if ($line -like '*event=screenshare_transport_batch_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestAvgFragmentsPerFrame = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_fragments_per_frame' -DefaultValue $latestAvgFragmentsPerFrame
            $latestAvgPayloadsPerFrame = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_transport_payloads_per_frame' -DefaultValue $latestAvgPayloadsPerFrame
            $latestBatchPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'batched_payloads_sent' -DefaultValue $latestBatchPayloadCount
            $latestLegacyPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'legacy_fragment_payloads_sent' -DefaultValue $latestLegacyPayloadCount
            $latestOrdinaryNonKeyBatchedPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'ordinary_non_key_batched_payloads_sent' -DefaultValue $latestOrdinaryNonKeyBatchedPayloadCount
            $latestOrdinaryNonKeyLegacyPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'ordinary_non_key_legacy_payloads_sent' -DefaultValue $latestOrdinaryNonKeyLegacyPayloadCount
            $latestKeyframeRecoveryBatchedPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'keyframe_recovery_batched_payloads_sent' -DefaultValue $latestKeyframeRecoveryBatchedPayloadCount
        }

        if ($line -match 'event=screenshare_viewer_frame_applied; role=helper_remote; age_ms=([0-9-]+);.*frames_completed=([0-9-]+);.*frames_enqueued_for_decode=([0-9-]+);.*frames_dropped_before_decode=([0-9-]+);.*frames_decoded=([0-9-]+);.*frames_dropped_after_decode=([0-9-]+);.*frames_applied=([0-9-]+);.*need_more_input_count=([0-9-]+);.*completed_without_picture_count=([0-9-]+);.*avg_decode_duration_ms=([0-9.]+);.*avg_apply_interval_ms=([0-9.]+)') {
            [void]$helperApply.Add([int]$matches[1])
            $latestHelperFramesCompleted = [int]$matches[2]
            $latestHelperFramesEnqueuedForDecode = [int]$matches[3]
            $latestHelperFramesDroppedBeforeDecode = [int]$matches[4]
            $latestHelperFramesDecoded = [int]$matches[5]
            $latestHelperFramesDroppedAfterDecode = [int]$matches[6]
            $latestHelperFramesApplied = [int]$matches[7]
            $latestHelperNeedMoreInputCount = [int]$matches[8]
            $latestHelperCompletedWithoutPictureCount = [int]$matches[9]
            $latestHelperDecodeDurationMs = [double]$matches[10]
            $latestHelperApplyIntervalMs = [double]$matches[11]
        }

        if ($line -like '*event=screenshare_helper_frame_loss_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestSummaryDominantLossClass = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_loss_class' -DefaultValue $latestSummaryDominantLossClass
            $latestHelperReassemblerLossCount = Get-StructuredLogIntField -Pairs $pairs -Key 'reassembler_loss_count'
            $latestHelperEnqueueRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'enqueue_reject_count'
            $latestHelperWaitingForRecoveryKeyframeRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'waiting_for_recovery_keyframe_reject_count'
            $latestHelperRecoveryWaitRejectBeforeRunwayCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_wait_reject_before_runway_count' -DefaultValue $latestHelperWaitingForRecoveryKeyframeRejectCount
            $latestHelperRecoveryRunwayOverflowRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_overflow_reject_count'
            $latestHelperSuppressedEmitDuringRecoveryWaitCount = Get-StructuredLogIntField -Pairs $pairs -Key 'suppressed_emit_during_recovery_wait_count'
            $latestHelperSoftStaleCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'soft_stale_cleanup_count'
            $latestHelperStaleSupersededRecoverySuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_superseded_recovery_suppressed_count' -DefaultValue $latestHelperSoftStaleCleanupCount
            $latestHelperBlockedByReservedRecoveryFrameRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'blocked_by_reserved_recovery_frame_reject_count'
            $latestHelperOlderEpochIgnoredDuringRecoveryLockCount = Get-StructuredLogIntField -Pairs $pairs -Key 'older_epoch_ignored_during_recovery_lock_count'
            $latestHelperNewerEpochNonKeyIgnoredDuringLockCount = Get-StructuredLogIntField -Pairs $pairs -Key 'newer_epoch_non_key_ignored_during_lock_count'
            $latestHelperDeferredPostRecoveryCandidateReplaceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'deferred_post_recovery_candidate_replace_count'
            $latestHelperDecodeWorkerDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_worker_drop_count'
            $latestHelperPostDecodeDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_decode_drop_count'
            $latestHelperDecodeQueueOverflowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_queue_overflow_count'
            $latestHelperDecodeAgeBudgetCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_age_budget_count'
            $latestHelperDecodeGenerationChangedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_generation_changed_count'
            $latestHelperDecodeStoppedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_stopped_count'
            $latestHelperDecodedApplyQueueOverflowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decoded_apply_queue_overflow_count' -DefaultValue $latestHelperDecodedApplyQueueOverflowCount
            $latestHelperDecodedFrameReplacedBeforeApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decoded_frame_replaced_before_apply_count' -FallbackAfterKey 'decoded_apply_queue_overflow_count' -FallbackOffset 1
            $latestHelperStaleDroppedAfterDecodeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_dropped_after_decode_count'
            $latestHelperDroppedWaitingForRecoveryKeyframeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'dropped_waiting_for_recovery_keyframe_count' -FallbackAfterKey 'decode_stopped_count' -FallbackOffset 2
            $latestHelperGapNonKeyPrunedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'gap_non_key_pruned_count'
            $latestHelperFutureTailQuarantinedDuringGapCount = Get-StructuredLogIntField -Pairs $pairs -Key 'future_tail_quarantined_during_gap_count'
            $latestHelperFutureTailQuarantinedAfterGapCount = Get-StructuredLogIntField -Pairs $pairs -Key 'future_tail_quarantined_after_gap_count' -DefaultValue $latestHelperFutureTailQuarantinedDuringGapCount
            $latestHelperPreCandidateGapTailRejectedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pre_candidate_gap_tail_rejected_count'
            $latestHelperRecoveryCandidatePresentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_candidate_present_count'
            $visibleRecoveryFloorValue = Get-StructuredLogStringField -Pairs $pairs -Key 'visible_recovery_floor_frame_id' -DefaultValue '(none)'
            $latestHelperVisibleRecoveryFloorFrameId = if ($visibleRecoveryFloorValue -match '^-?[0-9]+$') { [int64]$visibleRecoveryFloorValue } else { $latestHelperVisibleRecoveryFloorFrameId }
            $stableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'stable_visible_head_frame_id' -DefaultValue '(none)'
            $latestHelperStableVisibleHeadFrameId = if ($stableVisibleHeadValue -match '^-?[0-9]+$') { [int64]$stableVisibleHeadValue } else { $latestHelperStableVisibleHeadFrameId }
            $appliedHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'applied_head_frame_id' -DefaultValue '(none)'
            $latestHelperAppliedHeadFrameId = if ($appliedHeadValue -match '^-?[0-9]+$') { [int64]$appliedHeadValue } else { $latestHelperAppliedHeadFrameId }
            $orderedEmitHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'ordered_emit_head_frame_id' -DefaultValue '(none)'
            $latestHelperOrderedEmitHeadFrameId = if ($orderedEmitHeadValue -match '^-?[0-9]+$') { [int64]$orderedEmitHeadValue } else { $latestHelperOrderedEmitHeadFrameId }
            $winningRecoveryValue = Get-StructuredLogStringField -Pairs $pairs -Key 'winning_recovery_frame_id' -DefaultValue '(none)'
            $latestHelperWinningRecoveryFrameId = if ($winningRecoveryValue -match '^-?[0-9]+$') { [int64]$winningRecoveryValue } else { $latestHelperWinningRecoveryFrameId }
            $visibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'visible_head_frame_id' -DefaultValue '(none)'
            $latestHelperVisibleHeadFrameId = if ($visibleHeadValue -match '^-?[0-9]+$') { [int64]$visibleHeadValue } else { $latestHelperVisibleHeadFrameId }
            $latestHelperSupersededRecoveryTailCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'superseded_recovery_tail_cleanup_count' -DefaultValue $latestHelperSupersededRecoveryTailCleanupCount
            $latestHelperLateSameEpochAfterHeadAdvancedDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_same_epoch_after_head_advanced_drop_count' -DefaultValue $latestHelperLateSameEpochAfterHeadAdvancedDropCount
            $latestHelperStaleRunwayWindowAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_runway_window_abort_count' -DefaultValue $latestHelperStaleRunwayWindowAbortCount
            $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'runway_candidate_expired_after_head_advance_count' -DefaultValue $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount
            $latestHelperRunwayFollowersEmittedWithinActionableWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'runway_followers_emitted_within_actionable_window_count' -DefaultValue $latestHelperRunwayFollowersEmittedWithinActionableWindowCount
            $latestHelperRecoveryOwnerReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_replaced_count' -DefaultValue $latestHelperRecoveryOwnerReplacedCount
            $latestHelperOlderEpochCleanupAfterEpochAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'older_epoch_cleanup_after_epoch_advance_count' -DefaultValue $latestHelperOlderEpochCleanupAfterEpochAdvanceCount
            $latestHelperLateFragmentAfterAppliedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_applied_head_count' -DefaultValue $latestHelperLateFragmentAfterAppliedHeadCount
            $latestHelperLateFragmentAfterOrderedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_ordered_head_count' -DefaultValue $latestHelperLateFragmentAfterOrderedHeadCount
            $latestHelperLateFragmentAfterStableVisibleHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_stable_visible_head_count'
            $latestHelperLateFragmentAfterVisibleRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_visible_recovery_count'
            $latestHelperPreCandidateGapTailEmittedToViewerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pre_candidate_gap_tail_emitted_to_viewer_count' -DefaultValue $latestHelperPreCandidateGapTailEmittedToViewerCount
            $latestHelperActionableLateFragmentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_late_fragment_count' -DefaultValue $latestHelperActionableLateFragmentCount
            $latestHelperRecoveryRunwayContiguousFollowerBufferCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_contiguous_follower_buffer_count'
            $latestHelperRecoveryRunwayContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_contiguous_follower_apply_count'
            $latestHelperRecoveryRunwayAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_abort_count'
            $latestHelperRecoveryKeyframeResyncCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_resync_count'
            $latestHelperGapActive = Get-StructuredLogIntField -Pairs $pairs -Key 'gap_active'
            $gapExpectedValue = Get-StructuredLogStringField -Pairs $pairs -Key 'gap_expected_frame_id' -DefaultValue '(none)'
            $bufferedRecoveryKeyframeValue = Get-StructuredLogStringField -Pairs $pairs -Key 'buffered_recovery_keyframe_frame_id' -DefaultValue '(none)'
            $latestHelperGapExpectedFrameId = if ($gapExpectedValue -match '^-?[0-9]+$') { [int64]$gapExpectedValue } else { -1 }
            $latestHelperBufferedRecoveryKeyframeFrameId = if ($bufferedRecoveryKeyframeValue -match '^-?[0-9]+$') { [int64]$bufferedRecoveryKeyframeValue } else { -1 }
            $latestHelperFutureNonKeyBufferedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'future_non_key_buffered_count'
            $latestHelperRecoveryFollowerWindowBufferedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_follower_window_buffered_count'
            $latestHelperRecoveryFollowerWindowAppliedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_follower_window_applied_count'
            $latestHelperRecoveryFollowerWindowTrimmedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_follower_window_trimmed_count'
            $latestHelperProtectedRecoveryDeliveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'protected_recovery_delivery_count'
            $latestHelperRecoveryProgressCorridorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_count'
            $latestHelperRecoveryProgressCorridorSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_success_count'
            $latestHelperRecoveryProgressCorridorAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_abort_count'
            $latestHelperRecoveryProgressCorridorAppliedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_applied_count'
            $latestHelperRecoveryKeyframePendingVisibleApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_pending_visible_apply_count'
            $latestHelperStartupCorridorBufferedFollowerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_buffered_follower_count'
            $latestHelperStartupCorridorReleaseCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_release_count'
            $latestHelperStartupCorridorAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_abort_count'
            $latestHelperStartupCorridorAbortReason = Get-StructuredLogStringField -Pairs $pairs -Key 'startup_corridor_abort_reason' -DefaultValue $latestHelperStartupCorridorAbortReason
            $latestHelperDominantAdmissionRejectReason = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_helper_admission_reject_reason' -DefaultValue ''
            $latestHelperPostRecoveryVisibleGenerationResetCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_visible_generation_reset_count'
            $latestHelperPostRecoveryPurgedPreRecoveryFollowerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_purged_pre_recovery_follower_count'
            $latestHelperPostRecoveryStaleDropBypassCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_stale_drop_bypass_count'
            $latestHelperLateFragmentAfterSuccessfulRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_successful_recovery_count'
            $latestHelperUnattributedLossCount = Get-StructuredLogIntField -Pairs $pairs -Key 'unattributed_loss_count'
            $latestHelperRecentLosses = Get-StructuredLogStringField -Pairs $pairs -Key 'recent_losses'
        }

        if ($line -like '*event=screenshare_helper_quality_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestSummaryDominantLossClass = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_loss_class' -DefaultValue $latestSummaryDominantLossClass
            $latestHelperVisibleApplyRatio = Get-StructuredLogFloatField -Pairs $pairs -Key 'visible_apply_ratio'
            $latestHelperAvgDecodeCompleteToVisibleApplyMs = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_decode_complete_to_visible_apply_ms' -DefaultValue $latestHelperAvgDecodeCompleteToVisibleApplyMs
            $latestHelperAvgUiPostApplyMs = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_ui_post_apply_ms' -DefaultValue $latestHelperAvgUiPostApplyMs
            $latestHelperAvgVisibleHeadLagFrames = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_visible_head_lag_frames' -DefaultValue $latestHelperAvgVisibleHeadLagFrames
            $latestHelperAvgStableHeadLagFrames = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_stable_head_lag_frames' -DefaultValue $latestHelperAvgStableHeadLagFrames
            $latestHelperLastReservedApplyHoldMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_reserved_apply_hold_ms' -DefaultValue $latestHelperLastReservedApplyHoldMs
            $latestHelperLastRecoveryProgressCorridorHoldMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_recovery_progress_corridor_hold_ms' -DefaultValue $latestHelperLastRecoveryProgressCorridorHoldMs
            $latestHelperLastRecoveryRunwayAbortHoldMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_recovery_runway_abort_hold_ms' -DefaultValue $latestHelperLastRecoveryRunwayAbortHoldMs
            $latestHelperLastRecoveryProgressCorridorAbortReason = Get-StructuredLogStringField -Pairs $pairs -Key 'last_recovery_progress_corridor_abort_reason' -DefaultValue $latestHelperLastRecoveryProgressCorridorAbortReason
            $latestHelperGapCount = Get-StructuredLogIntField -Pairs $pairs -Key 'gap_count'
            $latestHelperRecoveryKeyframeApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_apply_count'
            $latestHelperResyncCount = Get-StructuredLogIntField -Pairs $pairs -Key 'resync_count'
            $latestHelperDominantReassemblerRootCause = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_reassembler_root_cause' -DefaultValue ''
            $latestHelperDominantAdmissionRejectReason = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_helper_admission_reject_reason' -DefaultValue $latestHelperDominantAdmissionRejectReason
            $latestHelperRecoveryWaitRejectBeforeRunwayCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_wait_reject_before_runway_count' -DefaultValue $latestHelperRecoveryWaitRejectBeforeRunwayCount
            $latestHelperRecoveryRunwayOverflowRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_overflow_reject_count' -DefaultValue $latestHelperRecoveryRunwayOverflowRejectCount
            $latestHelperSuppressedEmitDuringRecoveryWaitCount = Get-StructuredLogIntField -Pairs $pairs -Key 'suppressed_emit_during_recovery_wait_count' -DefaultValue $latestHelperSuppressedEmitDuringRecoveryWaitCount
            $latestHelperSoftStaleCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'soft_stale_cleanup_count' -DefaultValue $latestHelperSoftStaleCleanupCount
            $latestHelperStaleSupersededRecoverySuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_superseded_recovery_suppressed_count' -DefaultValue $latestHelperSoftStaleCleanupCount
            $latestHelperPreCandidateGapTailEmittedToViewerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pre_candidate_gap_tail_emitted_to_viewer_count' -DefaultValue $latestHelperPreCandidateGapTailEmittedToViewerCount
            $latestHelperRecoveryCandidatePresentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_candidate_present_count' -DefaultValue $latestHelperRecoveryCandidatePresentCount
            $visibleRecoveryFloorValue = Get-StructuredLogStringField -Pairs $pairs -Key 'visible_recovery_floor_frame_id' -DefaultValue $latestHelperVisibleRecoveryFloorFrameId
            $latestHelperVisibleRecoveryFloorFrameId = if ($visibleRecoveryFloorValue -match '^-?[0-9]+$') { [int64]$visibleRecoveryFloorValue } else { $latestHelperVisibleRecoveryFloorFrameId }
            $stableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'stable_visible_head_frame_id' -DefaultValue $latestHelperStableVisibleHeadFrameId
            $latestHelperStableVisibleHeadFrameId = if ($stableVisibleHeadValue -match '^-?[0-9]+$') { [int64]$stableVisibleHeadValue } else { $latestHelperStableVisibleHeadFrameId }
            $appliedHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'applied_head_frame_id' -DefaultValue $latestHelperAppliedHeadFrameId
            $latestHelperAppliedHeadFrameId = if ($appliedHeadValue -match '^-?[0-9]+$') { [int64]$appliedHeadValue } else { $latestHelperAppliedHeadFrameId }
            $orderedEmitHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'ordered_emit_head_frame_id' -DefaultValue $latestHelperOrderedEmitHeadFrameId
            $latestHelperOrderedEmitHeadFrameId = if ($orderedEmitHeadValue -match '^-?[0-9]+$') { [int64]$orderedEmitHeadValue } else { $latestHelperOrderedEmitHeadFrameId }
            $winningRecoveryValue = Get-StructuredLogStringField -Pairs $pairs -Key 'winning_recovery_frame_id' -DefaultValue $latestHelperWinningRecoveryFrameId
            $latestHelperWinningRecoveryFrameId = if ($winningRecoveryValue -match '^-?[0-9]+$') { [int64]$winningRecoveryValue } else { $latestHelperWinningRecoveryFrameId }
            $visibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'visible_head_frame_id' -DefaultValue $latestHelperVisibleHeadFrameId
            $latestHelperVisibleHeadFrameId = if ($visibleHeadValue -match '^-?[0-9]+$') { [int64]$visibleHeadValue } else { $latestHelperVisibleHeadFrameId }
            $latestHelperSupersededRecoveryTailCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'superseded_recovery_tail_cleanup_count' -DefaultValue $latestHelperSupersededRecoveryTailCleanupCount
            $latestHelperLateSameEpochAfterHeadAdvancedDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_same_epoch_after_head_advanced_drop_count' -DefaultValue $latestHelperLateSameEpochAfterHeadAdvancedDropCount
            $latestHelperStaleRunwayWindowAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_runway_window_abort_count' -DefaultValue $latestHelperStaleRunwayWindowAbortCount
            $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'runway_candidate_expired_after_head_advance_count' -DefaultValue $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount
            $latestHelperRunwayFollowersEmittedWithinActionableWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'runway_followers_emitted_within_actionable_window_count' -DefaultValue $latestHelperRunwayFollowersEmittedWithinActionableWindowCount
            $latestHelperRecoveryOwnerReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_replaced_count' -DefaultValue $latestHelperRecoveryOwnerReplacedCount
            $latestHelperOlderEpochCleanupAfterEpochAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'older_epoch_cleanup_after_epoch_advance_count' -DefaultValue $latestHelperOlderEpochCleanupAfterEpochAdvanceCount
            $latestHelperPreCandidateGapTailRejectedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pre_candidate_gap_tail_rejected_count' -DefaultValue $latestHelperPreCandidateGapTailRejectedCount
            $latestHelperLateFragmentAfterAppliedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_applied_head_count' -DefaultValue $latestHelperLateFragmentAfterAppliedHeadCount
            $latestHelperLateFragmentAfterOrderedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_ordered_head_count' -DefaultValue $latestHelperLateFragmentAfterOrderedHeadCount
            $latestHelperLateFragmentAfterStableVisibleHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_stable_visible_head_count' -DefaultValue $latestHelperLateFragmentAfterStableVisibleHeadCount
            $latestHelperLateFragmentAfterVisibleRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_visible_recovery_count' -DefaultValue $latestHelperLateFragmentAfterVisibleRecoveryCount
            $latestHelperActionableLateFragmentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_late_fragment_count' -DefaultValue $latestHelperActionableLateFragmentCount
            $latestHelperRecoveryRunwayContiguousFollowerBufferCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_contiguous_follower_buffer_count' -DefaultValue $latestHelperRecoveryRunwayContiguousFollowerBufferCount
            $latestHelperRecoveryRunwayContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_contiguous_follower_apply_count' -DefaultValue $latestHelperRecoveryRunwayContiguousFollowerApplyCount
            $latestHelperRecoveryRunwayAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_abort_count' -DefaultValue $latestHelperRecoveryRunwayAbortCount
            $latestHelperProtectedRecoveryDeliveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'protected_recovery_delivery_count' -DefaultValue $latestHelperProtectedRecoveryDeliveryCount
            $latestHelperRecoveryProgressCorridorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_count' -DefaultValue $latestHelperRecoveryProgressCorridorCount
            $latestHelperRecoveryProgressCorridorSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_success_count' -DefaultValue $latestHelperRecoveryProgressCorridorSuccessCount
            $latestHelperRecoveryProgressCorridorAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_abort_count' -DefaultValue $latestHelperRecoveryProgressCorridorAbortCount
            $latestHelperRecoveryProgressCorridorAppliedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_applied_count' -DefaultValue $latestHelperRecoveryProgressCorridorAppliedCount
            $latestHelperRecoveryKeyframePendingVisibleApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_pending_visible_apply_count' -DefaultValue $latestHelperRecoveryKeyframePendingVisibleApplyCount
            $latestHelperStartupCorridorBufferedFollowerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_buffered_follower_count' -DefaultValue $latestHelperStartupCorridorBufferedFollowerCount
            $latestHelperStartupCorridorReleaseCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_release_count' -DefaultValue $latestHelperStartupCorridorReleaseCount
            $latestHelperStartupCorridorAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_abort_count' -DefaultValue $latestHelperStartupCorridorAbortCount
            $latestHelperStartupCorridorAbortReason = Get-StructuredLogStringField -Pairs $pairs -Key 'startup_corridor_abort_reason' -DefaultValue $latestHelperStartupCorridorAbortReason
            $latestHelperRecoveryWindowActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_active' -DefaultValue $latestHelperRecoveryWindowActive
            $latestHelperActiveRecoveryWindowEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_epoch' -DefaultValue $latestHelperActiveRecoveryWindowEpoch
            $latestHelperActiveRecoveryWindowRecoveryFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_recovery_frame_id' -DefaultValue $latestHelperActiveRecoveryWindowRecoveryFrameId
            $latestHelperRecoveryWindowContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_contiguous_follower_apply_count' -DefaultValue $latestHelperRecoveryWindowContiguousFollowerApplyCount
            $latestHelperLateFragmentAfterSuccessfulRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_successful_recovery_count' -DefaultValue $latestHelperLateFragmentAfterSuccessfulRecoveryCount
            [void]$helperQualitySummaryLines.Add($line)
            while ($helperQualitySummaryLines.Count -gt 8) {
                $helperQualitySummaryLines.RemoveAt(0)
            }
        }

        if ($line -match 'event=screenshare_helper_decode_worker_summary; role=helper_remote;.*max_pending_encoded_depth=([0-9-]+);.*max_pending_decoded_depth=([0-9-]+);.*avg_enqueue_to_decode_start_ms=([0-9.]+);.*avg_enqueue_to_drop_ms=([0-9.]+);.*decode_worker_drop_queue_overflow_count=([0-9-]+);.*decode_worker_drop_age_budget_count=([0-9-]+);.*decode_worker_drop_generation_count=([0-9-]+);.*decode_worker_drop_stopped_count=([0-9-]+)') {
            $latestHelperMaxPendingEncodedDepth = [int]$matches[1]
            $latestHelperMaxPendingDecodedDepth = [int]$matches[2]
            $latestHelperAvgEnqueueToDecodeStartMs = [double]$matches[3]
            $latestHelperAvgEnqueueToDropMs = [double]$matches[4]
            $latestHelperDecodeWorkerDropQueueOverflowCount = [int]$matches[5]
            $latestHelperDecodeWorkerDropAgeBudgetCount = [int]$matches[6]
            $latestHelperDecodeWorkerDropGenerationCount = [int]$matches[7]
            $latestHelperDecodeWorkerDropStoppedCount = [int]$matches[8]
        }

        if ($line -like '*event=screenshare_helper_upstream_latency_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperUpstreamCaptureToFrameReadyAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_frame_ready_avg_ms' -DefaultValue $latestHelperUpstreamCaptureToFrameReadyAvgMs
            $latestHelperUpstreamCaptureToFrameReadyMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_frame_ready_median_ms' -DefaultValue $latestHelperUpstreamCaptureToFrameReadyMedianMs
            $latestHelperUpstreamCaptureToFrameReadyP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_frame_ready_p95_ms' -DefaultValue $latestHelperUpstreamCaptureToFrameReadyP95Ms
            $latestHelperUpstreamCaptureToFrameReadyMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_frame_ready_max_ms' -DefaultValue $latestHelperUpstreamCaptureToFrameReadyMaxMs
            $latestHelperUpstreamFrameReadyToViewerAcceptAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'frame_ready_to_viewer_accept_avg_ms' -DefaultValue $latestHelperUpstreamFrameReadyToViewerAcceptAvgMs
            $latestHelperUpstreamFrameReadyToViewerAcceptMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'frame_ready_to_viewer_accept_median_ms' -DefaultValue $latestHelperUpstreamFrameReadyToViewerAcceptMedianMs
            $latestHelperUpstreamFrameReadyToViewerAcceptP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'frame_ready_to_viewer_accept_p95_ms' -DefaultValue $latestHelperUpstreamFrameReadyToViewerAcceptP95Ms
            $latestHelperUpstreamFrameReadyToViewerAcceptMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'frame_ready_to_viewer_accept_max_ms' -DefaultValue $latestHelperUpstreamFrameReadyToViewerAcceptMaxMs
            $latestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'viewer_accept_to_decode_enqueue_avg_ms' -DefaultValue $latestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs
            $latestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'viewer_accept_to_decode_enqueue_median_ms' -DefaultValue $latestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs
            $latestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'viewer_accept_to_decode_enqueue_p95_ms' -DefaultValue $latestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms
            $latestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'viewer_accept_to_decode_enqueue_max_ms' -DefaultValue $latestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs
            $latestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_enqueue_to_decode_start_avg_ms' -DefaultValue $latestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs
            $latestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_enqueue_to_decode_start_median_ms' -DefaultValue $latestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs
            $latestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_enqueue_to_decode_start_p95_ms' -DefaultValue $latestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms
            $latestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_enqueue_to_decode_start_max_ms' -DefaultValue $latestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs
            $latestHelperUpstreamCaptureToDecodeStartAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_decode_start_avg_ms' -DefaultValue $latestHelperUpstreamCaptureToDecodeStartAvgMs
            $latestHelperUpstreamCaptureToDecodeStartMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_decode_start_median_ms' -DefaultValue $latestHelperUpstreamCaptureToDecodeStartMedianMs
            $latestHelperUpstreamCaptureToDecodeStartP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_decode_start_p95_ms' -DefaultValue $latestHelperUpstreamCaptureToDecodeStartP95Ms
            $latestHelperUpstreamCaptureToDecodeStartMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_decode_start_max_ms' -DefaultValue $latestHelperUpstreamCaptureToDecodeStartMaxMs
            $latestHelperUpstreamWorstEpochByCaptureToDecodeStart = Get-StructuredLogIntField -Pairs $pairs -Key 'worst_epoch_by_capture_to_decode_start' -DefaultValue $latestHelperUpstreamWorstEpochByCaptureToDecodeStart
            $latestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'worst_epoch_capture_to_decode_start_avg_ms' -DefaultValue $latestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs
            $latestHelperDominantUpstreamLatencyStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_upstream_latency_stage' -DefaultValue $latestHelperDominantUpstreamLatencyStage
            [void]$helperUpstreamLatencySummaryLines.Add($line)
            while ($helperUpstreamLatencySummaryLines.Count -gt 8) {
                $helperUpstreamLatencySummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_ready_path_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperReadyPathCaptureToFirstFragmentObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_first_fragment_observed_avg_ms' -DefaultValue $latestHelperReadyPathCaptureToFirstFragmentObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperReadyPathCaptureToFirstFragmentObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_first_fragment_observed_median_ms' -DefaultValue $latestHelperReadyPathCaptureToFirstFragmentObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperReadyPathCaptureToFirstFragmentObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_first_fragment_observed_p95_ms' -DefaultValue $latestHelperReadyPathCaptureToFirstFragmentObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperReadyPathCaptureToFirstFragmentObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_first_fragment_observed_max_ms' -DefaultValue $latestHelperReadyPathCaptureToFirstFragmentObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'first_fragment_to_last_fragment_observed_avg_ms' -DefaultValue $latestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'first_fragment_to_last_fragment_observed_median_ms' -DefaultValue $latestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'first_fragment_to_last_fragment_observed_p95_ms' -DefaultValue $latestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'first_fragment_to_last_fragment_observed_max_ms' -DefaultValue $latestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_fragment_to_assembly_complete_avg_ms' -DefaultValue $latestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_fragment_to_assembly_complete_median_ms' -DefaultValue $latestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'last_fragment_to_assembly_complete_p95_ms' -DefaultValue $latestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_fragment_to_assembly_complete_max_ms' -DefaultValue $latestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'assembly_complete_to_frame_emitted_avg_ms' -DefaultValue $latestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 13
            $latestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'assembly_complete_to_frame_emitted_median_ms' -DefaultValue $latestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 14
            $latestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'assembly_complete_to_frame_emitted_p95_ms' -DefaultValue $latestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 15
            $latestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'assembly_complete_to_frame_emitted_max_ms' -DefaultValue $latestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 16
            $latestHelperDominantReadyPathStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_ready_path_stage' -DefaultValue $latestHelperDominantReadyPathStage
            [void]$helperReadyPathSummaryLines.Add($line)
            while ($helperReadyPathSummaryLines.Count -gt 8) {
                $helperReadyPathSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_receive_path_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperReceivePathCaptureToEnvelopeSendAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_envelope_send_avg_ms' -DefaultValue $latestHelperReceivePathCaptureToEnvelopeSendAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperReceivePathCaptureToEnvelopeSendMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_envelope_send_median_ms' -DefaultValue $latestHelperReceivePathCaptureToEnvelopeSendMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperReceivePathCaptureToEnvelopeSendP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_envelope_send_p95_ms' -DefaultValue $latestHelperReceivePathCaptureToEnvelopeSendP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperReceivePathCaptureToEnvelopeSendMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_envelope_send_max_ms' -DefaultValue $latestHelperReceivePathCaptureToEnvelopeSendMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_ingress_avg_ms' -DefaultValue $latestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_ingress_median_ms' -DefaultValue $latestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_ingress_p95_ms' -DefaultValue $latestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_ingress_max_ms' -DefaultValue $latestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_ingress_to_envelope_parsed_avg_ms' -DefaultValue $latestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_ingress_to_envelope_parsed_median_ms' -DefaultValue $latestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_ingress_to_envelope_parsed_p95_ms' -DefaultValue $latestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_ingress_to_envelope_parsed_max_ms' -DefaultValue $latestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_parsed_to_secure_decrypt_avg_ms' -DefaultValue $latestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 13
            $latestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_parsed_to_secure_decrypt_median_ms' -DefaultValue $latestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 14
            $latestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_parsed_to_secure_decrypt_p95_ms' -DefaultValue $latestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 15
            $latestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_parsed_to_secure_decrypt_max_ms' -DefaultValue $latestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 16
            $latestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'secure_decrypt_to_fragment_deserialize_avg_ms' -DefaultValue $latestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 17
            $latestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'secure_decrypt_to_fragment_deserialize_median_ms' -DefaultValue $latestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 18
            $latestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'secure_decrypt_to_fragment_deserialize_p95_ms' -DefaultValue $latestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 19
            $latestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'secure_decrypt_to_fragment_deserialize_max_ms' -DefaultValue $latestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 20
            $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_deserialize_to_first_fragment_observed_avg_ms' -DefaultValue $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 21
            $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_deserialize_to_first_fragment_observed_median_ms' -DefaultValue $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 22
            $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_deserialize_to_first_fragment_observed_p95_ms' -DefaultValue $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 23
            $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_deserialize_to_first_fragment_observed_max_ms' -DefaultValue $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 24
            $latestHelperDominantReceivePathStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_receive_path_stage' -DefaultValue $latestHelperDominantReceivePathStage
            [void]$helperReceivePathSummaryLines.Add($line)
            while ($helperReceivePathSummaryLines.Count -gt 8) {
                $helperReceivePathSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_bridge_ingress_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_message_observed_avg_ms' -DefaultValue $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_message_observed_median_ms' -DefaultValue $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_message_observed_p95_ms' -DefaultValue $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_message_observed_max_ms' -DefaultValue $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_message_observed_to_binary_frame_decoded_avg_ms' -DefaultValue $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_message_observed_to_binary_frame_decoded_median_ms' -DefaultValue $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_message_observed_to_binary_frame_decoded_p95_ms' -DefaultValue $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_message_observed_to_binary_frame_decoded_max_ms' -DefaultValue $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_frame_decoded_to_bridge_ingress_avg_ms' -DefaultValue $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_frame_decoded_to_bridge_ingress_median_ms' -DefaultValue $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_frame_decoded_to_bridge_ingress_p95_ms' -DefaultValue $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_frame_decoded_to_bridge_ingress_max_ms' -DefaultValue $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperDominantBridgeIngressStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_bridge_ingress_stage' -DefaultValue $latestHelperDominantBridgeIngressStage
            [void]$helperBridgeIngressSummaryLines.Add($line)
            while ($helperBridgeIngressSummaryLines.Count -gt 8) {
                $helperBridgeIngressSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_nkn_receive_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_sdk_handle_msg_entered_avg_ms' -DefaultValue $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_sdk_handle_msg_entered_median_ms' -DefaultValue $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_sdk_handle_msg_entered_p95_ms' -DefaultValue $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_sdk_handle_msg_entered_max_ms' -DefaultValue $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sdk_handle_msg_entered_to_client_message_dispatch_avg_ms' -DefaultValue $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sdk_handle_msg_entered_to_client_message_dispatch_median_ms' -DefaultValue $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sdk_handle_msg_entered_to_client_message_dispatch_p95_ms' -DefaultValue $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sdk_handle_msg_entered_to_client_message_dispatch_max_ms' -DefaultValue $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'client_message_dispatch_to_multiclient_message_dispatch_avg_ms' -DefaultValue $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'client_message_dispatch_to_multiclient_message_dispatch_median_ms' -DefaultValue $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'client_message_dispatch_to_multiclient_message_dispatch_p95_ms' -DefaultValue $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'client_message_dispatch_to_multiclient_message_dispatch_max_ms' -DefaultValue $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'multiclient_message_dispatch_to_bridge_message_observed_avg_ms' -DefaultValue $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 13
            $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'multiclient_message_dispatch_to_bridge_message_observed_median_ms' -DefaultValue $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 14
            $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'multiclient_message_dispatch_to_bridge_message_observed_p95_ms' -DefaultValue $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 15
            $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'multiclient_message_dispatch_to_bridge_message_observed_max_ms' -DefaultValue $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 16
            $latestHelperDominantNknReceiveStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_nkn_receive_stage' -DefaultValue $latestHelperDominantNknReceiveStage
            [void]$helperNknReceiveSummaryLines.Add($line)
            while ($helperNknReceiveSummaryLines.Count -gt 8) {
                $helperNknReceiveSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_ws_receive_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_ws_receiver_write_entered_avg_ms' -DefaultValue $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_ws_receiver_write_entered_median_ms' -DefaultValue $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_ws_receiver_write_entered_p95_ms' -DefaultValue $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_ws_receiver_write_entered_max_ms' -DefaultValue $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_receiver_write_entered_to_ws_message_emitted_avg_ms' -DefaultValue $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_receiver_write_entered_to_ws_message_emitted_median_ms' -DefaultValue $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_receiver_write_entered_to_ws_message_emitted_p95_ms' -DefaultValue $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_receiver_write_entered_to_ws_message_emitted_max_ms' -DefaultValue $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_message_emitted_to_sdk_handle_msg_entered_avg_ms' -DefaultValue $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_message_emitted_to_sdk_handle_msg_entered_median_ms' -DefaultValue $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_message_emitted_to_sdk_handle_msg_entered_p95_ms' -DefaultValue $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_message_emitted_to_sdk_handle_msg_entered_max_ms' -DefaultValue $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperDominantWsReceiveStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_ws_receive_stage' -DefaultValue $latestHelperDominantWsReceiveStage
            [void]$helperWsReceiveSummaryLines.Add($line)
            while ($helperWsReceiveSummaryLines.Count -gt 8) {
                $helperWsReceiveSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_socket_receive_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_avg_ms' -DefaultValue $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_median_ms' -DefaultValue $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_p95_ms' -DefaultValue $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_max_ms' -DefaultValue $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'socket_data_event_emitted_to_ws_receiver_write_entered_avg_ms' -DefaultValue $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'socket_data_event_emitted_to_ws_receiver_write_entered_median_ms' -DefaultValue $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms' -DefaultValue $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'socket_data_event_emitted_to_ws_receiver_write_entered_max_ms' -DefaultValue $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperDominantSocketReceiveStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_socket_receive_stage' -DefaultValue $latestHelperDominantSocketReceiveStage
            [void]$helperSocketReceiveSummaryLines.Add($line)
            while ($helperSocketReceiveSummaryLines.Count -gt 32) {
                $helperSocketReceiveSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_bridge_event_loop_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestBridgeEventLoopP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'event_loop_p95_ms' -DefaultValue $latestBridgeEventLoopP95Ms
            $latestBridgeEventLoopMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'event_loop_max_ms' -DefaultValue $latestBridgeEventLoopMaxMs
            $latestBridgeEventLoopMeanMs = Get-StructuredLogIntField -Pairs $pairs -Key 'event_loop_mean_ms' -DefaultValue $latestBridgeEventLoopMeanMs
            $latestBridgeEventLoopSampleWindowMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sample_window_ms' -DefaultValue $latestBridgeEventLoopSampleWindowMs
            [void]$bridgeEventLoopSummaryLines.Add($line)
            while ($bridgeEventLoopSummaryLines.Count -gt 8) {
                $bridgeEventLoopSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_bridge_media_send_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_send_frame_observed_to_queue_enqueue_avg_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs -lt 0) {
                $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_ingress_avg_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_send_frame_observed_to_queue_enqueue_median_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs -lt 0) {
                $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_ingress_median_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_send_frame_observed_to_queue_enqueue_p95_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms -lt 0) {
                $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_ingress_p95_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_send_frame_observed_to_queue_enqueue_max_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs -lt 0) {
                $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_ingress_max_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_enqueue_to_queue_dequeue_avg_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs -lt 0) {
                $parsedBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_queue_avg_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_enqueue_to_queue_dequeue_median_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs -lt 0) {
                $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_queue_median_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_enqueue_to_queue_dequeue_p95_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms -lt 0) {
                $parsedBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_queue_p95_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_enqueue_to_queue_dequeue_max_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs -lt 0) {
                $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_queue_max_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_dequeue_to_media_send_started_avg_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs -lt 0) {
                $parsedBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_setup_avg_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_dequeue_to_media_send_started_median_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs -lt 0) {
                $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_setup_median_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_dequeue_to_media_send_started_p95_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms -lt 0) {
                $parsedBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_setup_p95_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_dequeue_to_media_send_started_max_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs -lt 0) {
                $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_setup_max_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'media_send_started_to_media_send_resolved_avg_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs -lt 0) {
                $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_resolved_avg_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'media_send_started_to_media_send_resolved_median_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs -lt 0) {
                $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_resolved_median_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'media_send_started_to_media_send_resolved_p95_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms -lt 0) {
                $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_resolved_p95_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'media_send_started_to_media_send_resolved_max_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs -lt 0) {
                $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_resolved_max_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendFramesSent = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_sent' -DefaultValue -1
            $parsedBridgeMediaSendFailures = Get-StructuredLogIntField -Pairs $pairs -Key 'send_failures' -DefaultValue -1
            $parsedBridgeMediaSendQueueDrops = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_drops' -DefaultValue -1
            $parsedBridgeMediaSendQueueMode = Get-StructuredLogStringField -Pairs $pairs -Key 'queue_mode' -DefaultValue 'normal'
            $parsedBridgeMediaSendQueueDepth = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_depth' -DefaultValue -1
            $parsedBridgeMediaSendOldestQueuedAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'oldest_queued_age_ms' -DefaultValue -1
            $parsedBridgeMediaSendSampleWindowMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sample_window_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendFramesSent -ge $bestBridgeMediaSendFramesSent) {
                $bestBridgeMediaSendFramesSent = $parsedBridgeMediaSendFramesSent
                $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs
                $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs
                $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms
                $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs
                $latestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = $parsedBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs
                $latestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs
                $latestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = $parsedBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms
                $latestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs
                $latestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = $parsedBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs
                $latestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs
                $latestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = $parsedBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms
                $latestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs
                $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs
                $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs
                $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms
                $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs
                $latestBridgeMediaSendFramesSent = $parsedBridgeMediaSendFramesSent
                $latestBridgeMediaSendFailures = $parsedBridgeMediaSendFailures
                $latestBridgeMediaSendQueueDrops = $parsedBridgeMediaSendQueueDrops
                $latestBridgeMediaSendQueueMode = $parsedBridgeMediaSendQueueMode
                $latestBridgeMediaSendQueueDepth = $parsedBridgeMediaSendQueueDepth
                $latestBridgeMediaSendOldestQueuedAgeMs = $parsedBridgeMediaSendOldestQueuedAgeMs
                $latestBridgeMediaSendSampleWindowMs = $parsedBridgeMediaSendSampleWindowMs
            }
            [void]$bridgeMediaSendSummaryLines.Add($line)
            while ($bridgeMediaSendSummaryLines.Count -gt 8) {
                $bridgeMediaSendSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_bridge_transport_health_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $parsedBridgeTransportHealthSelectedRpc = Get-StructuredLogStringField -Pairs $pairs -Key 'selected_rpc' -DefaultValue '(none)'
            $parsedBridgeTransportHealthSelectedRpcKey = Get-StructuredLogStringField -Pairs $pairs -Key 'selected_rpc_key' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthSelectedRpcKey)) {
                $parsedBridgeTransportHealthSelectedRpcKey = Get-StructuredLogStringField -Pairs $pairs -Key 'srk' -DefaultValue '(none)'
            }

            $parsedBridgeTransportHealthSelectedRpcStage = Get-StructuredLogStringField -Pairs $pairs -Key 'selected_rpc_stage' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthSelectedRpcStage)) {
                $parsedBridgeTransportHealthSelectedRpcStage = Get-StructuredLogStringField -Pairs $pairs -Key 'srs' -DefaultValue 'none'
            }

            $parsedBridgeTransportHealthConnectId = Get-StructuredLogStringField -Pairs $pairs -Key 'connect_id' -DefaultValue '(none)'
            $parsedBridgeTransportHealthConnectKey = Get-StructuredLogStringField -Pairs $pairs -Key 'connect_key' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthConnectKey)) {
                $parsedBridgeTransportHealthConnectKey = Get-StructuredLogStringField -Pairs $pairs -Key 'cky' -DefaultValue '(none)'
            }

            $parsedBridgeTransportHealthReadyEmitted = Get-StructuredLogIntField -Pairs $pairs -Key 'ready_emitted' -DefaultValue -1
            if ($parsedBridgeTransportHealthReadyEmitted -lt 0) {
                $parsedBridgeTransportHealthReadyEmitted = Get-StructuredLogIntField -Pairs $pairs -Key 'rdy' -DefaultValue -1
            }

            $parsedBridgeTransportHealthClientReadyAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'client_ready_age_ms' -DefaultValue -1
            if ($parsedBridgeTransportHealthClientReadyAgeMs -lt 0) {
                $parsedBridgeTransportHealthClientReadyAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'cra' -DefaultValue -1
            }

            $parsedBridgeTransportHealthDisconnectCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'disconnect_count_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthDisconnectCountSinceLast -lt 0) {
                $parsedBridgeTransportHealthDisconnectCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'dcc' -DefaultValue -1
            }

            $parsedBridgeTransportHealthConnectFailedCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'connect_failed_count_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthConnectFailedCountSinceLast -lt 0) {
                $parsedBridgeTransportHealthConnectFailedCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'cfc' -DefaultValue -1
            }

            $parsedBridgeTransportHealthWsErrorCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_error_count_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthWsErrorCountSinceLast -lt 0) {
                $parsedBridgeTransportHealthWsErrorCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'wec' -DefaultValue -1
            }

            $parsedBridgeTransportHealthRpcFallbackAttemptCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'rpc_fallback_attempt_count_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthRpcFallbackAttemptCountSinceLast -lt 0) {
                $parsedBridgeTransportHealthRpcFallbackAttemptCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'rfc' -DefaultValue -1
            }

            $parsedBridgeTransportHealthControlReady = Get-StructuredLogIntField -Pairs $pairs -Key 'control_ready' -DefaultValue -1
            if ($parsedBridgeTransportHealthControlReady -lt 0) {
                $parsedBridgeTransportHealthControlReady = Get-StructuredLogIntField -Pairs $pairs -Key 'cr' -DefaultValue -1
            }

            $parsedBridgeTransportHealthMediaReady = Get-StructuredLogIntField -Pairs $pairs -Key 'media_ready' -DefaultValue -1
            if ($parsedBridgeTransportHealthMediaReady -lt 0) {
                $parsedBridgeTransportHealthMediaReady = Get-StructuredLogIntField -Pairs $pairs -Key 'mr' -DefaultValue -1
            }

            $parsedBridgeTransportHealthBulkReady = Get-StructuredLogIntField -Pairs $pairs -Key 'bulk_ready' -DefaultValue -1
            if ($parsedBridgeTransportHealthBulkReady -lt 0) {
                $parsedBridgeTransportHealthBulkReady = Get-StructuredLogIntField -Pairs $pairs -Key 'br' -DefaultValue -1
            }

            $parsedBridgeTransportHealthFramesSentSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_sent_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthFramesSentSinceLast -lt 0) {
                $parsedBridgeTransportHealthFramesSentSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'fss' -DefaultValue -1
            }

            $parsedBridgeTransportHealthLatestDisconnectReason = Get-StructuredLogStringField -Pairs $pairs -Key 'latest_disconnect_reason' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthLatestDisconnectReason)) {
                $parsedBridgeTransportHealthLatestDisconnectReason = Get-StructuredLogStringField -Pairs $pairs -Key 'ldr' -DefaultValue '(none)'
            }

            $parsedBridgeTransportHealthSampleWindowMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sample_window_ms' -DefaultValue -1

            if ($parsedBridgeTransportHealthFramesSentSinceLast -gt 0 -and
                -not [string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthSelectedRpcKey) -and
                -not [string]::Equals($parsedBridgeTransportHealthSelectedRpcKey, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                [void]$bridgeTransportHealthSelectedRpcKeys.Add($parsedBridgeTransportHealthSelectedRpcKey)
                $latestBridgeTransportHealthUniqueSelectedRpcCount = $bridgeTransportHealthSelectedRpcKeys.Count
            }

            if ($parsedBridgeTransportHealthFramesSentSinceLast -ge $bestBridgeTransportHealthFramesSentSinceLast) {
                $bestBridgeTransportHealthFramesSentSinceLast = $parsedBridgeTransportHealthFramesSentSinceLast
                $latestBridgeTransportHealthSelectedRpc = $parsedBridgeTransportHealthSelectedRpc
                $latestBridgeTransportHealthSelectedRpcKey = $parsedBridgeTransportHealthSelectedRpcKey
                $latestBridgeTransportHealthSelectedRpcStage = $parsedBridgeTransportHealthSelectedRpcStage
                $latestBridgeTransportHealthConnectId = $parsedBridgeTransportHealthConnectId
                $latestBridgeTransportHealthConnectKey = $parsedBridgeTransportHealthConnectKey
                $latestBridgeTransportHealthReadyEmitted = $parsedBridgeTransportHealthReadyEmitted
                $latestBridgeTransportHealthClientReadyAgeMs = $parsedBridgeTransportHealthClientReadyAgeMs
                $latestBridgeTransportHealthDisconnectCountSinceLast = $parsedBridgeTransportHealthDisconnectCountSinceLast
                $latestBridgeTransportHealthConnectFailedCountSinceLast = $parsedBridgeTransportHealthConnectFailedCountSinceLast
                $latestBridgeTransportHealthWsErrorCountSinceLast = $parsedBridgeTransportHealthWsErrorCountSinceLast
                $latestBridgeTransportHealthRpcFallbackAttemptCountSinceLast = $parsedBridgeTransportHealthRpcFallbackAttemptCountSinceLast
                $latestBridgeTransportHealthControlReady = $parsedBridgeTransportHealthControlReady
                $latestBridgeTransportHealthMediaReady = $parsedBridgeTransportHealthMediaReady
                $latestBridgeTransportHealthBulkReady = $parsedBridgeTransportHealthBulkReady
                $latestBridgeTransportHealthFramesSentSinceLast = $parsedBridgeTransportHealthFramesSentSinceLast
                $latestBridgeTransportHealthLatestDisconnectReason = $parsedBridgeTransportHealthLatestDisconnectReason
                $latestBridgeTransportHealthSampleWindowMs = $parsedBridgeTransportHealthSampleWindowMs
            }

            [void]$bridgeTransportHealthSummaryLines.Add($line)
            while ($bridgeTransportHealthSummaryLines.Count -gt 32) {
                $bridgeTransportHealthSummaryLines.RemoveAt(0)
            }
        }

        if ($line -match 'event=screenshare_helper_frame_loss_epoch; role=helper_remote;') {
            [void]$helperEpochLossLines.Add($line)
            while ($helperEpochLossLines.Count -gt 16) {
                $helperEpochLossLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $streamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch'
            $framesEmitted = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_emitted'
            $framesApplied = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_applied'
            if ($streamEpoch -ge 0 -and $framesEmitted -gt 0) {
                $helperEpochVisibleRatioByEpoch[[string]$streamEpoch] = [math]::Round(($framesApplied / [double]$framesEmitted), 4)
            }
        }

        if ($line -like '*event=screenshare_helper_epoch_timeline; role=helper_remote;*') {
            [void]$helperEpochTimelineLines.Add($line)
            while ($helperEpochTimelineLines.Count -gt 16) {
                $helperEpochTimelineLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $streamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch'
            $timeInRecoveryLockMs = Get-StructuredLogIntField -Pairs $pairs -Key 'time_in_recovery_lock_ms'
            if ($streamEpoch -ge 0) {
                $helperEpochRecoveryLockMsByEpoch[[string]$streamEpoch] = $timeInRecoveryLockMs
            }
        }

        if ($line -like '*event=screenshare_helper_reassembler_root_cause_summary; role=helper_remote;*') {
            [void]$helperReassemblerRootCauseSummaryLines.Add($line)
            while ($helperReassemblerRootCauseSummaryLines.Count -gt 16) {
                $helperReassemblerRootCauseSummaryLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $streamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch'
            $dominantRootCause = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_root_cause' -DefaultValue 'none'
            if ($streamEpoch -ge 0) {
                $helperEpochRootCauseByEpoch[[string]$streamEpoch] = $dominantRootCause
                $helperRootCauseSummaryByEpoch[[string]$streamEpoch] = [pscustomobject]@{
                    StreamEpoch = $streamEpoch
                    AppliedHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'applied_head_frame_id'
                    OrderedEmitHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'ordered_emit_head_frame_id'
                    WinningRecoveryFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'winning_recovery_frame_id'
                    FragmentGapBeforeAssemblyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_gap_before_assembly_count'
                    LateFragmentAfterHeadAdvancedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_head_advanced_count'
                    LateFragmentAfterAppliedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_applied_head_count'
                    LateFragmentAfterOrderedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_ordered_head_count'
                    SupersededRecoveryTailCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'superseded_recovery_tail_cleanup_count'
                    RecoveryOwnerReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_replaced_count'
                    OlderEpochCleanupAfterEpochAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'older_epoch_cleanup_after_epoch_advance_count'
                    LateFragmentAfterStableVisibleHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_stable_visible_head_count'
                    ActionableLateFragmentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_late_fragment_count'
                    FutureTailPrunedWhileGapActiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'future_tail_pruned_while_gap_active_count'
                    ProtectedHeadMissingBudgetPressureCount = Get-StructuredLogIntField -Pairs $pairs -Key 'protected_head_missing_budget_pressure_count'
                    RecoveryKeyframeSupersededOrReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_superseded_or_replaced_count'
                    OrderedEmitBlockedThenResyncedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'ordered_emit_blocked_then_resynced_count'
                    DominantRootCause = $dominantRootCause
                }
            }
        }

        if ($line -like '*event=screenshare_helper_recovery_epoch_investigation; role=helper_remote;*') {
            [void]$helperRecoveryEpochInvestigationLines.Add($line)
            while ($helperRecoveryEpochInvestigationLines.Count -gt 16) {
                $helperRecoveryEpochInvestigationLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
        }

        if ($line -like '*event=screenshare_reassembler_recovery_owner_buffered;*' -or
            $line -like '*event=screenshare_reassembler_recovery_owner_replaced;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $sessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($latestHelperSessionId) -or
                [string]::IsNullOrWhiteSpace($sessionId) -or
                [string]::Equals($sessionId, $latestHelperSessionId, [System.StringComparison]::Ordinal)) {
                [void]$helperReassemblerRecoveryOwnerTransitionLines.Add($line)
                while ($helperReassemblerRecoveryOwnerTransitionLines.Count -gt 24) {
                    $helperReassemblerRecoveryOwnerTransitionLines.RemoveAt(0)
                }
            }
        }

        if ($line -like '*event=screenshare_reassembler_actionable_late_fragment;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $sessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($latestHelperSessionId) -or
                [string]::IsNullOrWhiteSpace($sessionId) -or
                [string]::Equals($sessionId, $latestHelperSessionId, [System.StringComparison]::Ordinal)) {
                [void]$helperReassemblerActionableLateFragmentLines.Add($line)
                while ($helperReassemblerActionableLateFragmentLines.Count -gt 24) {
                    $helperReassemblerActionableLateFragmentLines.RemoveAt(0)
                }
            }
        }

        if ($line -like '*event=screenshare_reassembler_older_epoch_cleanup_after_epoch_advance;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $sessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($latestHelperSessionId) -or
                [string]::IsNullOrWhiteSpace($sessionId) -or
                [string]::Equals($sessionId, $latestHelperSessionId, [System.StringComparison]::Ordinal)) {
                [void]$helperReassemblerOlderEpochCleanupLines.Add($line)
                while ($helperReassemblerOlderEpochCleanupLines.Count -gt 24) {
                    $helperReassemblerOlderEpochCleanupLines.RemoveAt(0)
                }
            }
        }

        if ($line -like '*event=screenshare_helper_pressure_epoch_summary; role=helper_remote;*') {
            [void]$helperPressureSummaryLines.Add($line)
            while ($helperPressureSummaryLines.Count -gt 16) {
                $helperPressureSummaryLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $streamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch'
            $dominantPressureBlocker = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_pressure_blocker' -DefaultValue 'none'
            if ($streamEpoch -ge 0) {
                $helperEpochPressureBlockerByEpoch[[string]$streamEpoch] = $dominantPressureBlocker
                $helperPressureSummaryByEpoch[[string]$streamEpoch] = [pscustomobject]@{
                    StreamEpoch = $streamEpoch
                    SteadyVisibleProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_active'
                    SteadyVisibleProgressActivationFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_activation_frame_id'
                    AppliedHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'applied_head_frame_id'
                    StableVisibleHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'stable_visible_head_frame_id'
                    LastSentStableVisibleHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'last_sent_stable_visible_head_frame_id'
                    PressureSendBypassedForVisibleProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pressure_send_bypassed_for_visible_progress_count'
                    ProofKeepaliveSendCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_send_count'
                    ProofKeepaliveTimerDrivenSendCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_timer_driven_send_count'
                    ProofKeepaliveLastHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_last_head_frame_id'
                    ProofKeepaliveLastSendAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_last_send_age_ms'
                    SteadyVisibleProgressClearedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_cleared_count'
                    SteadyVisibleProgressClearedReason = Get-StructuredLogStringField -Pairs $pairs -Key 'steady_visible_progress_cleared_reason'
                    ContinuityLossTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'continuity_loss_ticks'
                    WarmupTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'warmup_ticks'
                    BeforeFirstVisibleApplyTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'before_first_visible_apply_ticks'
                    AfterVisibleRecoveryFrameTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'after_visible_recovery_frame_ticks'
                    AfterVisibleRecoveryFrameSuppressedDueToSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'after_visible_recovery_frame_suppressed_due_to_success_count'
                    SlowApplyCadenceTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'slow_apply_cadence_ticks'
                    HighFrameAgeTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_ticks'
                    HighFrameAgeSuppressedDueToVisibleProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_due_to_visible_progress_count'
                    HighFrameAgeSuppressedDueToHeadAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_due_to_head_advance_count'
                    ActionableHighFrameAgeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_high_frame_age_count'
                    PostRecoveryHighFrameAgeSuppressedTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_high_frame_age_suppressed_ticks'
                    VisibleAppliesDuringSettleCount = Get-StructuredLogIntField -Pairs $pairs -Key 'visible_applies_during_settle_count'
                    RepeatedStaleDropsTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'repeated_stale_drops_ticks'
                    BridgeHealthTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_ticks'
                    BridgeHealthAdvisoryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_advisory_count'
                    BridgeHealthActionableCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_actionable_count'
                    BridgeHealthQuarantineSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_quarantine_suppressed_count'
                    BridgeHealthActionableWithoutQueueOrDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_became_actionable_without_queue_or_drop_count'
                    RecoveryWindowActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_active'
                    RecoveryWindowProgressed = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_progressed'
                    RecoveryWindowSucceeded = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_succeeded'
                    RecoveryWindowProgressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_progressed_count'
                    RecoveryWindowSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_success_count'
                    ActiveRecoveryWindowEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_epoch'
                    ActiveRecoveryWindowRecoveryFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_recovery_frame_id'
                    RecoveryWindowContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_contiguous_follower_apply_count'
                    BaselineEstablished = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_established'
                    BaselineCaptureToRenderMs = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_capture_to_render_ms'
                    AgeExcessMs = Get-StructuredLogIntField -Pairs $pairs -Key 'age_excess_ms'
                    ProgressStallMs = Get-StructuredLogIntField -Pairs $pairs -Key 'progress_stall_ms'
                    BaselineReseedInProgress = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_reseed_in_progress'
                    AgePressureConsecutiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'age_pressure_consecutive_count'
                    CadencePressureConsecutiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_pressure_consecutive_count'
                    CatchUpSuppressedDueToProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'catch_up_suppressed_due_to_progress_count'
                    BaselineFrozenDueToStallCount = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_frozen_due_to_stall_count'
                    BaselineReseedAfterRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_reseed_after_recovery_count'
                    CadenceStallWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_stall_window_count'
                    CadenceStallTriggerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_stall_trigger_count'
                    TimeSpentInHelperWarmupMs = Get-StructuredLogIntField -Pairs $pairs -Key 'time_spent_in_helper_warmup_ms'
                    PostRecoverySettleWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_count'
                    PostRecoverySettleWindowSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_success_count'
                    PostRecoverySettleWindowTimeoutCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_timeout_count'
                    VisibleAppliesBeforePressureReenabled = Get-StructuredLogIntField -Pairs $pairs -Key 'visible_applies_before_pressure_reenabled'
                    DominantPressureBlocker = $dominantPressureBlocker
                }

                $latestHelperPostRecoveryHighFrameAgeSuppressedTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_high_frame_age_suppressed_ticks'
                $latestHelperVisibleAppliesDuringSettleCount = Get-StructuredLogIntField -Pairs $pairs -Key 'visible_applies_during_settle_count'
                $latestHelperPostRecoverySettleWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_count'
                $latestHelperPostRecoverySettleWindowSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_success_count'
                $latestHelperPostRecoverySettleWindowTimeoutCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_timeout_count'
                $latestHelperVisibleAppliesBeforePressureReenabled = Get-StructuredLogIntField -Pairs $pairs -Key 'visible_applies_before_pressure_reenabled'
                $latestHelperRecoveryWindowActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_active'
                $latestHelperRecoveryWindowProgressed = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_progressed'
                $latestHelperRecoveryWindowSucceeded = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_succeeded'
                $latestHelperSteadyVisibleProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_active' -DefaultValue $latestHelperSteadyVisibleProgressActive
                $activationValue = Get-StructuredLogStringField -Pairs $pairs -Key 'steady_visible_progress_activation_frame_id' -DefaultValue $latestHelperSteadyVisibleProgressActivationFrameId
                if ($activationValue -match '^-?[0-9]+$') {
                    $latestHelperSteadyVisibleProgressActivationFrameId = [int64]$activationValue
                }
                $appliedHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'applied_head_frame_id' -DefaultValue $latestHelperAppliedHeadFrameId
                if ($appliedHeadValue -match '^-?[0-9]+$') {
                    $latestHelperAppliedHeadFrameId = [int64]$appliedHeadValue
                }
                $stableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'stable_visible_head_frame_id' -DefaultValue $latestHelperStableVisibleHeadFrameId
                if ($stableVisibleHeadValue -match '^-?[0-9]+$') {
                    $latestHelperStableVisibleHeadFrameId = [int64]$stableVisibleHeadValue
                }
                $lastSentStableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'last_sent_stable_visible_head_frame_id' -DefaultValue $latestHelperLastSentStableVisibleHeadFrameId
                if ($lastSentStableVisibleHeadValue -match '^-?[0-9]+$') {
                    $latestHelperLastSentStableVisibleHeadFrameId = [int64]$lastSentStableVisibleHeadValue
                }
                $latestHelperPressureSendBypassedForVisibleProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pressure_send_bypassed_for_visible_progress_count' -DefaultValue $latestHelperPressureSendBypassedForVisibleProgressCount
                $latestHelperProofKeepaliveSendCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_send_count' -DefaultValue $latestHelperProofKeepaliveSendCount
                $latestHelperProofKeepaliveTimerDrivenSendCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_timer_driven_send_count' -DefaultValue $latestHelperProofKeepaliveTimerDrivenSendCount
                $helperProofKeepaliveHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_proof_keepalive_last_head_frame_id' -DefaultValue $latestHelperProofKeepaliveLastHeadFrameId
                if ($helperProofKeepaliveHeadValue -match '^-?[0-9]+$') {
                    $latestHelperProofKeepaliveLastHeadFrameId = [int64]$helperProofKeepaliveHeadValue
                }
                $latestHelperProofKeepaliveLastSendAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_last_send_age_ms' -DefaultValue $latestHelperProofKeepaliveLastSendAgeMs
                $helperFirstVisibleApplyToSenderFactSendValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_first_visible_apply_to_sender_fact_send_ms' -DefaultValue ''
                if ($helperFirstVisibleApplyToSenderFactSendValue -match '^-?[0-9]+$') {
                    $latestHelperFirstVisibleApplyToSenderFactSendMs = [int64]$helperFirstVisibleApplyToSenderFactSendValue
                }
                $latestHelperSteadyVisibleProgressClearedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_cleared_count' -DefaultValue $latestHelperSteadyVisibleProgressClearedCount
                $latestHelperSteadyVisibleProgressClearedReason = Get-StructuredLogStringField -Pairs $pairs -Key 'steady_visible_progress_cleared_reason' -DefaultValue $latestHelperSteadyVisibleProgressClearedReason
                $latestHelperHighFrameAgeSuppressedDueToVisibleProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_due_to_visible_progress_count' -DefaultValue $latestHelperHighFrameAgeSuppressedDueToVisibleProgressCount
                $latestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_due_to_head_advance_count' -DefaultValue $latestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount
                $latestHelperActionableHighFrameAgeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_high_frame_age_count' -DefaultValue $latestHelperActionableHighFrameAgeCount
                $latestHelperBridgeHealthAdvisoryCount = [Math]::Max(
                    $latestHelperBridgeHealthAdvisoryCount,
                    (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_advisory_count' -DefaultValue $latestHelperBridgeHealthAdvisoryCount))
                $latestHelperBridgeHealthActionableCount = [Math]::Max(
                    $latestHelperBridgeHealthActionableCount,
                    (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_actionable_count' -DefaultValue $latestHelperBridgeHealthActionableCount))
                $latestHelperBridgeHealthQuarantineSuppressedCount = [Math]::Max(
                    $latestHelperBridgeHealthQuarantineSuppressedCount,
                    (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_quarantine_suppressed_count' -DefaultValue $latestHelperBridgeHealthQuarantineSuppressedCount))
                $latestHelperBridgeHealthActionableWithoutQueueOrDropCount = [Math]::Max(
                    $latestHelperBridgeHealthActionableWithoutQueueOrDropCount,
                    (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_became_actionable_without_queue_or_drop_count' -DefaultValue $latestHelperBridgeHealthActionableWithoutQueueOrDropCount))
                $latestHelperRecoveryWindowProgressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_progressed_count'
                $latestHelperRecoveryWindowSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_success_count'
                $latestHelperActiveRecoveryWindowEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_epoch'
                $latestHelperActiveRecoveryWindowRecoveryFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_recovery_frame_id'
                $latestHelperRecoveryWindowContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_contiguous_follower_apply_count'
                $latestHelperAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'after_visible_recovery_frame_suppressed_due_to_success_count'
                $latestHelperBaselineEstablished = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_established'
                $latestHelperBaselineCaptureToRenderMs = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_capture_to_render_ms'
                $latestHelperAgeExcessMs = Get-StructuredLogIntField -Pairs $pairs -Key 'age_excess_ms'
                $latestHelperProgressStallMs = Get-StructuredLogIntField -Pairs $pairs -Key 'progress_stall_ms'
                $latestHelperBaselineReseedInProgress = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_reseed_in_progress'
                $latestHelperAgePressureConsecutiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'age_pressure_consecutive_count'
                $latestHelperCadencePressureConsecutiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_pressure_consecutive_count'
                $latestHelperCatchUpSuppressedDueToProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'catch_up_suppressed_due_to_progress_count'
                $latestHelperBaselineFrozenDueToStallCount = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_frozen_due_to_stall_count'
                $latestHelperBaselineReseedAfterRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_reseed_after_recovery_count'
                $latestHelperCadenceStallWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_stall_window_count'
                $latestHelperCadenceStallTriggerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_stall_trigger_count'
            }
        }

        if ($line -like '*event=screenshare_sender_promotion_blocked;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $blockers = Get-StructuredLogStringField -Pairs $pairs -Key 'blockers'
            $helperSteadyProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_steady_visible_progress_active'
            $helperProgressProofSatisfied = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_progress_proof_satisfied'
            $senderHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_stable_visible_head_frame_id' -DefaultValue '(none)'
            $senderStableVisibleHeadFrameId = if ($senderHeadValue -match '^-?[0-9]+$') { [int64]$senderHeadValue } else { -1 }
            $helperPressureBlockerActive = $blockers -match '(^|,)helper_pressure(,|$)'
            $helperWarmupBlockerActive = $blockers -match '(^|,)helper_warmup(,|$)'

            if ($blockers -match '(^|,)helper_apply_count(,|$)') {
                if ($helperSteadyProgressActive -gt 0 -and $senderStableVisibleHeadFrameId -ge 0 -and $helperProgressProofSatisfied -le 0) {
                    $promotionBlockedByStaleHelperProofCount++
                }
                else {
                    $promotionBlockedByMissingHelperProofCount++
                }
            }

            if ($blockers -match '(^|,)encode_over_budget(,|$)') {
                if ($helperProgressProofSatisfied -gt 0 -and -not $helperPressureBlockerActive -and -not $helperWarmupBlockerActive) {
                    $promotionBlockedByEncodeBudgetCount++
                }
            }
        }

        if ($line -like '*event=screenshare_reduced_promotion_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestPromotionBlockerRateGateTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_rate_gate_ticks'
            $latestPromotionBlockerHelperPressureTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_helper_pressure_ticks'
            $latestPromotionBlockerHelperWarmupTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_helper_warmup_ticks'
            $latestPromotionBlockerHelperApplyCountTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_helper_apply_count_ticks' -FallbackAfterKey 'promotion_blocker_helper_warmup_ticks' -FallbackOffset 1
            $latestPromotionBlockerBridgeHealthTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_bridge_health_ticks'
            $latestPromotionBlockerRecoveryLockTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_recovery_lock_ticks'
            $latestPromotionBlockerQueueEvictTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_queue_evict_ticks'
            $latestPromotionBlockerCaptureAgeTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_capture_age_ticks'
            $latestPromotionBlockerEncodeBudgetTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_encode_budget_ticks'
            $latestPromotionBlockerTransitionGraceTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_transition_grace_ticks' -FallbackAfterKey 'promotion_blocker_encode_budget_ticks' -FallbackOffset 1
            $latestPromotionEncodeSoftSpikeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_encode_soft_spike_count'
            $latestPromotionEncodeSoftSpikeResetSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_encode_soft_spike_reset_suppressed_count'
            $promotionBlockedByEncodeBudgetAloneCount = Get-StructuredLogIntField -Pairs $pairs -Key 'blocked_by_encode_budget_alone' -DefaultValue $promotionBlockedByEncodeBudgetAloneCount
            $latestHealthyTickResetReasonCounts = Get-StructuredLogStringField -Pairs $pairs -Key 'healthy_tick_reset_reason_counts'
            $latestReducedPromotionRecentEntries = Get-StructuredLogStringField -Pairs $pairs -Key 'recent_entries'
            [void]$reducedPromotionSummaryLines.Add($line)
            while ($reducedPromotionSummaryLines.Count -gt 8) {
                $reducedPromotionSummaryLines.RemoveAt(0)
            }
        }

        if ($line -match 'event=screenshare_viewer_stale_frame_dropped; role=helper_remote;') {
            $helperStaleDrops++
        }

        if ($line -match 'event=screenshare_receiver_stale_frame_superseded;') {
            $receiverSupersededFrames++
        }

        if ($line -match 'event=helper_local_peer_address_ready;.*run_id=([^;]+);\s*listener_generation=(\d+)') {
            $latestHelperRunId = [string]$matches[1]
            $latestHelperListenerGeneration = [int64]$matches[2]
        }
    }

    $captureValues = @($captureToSend.ToArray())
    $helperValues = @($helperApply.ToArray())
    $worstVisibleApplyRatioEpoch = -1
    $worstVisibleApplyRatio = -1.0
    foreach ($entry in $helperEpochVisibleRatioByEpoch.GetEnumerator()) {
        $epochValue = [int]$entry.Key
        $ratioValue = [double]$entry.Value
        if ($worstVisibleApplyRatioEpoch -lt 0 -or $ratioValue -lt $worstVisibleApplyRatio -or ($ratioValue -eq $worstVisibleApplyRatio -and $epochValue -gt $worstVisibleApplyRatioEpoch)) {
            $worstVisibleApplyRatioEpoch = $epochValue
            $worstVisibleApplyRatio = $ratioValue
        }
    }

    $worstRecoveryLockEpoch = -1
    $worstRecoveryLockMs = -1
    foreach ($entry in $helperEpochRecoveryLockMsByEpoch.GetEnumerator()) {
        $epochValue = [int]$entry.Key
        $durationValue = [int64]$entry.Value
        if ($worstRecoveryLockEpoch -lt 0 -or $durationValue -gt $worstRecoveryLockMs -or ($durationValue -eq $worstRecoveryLockMs -and $epochValue -gt $worstRecoveryLockEpoch)) {
            $worstRecoveryLockEpoch = $epochValue
            $worstRecoveryLockMs = $durationValue
        }
    }

    $effectiveMediaPlaneActive = if (
        $latestMediaPlaneAttached -gt 0 -and
        $latestMediaPlaneFramesSent -gt 0 -and
        $latestBridgeMediaMessagesReceived -gt 0 -and
        $steadyStateControlFallbackQueuedCount -eq 0) { 1 } else { 0 }
    $recoveryUsedControlFallback = if ($recoveryControlFallbackQueuedCount -gt 0 -or $latestRecoveryBurstControlFallbackCount -gt 0) { 1 } else { 0 }
    $steadyStateUsedControlFallback = if ($steadyStateControlFallbackQueuedCount -gt 0) { 1 } else { 0 }

    $aggregateFragmentGapBeforeAssemblyCount = 0
    $aggregateLateFragmentAfterHeadAdvancedCount = 0
    $aggregateLateFragmentAfterAppliedHeadCount = 0
    $aggregateLateFragmentAfterOrderedHeadCount = 0
    $aggregateLateFragmentAfterStableVisibleHeadCount = 0
    $aggregateFutureTailPrunedWhileGapActiveCount = 0
    $aggregateProtectedHeadMissingBudgetPressureCount = 0
    $aggregateRecoveryKeyframeSupersededOrReplacedCount = 0
    $aggregateOrderedEmitBlockedThenResyncedCount = 0
    $aggregateRecoveryOwnerReplacedCount = 0
    $aggregateOlderEpochCleanupAfterEpochAdvanceCount = 0
    $aggregateActionableLateFragmentCount = 0
    foreach ($entry in $helperRootCauseSummaryByEpoch.Values) {
        $aggregateFragmentGapBeforeAssemblyCount += [int64]$entry.FragmentGapBeforeAssemblyCount
        $aggregateLateFragmentAfterHeadAdvancedCount += [int64]$entry.LateFragmentAfterHeadAdvancedCount
        $aggregateLateFragmentAfterAppliedHeadCount += [int64]$entry.LateFragmentAfterAppliedHeadCount
        $aggregateLateFragmentAfterOrderedHeadCount += [int64]$entry.LateFragmentAfterOrderedHeadCount
        $aggregateLateFragmentAfterStableVisibleHeadCount += [int64]$entry.LateFragmentAfterStableVisibleHeadCount
        $aggregateFutureTailPrunedWhileGapActiveCount += [int64]$entry.FutureTailPrunedWhileGapActiveCount
        $aggregateProtectedHeadMissingBudgetPressureCount += [int64]$entry.ProtectedHeadMissingBudgetPressureCount
        $aggregateRecoveryKeyframeSupersededOrReplacedCount += [int64]$entry.RecoveryKeyframeSupersededOrReplacedCount
        $aggregateOrderedEmitBlockedThenResyncedCount += [int64]$entry.OrderedEmitBlockedThenResyncedCount
        $aggregateRecoveryOwnerReplacedCount += [int64]$entry.RecoveryOwnerReplacedCount
        $aggregateOlderEpochCleanupAfterEpochAdvanceCount += [int64]$entry.OlderEpochCleanupAfterEpochAdvanceCount
        $aggregateActionableLateFragmentCount += [int64]$entry.ActionableLateFragmentCount
    }

    $dominantReassemblerRootCause = $latestHelperDominantReassemblerRootCause
    if ([string]::IsNullOrWhiteSpace($dominantReassemblerRootCause) -or [string]::Equals($dominantReassemblerRootCause, 'none', [System.StringComparison]::OrdinalIgnoreCase)) {
        $dominantReassemblerRootCause = Get-TopNamedCount -Candidates @(
            [pscustomobject]@{ Name = 'fragment_gap_before_assembly'; Count = $aggregateFragmentGapBeforeAssemblyCount },
            [pscustomobject]@{ Name = 'late_fragment_after_head_advanced'; Count = $aggregateLateFragmentAfterHeadAdvancedCount },
            [pscustomobject]@{ Name = 'future_tail_pruned_while_gap_active'; Count = $aggregateFutureTailPrunedWhileGapActiveCount },
            [pscustomobject]@{ Name = 'protected_head_missing_budget_pressure'; Count = $aggregateProtectedHeadMissingBudgetPressureCount },
            [pscustomobject]@{ Name = 'recovery_keyframe_superseded_or_replaced'; Count = $aggregateRecoveryKeyframeSupersededOrReplacedCount },
            [pscustomobject]@{ Name = 'ordered_emit_blocked_then_resynced'; Count = $aggregateOrderedEmitBlockedThenResyncedCount }
        )
    }

    $aggregateContinuityLossTicks = 0
    $aggregateWarmupTicks = 0
    $aggregateBeforeFirstVisibleApplyTicks = 0
    $aggregateAfterVisibleRecoveryFrameTicks = 0
    $aggregateSlowApplyCadenceTicks = 0
    $aggregateHighFrameAgeTicks = 0
    $aggregateHighFrameAgeSuppressedDueToVisibleProgressCount = 0
    $aggregateHighFrameAgeSuppressedDueToHeadAdvanceCount = 0
    $aggregateActionableHighFrameAgeCount = 0
    $aggregatePostRecoveryHighFrameAgeSuppressedTicks = 0
    $aggregateRepeatedStaleDropsTicks = 0
    $aggregateBridgeHealthTicks = 0
    foreach ($entry in $helperPressureSummaryByEpoch.Values) {
        $aggregateContinuityLossTicks += [int64]$entry.ContinuityLossTicks
        $aggregateWarmupTicks += [int64]$entry.WarmupTicks
        $aggregateBeforeFirstVisibleApplyTicks += [int64]$entry.BeforeFirstVisibleApplyTicks
        $aggregateAfterVisibleRecoveryFrameTicks += [int64]$entry.AfterVisibleRecoveryFrameTicks
        $aggregateSlowApplyCadenceTicks += [int64]$entry.SlowApplyCadenceTicks
        $aggregateHighFrameAgeTicks += [int64]$entry.HighFrameAgeTicks
        $aggregateHighFrameAgeSuppressedDueToVisibleProgressCount += [int64]$entry.HighFrameAgeSuppressedDueToVisibleProgressCount
        $aggregateHighFrameAgeSuppressedDueToHeadAdvanceCount += [int64]$entry.HighFrameAgeSuppressedDueToHeadAdvanceCount
        $aggregateActionableHighFrameAgeCount += [int64]$entry.ActionableHighFrameAgeCount
        $aggregatePostRecoveryHighFrameAgeSuppressedTicks += [int64]$entry.PostRecoveryHighFrameAgeSuppressedTicks
        $aggregateRepeatedStaleDropsTicks += [int64]$entry.RepeatedStaleDropsTicks
        $aggregateBridgeHealthTicks += [int64]$entry.BridgeHealthTicks
    }

    $latestPromotionEntryShowsStableProof = $false
    if (-not [string]::IsNullOrWhiteSpace($latestReducedPromotionRecentEntries) -and
        -not [string]::Equals($latestReducedPromotionRecentEntries, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
        $recentPromotionEntries = @($latestReducedPromotionRecentEntries -split '~')
        if ($recentPromotionEntries.Count -gt 0) {
            $latestPromotionEntry = $recentPromotionEntries[$recentPromotionEntries.Count - 1]
            if ($latestPromotionEntry -match '\|steady=1\|' -and $latestPromotionEntry -match '\|head=[0-9]+') {
                $latestPromotionEntryShowsStableProof = $true
            }
        }
    }

    $helperVisibleHeadRuntimeSenderMismatch = 0
    if ($latestHelperStableVisibleHeadFrameId -ge 0 -and
        $latestHelperSteadyVisibleProgressActive -gt 0 -and
        -not $latestPromotionEntryShowsStableProof -and
        (($promotionBlockedByMissingHelperProofCount + $promotionBlockedByStaleHelperProofCount) -gt 0)) {
        $helperVisibleHeadRuntimeSenderMismatch = 1
    }

    $effectiveDominantHelperAdmissionRejectReason = if (
        [string]::Equals($latestHelperDominantAdmissionRejectReason, 'waiting_for_recovery_keyframe', [System.StringComparison]::OrdinalIgnoreCase) -and
        [Math]::Max(0, $latestHelperRecoveryWaitRejectBeforeRunwayCount) -eq 0 -and
        [Math]::Max(0, $latestHelperWaitingForRecoveryKeyframeRejectCount) -eq 0 -and
        [Math]::Max(0, $latestHelperPreCandidateGapTailEmittedToViewerCount) -eq 0
    ) {
        'none'
    }
    elseif ([string]::IsNullOrWhiteSpace($latestHelperDominantAdmissionRejectReason)) {
        'none'
    }
    else {
        $latestHelperDominantAdmissionRejectReason
    }

    $dominantHelperPressureBlocker = Get-TopNamedCount -Candidates @(
        [pscustomobject]@{ Name = 'continuity_loss'; Count = $aggregateContinuityLossTicks },
        [pscustomobject]@{ Name = 'warmup'; Count = $aggregateWarmupTicks },
        [pscustomobject]@{ Name = 'before_first_visible_apply'; Count = $aggregateBeforeFirstVisibleApplyTicks },
        [pscustomobject]@{ Name = 'after_visible_recovery_frame'; Count = $aggregateAfterVisibleRecoveryFrameTicks },
        [pscustomobject]@{ Name = 'slow_apply_cadence'; Count = $aggregateSlowApplyCadenceTicks },
        [pscustomobject]@{ Name = 'high_frame_age'; Count = $aggregateHighFrameAgeTicks },
        [pscustomobject]@{ Name = 'repeated_stale_drops'; Count = $aggregateRepeatedStaleDropsTicks },
        [pscustomobject]@{ Name = 'bridge_health'; Count = $aggregateBridgeHealthTicks }
    )

    $latestOrdinaryRawLossCount = [Math]::Max(0, $latestRawFramesReplacedBeforeEncodeSlot) + [Math]::Max(0, $latestSourceSupersededPendingFrames)
    $latestOrdinarySenderLossCount = [Math]::Max(0, $latestFramesReplacedBeforeSendSlot) + [Math]::Max(0, $latestFramesDroppedByQueueEvict)
    $latestOrdinaryHelperLossCount = [Math]::Max(0, $latestHelperDecodeQueueOverflowCount) + [Math]::Max(0, $latestHelperDecodeAgeBudgetCount) + [Math]::Max(0, $latestHelperDecodedApplyQueueOverflowCount) + [Math]::Max(0, $latestHelperDecodedFrameReplacedBeforeApplyCount)
    $dominantOrdinaryFreshnessLossBoundary = Get-TopNamedCount -Candidates @(
        [pscustomobject]@{ Name = 'raw'; Count = $latestOrdinaryRawLossCount },
        [pscustomobject]@{ Name = 'sender'; Count = $latestOrdinarySenderLossCount },
        [pscustomobject]@{ Name = 'helper'; Count = $latestOrdinaryHelperLossCount }
    )

    $resolvedHealthSenderOperatingState = if ([string]::IsNullOrWhiteSpace($latestHealthSenderOperatingState)) { 'normal' } else { $latestHealthSenderOperatingState }
    $resolvedHealthSenderGuardState = if ([string]::IsNullOrWhiteSpace($latestHealthSenderGuardState)) { 'none' } else { $latestHealthSenderGuardState }
    $resolvedHealthHelperSessionPhase = if ([string]::IsNullOrWhiteSpace($latestHealthHelperSessionPhase)) { 'no_visible_baseline' } else { $latestHealthHelperSessionPhase }
    $resolvedHealthHelperRecoveryMechanism = if ([string]::IsNullOrWhiteSpace($latestHealthHelperRecoveryMechanism)) { 'none' } else { $latestHealthHelperRecoveryMechanism }
    $resolvedHealthDominantLossClass = if ([string]::IsNullOrWhiteSpace($latestHealthDominantLossClass)) { 'benign_stale_cleanup' } else { $latestHealthDominantLossClass }
    $resolvedHealthDominantPressureBlocker = if ([string]::IsNullOrWhiteSpace($latestHealthDominantPressureBlocker)) { 'none' } else { $latestHealthDominantPressureBlocker }
    $resolvedHealthDominantTroubleDomain = if ([string]::IsNullOrWhiteSpace($latestHealthDominantTroubleDomain)) { 'none' } else { $latestHealthDominantTroubleDomain }
    $resolvedHealthRecoveryActive = [Math]::Max(0, $latestHealthRecoveryActive)
    $resolvedHealthBaselineEstablished = [Math]::Max(0, $latestHealthBaselineEstablished)
    $resolvedHealthSteadyVisibleProgressActive = [Math]::Max(0, $latestHealthSteadyVisibleProgressActive)

    $needHealthFallback =
        $healthSnapshotLines.Count -eq 0 -or
        (($resolvedHealthHelperSessionPhase -eq 'no_visible_baseline') -and ($latestHelperBaselineEstablished -gt 0)) -or
        (($resolvedHealthBaselineEstablished -le 0) -and ($latestHelperBaselineEstablished -gt 0)) -or
        (($resolvedHealthSteadyVisibleProgressActive -le 0) -and ($latestHelperSteadyVisibleProgressActive -gt 0))

    if ($needHealthFallback) {
        $resolvedHealthSenderOperatingState = if ([string]::IsNullOrWhiteSpace($latestSummarySenderOperatingState)) { $resolvedHealthSenderOperatingState } else { $latestSummarySenderOperatingState }
        $resolvedHealthSenderGuardState = if ([string]::IsNullOrWhiteSpace($latestSummarySenderGuardState)) { $resolvedHealthSenderGuardState } else { $latestSummarySenderGuardState }
        $resolvedHealthDominantPressureBlocker = if ([string]::IsNullOrWhiteSpace($latestSummaryDominantPressureBlocker)) { $resolvedHealthDominantPressureBlocker } else { $latestSummaryDominantPressureBlocker }
        $resolvedHealthBaselineEstablished = [Math]::Max($resolvedHealthBaselineEstablished, [Math]::Max(0, $latestHelperBaselineEstablished))
        $resolvedHealthSteadyVisibleProgressActive = [Math]::Max($resolvedHealthSteadyVisibleProgressActive, [Math]::Max(0, $latestHelperSteadyVisibleProgressActive))
        $resolvedHealthRecoveryActive = [Math]::Max($resolvedHealthRecoveryActive, [Math]::Max(0, $latestRecoveryBurstActive))
        $resolvedHealthRecoveryActive = [Math]::Max($resolvedHealthRecoveryActive, [Math]::Max(0, $latestHelperRecoveryWindowActive))

        if (-not [string]::IsNullOrWhiteSpace($latestSummaryHelperRecoveryMechanism)) {
            $resolvedHealthHelperRecoveryMechanism = $latestSummaryHelperRecoveryMechanism
        }
        elseif ($latestHelperRecoveryProgressCorridorCount -gt 0 -or $latestHelperRecoveryWindowActive -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'recovery_corridor'
        }
        elseif ($latestHelperRecoveryKeyframePendingVisibleApplyCount -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'reserved_apply'
        }
        elseif ($latestHelperRecoveryFollowerWindowBufferedCount -gt 0 -or
                $latestHelperRecoveryFollowerWindowAppliedCount -gt 0 -or
                $latestHelperRecoveryFollowerWindowTrimmedCount -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'follower_window'
        }
        elseif ($latestHelperRecoveryRunwayContiguousFollowerBufferCount -gt 0 -or
                $latestHelperRecoveryRunwayContiguousFollowerApplyCount -gt 0 -or
                $latestHelperRecoveryRunwayAbortCount -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'runway_cleanup'
        }
        elseif ($latestHelperRecoveryWaitRejectBeforeRunwayCount -gt 0 -or
                $latestHelperSuppressedEmitDuringRecoveryWaitCount -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'waiting_for_recovery_keyframe'
        }

        if (-not [string]::IsNullOrWhiteSpace($latestSummaryHelperSessionPhase)) {
            $resolvedHealthHelperSessionPhase = $latestSummaryHelperSessionPhase
        }
        elseif ($resolvedHealthRecoveryActive -gt 0 -or $resolvedHealthHelperRecoveryMechanism -ne 'none') {
            $resolvedHealthHelperSessionPhase = 'recovering'
        }
        elseif ($resolvedHealthBaselineEstablished -gt 0) {
            if ($latestHelperProgressStallMs -gt 0 -and $resolvedHealthSteadyVisibleProgressActive -le 0) {
                $resolvedHealthHelperSessionPhase = 'stalled'
            }
            else {
                $resolvedHealthHelperSessionPhase = 'visible_stable'
            }
        }
        else {
            $resolvedHealthHelperSessionPhase = 'no_visible_baseline'
        }

        if (-not [string]::IsNullOrWhiteSpace($latestSummaryDominantLossClass)) {
            $resolvedHealthDominantLossClass = $latestSummaryDominantLossClass
        }
        elseif ($latestHelperReassemblerLossCount -gt 0 -or
                $latestHelperLateFragmentAfterAppliedHeadCount -gt 0 -or
                $latestHelperLateFragmentAfterVisibleRecoveryCount -gt 0 -or
                $latestHelperUnattributedLossCount -gt 0 -or
                $latestHelperActionableLateFragmentCount -gt 0) {
            $resolvedHealthDominantLossClass = 'current_epoch_actionable_loss'
        }
        elseif ($latestHelperWaitingForRecoveryKeyframeRejectCount -gt 0 -or
                $latestHelperRecoveryWaitRejectBeforeRunwayCount -gt 0 -or
                $latestHelperRecoveryRunwayOverflowRejectCount -gt 0 -or
                $latestHelperSuppressedEmitDuringRecoveryWaitCount -gt 0 -or
                $latestHelperBlockedByReservedRecoveryFrameRejectCount -gt 0 -or
                $latestHelperDeferredPostRecoveryCandidateReplaceCount -gt 0 -or
                $latestHelperPreCandidateGapTailRejectedCount -gt 0 -or
                $latestHelperFutureTailQuarantinedDuringGapCount -gt 0 -or
                $latestHelperFutureTailQuarantinedAfterGapCount -gt 0) {
            $resolvedHealthDominantLossClass = 'same_epoch_recovery_suppressed'
        }
        elseif ($latestHelperOlderEpochCleanupAfterEpochAdvanceCount -gt 0) {
            $resolvedHealthDominantLossClass = 'older_epoch_cleanup'
        }
        else {
            $resolvedHealthDominantLossClass = 'benign_stale_cleanup'
        }

        if ($resolvedHealthHelperSessionPhase -eq 'recovering' -or
            $resolvedHealthHelperSessionPhase -eq 'stalled' -or
            $resolvedHealthDominantLossClass -eq 'current_epoch_actionable_loss') {
            $resolvedHealthDominantTroubleDomain = 'helper'
        }
        elseif ($resolvedHealthDominantPressureBlocker -eq 'bridge_health' -or
                $resolvedHealthDominantPressureBlocker -eq 'queue_evict' -or
                $resolvedHealthDominantPressureBlocker -eq 'rate_gate') {
            $resolvedHealthDominantTroubleDomain = 'transport'
        }
        elseif ($resolvedHealthSenderGuardState -ne 'none' -or
                $resolvedHealthSenderOperatingState -ne 'normal') {
            $resolvedHealthDominantTroubleDomain = 'sender'
        }
        else {
            $resolvedHealthDominantTroubleDomain = 'none'
        }
    }

    if ($healthSnapshotLines.Count -eq 0) {
        $syntheticHealthSnapshotLine =
            "event=screenshare_health_snapshot; sender_operating_state=$resolvedHealthSenderOperatingState; sender_guard_state=$resolvedHealthSenderGuardState; helper_session_phase=$resolvedHealthHelperSessionPhase; helper_recovery_mechanism=$resolvedHealthHelperRecoveryMechanism; dominant_loss_class=$resolvedHealthDominantLossClass; dominant_pressure_blocker=$resolvedHealthDominantPressureBlocker; dominant_trouble_domain=$resolvedHealthDominantTroubleDomain; recovery_active=$resolvedHealthRecoveryActive; baseline_established=$resolvedHealthBaselineEstablished; steady_visible_progress_active=$resolvedHealthSteadyVisibleProgressActive"
        [void]$healthSnapshotLines.Add($syntheticHealthSnapshotLine)
    }

    return [pscustomobject]@{
        CaptureSampleCount = $captureValues.Count
        CaptureAvgMs = if ($captureValues.Count -gt 0) { [math]::Round((($captureValues | Measure-Object -Average).Average), 1) } else { -1 }
        CaptureMinMs = if ($captureValues.Count -gt 0) { ($captureValues | Measure-Object -Minimum).Minimum } else { -1 }
        CaptureMaxMs = if ($captureValues.Count -gt 0) { ($captureValues | Measure-Object -Maximum).Maximum } else { -1 }
        HelperApplyCount = if ($latestHelperFramesApplied -ge 0) { $latestHelperFramesApplied } else { $helperValues.Count }
        HelperApplySampleCount = $helperValues.Count
        HelperApplyAvgMs = if ($helperValues.Count -gt 0) { [math]::Round((($helperValues | Measure-Object -Average).Average), 1) } else { -1 }
        HelperApplyMinMs = if ($helperValues.Count -gt 0) { ($helperValues | Measure-Object -Minimum).Minimum } else { -1 }
        HelperApplyMaxMs = if ($helperValues.Count -gt 0) { ($helperValues | Measure-Object -Maximum).Maximum } else { -1 }
        HelperApplyP95Ms = if ($helperValues.Count -gt 0) { Get-PercentileValue -Values $helperValues -Percentile 95 } else { -1 }
        HelperStaleDrops = $helperStaleDrops
        ReceiverSupersededFrames = $receiverSupersededFrames
        PersistentSummaryCount = $persistentSummaries
        SinkWriterSummaryCount = $sinkWriterSummaries
        NormalModeSummaryCount = $normalModeSummaries
        ReducedModeSummaryCount = $reducedModeSummaries
        CatchUpModeSummaryCount = $catchUpModeSummaries
        BridgeHealthAdvisorySummaryCount = $bridgeHealthAdvisorySummaries
        BridgeHealthActionableSummaryCount = $bridgeHealthActionableSummaries
        LatestBridgeMediaMessagesReceived = $latestBridgeMediaMessagesReceived
        LatestMediaPlaneFramesSent = $latestMediaPlaneFramesSent
        LatestMediaPlaneAttached = $latestMediaPlaneAttached
        RecoveryControlFallbackQueuedCount = $recoveryControlFallbackQueuedCount
        SteadyStateControlFallbackQueuedCount = $steadyStateControlFallbackQueuedCount
        EffectiveMediaPlaneActive = $effectiveMediaPlaneActive
        RecoveryUsedControlFallback = $recoveryUsedControlFallback
        SteadyStateUsedControlFallback = $steadyStateUsedControlFallback
        LatestFramesQueued = $latestFramesQueued
        LatestFramesDeferredToSendSlot = $latestFramesDeferredToSendSlot
        LatestFramesReplacedBeforeSendSlot = $latestFramesReplacedBeforeSendSlot
        LatestFramesDroppedByQueueEvict = $latestFramesDroppedByQueueEvict
        LatestSendSlotEmptyCount = $latestSendSlotEmptyCount
        LatestSlotCoalescingActive = $latestSlotCoalescingActive
        LatestRawFramesDeferredToEncodeSlot = $latestRawFramesDeferredToEncodeSlot
        LatestRawFramesReplacedBeforeEncodeSlot = $latestRawFramesReplacedBeforeEncodeSlot
        LatestRawEncodeSlotEmptyCount = $latestRawEncodeSlotEmptyCount
        LatestRawSlotCoalescingActive = $latestRawSlotCoalescingActive
        LatestPromotionCaptureToSendBudgetMs = $latestPromotionCaptureToSendBudgetMs
        LatestSourceSupersededPendingFrames = $latestSourceSupersededPendingFrames
        LatestAvgFragmentsPerFrame = if ($latestAvgFragmentsPerFrame -ge 0) { [math]::Round($latestAvgFragmentsPerFrame, 2) } else { -1 }
        LatestAvgPayloadsPerFrame = if ($latestAvgPayloadsPerFrame -ge 0) { [math]::Round($latestAvgPayloadsPerFrame, 2) } else { -1 }
        LatestBatchPayloadCount = $latestBatchPayloadCount
        LatestLegacyPayloadCount = $latestLegacyPayloadCount
        LatestOrdinaryNonKeyBatchedPayloadCount = $latestOrdinaryNonKeyBatchedPayloadCount
        LatestOrdinaryNonKeyLegacyPayloadCount = $latestOrdinaryNonKeyLegacyPayloadCount
        LatestKeyframeRecoveryBatchedPayloadCount = $latestKeyframeRecoveryBatchedPayloadCount
        LatestEmittedDisplayableFrames = $latestEmittedDisplayableFrames
        LatestEmittedNonDisplayableUnits = $latestEmittedNonDisplayableUnits
        LatestEmittedIdrFrames = $latestEmittedIdrFrames
        LatestEmittedPFrames = $latestEmittedPFrames
        LatestDroppedBFrames = $latestDroppedBFrames
        LatestDroppedMultiPictureUnits = $latestDroppedMultiPictureUnits
        LatestDisplayableFrameRatio = if ($latestDisplayableFrameRatio -ge 0) { [math]::Round($latestDisplayableFrameRatio, 2) } else { -1 }
        LatestIdrFrameRatio = if ($latestIdrFrameRatio -ge 0) { [math]::Round($latestIdrFrameRatio, 2) } else { -1 }
        LatestAverageEncodedFrameBytes = if ($latestAverageEncodedFrameBytes -ge 0) { [math]::Round($latestAverageEncodedFrameBytes, 1) } else { -1 }
        LatestTransportIpOnlyMode = $latestTransportIpOnlyMode
        LatestLastAccessUnitKind = $latestLastAccessUnitKind
        LatestLowDelayConfigApplied = $latestLowDelayConfigApplied
        LatestHelperFramesCompleted = $latestHelperFramesCompleted
        LatestHelperFramesEnqueuedForDecode = $latestHelperFramesEnqueuedForDecode
        LatestHelperFramesDroppedBeforeDecode = $latestHelperFramesDroppedBeforeDecode
        LatestHelperFramesDecoded = $latestHelperFramesDecoded
        LatestHelperFramesDroppedAfterDecode = $latestHelperFramesDroppedAfterDecode
        LatestHelperFramesApplied = $latestHelperFramesApplied
        LatestHelperNeedMoreInputCount = $latestHelperNeedMoreInputCount
        LatestHelperCompletedWithoutPictureCount = $latestHelperCompletedWithoutPictureCount
        LatestHelperDecodeDurationMs = if ($latestHelperDecodeDurationMs -ge 0) { [math]::Round($latestHelperDecodeDurationMs, 1) } else { -1 }
        LatestHelperApplyIntervalMs = if ($latestHelperApplyIntervalMs -ge 0) { [math]::Round($latestHelperApplyIntervalMs, 1) } else { -1 }
        LatestHelperMaxPendingEncodedDepth = $latestHelperMaxPendingEncodedDepth
        LatestHelperMaxPendingDecodedDepth = $latestHelperMaxPendingDecodedDepth
        LatestHelperAvgEnqueueToDecodeStartMs = if ($latestHelperAvgEnqueueToDecodeStartMs -ge 0) { [math]::Round($latestHelperAvgEnqueueToDecodeStartMs, 1) } else { -1 }
        LatestHelperAvgEnqueueToDropMs = if ($latestHelperAvgEnqueueToDropMs -ge 0) { [math]::Round($latestHelperAvgEnqueueToDropMs, 1) } else { -1 }
        LatestHelperDecodeWorkerDropQueueOverflowCount = $latestHelperDecodeWorkerDropQueueOverflowCount
        LatestHelperDecodeWorkerDropAgeBudgetCount = $latestHelperDecodeWorkerDropAgeBudgetCount
        LatestHelperDecodeWorkerDropGenerationCount = $latestHelperDecodeWorkerDropGenerationCount
        LatestHelperDecodeWorkerDropStoppedCount = $latestHelperDecodeWorkerDropStoppedCount
        LatestHelperReassemblerLossCount = $latestHelperReassemblerLossCount
        LatestHelperEnqueueRejectCount = $latestHelperEnqueueRejectCount
        LatestHelperWaitingForRecoveryKeyframeRejectCount = $latestHelperWaitingForRecoveryKeyframeRejectCount
        LatestHelperRecoveryWaitRejectBeforeRunwayCount = [Math]::Max(0, $latestHelperRecoveryWaitRejectBeforeRunwayCount)
        LatestHelperRecoveryRunwayOverflowRejectCount = [Math]::Max(0, $latestHelperRecoveryRunwayOverflowRejectCount)
        LatestHelperSuppressedEmitDuringRecoveryWaitCount = [Math]::Max(0, $latestHelperSuppressedEmitDuringRecoveryWaitCount)
        LatestHelperStaleSupersededRecoverySuppressedCount = [Math]::Max(0, $latestHelperStaleSupersededRecoverySuppressedCount)
        LatestHelperSoftStaleCleanupCount = [Math]::Max(0, $latestHelperSoftStaleCleanupCount)
        LatestHelperBlockedByReservedRecoveryFrameRejectCount = $latestHelperBlockedByReservedRecoveryFrameRejectCount
        LatestHelperOlderEpochIgnoredDuringRecoveryLockCount = $latestHelperOlderEpochIgnoredDuringRecoveryLockCount
        LatestHelperNewerEpochNonKeyIgnoredDuringLockCount = $latestHelperNewerEpochNonKeyIgnoredDuringLockCount
        LatestHelperDeferredPostRecoveryCandidateReplaceCount = $latestHelperDeferredPostRecoveryCandidateReplaceCount
        LatestHelperDecodeWorkerDropCount = $latestHelperDecodeWorkerDropCount
        LatestHelperPostDecodeDropCount = $latestHelperPostDecodeDropCount
        LatestHelperDecodeQueueOverflowCount = $latestHelperDecodeQueueOverflowCount
        LatestHelperDecodeAgeBudgetCount = $latestHelperDecodeAgeBudgetCount
        LatestHelperDecodeGenerationChangedCount = $latestHelperDecodeGenerationChangedCount
        LatestHelperDecodeStoppedCount = $latestHelperDecodeStoppedCount
        LatestHelperDecodedApplyQueueOverflowCount = $latestHelperDecodedApplyQueueOverflowCount
        LatestHelperDecodedFrameReplacedBeforeApplyCount = $latestHelperDecodedFrameReplacedBeforeApplyCount
        LatestOrdinaryRawLossCount = $latestOrdinaryRawLossCount
        LatestOrdinarySenderLossCount = $latestOrdinarySenderLossCount
        LatestOrdinaryHelperLossCount = $latestOrdinaryHelperLossCount
        DominantOrdinaryFreshnessLossBoundary = $dominantOrdinaryFreshnessLossBoundary
        LatestHelperStaleDroppedAfterDecodeCount = [Math]::Max(0, $latestHelperStaleDroppedAfterDecodeCount)
        LatestHelperDroppedWaitingForRecoveryKeyframeCount = $latestHelperDroppedWaitingForRecoveryKeyframeCount
        LatestHelperGapNonKeyPrunedCount = $latestHelperGapNonKeyPrunedCount
        LatestHelperFutureTailQuarantinedDuringGapCount = [Math]::Max(0, $latestHelperFutureTailQuarantinedDuringGapCount)
        LatestHelperFutureTailQuarantinedAfterGapCount = [Math]::Max(0, $latestHelperFutureTailQuarantinedAfterGapCount)
        LatestHelperPreCandidateGapTailRejectedCount = [Math]::Max(0, $latestHelperPreCandidateGapTailRejectedCount)
        LatestHelperRecoveryCandidatePresentCount = [Math]::Max(0, $latestHelperRecoveryCandidatePresentCount)
        LatestHelperVisibleRecoveryFloorFrameId = $latestHelperVisibleRecoveryFloorFrameId
        LatestHelperStableVisibleHeadFrameId = $latestHelperStableVisibleHeadFrameId
        LatestHelperAppliedHeadFrameId = $latestHelperAppliedHeadFrameId
        LatestHelperOrderedEmitHeadFrameId = $latestHelperOrderedEmitHeadFrameId
        LatestHelperWinningRecoveryFrameId = $latestHelperWinningRecoveryFrameId
        LatestHelperVisibleHeadFrameId = $latestHelperVisibleHeadFrameId
        LatestHelperSupersededRecoveryTailCleanupCount = [Math]::Max(0, $latestHelperSupersededRecoveryTailCleanupCount)
        LatestHelperLateSameEpochAfterHeadAdvancedDropCount = [Math]::Max(0, $latestHelperLateSameEpochAfterHeadAdvancedDropCount)
        LatestHelperStaleRunwayWindowAbortCount = [Math]::Max(0, $latestHelperStaleRunwayWindowAbortCount)
        LatestHelperRunwayCandidateExpiredAfterHeadAdvanceCount = [Math]::Max(0, $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount)
        LatestHelperRunwayFollowersEmittedWithinActionableWindowCount = [Math]::Max(0, $latestHelperRunwayFollowersEmittedWithinActionableWindowCount)
        LatestHelperRecoveryOwnerReplacedCount = [Math]::Max(0, $latestHelperRecoveryOwnerReplacedCount)
        LatestHelperOlderEpochCleanupAfterEpochAdvanceCount = [Math]::Max(0, $latestHelperOlderEpochCleanupAfterEpochAdvanceCount)
        LatestHelperSteadyVisibleProgressActive = [Math]::Max(0, $latestHelperSteadyVisibleProgressActive)
        LatestHelperSteadyVisibleProgressActivationFrameId = $latestHelperSteadyVisibleProgressActivationFrameId
        LatestHelperFramesAppliedSinceLastGap = [Math]::Max(0, $latestHelperFramesAppliedSinceLastGap)
        LatestRemoteHelperFactHealthyActive = [Math]::Max(0, $latestRemoteHelperFactHealthyActive)
        LatestRemoteHelperFactHealthySource = if ([string]::IsNullOrWhiteSpace($latestRemoteHelperFactHealthySource)) { 'none' } else { $latestRemoteHelperFactHealthySource }
        LatestRemoteHelperFactProofFrameId = $latestRemoteHelperFactProofFrameId
        LatestRemoteHelperFactLastMessageAgeMs = $latestRemoteHelperFactLastMessageAgeMs
        LatestRemoteHelperFactHealthyClearCount = [Math]::Max(0, $latestRemoteHelperFactHealthyClearCount)
        LatestRemoteHelperFactHealthyClearReason = if ([string]::IsNullOrWhiteSpace($latestRemoteHelperFactHealthyClearReason)) { 'none' } else { $latestRemoteHelperFactHealthyClearReason }
        LatestHelperLastSentStableVisibleHeadFrameId = $latestHelperLastSentStableVisibleHeadFrameId
        LatestHelperPressureSendBypassedForVisibleProgressCount = [Math]::Max(0, $latestHelperPressureSendBypassedForVisibleProgressCount)
        LatestHelperProofKeepaliveSendCount = [Math]::Max(0, $latestHelperProofKeepaliveSendCount)
        LatestHelperProofKeepaliveTimerDrivenSendCount = [Math]::Max(0, $latestHelperProofKeepaliveTimerDrivenSendCount)
        LatestHelperProofKeepaliveLastHeadFrameId = $latestHelperProofKeepaliveLastHeadFrameId
        LatestHelperProofKeepaliveLastSendAgeMs = $latestHelperProofKeepaliveLastSendAgeMs
        LatestHelperFirstVisibleApplyToSenderFactSendMs = $latestHelperFirstVisibleApplyToSenderFactSendMs
        LatestHelperSteadyVisibleProgressClearedCount = [Math]::Max(0, $latestHelperSteadyVisibleProgressClearedCount)
        LatestHelperSteadyVisibleProgressClearedReason = if ([string]::IsNullOrWhiteSpace($latestHelperSteadyVisibleProgressClearedReason)) { 'none' } else { $latestHelperSteadyVisibleProgressClearedReason }
        LatestHelperLateFragmentAfterAppliedHeadCount = [Math]::Max(0, $latestHelperLateFragmentAfterAppliedHeadCount)
        LatestHelperLateFragmentAfterOrderedHeadCount = [Math]::Max(0, $latestHelperLateFragmentAfterOrderedHeadCount)
        LatestHelperLateFragmentAfterStableVisibleHeadCount = [Math]::Max(0, $latestHelperLateFragmentAfterStableVisibleHeadCount)
        LatestHelperLateFragmentAfterVisibleRecoveryCount = [Math]::Max(0, $latestHelperLateFragmentAfterVisibleRecoveryCount)
        LatestHelperPreCandidateGapTailEmittedToViewerCount = [Math]::Max(0, $latestHelperPreCandidateGapTailEmittedToViewerCount)
        LatestHelperHighFrameAgeSuppressedDueToVisibleProgressCount = [Math]::Max(0, $latestHelperHighFrameAgeSuppressedDueToVisibleProgressCount)
        LatestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount = [Math]::Max(0, $latestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount)
        LatestHelperActionableHighFrameAgeCount = [Math]::Max(0, $latestHelperActionableHighFrameAgeCount)
        LatestHelperActionableLateFragmentCount = [Math]::Max(0, $latestHelperActionableLateFragmentCount)
        LatestRecoveryBurstActive = [Math]::Max(0, $latestRecoveryBurstActive)
        LatestRecoveryBurstPhase = if ([string]::IsNullOrWhiteSpace($latestRecoveryBurstPhase)) { 'idle' } else { $latestRecoveryBurstPhase }
        LatestRecoveryBurstStreamEpoch = $latestRecoveryBurstStreamEpoch
        LatestRecoveryOwnerFrameId = $latestRecoveryOwnerFrameId
        LatestRecoveryProtectedFollowerCount = [Math]::Max(0, $latestRecoveryProtectedFollowerCount)
        LatestRecoveryGapCount = [Math]::Max(0, $latestRecoveryGapCount)
        LatestRecoveryGapToKeyframeRequestMs = $latestRecoveryGapToKeyframeRequestMs
        LatestRecoveryKeyframeRequestToOwnerEmitMs = $latestRecoveryKeyframeRequestToOwnerEmitMs
        LatestRecoveryOwnerAckWindowMs = $latestRecoveryOwnerAckWindowMs
        LatestRecoveryOwnerEmitToAckMs = $latestRecoveryOwnerEmitToAckMs
        LatestRecoveryPostAckHoldActive = [Math]::Max(0, $latestRecoveryPostAckHoldActive)
        LatestRecoveryPostAckHoldStartedCount = [Math]::Max(0, $latestRecoveryPostAckHoldStartedCount)
        LatestRecoveryPostAckHoldExpiredCount = [Math]::Max(0, $latestRecoveryPostAckHoldExpiredCount)
        LatestRecoveryPostAckHoldSuppressedReopenCount = [Math]::Max(0, $latestRecoveryPostAckHoldSuppressedReopenCount)
        LatestRecoveryOwnerAckFrameId = $latestRecoveryOwnerAckFrameId
        LatestRecoveryAckSource = if ([string]::IsNullOrWhiteSpace($latestRecoveryAckSource)) { 'none' } else { $latestRecoveryAckSource }
        LatestRecoveryOwnerEmitToFirstVisibleApplyMs = $latestRecoveryOwnerEmitToFirstVisibleApplyMs
        LatestRecoveryBurstControlFallbackCount = [Math]::Max(0, $latestRecoveryBurstControlFallbackCount)
        LatestRecoveryBurstTimeoutCount = [Math]::Max(0, $latestRecoveryBurstTimeoutCount)
        LatestRecoveryBurstCompletedCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedCount), $eventRecoveryBurstCompletedCount)
        LatestRecoveryBurstRestartSuppressedCount = [Math]::Max(0, $latestRecoveryBurstRestartSuppressedCount)
        LatestRecoveryBurstEncoderRerequestCount = [Math]::Max(0, $latestRecoveryBurstEncoderRerequestCount)
        LatestRecoveryOwnerPendingForcedResetCount = [Math]::Max([Math]::Max(0, $latestRecoveryOwnerPendingForcedResetCount), $eventRecoveryOwnerPendingForcedResetCount)
        LatestRecoveryKeyframeEmittedAfterForcedResetCount = [Math]::Max([Math]::Max(0, $latestRecoveryKeyframeEmittedAfterForcedResetCount), $eventRecoveryKeyframeEmittedAfterForcedResetCount)
        LatestRecoveryBurstCompletedByHelperAckCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByHelperAckCount), $eventRecoveryBurstCompletedByHelperAckCount)
        LatestRecoveryBurstCompletedByAppliedHeadAckCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByAppliedHeadAckCount), $eventRecoveryBurstCompletedByAppliedHeadAckCount)
        LatestRecoveryBurstCompletedByLastVisibleApplyAckCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByLastVisibleApplyAckCount), $eventRecoveryBurstCompletedByLastVisibleApplyAckCount)
        LatestRecoveryBurstCompletedByVisibleRecoveryFloorCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByVisibleRecoveryFloorCount), $eventRecoveryBurstCompletedByVisibleRecoveryFloorCount)
        LatestRecoveryBurstCompletedByVisibleApplyFallbackCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByVisibleApplyFallbackCount), $eventRecoveryBurstCompletedByVisibleApplyFallbackCount)
        LatestRecoveryBurstCompletedByTimeoutCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByTimeoutCount), $eventRecoveryBurstCompletedByTimeoutCount)
        LatestRecoveryBurstCompletedByProtectedFramesCount = [Math]::Max(0, $latestRecoveryBurstCompletedByProtectedFramesCount)
        LatestRecoveryBurstProfileTransitionDeferredCount = [Math]::Max(0, $latestRecoveryBurstProfileTransitionDeferredCount)
        LatestRecoveryBurstProfileTransitionTakeoverCount = [Math]::Max(0, $latestRecoveryBurstProfileTransitionTakeoverCount)
        LatestRecoveryBurstStaleRequestSuppressedCount = [Math]::Max(0, $latestRecoveryBurstStaleRequestSuppressedCount)
        LatestRecoveryBurstRequestSuppressedDueToHelperAckCount = [Math]::Max(0, $latestRecoveryBurstRequestSuppressedDueToHelperAckCount)
        LatestRecoveryBurstStartedWhileHelperProofHealthyCount = [Math]::Max(0, $latestRecoveryBurstStartedWhileHelperProofHealthyCount)
        LatestLastCompletedRecoveryEpoch = $latestLastCompletedRecoveryEpoch
        LatestLastCompletedRecoveryOwnerFrameId = $latestLastCompletedRecoveryOwnerFrameId
        LatestLastCompletedRecoveryAckFrameId = $latestLastCompletedRecoveryAckFrameId
        LatestLastCompletedRecoveryAckSource = if ([string]::IsNullOrWhiteSpace($latestLastCompletedRecoveryAckSource)) { 'none' } else { $latestLastCompletedRecoveryAckSource }
        LatestLastCompletedRecoveryOwnerEmitToAckMs = $latestLastCompletedRecoveryOwnerEmitToAckMs
        LatestLastCompletedRecoveryCompletionKind = if ([string]::IsNullOrWhiteSpace($latestLastCompletedRecoveryCompletionKind)) { 'none' } else { $latestLastCompletedRecoveryCompletionKind }
        LatestRecoveryCompletionAccountingMismatch = [Math]::Max(0, $latestRecoveryCompletionAccountingMismatch)
        LatestRecoveryOwnerPendingNonKeyHeldCount = [Math]::Max(0, $latestRecoveryOwnerPendingNonKeyHeldCount)
        LatestRecoveryOwnerPendingNonKeyReplacedCount = [Math]::Max(0, $latestRecoveryOwnerPendingNonKeyReplacedCount)
        LatestRecoveryOwnerUnackedNonKeyHeldCount = [Math]::Max(0, $latestRecoveryOwnerUnackedNonKeyHeldCount)
        LatestRecoveryOwnerUnackedNonKeyReplacedCount = [Math]::Max(0, $latestRecoveryOwnerUnackedNonKeyReplacedCount)
        LatestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = [Math]::Max(0, $latestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount)
        LatestRecoveryOwnerReplacedBeforeAckCount = [Math]::Max(0, $latestRecoveryOwnerReplacedBeforeAckCount)
        LatestHighFrameAgeSuppressedDuringOwnerAckCount = [Math]::Max(0, $latestHighFrameAgeSuppressedDuringOwnerAckCount)
        LatestRecoveryTimeoutWhileHelperHeadAdvancedCount = [Math]::Max(0, $latestRecoveryTimeoutWhileHelperHeadAdvancedCount)
        LatestSenderReceivedHelperProgressDuringContinuityLossCount = [Math]::Max(0, $latestSenderReceivedHelperProgressDuringContinuityLossCount)
        LatestHelperAckAfterFactSendMs = $latestHelperAckAfterFactSendMs
        LatestPostAckModeGraceSuppressedHighFrameAgeCount = [Math]::Max(0, $latestPostAckModeGraceSuppressedHighFrameAgeCount)
        LatestBootstrapGraceSuppressedCatchUpCount = [Math]::Max(0, $latestBootstrapGraceSuppressedCatchUpCount)
        LatestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = [Math]::Max(0, $latestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount)
        LatestCatchUpExitWhileRemoteHighFrameAgePressureCount = [Math]::Max(0, $latestCatchUpExitWhileRemoteHighFrameAgePressureCount)
        LatestProtectedRecoveryFramesDispatchedCount = [Math]::Max(0, $latestProtectedRecoveryFramesDispatchedCount)
        LatestRecoveryProtectedFrameBlockedByOrdinaryCount = [Math]::Max(0, $latestRecoveryProtectedFrameBlockedByOrdinaryCount)
        LatestLastAcknowledgedRecoveryOwnerFrameId = $latestLastAcknowledgedRecoveryOwnerFrameId
        LatestLastAcknowledgedHelperHeadFrameId = $latestLastAcknowledgedHelperHeadFrameId
        LatestRemoteHelperVisibleHeadFrameId = $latestRemoteHelperVisibleHeadFrameId
        LatestRemoteHelperVisibleRecoveryFloorFrameId = $latestRemoteHelperVisibleRecoveryFloorFrameId
        LatestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount = [Math]::Max(0, $latestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount)
        LatestLastAcknowledgedVisibleHelperHeadFrameId = $latestLastAcknowledgedVisibleHelperHeadFrameId
        LatestLastAcknowledgedHelperProofAgeMs = $latestLastAcknowledgedHelperProofAgeMs
        LatestPersistedReleaseFloorEpoch = $latestPersistedReleaseFloorEpoch
        LatestSatisfiedRecoveryFloorFrameId = $latestSatisfiedRecoveryFloorFrameId
        LatestSatisfiedRecoveryFloorSource = if ([string]::IsNullOrWhiteSpace($latestSatisfiedRecoveryFloorSource)) { 'none' } else { $latestSatisfiedRecoveryFloorSource }
        LatestSatisfiedRecoveryFloorVisibleProofCount = [Math]::Max(0, $latestSatisfiedRecoveryFloorVisibleProofCount)
        LatestContinuitySignalIgnoredDueToSatisfiedFloorCount = [Math]::Max(0, $latestContinuitySignalIgnoredDueToSatisfiedFloorCount)
        LatestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount = [Math]::Max(0, $latestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount)
        LatestRecoveryLockClearedByAcknowledgedProofCount = [Math]::Max(0, $latestRecoveryLockClearedByAcknowledgedProofCount)
        LatestRecoveryLockClearedByVisibleProofCount = [Math]::Max(0, $latestRecoveryLockClearedByVisibleProofCount)
        LatestRecoveryLockLastClearReason = if ([string]::IsNullOrWhiteSpace($latestRecoveryLockLastClearReason)) { 'none' } else { $latestRecoveryLockLastClearReason }
        LatestHelperProgressPastOwnerWithoutBurstAckCount = [Math]::Max(0, $latestHelperProgressPastOwnerWithoutBurstAckCount)
        LatestPostRecoveryAgeGraceActive = [Math]::Max(0, $latestPostRecoveryAgeGraceActive)
        LatestPostRecoveryAgeGraceSuppressedCount = [Math]::Max(0, $latestPostRecoveryAgeGraceSuppressedCount)
        RecoveryControlBootstrapRetrySkippedDueToBurstResolvedCount = [Math]::Max(0, $recoveryControlBootstrapRetrySkippedDueToBurstResolvedCount)
        RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount = [Math]::Max(0, $recoveryControlBootstrapRetryQueuedAfterBurstResolutionCount)
        RecoveryBurstCompletedWithoutHelperAdvance = [Math]::Max(0, $recoveryBurstCompletedWithoutHelperAdvance)
        RecoveryAckMissedDespiteHelperProgress = [Math]::Max(0, $recoveryAckMissedDespiteHelperProgress)
        LatestHelperRecoveryRunwayContiguousFollowerBufferCount = [Math]::Max(0, $latestHelperRecoveryRunwayContiguousFollowerBufferCount)
        LatestHelperRecoveryRunwayContiguousFollowerApplyCount = [Math]::Max(0, $latestHelperRecoveryRunwayContiguousFollowerApplyCount)
        LatestHelperRecoveryRunwayAbortCount = [Math]::Max(0, $latestHelperRecoveryRunwayAbortCount)
        LatestHelperRecoveryKeyframeResyncCount = $latestHelperRecoveryKeyframeResyncCount
        LatestHelperGapActive = $latestHelperGapActive
        LatestHelperGapExpectedFrameId = $latestHelperGapExpectedFrameId
        LatestHelperBufferedRecoveryKeyframeFrameId = $latestHelperBufferedRecoveryKeyframeFrameId
        LatestHelperFutureNonKeyBufferedCount = $latestHelperFutureNonKeyBufferedCount
        LatestHelperRecoveryFollowerWindowBufferedCount = [Math]::Max(0, $latestHelperRecoveryFollowerWindowBufferedCount)
        LatestHelperRecoveryFollowerWindowAppliedCount = [Math]::Max(0, $latestHelperRecoveryFollowerWindowAppliedCount)
        LatestHelperRecoveryFollowerWindowTrimmedCount = [Math]::Max(0, $latestHelperRecoveryFollowerWindowTrimmedCount)
        LatestHelperProtectedRecoveryDeliveryCount = [Math]::Max(0, $latestHelperProtectedRecoveryDeliveryCount)
        LatestHelperRecoveryProgressCorridorCount = [Math]::Max(0, $latestHelperRecoveryProgressCorridorCount)
        LatestHelperRecoveryProgressCorridorSuccessCount = [Math]::Max(0, $latestHelperRecoveryProgressCorridorSuccessCount)
        LatestHelperRecoveryProgressCorridorAbortCount = [Math]::Max(0, $latestHelperRecoveryProgressCorridorAbortCount)
        LatestHelperRecoveryProgressCorridorAppliedCount = [Math]::Max(0, $latestHelperRecoveryProgressCorridorAppliedCount)
        LatestHelperRecoveryKeyframePendingVisibleApplyCount = [Math]::Max(0, $latestHelperRecoveryKeyframePendingVisibleApplyCount)
        LatestHelperStartupCorridorBufferedFollowerCount = [Math]::Max(0, $latestHelperStartupCorridorBufferedFollowerCount)
        LatestHelperStartupCorridorReleaseCount = [Math]::Max(0, $latestHelperStartupCorridorReleaseCount)
        LatestHelperStartupCorridorAbortCount = [Math]::Max(0, $latestHelperStartupCorridorAbortCount)
        LatestHelperStartupCorridorAbortReason = if ([string]::IsNullOrWhiteSpace($latestHelperStartupCorridorAbortReason)) { 'none' } else { $latestHelperStartupCorridorAbortReason }
        LatestHelperPostRecoveryVisibleGenerationResetCount = [Math]::Max(0, $latestHelperPostRecoveryVisibleGenerationResetCount)
        LatestHelperPostRecoveryPurgedPreRecoveryFollowerCount = [Math]::Max(0, $latestHelperPostRecoveryPurgedPreRecoveryFollowerCount)
        LatestHelperPostRecoveryStaleDropBypassCount = [Math]::Max(0, $latestHelperPostRecoveryStaleDropBypassCount)
        LatestHelperLateFragmentAfterSuccessfulRecoveryCount = [Math]::Max(0, $latestHelperLateFragmentAfterSuccessfulRecoveryCount)
        LatestHelperUnattributedLossCount = $latestHelperUnattributedLossCount
        LatestHelperRecentLosses = $latestHelperRecentLosses
        LatestHelperVisibleApplyRatio = if ($latestHelperVisibleApplyRatio -ge 0) { [math]::Round($latestHelperVisibleApplyRatio, 2) } else { -1 }
        LatestHelperAvgDecodeCompleteToVisibleApplyMs = if ($latestHelperAvgDecodeCompleteToVisibleApplyMs -ge 0) { [math]::Round($latestHelperAvgDecodeCompleteToVisibleApplyMs, 1) } else { -1 }
        LatestHelperAvgUiPostApplyMs = if ($latestHelperAvgUiPostApplyMs -ge 0) { [math]::Round($latestHelperAvgUiPostApplyMs, 1) } else { -1 }
        LatestHelperAvgVisibleHeadLagFrames = if ($latestHelperAvgVisibleHeadLagFrames -ge 0) { [math]::Round($latestHelperAvgVisibleHeadLagFrames, 1) } else { -1 }
        LatestHelperAvgStableHeadLagFrames = if ($latestHelperAvgStableHeadLagFrames -ge 0) { [math]::Round($latestHelperAvgStableHeadLagFrames, 1) } else { -1 }
        LatestHelperLastReservedApplyHoldMs = [Math]::Max(-1, $latestHelperLastReservedApplyHoldMs)
        LatestHelperLastRecoveryProgressCorridorHoldMs = [Math]::Max(-1, $latestHelperLastRecoveryProgressCorridorHoldMs)
        LatestHelperLastRecoveryRunwayAbortHoldMs = [Math]::Max(-1, $latestHelperLastRecoveryRunwayAbortHoldMs)
        LatestHelperLastRecoveryProgressCorridorAbortReason = if ([string]::IsNullOrWhiteSpace($latestHelperLastRecoveryProgressCorridorAbortReason)) { 'none' } else { $latestHelperLastRecoveryProgressCorridorAbortReason }
        LatestHelperGapCount = $latestHelperGapCount
        LatestHelperRecoveryKeyframeApplyCount = $latestHelperRecoveryKeyframeApplyCount
        LatestHelperResyncCount = $latestHelperResyncCount
        LatestHelperDominantReassemblerRootCause = if ([string]::IsNullOrWhiteSpace($latestHelperDominantReassemblerRootCause)) { 'none' } else { $latestHelperDominantReassemblerRootCause }
        LatestHelperDominantAdmissionRejectReason = $effectiveDominantHelperAdmissionRejectReason
        LatestHealthSenderOperatingState = $resolvedHealthSenderOperatingState
        LatestHealthSenderGuardState = $resolvedHealthSenderGuardState
        LatestHealthHelperSessionPhase = $resolvedHealthHelperSessionPhase
        LatestHealthHelperRecoveryMechanism = $resolvedHealthHelperRecoveryMechanism
        LatestSummaryHelperSessionPhase = $latestSummaryHelperSessionPhase
        LatestSummaryHelperRecoveryMechanism = $latestSummaryHelperRecoveryMechanism
        LatestHealthDominantLossClass = $resolvedHealthDominantLossClass
        LatestHealthDominantPressureBlocker = $resolvedHealthDominantPressureBlocker
        LatestHealthDominantTroubleDomain = $resolvedHealthDominantTroubleDomain
        LatestHealthRecoveryActive = $resolvedHealthRecoveryActive
        LatestHealthBaselineEstablished = $resolvedHealthBaselineEstablished
        LatestHealthSteadyVisibleProgressActive = $resolvedHealthSteadyVisibleProgressActive
        LatestHelperPostRecoveryHighFrameAgeSuppressedTicks = [Math]::Max(0, $latestHelperPostRecoveryHighFrameAgeSuppressedTicks)
        LatestHelperVisibleAppliesDuringSettleCount = [Math]::Max(0, $latestHelperVisibleAppliesDuringSettleCount)
        LatestHelperPostRecoverySettleWindowCount = [Math]::Max(0, $latestHelperPostRecoverySettleWindowCount)
        LatestHelperPostRecoverySettleWindowSuccessCount = [Math]::Max(0, $latestHelperPostRecoverySettleWindowSuccessCount)
        LatestHelperPostRecoverySettleWindowTimeoutCount = [Math]::Max(0, $latestHelperPostRecoverySettleWindowTimeoutCount)
        LatestHelperVisibleAppliesBeforePressureReenabled = $latestHelperVisibleAppliesBeforePressureReenabled
        LatestHelperRecoveryWindowActive = [Math]::Max(0, $latestHelperRecoveryWindowActive)
        LatestHelperRecoveryWindowProgressed = [Math]::Max(0, $latestHelperRecoveryWindowProgressed)
        LatestHelperRecoveryWindowSucceeded = [Math]::Max(0, $latestHelperRecoveryWindowSucceeded)
        LatestHelperRecoveryWindowProgressedCount = [Math]::Max(0, $latestHelperRecoveryWindowProgressedCount)
        LatestHelperRecoveryWindowSuccessCount = [Math]::Max(0, $latestHelperRecoveryWindowSuccessCount)
        LatestHelperActiveRecoveryWindowEpoch = $latestHelperActiveRecoveryWindowEpoch
        LatestHelperActiveRecoveryWindowRecoveryFrameId = $latestHelperActiveRecoveryWindowRecoveryFrameId
        LatestHelperRecoveryWindowContiguousFollowerApplyCount = [Math]::Max(0, $latestHelperRecoveryWindowContiguousFollowerApplyCount)
        LatestHelperAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = [Math]::Max(0, $latestHelperAfterVisibleRecoveryFrameSuppressedDueToSuccessCount)
        LatestHelperRecoverySuccessCounterMismatch = if (
            (($latestHelperRecoveryWindowSuccessCount -ge 0) -and
             ($latestHelperRecoveryProgressCorridorSuccessCount -ge 0) -and
             ($latestHelperRecoveryWindowSuccessCount -ne $latestHelperRecoveryProgressCorridorSuccessCount)) -or
            (($latestHelperRecoveryWindowSuccessCount -ge 0) -and
             ($latestHelperPostRecoverySettleWindowSuccessCount -ge 0) -and
             ($latestHelperRecoveryWindowSuccessCount -ne $latestHelperPostRecoverySettleWindowSuccessCount))
        ) { 1 } else { 0 }
        LatestHelperBaselineEstablished = [Math]::Max(0, $latestHelperBaselineEstablished)
        LatestHelperBaselineCaptureToRenderMs = $latestHelperBaselineCaptureToRenderMs
        LatestHelperAgeExcessMs = $latestHelperAgeExcessMs
        LatestHelperProgressStallMs = $latestHelperProgressStallMs
        LatestHelperBaselineReseedInProgress = [Math]::Max(0, $latestHelperBaselineReseedInProgress)
        LatestHelperAgePressureConsecutiveCount = [Math]::Max(0, $latestHelperAgePressureConsecutiveCount)
        LatestHelperCadencePressureConsecutiveCount = [Math]::Max(0, $latestHelperCadencePressureConsecutiveCount)
        LatestHelperCatchUpSuppressedDueToProgressCount = [Math]::Max(0, $latestHelperCatchUpSuppressedDueToProgressCount)
        LatestHelperBaselineFrozenDueToStallCount = [Math]::Max(0, $latestHelperBaselineFrozenDueToStallCount)
        LatestHelperBaselineReseedAfterRecoveryCount = [Math]::Max(0, $latestHelperBaselineReseedAfterRecoveryCount)
        LatestHelperCadenceStallWindowCount = [Math]::Max(0, $latestHelperCadenceStallWindowCount)
        LatestHelperCadenceStallTriggerCount = [Math]::Max(0, $latestHelperCadenceStallTriggerCount)
        LatestHelperBridgeHealthAdvisoryCount = [Math]::Max(0, $latestHelperBridgeHealthAdvisoryCount)
        LatestHelperBridgeHealthActionableCount = [Math]::Max(0, $latestHelperBridgeHealthActionableCount)
        LatestHelperBridgeHealthQuarantineSuppressedCount = [Math]::Max(0, $latestHelperBridgeHealthQuarantineSuppressedCount)
        LatestHelperBridgeHealthActionableWithoutQueueOrDropCount = [Math]::Max(0, $latestHelperBridgeHealthActionableWithoutQueueOrDropCount)
        AggregateHighFrameAgeSuppressedDueToVisibleProgressCount = [Math]::Max(0, $aggregateHighFrameAgeSuppressedDueToVisibleProgressCount)
        AggregateHighFrameAgeSuppressedDueToHeadAdvanceCount = [Math]::Max(0, $aggregateHighFrameAgeSuppressedDueToHeadAdvanceCount)
        AggregateActionableHighFrameAgeCount = [Math]::Max(0, $aggregateActionableHighFrameAgeCount)
        AggregatePostRecoveryHighFrameAgeSuppressedTicks = [Math]::Max(0, $aggregatePostRecoveryHighFrameAgeSuppressedTicks)
        DominantReassemblerRootCause = $dominantReassemblerRootCause
        DominantHelperPressureBlocker = $dominantHelperPressureBlocker
        AggregateLateFragmentAfterAppliedHeadCount = [Math]::Max(0, $aggregateLateFragmentAfterAppliedHeadCount)
        AggregateLateFragmentAfterOrderedHeadCount = [Math]::Max(0, $aggregateLateFragmentAfterOrderedHeadCount)
        AggregateRecoveryOwnerReplacedCount = [Math]::Max(0, $aggregateRecoveryOwnerReplacedCount)
        AggregateOlderEpochCleanupAfterEpochAdvanceCount = [Math]::Max(0, $aggregateOlderEpochCleanupAfterEpochAdvanceCount)
        AggregateActionableLateFragmentCount = [Math]::Max(0, $aggregateActionableLateFragmentCount)
        WorstEpochByVisibleApplyRatio = $worstVisibleApplyRatioEpoch
        WorstEpochVisibleApplyRatio = if ($worstVisibleApplyRatio -ge 0) { [math]::Round($worstVisibleApplyRatio, 2) } else { -1 }
        WorstEpochByRecoveryLockTime = $worstRecoveryLockEpoch
        WorstEpochRecoveryLockTimeMs = $worstRecoveryLockMs
        LatestPromotionBlockerRateGateTicks = $latestPromotionBlockerRateGateTicks
        LatestPromotionBlockerHelperPressureTicks = $latestPromotionBlockerHelperPressureTicks
        LatestPromotionBlockerHelperWarmupTicks = $latestPromotionBlockerHelperWarmupTicks
        LatestPromotionBlockerHelperApplyCountTicks = $latestPromotionBlockerHelperApplyCountTicks
        LatestPromotionBlockerBridgeHealthTicks = $latestPromotionBlockerBridgeHealthTicks
        LatestPromotionBlockerRecoveryLockTicks = $latestPromotionBlockerRecoveryLockTicks
        LatestPromotionBlockerQueueEvictTicks = $latestPromotionBlockerQueueEvictTicks
        LatestPromotionBlockerCaptureAgeTicks = $latestPromotionBlockerCaptureAgeTicks
        LatestPromotionBlockerEncodeBudgetTicks = $latestPromotionBlockerEncodeBudgetTicks
        LatestPromotionBlockerTransitionGraceTicks = $latestPromotionBlockerTransitionGraceTicks
        LatestPromotionEncodeSoftSpikeCount = [Math]::Max(0, $latestPromotionEncodeSoftSpikeCount)
        LatestPromotionEncodeSoftSpikeResetSuppressedCount = [Math]::Max(0, $latestPromotionEncodeSoftSpikeResetSuppressedCount)
        PromotionBlockedByMissingHelperProofCount = $promotionBlockedByMissingHelperProofCount
        PromotionBlockedByStaleHelperProofCount = $promotionBlockedByStaleHelperProofCount
        PromotionBlockedByEncodeBudgetCount = $promotionBlockedByEncodeBudgetCount
        PromotionBlockedByEncodeBudgetAloneCount = $promotionBlockedByEncodeBudgetAloneCount
        HelperVisibleHeadRuntimeSenderMismatch = $helperVisibleHeadRuntimeSenderMismatch
        LatestHealthyTickResetReasonCounts = $latestHealthyTickResetReasonCounts
        LatestReducedPromotionRecentEntries = $latestReducedPromotionRecentEntries
        LatestHelperSessionId = $latestHelperSessionId
        LatestHelperRunId = $latestHelperRunId
        LatestHelperListenerGeneration = $latestHelperListenerGeneration
        LatestHelperUpstreamCaptureToFrameReadyAvgMs = $latestHelperUpstreamCaptureToFrameReadyAvgMs
        LatestHelperUpstreamCaptureToFrameReadyMedianMs = $latestHelperUpstreamCaptureToFrameReadyMedianMs
        LatestHelperUpstreamCaptureToFrameReadyP95Ms = $latestHelperUpstreamCaptureToFrameReadyP95Ms
        LatestHelperUpstreamCaptureToFrameReadyMaxMs = $latestHelperUpstreamCaptureToFrameReadyMaxMs
        LatestHelperUpstreamFrameReadyToViewerAcceptAvgMs = $latestHelperUpstreamFrameReadyToViewerAcceptAvgMs
        LatestHelperUpstreamFrameReadyToViewerAcceptMedianMs = $latestHelperUpstreamFrameReadyToViewerAcceptMedianMs
        LatestHelperUpstreamFrameReadyToViewerAcceptP95Ms = $latestHelperUpstreamFrameReadyToViewerAcceptP95Ms
        LatestHelperUpstreamFrameReadyToViewerAcceptMaxMs = $latestHelperUpstreamFrameReadyToViewerAcceptMaxMs
        LatestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs = $latestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs
        LatestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs = $latestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs
        LatestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms = $latestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms
        LatestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs = $latestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs
        LatestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs = $latestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs
        LatestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs = $latestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs
        LatestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms = $latestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms
        LatestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs = $latestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs
        LatestHelperUpstreamCaptureToDecodeStartAvgMs = $latestHelperUpstreamCaptureToDecodeStartAvgMs
        LatestHelperUpstreamCaptureToDecodeStartMedianMs = $latestHelperUpstreamCaptureToDecodeStartMedianMs
        LatestHelperUpstreamCaptureToDecodeStartP95Ms = $latestHelperUpstreamCaptureToDecodeStartP95Ms
        LatestHelperUpstreamCaptureToDecodeStartMaxMs = $latestHelperUpstreamCaptureToDecodeStartMaxMs
        LatestHelperUpstreamWorstEpochByCaptureToDecodeStart = $latestHelperUpstreamWorstEpochByCaptureToDecodeStart
        LatestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs = $latestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs
        LatestHelperDominantUpstreamLatencyStage = $latestHelperDominantUpstreamLatencyStage
        LatestHelperReadyPathCaptureToFirstFragmentObservedAvgMs = $latestHelperReadyPathCaptureToFirstFragmentObservedAvgMs
        LatestHelperReadyPathCaptureToFirstFragmentObservedMedianMs = $latestHelperReadyPathCaptureToFirstFragmentObservedMedianMs
        LatestHelperReadyPathCaptureToFirstFragmentObservedP95Ms = $latestHelperReadyPathCaptureToFirstFragmentObservedP95Ms
        LatestHelperReadyPathCaptureToFirstFragmentObservedMaxMs = $latestHelperReadyPathCaptureToFirstFragmentObservedMaxMs
        LatestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs = $latestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs
        LatestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs = $latestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs
        LatestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms = $latestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms
        LatestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs = $latestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs
        LatestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs = $latestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs
        LatestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs = $latestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs
        LatestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms = $latestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms
        LatestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs = $latestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs
        LatestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs = $latestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs
        LatestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs = $latestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs
        LatestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms = $latestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms
        LatestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs = $latestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs
        LatestHelperDominantReadyPathStage = $latestHelperDominantReadyPathStage
        LatestHelperReceivePathCaptureToEnvelopeSendAvgMs = $latestHelperReceivePathCaptureToEnvelopeSendAvgMs
        LatestHelperReceivePathCaptureToEnvelopeSendMedianMs = $latestHelperReceivePathCaptureToEnvelopeSendMedianMs
        LatestHelperReceivePathCaptureToEnvelopeSendP95Ms = $latestHelperReceivePathCaptureToEnvelopeSendP95Ms
        LatestHelperReceivePathCaptureToEnvelopeSendMaxMs = $latestHelperReceivePathCaptureToEnvelopeSendMaxMs
        LatestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs = $latestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs
        LatestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs = $latestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs
        LatestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms = $latestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms
        LatestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs = $latestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs
        LatestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs = $latestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs
        LatestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs = $latestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs
        LatestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms = $latestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms
        LatestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs = $latestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs
        LatestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs = $latestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs
        LatestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs = $latestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs
        LatestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms = $latestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms
        LatestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs = $latestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs
        LatestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs = $latestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs
        LatestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs = $latestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs
        LatestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms = $latestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms
        LatestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs = $latestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs
        LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs = $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs
        LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs = $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs
        LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms = $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms
        LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs = $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs
        LatestHelperDominantReceivePathStage = $latestHelperDominantReceivePathStage
        LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs = $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs
        LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs = $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs
        LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms = $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms
        LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs = $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs
        LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs = $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs
        LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs = $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs
        LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms = $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms
        LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs = $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs
        LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs = $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs
        LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs = $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs
        LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms = $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms
        LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs = $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs
        LatestHelperDominantBridgeIngressStage = $latestHelperDominantBridgeIngressStage
        LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs = $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs
        LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs = $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs
        LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms = $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms
        LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs = $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs
        LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs = $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs
        LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs = $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs
        LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms = $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms
        LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs = $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs
        LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs = $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs
        LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs = $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs
        LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms = $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms
        LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs = $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs
        LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs = $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs
        LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs = $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs
        LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms = $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms
        LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs = $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs
        LatestHelperDominantNknReceiveStage = $latestHelperDominantNknReceiveStage
        LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs = $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs
        LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs = $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs
        LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms = $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms
        LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs = $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs
        LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs = $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs
        LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs = $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs
        LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms = $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms
        LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs = $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs
        LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs = $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs
        LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs = $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs
        LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms = $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms
        LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs = $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs
        LatestHelperDominantWsReceiveStage = $latestHelperDominantWsReceiveStage
        LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs = $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs
        LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs = $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs
        LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms = $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms
        LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs = $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs
        LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs = $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs
        LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs = $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs
        LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms = $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms
        LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs = $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs
        LatestHelperDominantSocketReceiveStage = $latestHelperDominantSocketReceiveStage
        LatestBridgeEventLoopP95Ms = $latestBridgeEventLoopP95Ms
        LatestBridgeEventLoopMaxMs = $latestBridgeEventLoopMaxMs
        LatestBridgeEventLoopMeanMs = $latestBridgeEventLoopMeanMs
        LatestBridgeEventLoopSampleWindowMs = $latestBridgeEventLoopSampleWindowMs
        LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs
        LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs
        LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms
        LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs
        LatestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = $latestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs
        LatestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = $latestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs
        LatestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = $latestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms
        LatestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = $latestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs
        LatestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = $latestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs
        LatestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = $latestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs
        LatestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = $latestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms
        LatestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = $latestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs
        LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs
        LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs
        LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms
        LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs
        LatestBridgeMediaSendFramesSent = $latestBridgeMediaSendFramesSent
        LatestBridgeMediaSendFailures = $latestBridgeMediaSendFailures
        LatestBridgeMediaSendQueueDrops = $latestBridgeMediaSendQueueDrops
        LatestBridgeMediaSendQueueMode = $latestBridgeMediaSendQueueMode
        LatestBridgeMediaSendQueueDepth = $latestBridgeMediaSendQueueDepth
        LatestBridgeMediaSendOldestQueuedAgeMs = $latestBridgeMediaSendOldestQueuedAgeMs
        LatestBridgeMediaSendSampleWindowMs = $latestBridgeMediaSendSampleWindowMs
        LatestBridgeTransportHealthSelectedRpc = $latestBridgeTransportHealthSelectedRpc
        LatestBridgeTransportHealthSelectedRpcKey = $latestBridgeTransportHealthSelectedRpcKey
        LatestBridgeTransportHealthSelectedRpcStage = $latestBridgeTransportHealthSelectedRpcStage
        LatestBridgeTransportHealthConnectId = $latestBridgeTransportHealthConnectId
        LatestBridgeTransportHealthConnectKey = $latestBridgeTransportHealthConnectKey
        LatestBridgeTransportHealthReadyEmitted = $latestBridgeTransportHealthReadyEmitted
        LatestBridgeTransportHealthClientReadyAgeMs = $latestBridgeTransportHealthClientReadyAgeMs
        LatestBridgeTransportHealthDisconnectCountSinceLast = $latestBridgeTransportHealthDisconnectCountSinceLast
        LatestBridgeTransportHealthConnectFailedCountSinceLast = $latestBridgeTransportHealthConnectFailedCountSinceLast
        LatestBridgeTransportHealthWsErrorCountSinceLast = $latestBridgeTransportHealthWsErrorCountSinceLast
        LatestBridgeTransportHealthRpcFallbackAttemptCountSinceLast = $latestBridgeTransportHealthRpcFallbackAttemptCountSinceLast
        LatestBridgeTransportHealthControlReady = $latestBridgeTransportHealthControlReady
        LatestBridgeTransportHealthMediaReady = $latestBridgeTransportHealthMediaReady
        LatestBridgeTransportHealthBulkReady = $latestBridgeTransportHealthBulkReady
        LatestBridgeTransportHealthFramesSentSinceLast = $latestBridgeTransportHealthFramesSentSinceLast
        LatestBridgeTransportHealthLatestDisconnectReason = $latestBridgeTransportHealthLatestDisconnectReason
        LatestBridgeTransportHealthSampleWindowMs = $latestBridgeTransportHealthSampleWindowMs
        LatestBridgeTransportHealthUniqueSelectedRpcCount = $latestBridgeTransportHealthUniqueSelectedRpcCount
        HelperQualitySummaryLines = @($helperQualitySummaryLines.ToArray())
        HelperUpstreamLatencySummaryLines = @($helperUpstreamLatencySummaryLines.ToArray())
        HelperReadyPathSummaryLines = @($helperReadyPathSummaryLines.ToArray())
        HelperReceivePathSummaryLines = @($helperReceivePathSummaryLines.ToArray())
        HelperBridgeIngressSummaryLines = @($helperBridgeIngressSummaryLines.ToArray())
        HelperNknReceiveSummaryLines = @($helperNknReceiveSummaryLines.ToArray())
        HelperWsReceiveSummaryLines = @($helperWsReceiveSummaryLines.ToArray())
        HelperSocketReceiveSummaryLines = @($helperSocketReceiveSummaryLines.ToArray())
        BridgeEventLoopSummaryLines = @($bridgeEventLoopSummaryLines.ToArray())
        BridgeMediaSendSummaryLines = @($bridgeMediaSendSummaryLines.ToArray())
        BridgeTransportHealthSummaryLines = @($bridgeTransportHealthSummaryLines.ToArray())
        HelperEpochLossLines = @($helperEpochLossLines.ToArray())
        HelperEpochTimelineLines = @($helperEpochTimelineLines.ToArray())
        HelperReassemblerRootCauseSummaryLines = @($helperReassemblerRootCauseSummaryLines.ToArray())
        HelperRecoveryEpochInvestigationLines = @($helperRecoveryEpochInvestigationLines.ToArray())
        HelperReassemblerRecoveryOwnerTransitionLines = @($helperReassemblerRecoveryOwnerTransitionLines.ToArray())
        HelperReassemblerActionableLateFragmentLines = @($helperReassemblerActionableLateFragmentLines.ToArray())
        HelperReassemblerOlderEpochCleanupLines = @($helperReassemblerOlderEpochCleanupLines.ToArray())
        HelperPressureSummaryLines = @($helperPressureSummaryLines.ToArray())
        HealthSnapshotLines = @($healthSnapshotLines.ToArray())
        ReducedPromotionSummaryLines = @($reducedPromotionSummaryLines.ToArray())
        LogPath = $logPath
    }
}

function Write-SoakDiagnosticsArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)]$Summary
    )

    $artifactRoot = Join-Path $RepoRoot 'artifacts\soak'
    $artifactDir = Join-Path $artifactRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

    $helperDecodeWorkerSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("frames_completed={0}" -f $Summary.LatestHelperFramesCompleted),
        ("frames_enqueued_for_decode={0}" -f $Summary.LatestHelperFramesEnqueuedForDecode),
        ("frames_dropped_before_decode={0}" -f $Summary.LatestHelperFramesDroppedBeforeDecode),
        ("frames_decoded={0}" -f $Summary.LatestHelperFramesDecoded),
        ("frames_dropped_after_decode={0}" -f $Summary.LatestHelperFramesDroppedAfterDecode),
        ("frames_applied={0}" -f $Summary.LatestHelperFramesApplied),
        ("max_pending_encoded_depth={0}" -f $Summary.LatestHelperMaxPendingEncodedDepth),
        ("max_pending_decoded_depth={0}" -f $Summary.LatestHelperMaxPendingDecodedDepth),
        ("avg_enqueue_to_decode_start_ms={0}" -f $Summary.LatestHelperAvgEnqueueToDecodeStartMs),
        ("avg_enqueue_to_drop_ms={0}" -f $Summary.LatestHelperAvgEnqueueToDropMs),
        ("decode_worker_drop_queue_overflow_count={0}" -f $Summary.LatestHelperDecodeWorkerDropQueueOverflowCount),
        ("decode_worker_drop_age_budget_count={0}" -f $Summary.LatestHelperDecodeWorkerDropAgeBudgetCount),
        ("decode_worker_drop_generation_count={0}" -f $Summary.LatestHelperDecodeWorkerDropGenerationCount),
        ("decode_worker_drop_stopped_count={0}" -f $Summary.LatestHelperDecodeWorkerDropStoppedCount),
        ("decode_queue_overflow_count={0}" -f $Summary.LatestHelperDecodeQueueOverflowCount),
        ("decode_age_budget_count={0}" -f $Summary.LatestHelperDecodeAgeBudgetCount),
        ("decode_generation_changed_count={0}" -f $Summary.LatestHelperDecodeGenerationChangedCount),
        ("decode_stopped_count={0}" -f $Summary.LatestHelperDecodeStoppedCount),
        ("decoded_apply_queue_overflow_count={0}" -f $Summary.LatestHelperDecodedApplyQueueOverflowCount),
        ("decoded_frame_replaced_before_apply_count={0}" -f $Summary.LatestHelperDecodedFrameReplacedBeforeApplyCount),
        ("stale_dropped_after_decode_count={0}" -f $Summary.LatestHelperStaleDroppedAfterDecodeCount),
        ("dropped_waiting_for_recovery_keyframe_count={0}" -f $Summary.LatestHelperDroppedWaitingForRecoveryKeyframeCount),
        ("waiting_for_recovery_keyframe_reject_count={0}" -f $Summary.LatestHelperWaitingForRecoveryKeyframeRejectCount),
        ("recovery_wait_reject_before_runway_count={0}" -f $Summary.LatestHelperRecoveryWaitRejectBeforeRunwayCount),
        ("recovery_runway_overflow_reject_count={0}" -f $Summary.LatestHelperRecoveryRunwayOverflowRejectCount),
        ("suppressed_emit_during_recovery_wait_count={0}" -f $Summary.LatestHelperSuppressedEmitDuringRecoveryWaitCount),
        ("stale_superseded_recovery_suppressed_count={0}" -f $Summary.LatestHelperStaleSupersededRecoverySuppressedCount),
        ("soft_stale_cleanup_count={0}" -f $Summary.LatestHelperSoftStaleCleanupCount),
        ("blocked_by_reserved_recovery_frame_reject_count={0}" -f $Summary.LatestHelperBlockedByReservedRecoveryFrameRejectCount),
        ("older_epoch_ignored_during_recovery_lock_count={0}" -f $Summary.LatestHelperOlderEpochIgnoredDuringRecoveryLockCount),
        ("newer_epoch_non_key_ignored_during_lock_count={0}" -f $Summary.LatestHelperNewerEpochNonKeyIgnoredDuringLockCount),
        ("deferred_post_recovery_candidate_replace_count={0}" -f $Summary.LatestHelperDeferredPostRecoveryCandidateReplaceCount),
        ("future_tail_quarantined_during_gap_count={0}" -f $Summary.LatestHelperFutureTailQuarantinedDuringGapCount),
        ("future_tail_quarantined_after_gap_count={0}" -f $Summary.LatestHelperFutureTailQuarantinedAfterGapCount),
        ("pre_candidate_gap_tail_rejected_count={0}" -f $Summary.LatestHelperPreCandidateGapTailRejectedCount),
        ("recovery_candidate_present_count={0}" -f $Summary.LatestHelperRecoveryCandidatePresentCount),
        ("visible_recovery_floor_frame_id={0}" -f $Summary.LatestHelperVisibleRecoveryFloorFrameId),
        ("stable_visible_head_frame_id={0}" -f $Summary.LatestHelperStableVisibleHeadFrameId),
        ("applied_head_frame_id={0}" -f $Summary.LatestHelperAppliedHeadFrameId),
        ("visible_head_frame_id={0}" -f $Summary.LatestHelperVisibleHeadFrameId),
        ("ordered_emit_head_frame_id={0}" -f $Summary.LatestHelperOrderedEmitHeadFrameId),
        ("winning_recovery_frame_id={0}" -f $Summary.LatestHelperWinningRecoveryFrameId),
        ("recovery_owner_replaced_count={0}" -f $Summary.LatestHelperRecoveryOwnerReplacedCount),
        ("older_epoch_cleanup_after_epoch_advance_count={0}" -f $Summary.LatestHelperOlderEpochCleanupAfterEpochAdvanceCount),
        ("superseded_recovery_tail_cleanup_count={0}" -f $Summary.LatestHelperSupersededRecoveryTailCleanupCount),
        ("late_same_epoch_after_head_advanced_drop_count={0}" -f $Summary.LatestHelperLateSameEpochAfterHeadAdvancedDropCount),
        ("stale_runway_window_abort_count={0}" -f $Summary.LatestHelperStaleRunwayWindowAbortCount),
        ("runway_candidate_expired_after_head_advance_count={0}" -f $Summary.LatestHelperRunwayCandidateExpiredAfterHeadAdvanceCount),
        ("runway_followers_emitted_within_actionable_window_count={0}" -f $Summary.LatestHelperRunwayFollowersEmittedWithinActionableWindowCount),
        ("steady_visible_progress_active={0}" -f $Summary.LatestHelperSteadyVisibleProgressActive),
        ("steady_visible_progress_activation_frame_id={0}" -f $Summary.LatestHelperSteadyVisibleProgressActivationFrameId),
        ("frames_applied_since_last_gap={0}" -f $Summary.LatestHelperFramesAppliedSinceLastGap),
        ("last_sent_stable_visible_head_frame_id={0}" -f $Summary.LatestHelperLastSentStableVisibleHeadFrameId),
        ("steady_visible_progress_cleared_count={0}" -f $Summary.LatestHelperSteadyVisibleProgressClearedCount),
        ("steady_visible_progress_cleared_reason={0}" -f $Summary.LatestHelperSteadyVisibleProgressClearedReason),
        ("pre_candidate_gap_tail_emitted_to_viewer_count={0}" -f $Summary.LatestHelperPreCandidateGapTailEmittedToViewerCount),
        ("late_fragment_after_applied_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterAppliedHeadCount),
        ("late_fragment_after_ordered_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterOrderedHeadCount),
        ("late_fragment_after_stable_visible_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterStableVisibleHeadCount),
        ("late_fragment_after_visible_recovery_count={0}" -f $Summary.LatestHelperLateFragmentAfterVisibleRecoveryCount),
        ("actionable_late_fragment_count={0}" -f $Summary.LatestHelperActionableLateFragmentCount),
        ("recovery_runway_contiguous_follower_buffer_count={0}" -f $Summary.LatestHelperRecoveryRunwayContiguousFollowerBufferCount),
        ("recovery_runway_contiguous_follower_apply_count={0}" -f $Summary.LatestHelperRecoveryRunwayContiguousFollowerApplyCount),
        ("recovery_runway_abort_count={0}" -f $Summary.LatestHelperRecoveryRunwayAbortCount),
        ("recovery_follower_window_buffered_count={0}" -f $Summary.LatestHelperRecoveryFollowerWindowBufferedCount),
        ("recovery_follower_window_applied_count={0}" -f $Summary.LatestHelperRecoveryFollowerWindowAppliedCount),
        ("recovery_follower_window_trimmed_count={0}" -f $Summary.LatestHelperRecoveryFollowerWindowTrimmedCount),
        ("protected_recovery_delivery_count={0}" -f $Summary.LatestHelperProtectedRecoveryDeliveryCount),
        ("recovery_progress_corridor_count={0}" -f $Summary.LatestHelperRecoveryProgressCorridorCount),
        ("recovery_progress_corridor_success_count={0}" -f $Summary.LatestHelperRecoveryProgressCorridorSuccessCount),
        ("recovery_progress_corridor_abort_count={0}" -f $Summary.LatestHelperRecoveryProgressCorridorAbortCount),
        ("recovery_progress_corridor_applied_count={0}" -f $Summary.LatestHelperRecoveryProgressCorridorAppliedCount),
        ("recovery_keyframe_pending_visible_apply_count={0}" -f $Summary.LatestHelperRecoveryKeyframePendingVisibleApplyCount),
        ("startup_corridor_buffered_follower_count={0}" -f $Summary.LatestHelperStartupCorridorBufferedFollowerCount),
        ("startup_corridor_release_count={0}" -f $Summary.LatestHelperStartupCorridorReleaseCount),
        ("startup_corridor_abort_count={0}" -f $Summary.LatestHelperStartupCorridorAbortCount),
        ("startup_corridor_abort_reason={0}" -f $Summary.LatestHelperStartupCorridorAbortReason),
        ("post_recovery_visible_generation_reset_count={0}" -f $Summary.LatestHelperPostRecoveryVisibleGenerationResetCount),
        ("post_recovery_purged_pre_recovery_follower_count={0}" -f $Summary.LatestHelperPostRecoveryPurgedPreRecoveryFollowerCount),
        ("post_recovery_stale_drop_bypass_count={0}" -f $Summary.LatestHelperPostRecoveryStaleDropBypassCount),
        ("late_fragment_after_successful_recovery_count={0}" -f $Summary.LatestHelperLateFragmentAfterSuccessfulRecoveryCount),
        ("post_recovery_high_frame_age_suppressed_ticks={0}" -f $Summary.AggregatePostRecoveryHighFrameAgeSuppressedTicks),
        ("dominant_helper_admission_reject_reason={0}" -f $Summary.LatestHelperDominantAdmissionRejectReason),
        ("high_frame_age_suppressed_due_to_visible_progress_count={0}" -f $Summary.AggregateHighFrameAgeSuppressedDueToVisibleProgressCount),
        ("high_frame_age_suppressed_due_to_head_advance_count={0}" -f $Summary.LatestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount),
        ("actionable_high_frame_age_count={0}" -f $Summary.LatestHelperActionableHighFrameAgeCount),
        ("unattributed_loss_count={0}" -f $Summary.LatestHelperUnattributedLossCount),
        '',
        'helper_epoch_loss_lines:'
    ) + @($Summary.HelperEpochLossLines)

    $helperQualitySummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("visible_apply_ratio={0}" -f $Summary.LatestHelperVisibleApplyRatio),
        ("helper_apply_ms_avg={0}" -f $Summary.HelperApplyAvgMs),
        ("helper_apply_ms_p95={0}" -f $Summary.HelperApplyP95Ms),
        ("avg_decode_complete_to_visible_apply_ms={0}" -f $Summary.LatestHelperAvgDecodeCompleteToVisibleApplyMs),
        ("avg_ui_post_apply_ms={0}" -f $Summary.LatestHelperAvgUiPostApplyMs),
        ("avg_visible_head_lag_frames={0}" -f $Summary.LatestHelperAvgVisibleHeadLagFrames),
        ("avg_stable_head_lag_frames={0}" -f $Summary.LatestHelperAvgStableHeadLagFrames),
        ("last_reserved_apply_hold_ms={0}" -f $Summary.LatestHelperLastReservedApplyHoldMs),
        ("last_recovery_progress_corridor_hold_ms={0}" -f $Summary.LatestHelperLastRecoveryProgressCorridorHoldMs),
        ("last_recovery_runway_abort_hold_ms={0}" -f $Summary.LatestHelperLastRecoveryRunwayAbortHoldMs),
        ("last_recovery_progress_corridor_abort_reason={0}" -f $Summary.LatestHelperLastRecoveryProgressCorridorAbortReason),
        ("reassembler_loss_count={0}" -f $Summary.LatestHelperReassemblerLossCount),
        ("gap_count={0}" -f $Summary.LatestHelperGapCount),
        ("recovery_keyframe_apply_count={0}" -f $Summary.LatestHelperRecoveryKeyframeApplyCount),
        ("resync_count={0}" -f $Summary.LatestHelperResyncCount),
        ("dominant_reassembler_root_cause={0}" -f $Summary.DominantReassemblerRootCause),
        ("dominant_helper_admission_reject_reason={0}" -f $Summary.LatestHelperDominantAdmissionRejectReason),
        ("recovery_wait_reject_before_runway_count={0}" -f $Summary.LatestHelperRecoveryWaitRejectBeforeRunwayCount),
        ("recovery_runway_overflow_reject_count={0}" -f $Summary.LatestHelperRecoveryRunwayOverflowRejectCount),
        ("suppressed_emit_during_recovery_wait_count={0}" -f $Summary.LatestHelperSuppressedEmitDuringRecoveryWaitCount),
        ("stale_superseded_recovery_suppressed_count={0}" -f $Summary.LatestHelperStaleSupersededRecoverySuppressedCount),
        ("soft_stale_cleanup_count={0}" -f $Summary.LatestHelperSoftStaleCleanupCount),
        ("pre_candidate_gap_tail_emitted_to_viewer_count={0}" -f $Summary.LatestHelperPreCandidateGapTailEmittedToViewerCount),
        ("recovery_candidate_present_count={0}" -f $Summary.LatestHelperRecoveryCandidatePresentCount),
        ("visible_recovery_floor_frame_id={0}" -f $Summary.LatestHelperVisibleRecoveryFloorFrameId),
        ("stable_visible_head_frame_id={0}" -f $Summary.LatestHelperStableVisibleHeadFrameId),
        ("applied_head_frame_id={0}" -f $Summary.LatestHelperAppliedHeadFrameId),
        ("visible_head_frame_id={0}" -f $Summary.LatestHelperVisibleHeadFrameId),
        ("ordered_emit_head_frame_id={0}" -f $Summary.LatestHelperOrderedEmitHeadFrameId),
        ("winning_recovery_frame_id={0}" -f $Summary.LatestHelperWinningRecoveryFrameId),
        ("recovery_owner_replaced_count={0}" -f $Summary.LatestHelperRecoveryOwnerReplacedCount),
        ("older_epoch_cleanup_after_epoch_advance_count={0}" -f $Summary.LatestHelperOlderEpochCleanupAfterEpochAdvanceCount),
        ("late_same_epoch_after_head_advanced_drop_count={0}" -f $Summary.LatestHelperLateSameEpochAfterHeadAdvancedDropCount),
        ("stale_runway_window_abort_count={0}" -f $Summary.LatestHelperStaleRunwayWindowAbortCount),
        ("runway_candidate_expired_after_head_advance_count={0}" -f $Summary.LatestHelperRunwayCandidateExpiredAfterHeadAdvanceCount),
        ("runway_followers_emitted_within_actionable_window_count={0}" -f $Summary.LatestHelperRunwayFollowersEmittedWithinActionableWindowCount),
        ("steady_visible_progress_active={0}" -f $Summary.LatestHelperSteadyVisibleProgressActive),
        ("frames_applied_since_last_gap={0}" -f $Summary.LatestHelperFramesAppliedSinceLastGap),
        ("pre_candidate_gap_tail_rejected_count={0}" -f $Summary.LatestHelperPreCandidateGapTailRejectedCount),
        ("late_fragment_after_applied_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterAppliedHeadCount),
        ("late_fragment_after_ordered_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterOrderedHeadCount),
        ("late_fragment_after_stable_visible_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterStableVisibleHeadCount),
        ("late_fragment_after_visible_recovery_count={0}" -f $Summary.LatestHelperLateFragmentAfterVisibleRecoveryCount),
        ("actionable_late_fragment_count={0}" -f $Summary.LatestHelperActionableLateFragmentCount),
        ("dominant_helper_pressure_blocker={0}" -f $Summary.DominantHelperPressureBlocker),
        ("baseline_established={0}" -f $Summary.LatestHelperBaselineEstablished),
        ("baseline_capture_to_render_ms={0}" -f $Summary.LatestHelperBaselineCaptureToRenderMs),
        ("age_excess_ms={0}" -f $Summary.LatestHelperAgeExcessMs),
        ("progress_stall_ms={0}" -f $Summary.LatestHelperProgressStallMs),
        ("baseline_reseed_in_progress={0}" -f $Summary.LatestHelperBaselineReseedInProgress),
        ("age_pressure_consecutive_count={0}" -f $Summary.LatestHelperAgePressureConsecutiveCount),
        ("cadence_pressure_consecutive_count={0}" -f $Summary.LatestHelperCadencePressureConsecutiveCount),
        ("post_recovery_age_grace_active={0}" -f $Summary.LatestPostRecoveryAgeGraceActive),
        ("post_recovery_age_grace_suppressed_count={0}" -f $Summary.LatestPostRecoveryAgeGraceSuppressedCount),
        ("catch_up_suppressed_due_to_progress_count={0}" -f $Summary.LatestHelperCatchUpSuppressedDueToProgressCount),
        ("baseline_frozen_due_to_stall_count={0}" -f $Summary.LatestHelperBaselineFrozenDueToStallCount),
        ("baseline_reseed_after_recovery_count={0}" -f $Summary.LatestHelperBaselineReseedAfterRecoveryCount),
        ("cadence_stall_window_count={0}" -f $Summary.LatestHelperCadenceStallWindowCount),
        ("cadence_stall_trigger_count={0}" -f $Summary.LatestHelperCadenceStallTriggerCount),
        ("high_frame_age_suppressed_due_to_visible_progress_count={0}" -f $Summary.AggregateHighFrameAgeSuppressedDueToVisibleProgressCount),
        ("high_frame_age_suppressed_due_to_head_advance_count={0}" -f $Summary.LatestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount),
        ("actionable_high_frame_age_count={0}" -f $Summary.LatestHelperActionableHighFrameAgeCount),
        ("worst_epoch_by_visible_apply_ratio={0}" -f $Summary.WorstEpochByVisibleApplyRatio),
        ("worst_epoch_visible_apply_ratio={0}" -f $Summary.WorstEpochVisibleApplyRatio),
        ("worst_epoch_by_recovery_lock_time={0}" -f $Summary.WorstEpochByRecoveryLockTime),
        ("worst_epoch_recovery_lock_time_ms={0}" -f $Summary.WorstEpochRecoveryLockTimeMs),
        ("recovery_follower_window_buffered_count={0}" -f $Summary.LatestHelperRecoveryFollowerWindowBufferedCount),
        ("recovery_follower_window_applied_count={0}" -f $Summary.LatestHelperRecoveryFollowerWindowAppliedCount),
        ("recovery_runway_contiguous_follower_buffer_count={0}" -f $Summary.LatestHelperRecoveryRunwayContiguousFollowerBufferCount),
        ("recovery_runway_contiguous_follower_apply_count={0}" -f $Summary.LatestHelperRecoveryRunwayContiguousFollowerApplyCount),
        ("recovery_runway_abort_count={0}" -f $Summary.LatestHelperRecoveryRunwayAbortCount),
        ("recovery_progress_corridor_count={0}" -f $Summary.LatestHelperRecoveryProgressCorridorCount),
        ("recovery_progress_corridor_success_count={0}" -f $Summary.LatestHelperRecoveryProgressCorridorSuccessCount),
        ("recovery_progress_corridor_abort_count={0}" -f $Summary.LatestHelperRecoveryProgressCorridorAbortCount),
        ("recovery_progress_corridor_applied_count={0}" -f $Summary.LatestHelperRecoveryProgressCorridorAppliedCount),
        ("recovery_keyframe_pending_visible_apply_count={0}" -f $Summary.LatestHelperRecoveryKeyframePendingVisibleApplyCount),
        ("startup_corridor_buffered_follower_count={0}" -f $Summary.LatestHelperStartupCorridorBufferedFollowerCount),
        ("startup_corridor_release_count={0}" -f $Summary.LatestHelperStartupCorridorReleaseCount),
        ("startup_corridor_abort_count={0}" -f $Summary.LatestHelperStartupCorridorAbortCount),
        ("startup_corridor_abort_reason={0}" -f $Summary.LatestHelperStartupCorridorAbortReason),
        ("recovery_follower_window_trimmed_count={0}" -f $Summary.LatestHelperRecoveryFollowerWindowTrimmedCount),
        ("protected_recovery_delivery_count={0}" -f $Summary.LatestHelperProtectedRecoveryDeliveryCount),
        ("recovery_post_ack_hold_active={0}" -f $Summary.LatestRecoveryPostAckHoldActive),
        ("recovery_post_ack_hold_started_count={0}" -f $Summary.LatestRecoveryPostAckHoldStartedCount),
        ("recovery_post_ack_hold_expired_count={0}" -f $Summary.LatestRecoveryPostAckHoldExpiredCount),
        ("recovery_post_ack_hold_suppressed_reopen_count={0}" -f $Summary.LatestRecoveryPostAckHoldSuppressedReopenCount),
        ("late_fragment_after_successful_recovery_count={0}" -f $Summary.LatestHelperLateFragmentAfterSuccessfulRecoveryCount),
        '',
        'helper_quality_summary_lines:'
    ) + @($Summary.HelperQualitySummaryLines)

    $helperUpstreamLatencySummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("capture_to_frame_ready_avg_ms={0}" -f $Summary.LatestHelperUpstreamCaptureToFrameReadyAvgMs),
        ("capture_to_frame_ready_median_ms={0}" -f $Summary.LatestHelperUpstreamCaptureToFrameReadyMedianMs),
        ("capture_to_frame_ready_p95_ms={0}" -f $Summary.LatestHelperUpstreamCaptureToFrameReadyP95Ms),
        ("capture_to_frame_ready_max_ms={0}" -f $Summary.LatestHelperUpstreamCaptureToFrameReadyMaxMs),
        ("frame_ready_to_viewer_accept_avg_ms={0}" -f $Summary.LatestHelperUpstreamFrameReadyToViewerAcceptAvgMs),
        ("frame_ready_to_viewer_accept_median_ms={0}" -f $Summary.LatestHelperUpstreamFrameReadyToViewerAcceptMedianMs),
        ("frame_ready_to_viewer_accept_p95_ms={0}" -f $Summary.LatestHelperUpstreamFrameReadyToViewerAcceptP95Ms),
        ("frame_ready_to_viewer_accept_max_ms={0}" -f $Summary.LatestHelperUpstreamFrameReadyToViewerAcceptMaxMs),
        ("viewer_accept_to_decode_enqueue_avg_ms={0}" -f $Summary.LatestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs),
        ("viewer_accept_to_decode_enqueue_median_ms={0}" -f $Summary.LatestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs),
        ("viewer_accept_to_decode_enqueue_p95_ms={0}" -f $Summary.LatestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms),
        ("viewer_accept_to_decode_enqueue_max_ms={0}" -f $Summary.LatestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs),
        ("decode_enqueue_to_decode_start_avg_ms={0}" -f $Summary.LatestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs),
        ("decode_enqueue_to_decode_start_median_ms={0}" -f $Summary.LatestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs),
        ("decode_enqueue_to_decode_start_p95_ms={0}" -f $Summary.LatestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms),
        ("decode_enqueue_to_decode_start_max_ms={0}" -f $Summary.LatestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs),
        ("capture_to_decode_start_avg_ms={0}" -f $Summary.LatestHelperUpstreamCaptureToDecodeStartAvgMs),
        ("capture_to_decode_start_median_ms={0}" -f $Summary.LatestHelperUpstreamCaptureToDecodeStartMedianMs),
        ("capture_to_decode_start_p95_ms={0}" -f $Summary.LatestHelperUpstreamCaptureToDecodeStartP95Ms),
        ("capture_to_decode_start_max_ms={0}" -f $Summary.LatestHelperUpstreamCaptureToDecodeStartMaxMs),
        ("worst_epoch_by_capture_to_decode_start={0}" -f $Summary.LatestHelperUpstreamWorstEpochByCaptureToDecodeStart),
        ("worst_epoch_capture_to_decode_start_avg_ms={0}" -f $Summary.LatestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs),
        ("dominant_upstream_latency_stage={0}" -f $Summary.LatestHelperDominantUpstreamLatencyStage),
        ("helper_session_phase={0}" -f $(if ([string]::IsNullOrWhiteSpace($Summary.LatestSummaryHelperSessionPhase)) { '(none)' } else { $Summary.LatestSummaryHelperSessionPhase })),
        ("helper_recovery_mechanism={0}" -f $(if ([string]::IsNullOrWhiteSpace($Summary.LatestSummaryHelperRecoveryMechanism)) { '(none)' } else { $Summary.LatestSummaryHelperRecoveryMechanism })),
        '',
        'helper_upstream_latency_summary_lines:'
    ) + @($Summary.HelperUpstreamLatencySummaryLines)

    $helperReadyPathSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("capture_to_first_fragment_observed_avg_ms={0}" -f $Summary.LatestHelperReadyPathCaptureToFirstFragmentObservedAvgMs),
        ("capture_to_first_fragment_observed_median_ms={0}" -f $Summary.LatestHelperReadyPathCaptureToFirstFragmentObservedMedianMs),
        ("capture_to_first_fragment_observed_p95_ms={0}" -f $Summary.LatestHelperReadyPathCaptureToFirstFragmentObservedP95Ms),
        ("capture_to_first_fragment_observed_max_ms={0}" -f $Summary.LatestHelperReadyPathCaptureToFirstFragmentObservedMaxMs),
        ("first_fragment_to_last_fragment_observed_avg_ms={0}" -f $Summary.LatestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs),
        ("first_fragment_to_last_fragment_observed_median_ms={0}" -f $Summary.LatestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs),
        ("first_fragment_to_last_fragment_observed_p95_ms={0}" -f $Summary.LatestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms),
        ("first_fragment_to_last_fragment_observed_max_ms={0}" -f $Summary.LatestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs),
        ("last_fragment_to_assembly_complete_avg_ms={0}" -f $Summary.LatestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs),
        ("last_fragment_to_assembly_complete_median_ms={0}" -f $Summary.LatestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs),
        ("last_fragment_to_assembly_complete_p95_ms={0}" -f $Summary.LatestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms),
        ("last_fragment_to_assembly_complete_max_ms={0}" -f $Summary.LatestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs),
        ("assembly_complete_to_frame_emitted_avg_ms={0}" -f $Summary.LatestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs),
        ("assembly_complete_to_frame_emitted_median_ms={0}" -f $Summary.LatestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs),
        ("assembly_complete_to_frame_emitted_p95_ms={0}" -f $Summary.LatestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms),
        ("assembly_complete_to_frame_emitted_max_ms={0}" -f $Summary.LatestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs),
        ("dominant_ready_path_stage={0}" -f $Summary.LatestHelperDominantReadyPathStage),
        ("helper_session_phase={0}" -f $Summary.LatestSummaryHelperSessionPhase),
        ("helper_recovery_mechanism={0}" -f $Summary.LatestSummaryHelperRecoveryMechanism),
        '',
        'helper_ready_path_summary_lines:'
    ) + @($Summary.HelperReadyPathSummaryLines)

    $helperReceivePathSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("capture_to_envelope_send_avg_ms={0}" -f $Summary.LatestHelperReceivePathCaptureToEnvelopeSendAvgMs),
        ("capture_to_envelope_send_median_ms={0}" -f $Summary.LatestHelperReceivePathCaptureToEnvelopeSendMedianMs),
        ("capture_to_envelope_send_p95_ms={0}" -f $Summary.LatestHelperReceivePathCaptureToEnvelopeSendP95Ms),
        ("capture_to_envelope_send_max_ms={0}" -f $Summary.LatestHelperReceivePathCaptureToEnvelopeSendMaxMs),
        ("envelope_send_to_bridge_ingress_avg_ms={0}" -f $Summary.LatestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs),
        ("envelope_send_to_bridge_ingress_median_ms={0}" -f $Summary.LatestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs),
        ("envelope_send_to_bridge_ingress_p95_ms={0}" -f $Summary.LatestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms),
        ("envelope_send_to_bridge_ingress_max_ms={0}" -f $Summary.LatestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs),
        ("bridge_ingress_to_envelope_parsed_avg_ms={0}" -f $Summary.LatestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs),
        ("bridge_ingress_to_envelope_parsed_median_ms={0}" -f $Summary.LatestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs),
        ("bridge_ingress_to_envelope_parsed_p95_ms={0}" -f $Summary.LatestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms),
        ("bridge_ingress_to_envelope_parsed_max_ms={0}" -f $Summary.LatestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs),
        ("envelope_parsed_to_secure_decrypt_avg_ms={0}" -f $Summary.LatestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs),
        ("envelope_parsed_to_secure_decrypt_median_ms={0}" -f $Summary.LatestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs),
        ("envelope_parsed_to_secure_decrypt_p95_ms={0}" -f $Summary.LatestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms),
        ("envelope_parsed_to_secure_decrypt_max_ms={0}" -f $Summary.LatestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs),
        ("secure_decrypt_to_fragment_deserialize_avg_ms={0}" -f $Summary.LatestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs),
        ("secure_decrypt_to_fragment_deserialize_median_ms={0}" -f $Summary.LatestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs),
        ("secure_decrypt_to_fragment_deserialize_p95_ms={0}" -f $Summary.LatestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms),
        ("secure_decrypt_to_fragment_deserialize_max_ms={0}" -f $Summary.LatestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs),
        ("fragment_deserialize_to_first_fragment_observed_avg_ms={0}" -f $Summary.LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs),
        ("fragment_deserialize_to_first_fragment_observed_median_ms={0}" -f $Summary.LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs),
        ("fragment_deserialize_to_first_fragment_observed_p95_ms={0}" -f $Summary.LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms),
        ("fragment_deserialize_to_first_fragment_observed_max_ms={0}" -f $Summary.LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs),
        ("dominant_receive_path_stage={0}" -f $Summary.LatestHelperDominantReceivePathStage),
        ("helper_session_phase={0}" -f $Summary.LatestSummaryHelperSessionPhase),
        ("helper_recovery_mechanism={0}" -f $Summary.LatestSummaryHelperRecoveryMechanism),
        '',
        'helper_receive_path_summary_lines:'
    ) + @($Summary.HelperReceivePathSummaryLines)

    $helperBridgeIngressSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("envelope_send_to_bridge_message_observed_avg_ms={0}" -f $Summary.LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs),
        ("envelope_send_to_bridge_message_observed_median_ms={0}" -f $Summary.LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs),
        ("envelope_send_to_bridge_message_observed_p95_ms={0}" -f $Summary.LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms),
        ("envelope_send_to_bridge_message_observed_max_ms={0}" -f $Summary.LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs),
        ("bridge_message_observed_to_binary_frame_decoded_avg_ms={0}" -f $Summary.LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs),
        ("bridge_message_observed_to_binary_frame_decoded_median_ms={0}" -f $Summary.LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs),
        ("bridge_message_observed_to_binary_frame_decoded_p95_ms={0}" -f $Summary.LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms),
        ("bridge_message_observed_to_binary_frame_decoded_max_ms={0}" -f $Summary.LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs),
        ("binary_frame_decoded_to_bridge_ingress_avg_ms={0}" -f $Summary.LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs),
        ("binary_frame_decoded_to_bridge_ingress_median_ms={0}" -f $Summary.LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs),
        ("binary_frame_decoded_to_bridge_ingress_p95_ms={0}" -f $Summary.LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms),
        ("binary_frame_decoded_to_bridge_ingress_max_ms={0}" -f $Summary.LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs),
        ("dominant_bridge_ingress_stage={0}" -f $Summary.LatestHelperDominantBridgeIngressStage),
        ("helper_session_phase={0}" -f $Summary.LatestSummaryHelperSessionPhase),
        ("helper_recovery_mechanism={0}" -f $Summary.LatestSummaryHelperRecoveryMechanism),
        '',
        'helper_bridge_ingress_summary_lines:'
    ) + @($Summary.HelperBridgeIngressSummaryLines)

    $helperNknReceiveSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("envelope_send_to_sdk_handle_msg_entered_avg_ms={0}" -f $Summary.LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs),
        ("envelope_send_to_sdk_handle_msg_entered_median_ms={0}" -f $Summary.LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs),
        ("envelope_send_to_sdk_handle_msg_entered_p95_ms={0}" -f $Summary.LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms),
        ("envelope_send_to_sdk_handle_msg_entered_max_ms={0}" -f $Summary.LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs),
        ("sdk_handle_msg_entered_to_client_message_dispatch_avg_ms={0}" -f $Summary.LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs),
        ("sdk_handle_msg_entered_to_client_message_dispatch_median_ms={0}" -f $Summary.LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs),
        ("sdk_handle_msg_entered_to_client_message_dispatch_p95_ms={0}" -f $Summary.LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms),
        ("sdk_handle_msg_entered_to_client_message_dispatch_max_ms={0}" -f $Summary.LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs),
        ("client_message_dispatch_to_multiclient_message_dispatch_avg_ms={0}" -f $Summary.LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs),
        ("client_message_dispatch_to_multiclient_message_dispatch_median_ms={0}" -f $Summary.LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs),
        ("client_message_dispatch_to_multiclient_message_dispatch_p95_ms={0}" -f $Summary.LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms),
        ("client_message_dispatch_to_multiclient_message_dispatch_max_ms={0}" -f $Summary.LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs),
        ("multiclient_message_dispatch_to_bridge_message_observed_avg_ms={0}" -f $Summary.LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs),
        ("multiclient_message_dispatch_to_bridge_message_observed_median_ms={0}" -f $Summary.LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs),
        ("multiclient_message_dispatch_to_bridge_message_observed_p95_ms={0}" -f $Summary.LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms),
        ("multiclient_message_dispatch_to_bridge_message_observed_max_ms={0}" -f $Summary.LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs),
        ("dominant_nkn_receive_stage={0}" -f $Summary.LatestHelperDominantNknReceiveStage),
        ("helper_session_phase={0}" -f $Summary.LatestSummaryHelperSessionPhase),
        ("helper_recovery_mechanism={0}" -f $Summary.LatestSummaryHelperRecoveryMechanism),
        '',
        'helper_nkn_receive_summary_lines:'
    ) + @($Summary.HelperNknReceiveSummaryLines)

    $helperWsReceiveSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("envelope_send_to_ws_receiver_write_entered_avg_ms={0}" -f $Summary.LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs),
        ("envelope_send_to_ws_receiver_write_entered_median_ms={0}" -f $Summary.LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs),
        ("envelope_send_to_ws_receiver_write_entered_p95_ms={0}" -f $Summary.LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms),
        ("envelope_send_to_ws_receiver_write_entered_max_ms={0}" -f $Summary.LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs),
        ("ws_receiver_write_entered_to_ws_message_emitted_avg_ms={0}" -f $Summary.LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs),
        ("ws_receiver_write_entered_to_ws_message_emitted_median_ms={0}" -f $Summary.LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs),
        ("ws_receiver_write_entered_to_ws_message_emitted_p95_ms={0}" -f $Summary.LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms),
        ("ws_receiver_write_entered_to_ws_message_emitted_max_ms={0}" -f $Summary.LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs),
        ("ws_message_emitted_to_sdk_handle_msg_entered_avg_ms={0}" -f $Summary.LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs),
        ("ws_message_emitted_to_sdk_handle_msg_entered_median_ms={0}" -f $Summary.LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs),
        ("ws_message_emitted_to_sdk_handle_msg_entered_p95_ms={0}" -f $Summary.LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms),
        ("ws_message_emitted_to_sdk_handle_msg_entered_max_ms={0}" -f $Summary.LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs),
        ("dominant_ws_receive_stage={0}" -f $Summary.LatestHelperDominantWsReceiveStage),
        ("helper_session_phase={0}" -f $Summary.LatestSummaryHelperSessionPhase),
        ("helper_recovery_mechanism={0}" -f $Summary.LatestSummaryHelperRecoveryMechanism),
        '',
        'helper_ws_receive_summary_lines:'
    ) + @($Summary.HelperWsReceiveSummaryLines)

    $helperSocketReceiveSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("envelope_send_to_socket_data_event_emitted_avg_ms={0}" -f $Summary.LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs),
        ("envelope_send_to_socket_data_event_emitted_median_ms={0}" -f $Summary.LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs),
        ("envelope_send_to_socket_data_event_emitted_p95_ms={0}" -f $Summary.LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms),
        ("envelope_send_to_socket_data_event_emitted_max_ms={0}" -f $Summary.LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs),
        ("socket_data_event_emitted_to_ws_receiver_write_entered_avg_ms={0}" -f $Summary.LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs),
        ("socket_data_event_emitted_to_ws_receiver_write_entered_median_ms={0}" -f $Summary.LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs),
        ("socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms={0}" -f $Summary.LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms),
        ("socket_data_event_emitted_to_ws_receiver_write_entered_max_ms={0}" -f $Summary.LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs),
        ("dominant_socket_receive_stage={0}" -f $Summary.LatestHelperDominantSocketReceiveStage),
        ("helper_session_phase={0}" -f $Summary.LatestSummaryHelperSessionPhase),
        ("helper_recovery_mechanism={0}" -f $Summary.LatestSummaryHelperRecoveryMechanism),
        '',
        'helper_socket_receive_summary_lines:'
    ) + @($Summary.HelperSocketReceiveSummaryLines)

    $bridgeEventLoopSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("event_loop_p95_ms={0}" -f $Summary.LatestBridgeEventLoopP95Ms),
        ("event_loop_max_ms={0}" -f $Summary.LatestBridgeEventLoopMaxMs),
        ("event_loop_mean_ms={0}" -f $Summary.LatestBridgeEventLoopMeanMs),
        ("sample_window_ms={0}" -f $Summary.LatestBridgeEventLoopSampleWindowMs),
        '',
        'bridge_event_loop_summary_lines:'
    ) + @($Summary.BridgeEventLoopSummaryLines)

    $bridgeMediaSendSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("binary_send_frame_observed_to_queue_enqueue_avg_ms={0}" -f $Summary.LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs),
        ("binary_send_frame_observed_to_queue_enqueue_median_ms={0}" -f $Summary.LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs),
        ("binary_send_frame_observed_to_queue_enqueue_p95_ms={0}" -f $Summary.LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms),
        ("binary_send_frame_observed_to_queue_enqueue_max_ms={0}" -f $Summary.LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs),
        ("queue_enqueue_to_queue_dequeue_avg_ms={0}" -f $Summary.LatestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs),
        ("queue_enqueue_to_queue_dequeue_median_ms={0}" -f $Summary.LatestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs),
        ("queue_enqueue_to_queue_dequeue_p95_ms={0}" -f $Summary.LatestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms),
        ("queue_enqueue_to_queue_dequeue_max_ms={0}" -f $Summary.LatestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs),
        ("queue_dequeue_to_media_send_started_avg_ms={0}" -f $Summary.LatestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs),
        ("queue_dequeue_to_media_send_started_median_ms={0}" -f $Summary.LatestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs),
        ("queue_dequeue_to_media_send_started_p95_ms={0}" -f $Summary.LatestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms),
        ("queue_dequeue_to_media_send_started_max_ms={0}" -f $Summary.LatestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs),
        ("media_send_started_to_media_send_resolved_avg_ms={0}" -f $Summary.LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs),
        ("media_send_started_to_media_send_resolved_median_ms={0}" -f $Summary.LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs),
        ("media_send_started_to_media_send_resolved_p95_ms={0}" -f $Summary.LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms),
        ("media_send_started_to_media_send_resolved_max_ms={0}" -f $Summary.LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs),
        ("frames_sent={0}" -f $Summary.LatestBridgeMediaSendFramesSent),
        ("send_failures={0}" -f $Summary.LatestBridgeMediaSendFailures),
        ("queue_drops={0}" -f $Summary.LatestBridgeMediaSendQueueDrops),
        ("queue_mode={0}" -f $Summary.LatestBridgeMediaSendQueueMode),
        ("queue_depth={0}" -f $Summary.LatestBridgeMediaSendQueueDepth),
        ("oldest_queued_age_ms={0}" -f $Summary.LatestBridgeMediaSendOldestQueuedAgeMs),
        ("sample_window_ms={0}" -f $Summary.LatestBridgeMediaSendSampleWindowMs),
        '',
        'bridge_media_send_summary_lines:'
    ) + @($Summary.BridgeMediaSendSummaryLines)

    $bridgeTransportHealthSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("selected_rpc={0}" -f $Summary.LatestBridgeTransportHealthSelectedRpc),
        ("selected_rpc_key={0}" -f $Summary.LatestBridgeTransportHealthSelectedRpcKey),
        ("selected_rpc_stage={0}" -f $Summary.LatestBridgeTransportHealthSelectedRpcStage),
        ("connect_id={0}" -f $Summary.LatestBridgeTransportHealthConnectId),
        ("connect_key={0}" -f $Summary.LatestBridgeTransportHealthConnectKey),
        ("ready_emitted={0}" -f $Summary.LatestBridgeTransportHealthReadyEmitted),
        ("client_ready_age_ms={0}" -f $Summary.LatestBridgeTransportHealthClientReadyAgeMs),
        ("disconnect_count_since_last={0}" -f $Summary.LatestBridgeTransportHealthDisconnectCountSinceLast),
        ("connect_failed_count_since_last={0}" -f $Summary.LatestBridgeTransportHealthConnectFailedCountSinceLast),
        ("ws_error_count_since_last={0}" -f $Summary.LatestBridgeTransportHealthWsErrorCountSinceLast),
        ("rpc_fallback_attempt_count_since_last={0}" -f $Summary.LatestBridgeTransportHealthRpcFallbackAttemptCountSinceLast),
        ("control_ready={0}" -f $Summary.LatestBridgeTransportHealthControlReady),
        ("media_ready={0}" -f $Summary.LatestBridgeTransportHealthMediaReady),
        ("bulk_ready={0}" -f $Summary.LatestBridgeTransportHealthBulkReady),
        ("frames_sent_since_last={0}" -f $Summary.LatestBridgeTransportHealthFramesSentSinceLast),
        ("latest_disconnect_reason={0}" -f $Summary.LatestBridgeTransportHealthLatestDisconnectReason),
        ("sample_window_ms={0}" -f $Summary.LatestBridgeTransportHealthSampleWindowMs),
        ("unique_selected_rpc_count={0}" -f $Summary.LatestBridgeTransportHealthUniqueSelectedRpcCount),
        '',
        'bridge_transport_health_summary_lines:'
    ) + @($Summary.BridgeTransportHealthSummaryLines)

    $helperEpochLossSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("worst_epoch_by_visible_apply_ratio={0}" -f $Summary.WorstEpochByVisibleApplyRatio),
        ("worst_epoch_visible_apply_ratio={0}" -f $Summary.WorstEpochVisibleApplyRatio),
        ("applied_head_frame_id={0}" -f $Summary.LatestHelperAppliedHeadFrameId),
        ("ordered_emit_head_frame_id={0}" -f $Summary.LatestHelperOrderedEmitHeadFrameId),
        ("winning_recovery_frame_id={0}" -f $Summary.LatestHelperWinningRecoveryFrameId),
        ("recovery_owner_replaced_count={0}" -f $Summary.LatestHelperRecoveryOwnerReplacedCount),
        ("late_fragment_after_applied_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterAppliedHeadCount),
        ("late_fragment_after_ordered_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterOrderedHeadCount),
        '',
        'helper_epoch_loss_lines:'
    ) + @($Summary.HelperEpochLossLines)

    $promotionSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("promotion_blocker_rate_gate_ticks={0}" -f $Summary.LatestPromotionBlockerRateGateTicks),
        ("promotion_blocker_helper_pressure_ticks={0}" -f $Summary.LatestPromotionBlockerHelperPressureTicks),
        ("promotion_blocker_helper_warmup_ticks={0}" -f $Summary.LatestPromotionBlockerHelperWarmupTicks),
        ("promotion_blocker_helper_apply_count_ticks={0}" -f $Summary.LatestPromotionBlockerHelperApplyCountTicks),
        ("promotion_blocker_bridge_health_ticks={0}" -f $Summary.LatestPromotionBlockerBridgeHealthTicks),
        ("promotion_blocker_recovery_lock_ticks={0}" -f $Summary.LatestPromotionBlockerRecoveryLockTicks),
        ("promotion_blocker_queue_evict_ticks={0}" -f $Summary.LatestPromotionBlockerQueueEvictTicks),
        ("promotion_blocker_capture_age_ticks={0}" -f $Summary.LatestPromotionBlockerCaptureAgeTicks),
        ("promotion_blocker_encode_budget_ticks={0}" -f $Summary.LatestPromotionBlockerEncodeBudgetTicks),
        ("promotion_blocker_transition_grace_ticks={0}" -f $Summary.LatestPromotionBlockerTransitionGraceTicks),
        ("promotion_encode_soft_spike_count={0}" -f $Summary.LatestPromotionEncodeSoftSpikeCount),
        ("promotion_encode_soft_spike_reset_suppressed_count={0}" -f $Summary.LatestPromotionEncodeSoftSpikeResetSuppressedCount),
        ("blocked_by_missing_helper_proof={0}" -f $Summary.PromotionBlockedByMissingHelperProofCount),
        ("blocked_by_stale_helper_proof={0}" -f $Summary.PromotionBlockedByStaleHelperProofCount),
        ("blocked_by_encode_budget={0}" -f $Summary.PromotionBlockedByEncodeBudgetCount),
        ("blocked_by_encode_budget_alone={0}" -f $Summary.PromotionBlockedByEncodeBudgetAloneCount),
        ("helper_visible_head_runtime_sender_mismatch={0}" -f $Summary.HelperVisibleHeadRuntimeSenderMismatch),
        ("healthy_tick_reset_reason_counts={0}" -f $(if ([string]::IsNullOrWhiteSpace($Summary.LatestHealthyTickResetReasonCounts)) { '(none)' } else { $Summary.LatestHealthyTickResetReasonCounts })),
        ("recent_entries={0}" -f $(if ([string]::IsNullOrWhiteSpace($Summary.LatestReducedPromotionRecentEntries)) { '(none)' } else { $Summary.LatestReducedPromotionRecentEntries })),
        '',
        'promotion_summary_lines:'
    ) + @($Summary.ReducedPromotionSummaryLines)

    $senderCadenceSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("frames_queued={0}" -f $Summary.LatestFramesQueued),
        ("frames_deferred_to_send_slot={0}" -f $Summary.LatestFramesDeferredToSendSlot),
        ("frames_replaced_before_send_slot={0}" -f $Summary.LatestFramesReplacedBeforeSendSlot),
        ("frames_dropped_by_queue_evict={0}" -f $Summary.LatestFramesDroppedByQueueEvict),
        ("protected_recovery_frames_dispatched_count={0}" -f $Summary.LatestProtectedRecoveryFramesDispatchedCount),
        ("recovery_protected_frame_blocked_by_ordinary_count={0}" -f $Summary.LatestRecoveryProtectedFrameBlockedByOrdinaryCount),
        ("ordinary_sender_slot_replace_count={0}" -f $Summary.LatestFramesReplacedBeforeSendSlot),
        ("ordinary_sender_queue_evict_count={0}" -f $Summary.LatestFramesDroppedByQueueEvict),
        ("send_slot_empty_count={0}" -f $Summary.LatestSendSlotEmptyCount),
        ("slot_coalescing_active={0}" -f $Summary.LatestSlotCoalescingActive),
        ("raw_frames_deferred_to_encode_slot={0}" -f $Summary.LatestRawFramesDeferredToEncodeSlot),
        ("raw_frames_replaced_before_encode_slot={0}" -f $Summary.LatestRawFramesReplacedBeforeEncodeSlot),
        ("raw_encode_slot_empty_count={0}" -f $Summary.LatestRawEncodeSlotEmptyCount),
        ("raw_slot_coalescing_active={0}" -f $Summary.LatestRawSlotCoalescingActive),
        ("source_superseded_pending_frames={0}" -f $Summary.LatestSourceSupersededPendingFrames),
        ("helper_decoded_apply_queue_overflow_count={0}" -f $Summary.LatestHelperDecodedApplyQueueOverflowCount),
        ("ordinary_raw_loss_count={0}" -f $Summary.LatestOrdinaryRawLossCount),
        ("ordinary_sender_loss_count={0}" -f $Summary.LatestOrdinarySenderLossCount),
        ("ordinary_helper_loss_count={0}" -f $Summary.LatestOrdinaryHelperLossCount),
        ("dominant_ordinary_freshness_loss_boundary={0}" -f $Summary.DominantOrdinaryFreshnessLossBoundary),
        ("promotion_capture_to_send_budget_ms={0}" -f $Summary.LatestPromotionCaptureToSendBudgetMs),
        ("promotion_blocker_rate_gate_ticks={0}" -f $Summary.LatestPromotionBlockerRateGateTicks),
        ("promotion_blocker_helper_pressure_ticks={0}" -f $Summary.LatestPromotionBlockerHelperPressureTicks),
        ("recovery_burst_active={0}" -f $Summary.LatestRecoveryBurstActive),
        ("recovery_burst_phase={0}" -f $Summary.LatestRecoveryBurstPhase),
        ("recovery_burst_stream_epoch={0}" -f $Summary.LatestRecoveryBurstStreamEpoch),
        ("recovery_owner_frame_id={0}" -f $Summary.LatestRecoveryOwnerFrameId),
        ("recovery_protected_follower_count={0}" -f $Summary.LatestRecoveryProtectedFollowerCount),
        ("recovery_gap_count={0}" -f $Summary.LatestRecoveryGapCount),
        ("recovery_gap_to_keyframe_request_ms={0}" -f $Summary.LatestRecoveryGapToKeyframeRequestMs),
        ("recovery_keyframe_request_to_owner_emit_ms={0}" -f $Summary.LatestRecoveryKeyframeRequestToOwnerEmitMs),
        ("recovery_owner_ack_window_ms={0}" -f $Summary.LatestRecoveryOwnerAckWindowMs),
        ("recovery_owner_emit_to_ack_ms={0}" -f $Summary.LatestRecoveryOwnerEmitToAckMs),
        ("recovery_post_ack_hold_active={0}" -f $Summary.LatestRecoveryPostAckHoldActive),
        ("recovery_post_ack_hold_started_count={0}" -f $Summary.LatestRecoveryPostAckHoldStartedCount),
        ("recovery_post_ack_hold_expired_count={0}" -f $Summary.LatestRecoveryPostAckHoldExpiredCount),
        ("recovery_post_ack_hold_suppressed_reopen_count={0}" -f $Summary.LatestRecoveryPostAckHoldSuppressedReopenCount),
        ("recovery_owner_ack_frame_id={0}" -f $Summary.LatestRecoveryOwnerAckFrameId),
        ("recovery_ack_source={0}" -f $Summary.LatestRecoveryAckSource),
        ("recovery_owner_emit_to_first_visible_apply_ms={0}" -f $Summary.LatestRecoveryOwnerEmitToFirstVisibleApplyMs),
        ("recovery_burst_control_fallback_count={0}" -f $Summary.LatestRecoveryBurstControlFallbackCount),
        ("recovery_burst_timeout_count={0}" -f $Summary.LatestRecoveryBurstTimeoutCount),
        ("recovery_burst_completed_count={0}" -f $Summary.LatestRecoveryBurstCompletedCount),
        ("recovery_burst_restart_suppressed_count={0}" -f $Summary.LatestRecoveryBurstRestartSuppressedCount),
        ("recovery_burst_encoder_rerequest_count={0}" -f $Summary.LatestRecoveryBurstEncoderRerequestCount),
        ("recovery_owner_pending_forced_reset_count={0}" -f $Summary.LatestRecoveryOwnerPendingForcedResetCount),
        ("recovery_keyframe_emitted_after_forced_reset_count={0}" -f $Summary.LatestRecoveryKeyframeEmittedAfterForcedResetCount),
        ("recovery_burst_completed_by_helper_ack_count={0}" -f $Summary.LatestRecoveryBurstCompletedByHelperAckCount),
        ("recovery_burst_completed_by_applied_head_ack_count={0}" -f $Summary.LatestRecoveryBurstCompletedByAppliedHeadAckCount),
        ("recovery_burst_completed_by_last_visible_apply_ack_count={0}" -f $Summary.LatestRecoveryBurstCompletedByLastVisibleApplyAckCount),
        ("recovery_burst_completed_by_visible_recovery_floor_count={0}" -f $Summary.LatestRecoveryBurstCompletedByVisibleRecoveryFloorCount),
        ("recovery_burst_completed_by_visible_apply_fallback_count={0}" -f $Summary.LatestRecoveryBurstCompletedByVisibleApplyFallbackCount),
        ("recovery_burst_completed_by_timeout_count={0}" -f $Summary.LatestRecoveryBurstCompletedByTimeoutCount),
        ("recovery_burst_completed_by_protected_frames_count={0}" -f $Summary.LatestRecoveryBurstCompletedByProtectedFramesCount),
        ("recovery_burst_profile_transition_deferred_count={0}" -f $Summary.LatestRecoveryBurstProfileTransitionDeferredCount),
        ("recovery_burst_profile_transition_takeover_count={0}" -f $Summary.LatestRecoveryBurstProfileTransitionTakeoverCount),
        ("recovery_burst_stale_request_suppressed_count={0}" -f $Summary.LatestRecoveryBurstStaleRequestSuppressedCount),
        ("recovery_burst_request_suppressed_due_to_helper_ack_count={0}" -f $Summary.LatestRecoveryBurstRequestSuppressedDueToHelperAckCount),
        ("recovery_burst_started_while_helper_proof_healthy_count={0}" -f $Summary.LatestRecoveryBurstStartedWhileHelperProofHealthyCount),
        ("helper_progress_past_owner_without_burst_ack_count={0}" -f $Summary.LatestHelperProgressPastOwnerWithoutBurstAckCount),
        ("post_recovery_age_grace_active={0}" -f $Summary.LatestPostRecoveryAgeGraceActive),
        ("post_recovery_age_grace_suppressed_count={0}" -f $Summary.LatestPostRecoveryAgeGraceSuppressedCount),
        ("last_completed_recovery_epoch={0}" -f $Summary.LatestLastCompletedRecoveryEpoch),
        ("last_completed_recovery_owner_frame_id={0}" -f $Summary.LatestLastCompletedRecoveryOwnerFrameId),
        ("last_completed_recovery_ack_frame_id={0}" -f $Summary.LatestLastCompletedRecoveryAckFrameId),
        ("last_completed_recovery_ack_source={0}" -f $Summary.LatestLastCompletedRecoveryAckSource),
        ("last_completed_recovery_owner_emit_to_ack_ms={0}" -f $Summary.LatestLastCompletedRecoveryOwnerEmitToAckMs),
        ("last_completed_recovery_completion_kind={0}" -f $Summary.LatestLastCompletedRecoveryCompletionKind),
        ("recovery_completion_accounting_mismatch={0}" -f $Summary.LatestRecoveryCompletionAccountingMismatch),
        ("recovery_owner_pending_non_key_held_count={0}" -f $Summary.LatestRecoveryOwnerPendingNonKeyHeldCount),
        ("recovery_owner_pending_non_key_replaced_count={0}" -f $Summary.LatestRecoveryOwnerPendingNonKeyReplacedCount),
        ("recovery_owner_unacked_non_key_held_count={0}" -f $Summary.LatestRecoveryOwnerUnackedNonKeyHeldCount),
        ("recovery_owner_unacked_non_key_replaced_count={0}" -f $Summary.LatestRecoveryOwnerUnackedNonKeyReplacedCount),
        ("recovery_same_epoch_keyframe_suppressed_while_owner_unacked_count={0}" -f $Summary.LatestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount),
        ("recovery_owner_replaced_before_ack_count={0}" -f $Summary.LatestRecoveryOwnerReplacedBeforeAckCount),
        ("high_frame_age_suppressed_during_owner_ack_count={0}" -f $Summary.LatestHighFrameAgeSuppressedDuringOwnerAckCount),
        ("helper_progress_fact_bypass_send_count={0}" -f $Summary.LatestHelperPressureSendBypassedForVisibleProgressCount),
        ("helper_proof_keepalive_send_count={0}" -f $Summary.LatestHelperProofKeepaliveSendCount),
        ("helper_proof_keepalive_timer_driven_send_count={0}" -f $Summary.LatestHelperProofKeepaliveTimerDrivenSendCount),
        ("helper_proof_keepalive_last_head_frame_id={0}" -f $Summary.LatestHelperProofKeepaliveLastHeadFrameId),
        ("helper_proof_keepalive_last_send_age_ms={0}" -f $Summary.LatestHelperProofKeepaliveLastSendAgeMs),
        ("remote_helper_fact_healthy_active={0}" -f $Summary.LatestRemoteHelperFactHealthyActive),
        ("remote_helper_fact_healthy_source={0}" -f $Summary.LatestRemoteHelperFactHealthySource),
        ("remote_helper_fact_proof_frame_id={0}" -f $Summary.LatestRemoteHelperFactProofFrameId),
        ("remote_helper_fact_last_message_age_ms={0}" -f $Summary.LatestRemoteHelperFactLastMessageAgeMs),
        ("remote_helper_fact_healthy_clear_count={0}" -f $Summary.LatestRemoteHelperFactHealthyClearCount),
        ("remote_helper_fact_healthy_clear_reason={0}" -f $Summary.LatestRemoteHelperFactHealthyClearReason),
        ("helper_first_visible_apply_to_sender_fact_send_ms={0}" -f $Summary.LatestHelperFirstVisibleApplyToSenderFactSendMs),
        ("sender_received_helper_progress_during_continuity_loss_count={0}" -f $Summary.LatestSenderReceivedHelperProgressDuringContinuityLossCount),
        ("recovery_timeout_while_helper_head_advanced_count={0}" -f $Summary.LatestRecoveryTimeoutWhileHelperHeadAdvancedCount),
        ("helper_ack_after_fact_send_ms={0}" -f $Summary.LatestHelperAckAfterFactSendMs),
        ("post_ack_mode_grace_suppressed_high_frame_age_count={0}" -f $Summary.LatestPostAckModeGraceSuppressedHighFrameAgeCount),
        ("bootstrap_grace_suppressed_catch_up_count={0}" -f $Summary.LatestBootstrapGraceSuppressedCatchUpCount),
        ("catch_up_recovery_suppressed_due_to_remote_high_frame_age_count={0}" -f $Summary.LatestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount),
        ("catch_up_exit_while_remote_high_frame_age_pressure_count={0}" -f $Summary.LatestCatchUpExitWhileRemoteHighFrameAgePressureCount),
        ("last_acknowledged_recovery_owner_frame_id={0}" -f $Summary.LatestLastAcknowledgedRecoveryOwnerFrameId),
        ("last_acknowledged_helper_head_frame_id={0}" -f $Summary.LatestLastAcknowledgedHelperHeadFrameId),
        ("remote_helper_visible_head_frame_id={0}" -f $Summary.LatestRemoteHelperVisibleHeadFrameId),
        ("remote_helper_visible_recovery_floor_frame_id={0}" -f $Summary.LatestRemoteHelperVisibleRecoveryFloorFrameId),
        ("remote_helper_current_epoch_recovery_keyframe_apply_count={0}" -f $Summary.LatestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount),
        ("last_acknowledged_visible_helper_head_frame_id={0}" -f $Summary.LatestLastAcknowledgedVisibleHelperHeadFrameId),
        ("last_acknowledged_helper_proof_age_ms={0}" -f $Summary.LatestLastAcknowledgedHelperProofAgeMs),
        ("persisted_release_floor_epoch={0}" -f $Summary.LatestPersistedReleaseFloorEpoch),
        ("satisfied_recovery_floor_frame_id={0}" -f $Summary.LatestSatisfiedRecoveryFloorFrameId),
        ("satisfied_recovery_floor_source={0}" -f $Summary.LatestSatisfiedRecoveryFloorSource),
        ("satisfied_recovery_floor_visible_proof_count={0}" -f $Summary.LatestSatisfiedRecoveryFloorVisibleProofCount),
        ("continuity_signal_ignored_due_to_satisfied_floor_count={0}" -f $Summary.LatestContinuitySignalIgnoredDueToSatisfiedFloorCount),
        ("continuity_signal_ignored_due_to_visible_satisfied_floor_count={0}" -f $Summary.LatestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount),
        ("recovery_lock_cleared_by_acknowledged_proof_count={0}" -f $Summary.LatestRecoveryLockClearedByAcknowledgedProofCount),
        ("recovery_lock_cleared_by_visible_proof_count={0}" -f $Summary.LatestRecoveryLockClearedByVisibleProofCount),
        ("recovery_lock_last_clear_reason={0}" -f $Summary.LatestRecoveryLockLastClearReason),
        ("recovery_control_bootstrap_retry_skipped_due_to_burst_resolved_count={0}" -f $Summary.RecoveryControlBootstrapRetrySkippedDueToBurstResolvedCount),
        ("recovery_control_bootstrap_retry_queued_after_burst_resolution_count={0}" -f $Summary.RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount),
        ("recovery_burst_completed_without_helper_advance={0}" -f $Summary.RecoveryBurstCompletedWithoutHelperAdvance),
        ("recovery_ack_missed_despite_helper_progress={0}" -f $Summary.RecoveryAckMissedDespiteHelperProgress)
    )

    $transportModeSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("effective_media_plane_active={0}" -f $Summary.EffectiveMediaPlaneActive),
        ("recovery_used_control_fallback={0}" -f $Summary.RecoveryUsedControlFallback),
        ("steady_state_used_control_fallback={0}" -f $Summary.SteadyStateUsedControlFallback),
        ("bridge_media_messages_received={0}" -f $Summary.LatestBridgeMediaMessagesReceived),
        ("media_plane_frames_sent={0}" -f $Summary.LatestMediaPlaneFramesSent),
        ("media_plane_attached={0}" -f $Summary.LatestMediaPlaneAttached),
        ("recovery_control_fallback_queued_count={0}" -f $Summary.RecoveryControlFallbackQueuedCount),
        ("steady_state_control_fallback_queued_count={0}" -f $Summary.SteadyStateControlFallbackQueuedCount),
        ("avg_fragments_per_frame={0}" -f $Summary.LatestAvgFragmentsPerFrame),
        ("avg_transport_payloads_per_frame={0}" -f $Summary.LatestAvgPayloadsPerFrame),
        ("batched_payloads_sent={0}" -f $Summary.LatestBatchPayloadCount),
        ("legacy_fragment_payloads_sent={0}" -f $Summary.LatestLegacyPayloadCount),
        ("ordinary_non_key_batched_payloads_sent={0}" -f $Summary.LatestOrdinaryNonKeyBatchedPayloadCount),
        ("ordinary_non_key_legacy_payloads_sent={0}" -f $Summary.LatestOrdinaryNonKeyLegacyPayloadCount),
        ("keyframe_recovery_batched_payloads_sent={0}" -f $Summary.LatestKeyframeRecoveryBatchedPayloadCount)
    )

    $recoveryBurstSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("recovery_burst_active={0}" -f $Summary.LatestRecoveryBurstActive),
        ("recovery_burst_phase={0}" -f $Summary.LatestRecoveryBurstPhase),
        ("recovery_burst_stream_epoch={0}" -f $Summary.LatestRecoveryBurstStreamEpoch),
        ("recovery_owner_frame_id={0}" -f $Summary.LatestRecoveryOwnerFrameId),
        ("recovery_protected_follower_count={0}" -f $Summary.LatestRecoveryProtectedFollowerCount),
        ("recovery_gap_count={0}" -f $Summary.LatestRecoveryGapCount),
        ("recovery_gap_to_keyframe_request_ms={0}" -f $Summary.LatestRecoveryGapToKeyframeRequestMs),
        ("recovery_keyframe_request_to_owner_emit_ms={0}" -f $Summary.LatestRecoveryKeyframeRequestToOwnerEmitMs),
        ("recovery_owner_ack_window_ms={0}" -f $Summary.LatestRecoveryOwnerAckWindowMs),
        ("recovery_owner_emit_to_ack_ms={0}" -f $Summary.LatestRecoveryOwnerEmitToAckMs),
        ("recovery_post_ack_hold_active={0}" -f $Summary.LatestRecoveryPostAckHoldActive),
        ("recovery_post_ack_hold_started_count={0}" -f $Summary.LatestRecoveryPostAckHoldStartedCount),
        ("recovery_post_ack_hold_expired_count={0}" -f $Summary.LatestRecoveryPostAckHoldExpiredCount),
        ("recovery_post_ack_hold_suppressed_reopen_count={0}" -f $Summary.LatestRecoveryPostAckHoldSuppressedReopenCount),
        ("recovery_owner_ack_frame_id={0}" -f $Summary.LatestRecoveryOwnerAckFrameId),
        ("recovery_ack_source={0}" -f $Summary.LatestRecoveryAckSource),
        ("recovery_owner_emit_to_first_visible_apply_ms={0}" -f $Summary.LatestRecoveryOwnerEmitToFirstVisibleApplyMs),
        ("recovery_burst_control_fallback_count={0}" -f $Summary.LatestRecoveryBurstControlFallbackCount),
        ("recovery_burst_timeout_count={0}" -f $Summary.LatestRecoveryBurstTimeoutCount),
        ("recovery_burst_completed_count={0}" -f $Summary.LatestRecoveryBurstCompletedCount),
        ("recovery_burst_restart_suppressed_count={0}" -f $Summary.LatestRecoveryBurstRestartSuppressedCount),
        ("recovery_burst_encoder_rerequest_count={0}" -f $Summary.LatestRecoveryBurstEncoderRerequestCount),
        ("recovery_owner_pending_forced_reset_count={0}" -f $Summary.LatestRecoveryOwnerPendingForcedResetCount),
        ("recovery_keyframe_emitted_after_forced_reset_count={0}" -f $Summary.LatestRecoveryKeyframeEmittedAfterForcedResetCount),
        ("recovery_burst_completed_by_helper_ack_count={0}" -f $Summary.LatestRecoveryBurstCompletedByHelperAckCount),
        ("recovery_burst_completed_by_applied_head_ack_count={0}" -f $Summary.LatestRecoveryBurstCompletedByAppliedHeadAckCount),
        ("recovery_burst_completed_by_last_visible_apply_ack_count={0}" -f $Summary.LatestRecoveryBurstCompletedByLastVisibleApplyAckCount),
        ("recovery_burst_completed_by_visible_recovery_floor_count={0}" -f $Summary.LatestRecoveryBurstCompletedByVisibleRecoveryFloorCount),
        ("recovery_burst_completed_by_visible_apply_fallback_count={0}" -f $Summary.LatestRecoveryBurstCompletedByVisibleApplyFallbackCount),
        ("recovery_burst_completed_by_timeout_count={0}" -f $Summary.LatestRecoveryBurstCompletedByTimeoutCount),
        ("recovery_burst_completed_by_protected_frames_count={0}" -f $Summary.LatestRecoveryBurstCompletedByProtectedFramesCount),
        ("recovery_burst_profile_transition_deferred_count={0}" -f $Summary.LatestRecoveryBurstProfileTransitionDeferredCount),
        ("recovery_burst_profile_transition_takeover_count={0}" -f $Summary.LatestRecoveryBurstProfileTransitionTakeoverCount),
        ("recovery_burst_stale_request_suppressed_count={0}" -f $Summary.LatestRecoveryBurstStaleRequestSuppressedCount),
        ("recovery_burst_request_suppressed_due_to_helper_ack_count={0}" -f $Summary.LatestRecoveryBurstRequestSuppressedDueToHelperAckCount),
        ("recovery_burst_started_while_helper_proof_healthy_count={0}" -f $Summary.LatestRecoveryBurstStartedWhileHelperProofHealthyCount),
        ("helper_progress_past_owner_without_burst_ack_count={0}" -f $Summary.LatestHelperProgressPastOwnerWithoutBurstAckCount),
        ("post_recovery_age_grace_active={0}" -f $Summary.LatestPostRecoveryAgeGraceActive),
        ("post_recovery_age_grace_suppressed_count={0}" -f $Summary.LatestPostRecoveryAgeGraceSuppressedCount),
        ("last_completed_recovery_epoch={0}" -f $Summary.LatestLastCompletedRecoveryEpoch),
        ("last_completed_recovery_owner_frame_id={0}" -f $Summary.LatestLastCompletedRecoveryOwnerFrameId),
        ("last_completed_recovery_ack_frame_id={0}" -f $Summary.LatestLastCompletedRecoveryAckFrameId),
        ("last_completed_recovery_ack_source={0}" -f $Summary.LatestLastCompletedRecoveryAckSource),
        ("last_completed_recovery_owner_emit_to_ack_ms={0}" -f $Summary.LatestLastCompletedRecoveryOwnerEmitToAckMs),
        ("last_completed_recovery_completion_kind={0}" -f $Summary.LatestLastCompletedRecoveryCompletionKind),
        ("recovery_completion_accounting_mismatch={0}" -f $Summary.LatestRecoveryCompletionAccountingMismatch),
        ("recovery_owner_pending_non_key_held_count={0}" -f $Summary.LatestRecoveryOwnerPendingNonKeyHeldCount),
        ("recovery_owner_pending_non_key_replaced_count={0}" -f $Summary.LatestRecoveryOwnerPendingNonKeyReplacedCount),
        ("recovery_owner_unacked_non_key_held_count={0}" -f $Summary.LatestRecoveryOwnerUnackedNonKeyHeldCount),
        ("recovery_owner_unacked_non_key_replaced_count={0}" -f $Summary.LatestRecoveryOwnerUnackedNonKeyReplacedCount),
        ("recovery_same_epoch_keyframe_suppressed_while_owner_unacked_count={0}" -f $Summary.LatestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount),
        ("recovery_owner_replaced_before_ack_count={0}" -f $Summary.LatestRecoveryOwnerReplacedBeforeAckCount),
        ("high_frame_age_suppressed_during_owner_ack_count={0}" -f $Summary.LatestHighFrameAgeSuppressedDuringOwnerAckCount),
        ("helper_progress_fact_bypass_send_count={0}" -f $Summary.LatestHelperPressureSendBypassedForVisibleProgressCount),
        ("helper_proof_keepalive_send_count={0}" -f $Summary.LatestHelperProofKeepaliveSendCount),
        ("helper_proof_keepalive_timer_driven_send_count={0}" -f $Summary.LatestHelperProofKeepaliveTimerDrivenSendCount),
        ("helper_proof_keepalive_last_head_frame_id={0}" -f $Summary.LatestHelperProofKeepaliveLastHeadFrameId),
        ("helper_proof_keepalive_last_send_age_ms={0}" -f $Summary.LatestHelperProofKeepaliveLastSendAgeMs),
        ("remote_helper_fact_healthy_active={0}" -f $Summary.LatestRemoteHelperFactHealthyActive),
        ("remote_helper_fact_healthy_source={0}" -f $Summary.LatestRemoteHelperFactHealthySource),
        ("remote_helper_fact_proof_frame_id={0}" -f $Summary.LatestRemoteHelperFactProofFrameId),
        ("remote_helper_fact_last_message_age_ms={0}" -f $Summary.LatestRemoteHelperFactLastMessageAgeMs),
        ("remote_helper_fact_healthy_clear_count={0}" -f $Summary.LatestRemoteHelperFactHealthyClearCount),
        ("remote_helper_fact_healthy_clear_reason={0}" -f $Summary.LatestRemoteHelperFactHealthyClearReason),
        ("helper_first_visible_apply_to_sender_fact_send_ms={0}" -f $Summary.LatestHelperFirstVisibleApplyToSenderFactSendMs),
        ("sender_received_helper_progress_during_continuity_loss_count={0}" -f $Summary.LatestSenderReceivedHelperProgressDuringContinuityLossCount),
        ("helper_ack_after_fact_send_ms={0}" -f $Summary.LatestHelperAckAfterFactSendMs),
        ("last_acknowledged_recovery_owner_frame_id={0}" -f $Summary.LatestLastAcknowledgedRecoveryOwnerFrameId),
        ("last_acknowledged_helper_head_frame_id={0}" -f $Summary.LatestLastAcknowledgedHelperHeadFrameId),
        ("remote_helper_visible_head_frame_id={0}" -f $Summary.LatestRemoteHelperVisibleHeadFrameId),
        ("remote_helper_visible_recovery_floor_frame_id={0}" -f $Summary.LatestRemoteHelperVisibleRecoveryFloorFrameId),
        ("remote_helper_current_epoch_recovery_keyframe_apply_count={0}" -f $Summary.LatestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount),
        ("last_acknowledged_visible_helper_head_frame_id={0}" -f $Summary.LatestLastAcknowledgedVisibleHelperHeadFrameId),
        ("last_acknowledged_helper_proof_age_ms={0}" -f $Summary.LatestLastAcknowledgedHelperProofAgeMs),
        ("persisted_release_floor_epoch={0}" -f $Summary.LatestPersistedReleaseFloorEpoch),
        ("satisfied_recovery_floor_frame_id={0}" -f $Summary.LatestSatisfiedRecoveryFloorFrameId),
        ("satisfied_recovery_floor_source={0}" -f $Summary.LatestSatisfiedRecoveryFloorSource),
        ("satisfied_recovery_floor_visible_proof_count={0}" -f $Summary.LatestSatisfiedRecoveryFloorVisibleProofCount),
        ("continuity_signal_ignored_due_to_satisfied_floor_count={0}" -f $Summary.LatestContinuitySignalIgnoredDueToSatisfiedFloorCount),
        ("continuity_signal_ignored_due_to_visible_satisfied_floor_count={0}" -f $Summary.LatestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount),
        ("recovery_lock_cleared_by_acknowledged_proof_count={0}" -f $Summary.LatestRecoveryLockClearedByAcknowledgedProofCount),
        ("recovery_lock_cleared_by_visible_proof_count={0}" -f $Summary.LatestRecoveryLockClearedByVisibleProofCount),
        ("recovery_lock_last_clear_reason={0}" -f $Summary.LatestRecoveryLockLastClearReason),
        ("recovery_control_bootstrap_retry_skipped_due_to_burst_resolved_count={0}" -f $Summary.RecoveryControlBootstrapRetrySkippedDueToBurstResolvedCount),
        ("recovery_control_bootstrap_retry_queued_after_burst_resolution_count={0}" -f $Summary.RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount),
        ("recovery_burst_completed_without_helper_advance={0}" -f $Summary.RecoveryBurstCompletedWithoutHelperAdvance),
        ("recovery_ack_missed_despite_helper_progress={0}" -f $Summary.RecoveryAckMissedDespiteHelperProgress)
    )

    $helperEpochTimelineSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("worst_epoch_by_recovery_lock_time={0}" -f $Summary.WorstEpochByRecoveryLockTime),
        ("worst_epoch_recovery_lock_time_ms={0}" -f $Summary.WorstEpochRecoveryLockTimeMs),
        '',
        'helper_epoch_timeline_lines:'
    ) + @($Summary.HelperEpochTimelineLines)

    $helperReassemblerRootCauseSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("dominant_reassembler_root_cause={0}" -f $Summary.DominantReassemblerRootCause),
        ("late_fragment_after_applied_head_count={0}" -f $Summary.AggregateLateFragmentAfterAppliedHeadCount),
        ("late_fragment_after_ordered_head_count={0}" -f $Summary.AggregateLateFragmentAfterOrderedHeadCount),
        ("late_fragment_after_stable_visible_head_count={0}" -f $Summary.LatestHelperLateFragmentAfterStableVisibleHeadCount),
        ("winning_recovery_frame_id={0}" -f $Summary.LatestHelperWinningRecoveryFrameId),
        ("ordered_emit_head_frame_id={0}" -f $Summary.LatestHelperOrderedEmitHeadFrameId),
        ("recovery_owner_replaced_count={0}" -f $Summary.AggregateRecoveryOwnerReplacedCount),
        ("older_epoch_cleanup_after_epoch_advance_count={0}" -f $Summary.AggregateOlderEpochCleanupAfterEpochAdvanceCount),
        ("actionable_late_fragment_count={0}" -f $Summary.AggregateActionableLateFragmentCount),
        ("worst_epoch_by_visible_apply_ratio={0}" -f $Summary.WorstEpochByVisibleApplyRatio),
        ("worst_epoch_visible_apply_ratio={0}" -f $Summary.WorstEpochVisibleApplyRatio),
        '',
        'helper_reassembler_root_cause_summary_lines:'
    ) + @($Summary.HelperReassemblerRootCauseSummaryLines)

    $helperPressureSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("dominant_helper_pressure_blocker={0}" -f $Summary.DominantHelperPressureBlocker),
        ("baseline_established={0}" -f $Summary.LatestHelperBaselineEstablished),
        ("baseline_capture_to_render_ms={0}" -f $Summary.LatestHelperBaselineCaptureToRenderMs),
        ("age_excess_ms={0}" -f $Summary.LatestHelperAgeExcessMs),
        ("applied_head_frame_id={0}" -f $Summary.LatestHelperAppliedHeadFrameId),
        ("high_frame_age_suppressed_due_to_visible_progress_count={0}" -f $Summary.AggregateHighFrameAgeSuppressedDueToVisibleProgressCount),
        ("progress_stall_ms={0}" -f $Summary.LatestHelperProgressStallMs),
        ("baseline_reseed_in_progress={0}" -f $Summary.LatestHelperBaselineReseedInProgress),
        ("age_pressure_consecutive_count={0}" -f $Summary.LatestHelperAgePressureConsecutiveCount),
        ("cadence_pressure_consecutive_count={0}" -f $Summary.LatestHelperCadencePressureConsecutiveCount),
        ("post_recovery_age_grace_active={0}" -f $Summary.LatestPostRecoveryAgeGraceActive),
        ("post_recovery_age_grace_suppressed_count={0}" -f $Summary.LatestPostRecoveryAgeGraceSuppressedCount),
        ("catch_up_suppressed_due_to_progress_count={0}" -f $Summary.LatestHelperCatchUpSuppressedDueToProgressCount),
        ("baseline_frozen_due_to_stall_count={0}" -f $Summary.LatestHelperBaselineFrozenDueToStallCount),
        ("baseline_reseed_after_recovery_count={0}" -f $Summary.LatestHelperBaselineReseedAfterRecoveryCount),
        ("cadence_stall_window_count={0}" -f $Summary.LatestHelperCadenceStallWindowCount),
        ("cadence_stall_trigger_count={0}" -f $Summary.LatestHelperCadenceStallTriggerCount),
        ("steady_visible_progress_active={0}" -f $Summary.LatestHelperSteadyVisibleProgressActive),
        ("steady_visible_progress_activation_frame_id={0}" -f $Summary.LatestHelperSteadyVisibleProgressActivationFrameId),
        ("stable_visible_head_frame_id={0}" -f $Summary.LatestHelperStableVisibleHeadFrameId),
        ("last_sent_stable_visible_head_frame_id={0}" -f $Summary.LatestHelperLastSentStableVisibleHeadFrameId),
        ("helper_proof_keepalive_send_count={0}" -f $Summary.LatestHelperProofKeepaliveSendCount),
        ("helper_proof_keepalive_timer_driven_send_count={0}" -f $Summary.LatestHelperProofKeepaliveTimerDrivenSendCount),
        ("helper_proof_keepalive_last_head_frame_id={0}" -f $Summary.LatestHelperProofKeepaliveLastHeadFrameId),
        ("helper_proof_keepalive_last_send_age_ms={0}" -f $Summary.LatestHelperProofKeepaliveLastSendAgeMs),
        ("steady_visible_progress_cleared_count={0}" -f $Summary.LatestHelperSteadyVisibleProgressClearedCount),
        ("steady_visible_progress_cleared_reason={0}" -f $Summary.LatestHelperSteadyVisibleProgressClearedReason),
        ("post_recovery_high_frame_age_suppressed_ticks={0}" -f $Summary.AggregatePostRecoveryHighFrameAgeSuppressedTicks),
        ("high_frame_age_suppressed_due_to_head_advance_count={0}" -f $Summary.LatestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount),
        ("actionable_high_frame_age_count={0}" -f $Summary.LatestHelperActionableHighFrameAgeCount),
        ("bridge_health_advisory_count={0}" -f $Summary.LatestHelperBridgeHealthAdvisoryCount),
        ("bridge_health_actionable_count={0}" -f $Summary.LatestHelperBridgeHealthActionableCount),
        ("bridge_health_quarantine_suppressed_count={0}" -f $Summary.LatestHelperBridgeHealthQuarantineSuppressedCount),
        ("bridge_health_became_actionable_without_queue_or_drop_count={0}" -f $Summary.LatestHelperBridgeHealthActionableWithoutQueueOrDropCount),
        ("worst_epoch_by_recovery_lock_time={0}" -f $Summary.WorstEpochByRecoveryLockTime),
        ("worst_epoch_recovery_lock_time_ms={0}" -f $Summary.WorstEpochRecoveryLockTimeMs),
        '',
        'helper_pressure_summary_lines:'
    ) + @($Summary.HelperPressureSummaryLines)

    $helperRecoveryInvestigationSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("session_id={0}" -f $Summary.LatestHelperSessionId),
        ("dominant_reassembler_root_cause={0}" -f $Summary.DominantReassemblerRootCause),
        ("recovery_owner_replaced_count={0}" -f $Summary.LatestHelperRecoveryOwnerReplacedCount),
        ("older_epoch_cleanup_after_epoch_advance_count={0}" -f $Summary.LatestHelperOlderEpochCleanupAfterEpochAdvanceCount),
        ("actionable_late_fragment_count={0}" -f $Summary.LatestHelperActionableLateFragmentCount),
        ("baseline_established={0}" -f $Summary.LatestHelperBaselineEstablished),
        ("visible_head_frame_id={0}" -f $Summary.LatestHelperVisibleHeadFrameId),
        ("stable_visible_head_frame_id={0}" -f $Summary.LatestHelperStableVisibleHeadFrameId),
        ("visible_recovery_floor_frame_id={0}" -f $Summary.LatestHelperVisibleRecoveryFloorFrameId),
        ("applied_head_frame_id={0}" -f $Summary.LatestHelperAppliedHeadFrameId),
        ("ordered_emit_head_frame_id={0}" -f $Summary.LatestHelperOrderedEmitHeadFrameId),
        ("winning_recovery_frame_id={0}" -f $Summary.LatestHelperWinningRecoveryFrameId),
        '',
        'helper_recovery_epoch_investigation_lines:'
    ) + @($Summary.HelperRecoveryEpochInvestigationLines) + @(
        '',
        'helper_reassembler_recovery_owner_transition_lines:'
    ) + @($Summary.HelperReassemblerRecoveryOwnerTransitionLines) + @(
        '',
        'helper_reassembler_actionable_late_fragment_lines:'
    ) + @($Summary.HelperReassemblerActionableLateFragmentLines) + @(
        '',
        'helper_reassembler_older_epoch_cleanup_lines:'
    ) + @($Summary.HelperReassemblerOlderEpochCleanupLines)

    $healthSnapshotSummary = @(
        ("log_path={0}" -f $Summary.LogPath),
        ("sender_operating_state={0}" -f $Summary.LatestHealthSenderOperatingState),
        ("sender_guard_state={0}" -f $Summary.LatestHealthSenderGuardState),
        ("helper_session_phase={0}" -f $Summary.LatestHealthHelperSessionPhase),
        ("helper_recovery_mechanism={0}" -f $Summary.LatestHealthHelperRecoveryMechanism),
        ("dominant_loss_class={0}" -f $Summary.LatestHealthDominantLossClass),
        ("dominant_pressure_blocker={0}" -f $Summary.LatestHealthDominantPressureBlocker),
        ("dominant_trouble_domain={0}" -f $Summary.LatestHealthDominantTroubleDomain),
        ("recovery_active={0}" -f $Summary.LatestHealthRecoveryActive),
        ("baseline_established={0}" -f $Summary.LatestHealthBaselineEstablished),
        ("steady_visible_progress_active={0}" -f $Summary.LatestHealthSteadyVisibleProgressActive),
        '',
        'health_snapshot_lines:'
    ) + @($Summary.HealthSnapshotLines)

    Set-Content -Path (Join-Path $artifactDir 'helper-decode-worker-summary.txt') -Value $helperDecodeWorkerSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-quality-summary.txt') -Value $helperQualitySummary
    Set-Content -Path (Join-Path $artifactDir 'helper-upstream-latency-summary.txt') -Value $helperUpstreamLatencySummary
    Set-Content -Path (Join-Path $artifactDir 'helper-ready-path-summary.txt') -Value $helperReadyPathSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-receive-path-summary.txt') -Value $helperReceivePathSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-bridge-ingress-summary.txt') -Value $helperBridgeIngressSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-nkn-receive-summary.txt') -Value $helperNknReceiveSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-ws-receive-summary.txt') -Value $helperWsReceiveSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-socket-receive-summary.txt') -Value $helperSocketReceiveSummary
    Set-Content -Path (Join-Path $artifactDir 'bridge-event-loop-summary.txt') -Value $bridgeEventLoopSummary
    Set-Content -Path (Join-Path $artifactDir 'bridge-media-send-summary.txt') -Value $bridgeMediaSendSummary
    Set-Content -Path (Join-Path $artifactDir 'bridge-transport-health-summary.txt') -Value $bridgeTransportHealthSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-frame-loss-epoch.txt') -Value $helperEpochLossSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-epoch-timeline.txt') -Value $helperEpochTimelineSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-reassembler-root-cause-summary.txt') -Value $helperReassemblerRootCauseSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-pressure-summary.txt') -Value $helperPressureSummary
    Set-Content -Path (Join-Path $artifactDir 'helper-recovery-investigation-summary.txt') -Value $helperRecoveryInvestigationSummary
    Set-Content -Path (Join-Path $artifactDir 'health-snapshot-summary.txt') -Value $healthSnapshotSummary
    Set-Content -Path (Join-Path $artifactDir 'reduced-promotion-summary.txt') -Value $promotionSummary
    Set-Content -Path (Join-Path $artifactDir 'sender-cadence-summary.txt') -Value $senderCadenceSummary
    Set-Content -Path (Join-Path $artifactDir 'recovery-burst-summary.txt') -Value $recoveryBurstSummary
    Set-Content -Path (Join-Path $artifactDir 'transport-mode-summary.txt') -Value $transportModeSummary

    return $artifactDir
}

function Write-StabilizationArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)]$CurrentMetrics,
        $StrongBaselineMetrics,
        $SafeBaselineMetrics
    )

    $invariantFailures = New-Object System.Collections.Generic.List[string]
    $regressionFailures = New-Object System.Collections.Generic.List[string]
    $latencyGateMetricName = [string]$CurrentMetrics.latency_proxy_name
    $latencyGateCurrentValue = $CurrentMetrics.latency_proxy_ms
    $latencyGateBaselineMetricName = if ($null -ne $SafeBaselineMetrics) { [string]$SafeBaselineMetrics.latency_proxy_name } else { '(none)' }
    $latencyGateBaselineValue = if ($null -ne $SafeBaselineMetrics) { $SafeBaselineMetrics.latency_proxy_ms } else { $null }

    if ($Summary.LatestRecoveryOwnerReplacedBeforeAckCount -gt 0) {
        $invariantFailures.Add(("recovery_owner_replaced_before_ack_count={0}" -f $Summary.LatestRecoveryOwnerReplacedBeforeAckCount))
    }

    if ($Summary.LatestHelperRecoveryRunwayOverflowRejectCount -gt 1) {
        $invariantFailures.Add(("recovery_runway_overflow_reject_count={0}" -f $Summary.LatestHelperRecoveryRunwayOverflowRejectCount))
    }

    if ($Summary.LatestHelperStartupCorridorReleaseCount -gt 0) {
        $invariantFailures.Add(("startup_corridor_release_count={0}" -f $Summary.LatestHelperStartupCorridorReleaseCount))
    }

    if ($Summary.LatestHelperRecoveryFollowerWindowBufferedCount -gt 0) {
        $invariantFailures.Add(("recovery_follower_window_buffered_count={0}" -f $Summary.LatestHelperRecoveryFollowerWindowBufferedCount))
    }

    if ($Summary.LatestRecoveryCompletionAccountingMismatch -gt 0) {
        $invariantFailures.Add(("recovery_completion_accounting_mismatch={0}" -f $Summary.LatestRecoveryCompletionAccountingMismatch))
    }

    if ($Summary.RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount -gt 0) {
        $invariantFailures.Add(("recovery_control_bootstrap_retry_queued_after_burst_resolution_count={0}" -f $Summary.RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount))
    }

    if ($Summary.LatestHelperBridgeHealthActionableWithoutQueueOrDropCount -gt 0) {
        $invariantFailures.Add(("bridge_health_became_actionable_without_queue_or_drop_count={0}" -f $Summary.LatestHelperBridgeHealthActionableWithoutQueueOrDropCount))
    }

    if (@(
            'late_fragment_after_applied_head',
            'late_fragment_after_ordered_head',
            'late_fragment_after_stable_visible_head',
            'superseded_recovery_tail_cleanup'
        ) -contains $Summary.DominantReassemblerRootCause) {
        $invariantFailures.Add(("dominant_reassembler_root_cause_benign={0}" -f $Summary.DominantReassemblerRootCause))
    }

    if ($null -ne $SafeBaselineMetrics) {
        $safeLatencyMetricKey = switch ($SafeBaselineMetrics.latency_proxy_name) {
            'helper_apply_ms_avg' { 'helper_apply_ms_avg' }
            'avg_capture_to_render_ms' { 'baseline_capture_to_render_ms' }
            'baseline_capture_to_render_ms' { 'baseline_capture_to_render_ms' }
            default { 'helper_apply_ms_avg' }
        }
        $currentComparableLatency = $CurrentMetrics[$safeLatencyMetricKey]
        $latencyGateMetricName = $safeLatencyMetricKey
        $latencyGateCurrentValue = $currentComparableLatency
        if ($null -ne $currentComparableLatency -and
            $null -ne $SafeBaselineMetrics.latency_proxy_ms -and
            $currentComparableLatency -gt $SafeBaselineMetrics.latency_proxy_ms) {
            $regressionFailures.Add(
                ("latency_proxy_regressed current_{0}={1} safe_baseline_{2}={3}" -f
                    $safeLatencyMetricKey,
                    $currentComparableLatency.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                    $SafeBaselineMetrics.latency_proxy_name,
                    $SafeBaselineMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
        }

        if ($null -ne $CurrentMetrics.reassembler_loss_count -and
            $null -ne $SafeBaselineMetrics.reassembler_loss_count -and
            $CurrentMetrics.reassembler_loss_count -gt $SafeBaselineMetrics.reassembler_loss_count) {
            $regressionFailures.Add(
                ("reassembler_loss_regressed current={0} safe_baseline={1}" -f
                    $CurrentMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                    $SafeBaselineMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
        }
    }

    if ($null -ne $CurrentMetrics.visible_apply_ratio -and
        $CurrentMetrics.visible_apply_ratio -lt 0.98) {
        $regressionFailures.Add(
            ("visible_apply_ratio_below_target current={0} target=0.98" -f
                $CurrentMetrics.visible_apply_ratio.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
    }

    if ($null -ne $CurrentMetrics.helper_apply_ms_avg -and
        $CurrentMetrics.helper_apply_ms_avg -gt 550) {
        $regressionFailures.Add(
            ("helper_apply_ms_avg_above_target current={0} target=550" -f
                $CurrentMetrics.helper_apply_ms_avg.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
    }

    if ($null -ne $CurrentMetrics.reassembler_loss_count -and
        $CurrentMetrics.reassembler_loss_count -gt 15) {
        $regressionFailures.Add(
            ("reassembler_loss_count_above_target current={0} target=15" -f
                $CurrentMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
    }

    $comparisonLines = @()
    $comparisonLines += @(New-BaselineComparisonReport -Label 'strong' -CurrentMetrics $CurrentMetrics -BaselineMetrics $StrongBaselineMetrics)
    $comparisonLines += ''
    $comparisonLines += @(New-BaselineComparisonReport -Label 'safe' -CurrentMetrics $CurrentMetrics -BaselineMetrics $SafeBaselineMetrics)

    Set-Content -Path (Join-Path $ArtifactDir 'baseline-comparison.txt') -Value $comparisonLines

    $gateStatus = if ($invariantFailures.Count -eq 0 -and $regressionFailures.Count -eq 0) { 'pass' } else { 'fail' }
    $gateLines = @(
        ("behavior_first_gate_status={0}" -f $gateStatus),
        ("invariant_failure_count={0}" -f $invariantFailures.Count),
        ("regression_failure_count={0}" -f $regressionFailures.Count),
        ("latency_gate_metric_name={0}" -f $latencyGateMetricName),
        ("latency_gate_current_value={0}" -f $(if ($null -ne $latencyGateCurrentValue) { $latencyGateCurrentValue.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ("latency_gate_baseline_metric_name={0}" -f $latencyGateBaselineMetricName),
        ("latency_gate_baseline_value={0}" -f $(if ($null -ne $latencyGateBaselineValue) { $latencyGateBaselineValue.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ("current_latency_proxy_name={0}" -f $CurrentMetrics.latency_proxy_name),
        ("current_latency_proxy_ms={0}" -f $(if ($null -ne $CurrentMetrics.latency_proxy_ms) { $CurrentMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ("current_reassembler_loss_count={0}" -f $(if ($null -ne $CurrentMetrics.reassembler_loss_count) { $CurrentMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ("current_recovery_post_ack_hold_started_count={0}" -f $Summary.LatestRecoveryPostAckHoldStartedCount),
        ("current_recovery_post_ack_hold_expired_count={0}" -f $Summary.LatestRecoveryPostAckHoldExpiredCount),
        '',
        'invariant_failures:'
    ) + $(if ($invariantFailures.Count -gt 0) { @($invariantFailures.ToArray()) } else { @('none') }) + @(
        '',
        'regression_failures:'
    ) + $(if ($regressionFailures.Count -gt 0) { @($regressionFailures.ToArray()) } else { @('none') })

    $gateLines | Set-Content -Path (Join-Path $ArtifactDir 'stability-gates-summary.txt')

    return [pscustomobject]@{
        GateStatus = $gateStatus
        InvariantFailures = @($invariantFailures.ToArray())
        RegressionFailures = @($regressionFailures.ToArray())
    }
}

$repoRoot = Resolve-RepoRoot
$resolvedStrongBaselineArtifactDir = if ([string]::IsNullOrWhiteSpace($StrongBaselineArtifactDir)) {
    Join-Path $repoRoot 'artifacts\soak\20260418-154032'
}
else {
    if ([System.IO.Path]::IsPathRooted($StrongBaselineArtifactDir)) { $StrongBaselineArtifactDir } else { Join-Path $repoRoot $StrongBaselineArtifactDir }
}

$resolvedSafeBaselineArtifactDir = if ([string]::IsNullOrWhiteSpace($SafeBaselineArtifactDir)) {
    Join-Path $repoRoot 'artifacts\soak\20260418-200524'
}
else {
    if ([System.IO.Path]::IsPathRooted($SafeBaselineArtifactDir)) { $SafeBaselineArtifactDir } else { Join-Path $repoRoot $SafeBaselineArtifactDir }
}

$guiSmokeScript = Join-Path $repoRoot "tools\GuiSmoke-Windows.ps1"
if (-not (Test-Path $guiSmokeScript)) {
    throw "GUI smoke harness not found: $guiSmokeScript"
}

$resolvedExePath = Resolve-ExePath -RepoRoot $repoRoot -RequestedPath $ExePath
Stop-NLinkProcesses -ResolvedExePath $resolvedExePath
Build-LocalExeIfNeeded -RepoRoot $repoRoot -ResolvedExePath $resolvedExePath -ForceBuild:$Build.IsPresent
Ensure-NknBridgeRuntimeForExe -RepoRoot $repoRoot -ResolvedExePath $resolvedExePath

$previousScenarioEnv = $env:NLINK_GUI_SMOKE_SCENARIOS
$previousTransportEnv = $env:NLINK_TRANSPORT
$previousDurationEnv = $env:NLINK_SCREENSHARE_SOAK_SECONDS

try {
    $env:NLINK_GUI_SMOKE_SCENARIOS = 'SCREENSHARE_NKN_SOAK'
    $env:NLINK_TRANSPORT = 'NKN'
    $env:NLINK_SCREENSHARE_SOAK_SECONDS = [string][Math]::Max(1, $DurationSeconds)

    Write-Host "Running live NKN screenshare soak..." -ForegroundColor Cyan
    Write-Host "  ExePath: $resolvedExePath"
    Write-Host "  DurationSeconds: $DurationSeconds"
    Write-Host "  TimeoutSeconds: $TimeoutSeconds"

    $guiHarnessExitCode = 0
    & powershell -ExecutionPolicy Bypass -File $guiSmokeScript -ExePath $resolvedExePath -TimeoutSeconds $TimeoutSeconds
    $guiHarnessExitCode = $LASTEXITCODE

    $summary = Get-SoakSummaryFromLog
    if ($summary.HelperQualitySummaryLines.Count -gt 0) {
        $missingHelperDiagnostics = @()
        if ($summary.HelperEpochLossLines.Count -eq 0) { $missingHelperDiagnostics += 'helper-frame-loss-epoch' }
        if ($summary.HelperEpochTimelineLines.Count -eq 0) { $missingHelperDiagnostics += 'helper-epoch-timeline' }
        if ($summary.HelperReassemblerRootCauseSummaryLines.Count -eq 0) { $missingHelperDiagnostics += 'helper-reassembler-root-cause-summary' }
        if ($summary.HelperPressureSummaryLines.Count -eq 0) { $missingHelperDiagnostics += 'helper-pressure-summary' }
        if ($missingHelperDiagnostics.Count -gt 0) {
            throw ("Helper debug artifacts were not emitted to the app log: {0}" -f ($missingHelperDiagnostics -join ', '))
        }
    }

    $soakArtifactDir = Write-SoakDiagnosticsArtifacts -RepoRoot $repoRoot -Summary $summary
    $currentComparisonMetrics = Get-CurrentSoakComparisonMetrics -Summary $summary
    $currentComparisonMetrics['artifact_dir'] = $soakArtifactDir
    $strongBaselineMetrics = Get-BaselineSoakComparisonMetrics -ArtifactDir $resolvedStrongBaselineArtifactDir
    $safeBaselineMetrics = Get-BaselineSoakComparisonMetrics -ArtifactDir $resolvedSafeBaselineArtifactDir
    $stabilizationArtifacts = Write-StabilizationArtifacts -ArtifactDir $soakArtifactDir -Summary $summary -CurrentMetrics $currentComparisonMetrics -StrongBaselineMetrics $strongBaselineMetrics -SafeBaselineMetrics $safeBaselineMetrics
    Write-Host ("[NKN Soak] capture_to_send_ms avg={0} min={1} max={2} samples={3}" -f `
        $summary.CaptureAvgMs,
        $summary.CaptureMinMs,
        $summary.CaptureMaxMs,
        $summary.CaptureSampleCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_apply_ms avg={0} min={1} max={2} p95={3} samples={4}; helper_stale_drops={5}" -f `
        $summary.HelperApplyAvgMs,
        $summary.HelperApplyMinMs,
        $summary.HelperApplyMaxMs,
        $summary.HelperApplyP95Ms,
        $summary.HelperApplyCount,
        $summary.HelperStaleDrops) -ForegroundColor Green
    $decodedRatio = if ($summary.LatestHelperFramesCompleted -gt 0 -and $summary.LatestHelperFramesDecoded -ge 0) { [math]::Round(($summary.LatestHelperFramesDecoded / [double]$summary.LatestHelperFramesCompleted), 2) } else { -1 }
    $appliedRatio = if ($summary.LatestHelperFramesCompleted -gt 0 -and $summary.LatestHelperFramesApplied -ge 0) { [math]::Round(($summary.LatestHelperFramesApplied / [double]$summary.LatestHelperFramesCompleted), 2) } else { -1 }
    Write-Host ("[NKN Soak] helper_cadence decode_avg_ms={0} apply_avg_interval_ms={1}; receiver_completed={2} decode_enqueued={3} helper_decoded={4} helper_applied={5}; decoded_ratio={6} applied_ratio={7}; dropped_before_decode={8} dropped_after_decode={9}; receiver_superseded_frames={10}" -f `
        $summary.LatestHelperDecodeDurationMs,
        $summary.LatestHelperApplyIntervalMs,
        $summary.LatestHelperFramesCompleted,
        $summary.LatestHelperFramesEnqueuedForDecode,
        $summary.LatestHelperFramesDecoded,
        $summary.LatestHelperFramesApplied,
        $decodedRatio,
        $appliedRatio,
        $summary.LatestHelperFramesDroppedBeforeDecode,
        $summary.LatestHelperFramesDroppedAfterDecode,
        $summary.ReceiverSupersededFrames) -ForegroundColor Green
    Write-Host ("[NKN Soak] recovery_burst phase={0}; gap_count={1}; gap_to_request_ms={2}; request_to_owner_ms={3}; owner_to_first_visible_apply_ms={4}; control_fallbacks={5}; completed={6}; timeouts={7}; suppressed_restarts={8}; rerequests={9}; forced_resets={10}; emitted_after_forced_reset={11}" -f `
        $summary.LatestRecoveryBurstPhase,
        $summary.LatestRecoveryGapCount,
        $summary.LatestRecoveryGapToKeyframeRequestMs,
        $summary.LatestRecoveryKeyframeRequestToOwnerEmitMs,
        $summary.LatestRecoveryOwnerEmitToFirstVisibleApplyMs,
        $summary.LatestRecoveryBurstControlFallbackCount,
        $summary.LatestRecoveryBurstCompletedCount,
        $summary.LatestRecoveryBurstTimeoutCount,
        $summary.LatestRecoveryBurstRestartSuppressedCount,
        $summary.LatestRecoveryBurstEncoderRerequestCount,
        $summary.LatestRecoveryOwnerPendingForcedResetCount,
        $summary.LatestRecoveryKeyframeEmittedAfterForcedResetCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_decode_worker max_pending_encoded_depth={0} max_pending_decoded_depth={1}; avg_enqueue_to_decode_start_ms={2} avg_enqueue_to_drop_ms={3}; queue_overflow={4} age_budget={5} generation_changed={6} stopped={7}" -f `
        $summary.LatestHelperMaxPendingEncodedDepth,
        $summary.LatestHelperMaxPendingDecodedDepth,
        $summary.LatestHelperAvgEnqueueToDecodeStartMs,
        $summary.LatestHelperAvgEnqueueToDropMs,
        $summary.LatestHelperDecodeWorkerDropQueueOverflowCount,
        $summary.LatestHelperDecodeWorkerDropAgeBudgetCount,
        $summary.LatestHelperDecodeWorkerDropGenerationCount,
        $summary.LatestHelperDecodeWorkerDropStoppedCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_frame_loss reassembler_loss_count={0} enqueue_reject_count={1} decode_worker_drop_count={2} post_decode_drop_count={3} decoded_frame_replaced_before_apply_count={4} stale_dropped_after_decode_count={5} dropped_waiting_for_recovery_keyframe_count={6} waiting_before_runway_count={7} runway_overflow_reject_count={8} suppressed_emit_during_recovery_wait_count={9} stale_superseded_recovery_suppressed_count={10} soft_stale_cleanup_count={11} pre_candidate_gap_tail_emitted_to_viewer_count={12} gap_non_key_pruned_count={13} future_tail_quarantined_during_gap_count={14} future_tail_quarantined_after_gap_count={15} pre_candidate_gap_tail_rejected_count={16} recovery_candidate_present_count={17} visible_recovery_floor_frame_id={18} stable_visible_head_frame_id={19} applied_head_frame_id={20} visible_head_frame_id={21} ordered_emit_head_frame_id={22} winning_recovery_frame_id={23} recovery_owner_replaced_count={24} late_fragment_after_applied_head_count={25} late_fragment_after_ordered_head_count={26} late_fragment_after_stable_visible_head_count={27} late_fragment_after_visible_recovery_count={28} actionable_late_fragment_count={29} runway_buffered_count={30} runway_applied_count={31} runway_abort_count={32} recovery_follower_window_buffered_count={33} recovery_follower_window_applied_count={34} recovery_follower_window_trimmed_count={35} recovery_progress_corridor_count={36} recovery_progress_corridor_success_count={37} recovery_progress_corridor_abort_count={38} recovery_progress_corridor_applied_count={39} recovery_keyframe_resync_count={40} gap_active={41} gap_expected_frame_id={42} buffered_recovery_keyframe_frame_id={43} future_non_key_buffered_count={44} post_recovery_visible_generation_reset_count={45} post_recovery_purged_pre_recovery_follower_count={46} post_recovery_stale_drop_bypass_count={47} unattributed_loss_count={48}; recent_losses={49}" -f `
        $summary.LatestHelperReassemblerLossCount,
        $summary.LatestHelperEnqueueRejectCount,
        $summary.LatestHelperDecodeWorkerDropCount,
        $summary.LatestHelperPostDecodeDropCount,
        $summary.LatestHelperDecodedFrameReplacedBeforeApplyCount,
        $summary.LatestHelperStaleDroppedAfterDecodeCount,
        $summary.LatestHelperDroppedWaitingForRecoveryKeyframeCount,
        $summary.LatestHelperRecoveryWaitRejectBeforeRunwayCount,
        $summary.LatestHelperRecoveryRunwayOverflowRejectCount,
        $summary.LatestHelperSuppressedEmitDuringRecoveryWaitCount,
        $summary.LatestHelperStaleSupersededRecoverySuppressedCount,
        $summary.LatestHelperSoftStaleCleanupCount,
        $summary.LatestHelperPreCandidateGapTailEmittedToViewerCount,
        $summary.LatestHelperGapNonKeyPrunedCount,
        $summary.LatestHelperFutureTailQuarantinedDuringGapCount,
        $summary.LatestHelperFutureTailQuarantinedAfterGapCount,
        $summary.LatestHelperPreCandidateGapTailRejectedCount,
        $summary.LatestHelperRecoveryCandidatePresentCount,
        $summary.LatestHelperVisibleRecoveryFloorFrameId,
        $summary.LatestHelperStableVisibleHeadFrameId,
        $summary.LatestHelperAppliedHeadFrameId,
        $summary.LatestHelperVisibleHeadFrameId,
        $summary.LatestHelperOrderedEmitHeadFrameId,
        $summary.LatestHelperWinningRecoveryFrameId,
        $summary.LatestHelperRecoveryOwnerReplacedCount,
        $summary.LatestHelperLateFragmentAfterAppliedHeadCount,
        $summary.LatestHelperLateFragmentAfterOrderedHeadCount,
        $summary.LatestHelperLateFragmentAfterStableVisibleHeadCount,
        $summary.LatestHelperLateFragmentAfterVisibleRecoveryCount,
        $summary.LatestHelperActionableLateFragmentCount,
        $summary.LatestHelperRecoveryRunwayContiguousFollowerBufferCount,
        $summary.LatestHelperRecoveryRunwayContiguousFollowerApplyCount,
        $summary.LatestHelperRecoveryRunwayAbortCount,
        $summary.LatestHelperRecoveryFollowerWindowBufferedCount,
        $summary.LatestHelperRecoveryFollowerWindowAppliedCount,
        $summary.LatestHelperRecoveryFollowerWindowTrimmedCount,
        $summary.LatestHelperRecoveryProgressCorridorCount,
        $summary.LatestHelperRecoveryProgressCorridorSuccessCount,
        $summary.LatestHelperRecoveryProgressCorridorAbortCount,
        $summary.LatestHelperRecoveryProgressCorridorAppliedCount,
        $summary.LatestHelperRecoveryKeyframeResyncCount,
        $summary.LatestHelperGapActive,
        $summary.LatestHelperGapExpectedFrameId,
        $summary.LatestHelperBufferedRecoveryKeyframeFrameId,
        $summary.LatestHelperFutureNonKeyBufferedCount,
        $summary.LatestHelperPostRecoveryVisibleGenerationResetCount,
        $summary.LatestHelperPostRecoveryPurgedPreRecoveryFollowerCount,
        $summary.LatestHelperPostRecoveryStaleDropBypassCount,
        $summary.LatestHelperUnattributedLossCount,
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestHelperRecentLosses)) { '(none)' } else { $summary.LatestHelperRecentLosses }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_quality visible_apply_ratio={0} gap_count={1} recovery_keyframe_apply_count={2} resync_count={3}; dominant_reassembler_root_cause={4}; dominant_helper_admission_reject_reason={5}; dominant_helper_pressure_blocker={6}; baseline_capture_to_render_ms={7}; age_excess_ms={8}; progress_stall_ms={9}; baseline_reseed_in_progress={10}; age_pressure_consecutive_count={11}; cadence_pressure_consecutive_count={12}; catch_up_suppressed_due_to_progress_count={13}; baseline_frozen_due_to_stall_count={14}; baseline_reseed_after_recovery_count={15}; cadence_stall_window_count={16}; cadence_stall_trigger_count={17}; high_frame_age_suppressed_due_to_visible_progress_count={18}; high_frame_age_suppressed_due_to_head_advance_count={19}; actionable_high_frame_age_count={20}; post_recovery_high_frame_age_suppressed_ticks={21}; recovery_progress_corridor_count={22}; recovery_progress_corridor_success_count={23}; recovery_progress_corridor_abort_count={24}; recovery_progress_corridor_applied_count={25}; recovery_candidate_present_count={26}; visible_recovery_floor_frame_id={27}; stable_visible_head_frame_id={28}; applied_head_frame_id={29}; visible_head_frame_id={30}; ordered_emit_head_frame_id={31}; winning_recovery_frame_id={32}; recovery_owner_replaced_count={33}; steady_visible_progress_active={34}; frames_applied_since_last_gap={35}; pre_candidate_gap_tail_emitted_to_viewer_count={36}; late_fragment_after_applied_head_count={37}; late_fragment_after_ordered_head_count={38}; late_fragment_after_stable_visible_head_count={39}; late_fragment_after_visible_recovery_count={40}; actionable_late_fragment_count={41}; suppressed_emit_during_recovery_wait_count={42}; stale_superseded_recovery_suppressed_count={43}; soft_stale_cleanup_count={44}; pre_candidate_gap_tail_rejected_count={45}" -f `
        $summary.LatestHelperVisibleApplyRatio,
        $summary.LatestHelperGapCount,
        $summary.LatestHelperRecoveryKeyframeApplyCount,
        $summary.LatestHelperResyncCount,
        $summary.DominantReassemblerRootCause,
        $summary.LatestHelperDominantAdmissionRejectReason,
        $summary.DominantHelperPressureBlocker,
        $summary.LatestHelperBaselineCaptureToRenderMs,
        $summary.LatestHelperAgeExcessMs,
        $summary.LatestHelperProgressStallMs,
        $summary.LatestHelperBaselineReseedInProgress,
        $summary.LatestHelperAgePressureConsecutiveCount,
        $summary.LatestHelperCadencePressureConsecutiveCount,
        $summary.LatestHelperCatchUpSuppressedDueToProgressCount,
        $summary.LatestHelperBaselineFrozenDueToStallCount,
        $summary.LatestHelperBaselineReseedAfterRecoveryCount,
        $summary.LatestHelperCadenceStallWindowCount,
        $summary.LatestHelperCadenceStallTriggerCount,
        $summary.AggregateHighFrameAgeSuppressedDueToVisibleProgressCount,
        $summary.LatestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount,
        $summary.LatestHelperActionableHighFrameAgeCount,
        $summary.AggregatePostRecoveryHighFrameAgeSuppressedTicks,
        $summary.LatestHelperRecoveryProgressCorridorCount,
        $summary.LatestHelperRecoveryProgressCorridorSuccessCount,
        $summary.LatestHelperRecoveryProgressCorridorAbortCount,
        $summary.LatestHelperRecoveryProgressCorridorAppliedCount,
        $summary.LatestHelperRecoveryCandidatePresentCount,
        $summary.LatestHelperVisibleRecoveryFloorFrameId,
        $summary.LatestHelperStableVisibleHeadFrameId,
        $summary.LatestHelperAppliedHeadFrameId,
        $summary.LatestHelperVisibleHeadFrameId,
        $summary.LatestHelperOrderedEmitHeadFrameId,
        $summary.LatestHelperWinningRecoveryFrameId,
        $summary.LatestHelperRecoveryOwnerReplacedCount,
        $summary.LatestHelperSteadyVisibleProgressActive,
        $summary.LatestHelperFramesAppliedSinceLastGap,
        $summary.LatestHelperPreCandidateGapTailEmittedToViewerCount,
        $summary.LatestHelperLateFragmentAfterAppliedHeadCount,
        $summary.LatestHelperLateFragmentAfterOrderedHeadCount,
        $summary.LatestHelperLateFragmentAfterStableVisibleHeadCount,
        $summary.LatestHelperLateFragmentAfterVisibleRecoveryCount,
        $summary.LatestHelperActionableLateFragmentCount,
        $summary.LatestHelperSuppressedEmitDuringRecoveryWaitCount,
        $summary.LatestHelperStaleSupersededRecoverySuppressedCount,
        $summary.LatestHelperSoftStaleCleanupCount,
        $summary.LatestHelperPreCandidateGapTailRejectedCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_visible_progress steady_active={0}; activation_frame_id={1}; stable_visible_head_frame_id={2}; last_sent_stable_visible_head_frame_id={3}; frames_applied_since_last_gap={4}; steady_visible_progress_cleared_count={5}; steady_visible_progress_cleared_reason={6}; helper_visible_head_runtime_sender_mismatch={7}" -f `
        $summary.LatestHelperSteadyVisibleProgressActive,
        $summary.LatestHelperSteadyVisibleProgressActivationFrameId,
        $summary.LatestHelperStableVisibleHeadFrameId,
        $summary.LatestHelperLastSentStableVisibleHeadFrameId,
        $summary.LatestHelperFramesAppliedSinceLastGap,
        $summary.LatestHelperSteadyVisibleProgressClearedCount,
        $summary.LatestHelperSteadyVisibleProgressClearedReason,
        $summary.HelperVisibleHeadRuntimeSenderMismatch) -ForegroundColor Green
    Write-Host ("[NKN Soak] health sender_operating_state={0}; sender_guard_state={1}; helper_session_phase={2}; helper_recovery_mechanism={3}; dominant_loss_class={4}; dominant_pressure_blocker={5}; dominant_trouble_domain={6}" -f `
        $summary.LatestHealthSenderOperatingState,
        $summary.LatestHealthSenderGuardState,
        $summary.LatestHealthHelperSessionPhase,
        $summary.LatestHealthHelperRecoveryMechanism,
        $summary.LatestHealthDominantLossClass,
        $summary.LatestHealthDominantPressureBlocker,
        $summary.LatestHealthDominantTroubleDomain) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_startup_corridor recovery_keyframe_pending_visible_apply_count={0}; startup_corridor_buffered_follower_count={1}; startup_corridor_release_count={2}; startup_corridor_abort_count={3}; startup_corridor_abort_reason={4}" -f `
        $summary.LatestHelperRecoveryKeyframePendingVisibleApplyCount,
        $summary.LatestHelperStartupCorridorBufferedFollowerCount,
        $summary.LatestHelperStartupCorridorReleaseCount,
        $summary.LatestHelperStartupCorridorAbortCount,
        $summary.LatestHelperStartupCorridorAbortReason) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_worst_epochs visible_apply_ratio stream_epoch={0} ratio={1}; recovery_lock stream_epoch={2} time_ms={3}" -f `
        $summary.WorstEpochByVisibleApplyRatio,
        $summary.WorstEpochVisibleApplyRatio,
        $summary.WorstEpochByRecoveryLockTime,
        $summary.WorstEpochRecoveryLockTimeMs) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_decode_loss_buckets decode_queue_overflow_count={0} decode_age_budget_count={1} decode_generation_changed_count={2} decode_stopped_count={3} decoded_apply_queue_overflow_count={4}" -f `
        $summary.LatestHelperDecodeQueueOverflowCount,
        $summary.LatestHelperDecodeAgeBudgetCount,
        $summary.LatestHelperDecodeGenerationChangedCount,
        $summary.LatestHelperDecodeStoppedCount,
        $summary.LatestHelperDecodedApplyQueueOverflowCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_decode_outcomes need_more_input={0} completed_without_picture={1}" -f `
        $summary.LatestHelperNeedMoreInputCount,
        $summary.LatestHelperCompletedWithoutPictureCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] reduced_promotion blockers rate_gate={0} helper_pressure={1} helper_warmup={2} helper_apply_count={3} bridge_health={4} recovery_lock={5} queue_evict={6} capture_age={7} encode_budget={8} transition_grace={9}; soft_spikes={10}; soft_spike_resets_suppressed={11}; blocked_by_missing_helper_proof={12}; blocked_by_stale_helper_proof={13}; blocked_by_encode_budget={14}; blocked_by_encode_budget_alone={15}; reset_reasons={16}" -f `
        $summary.LatestPromotionBlockerRateGateTicks,
        $summary.LatestPromotionBlockerHelperPressureTicks,
        $summary.LatestPromotionBlockerHelperWarmupTicks,
        $summary.LatestPromotionBlockerHelperApplyCountTicks,
        $summary.LatestPromotionBlockerBridgeHealthTicks,
        $summary.LatestPromotionBlockerRecoveryLockTicks,
        $summary.LatestPromotionBlockerQueueEvictTicks,
        $summary.LatestPromotionBlockerCaptureAgeTicks,
        $summary.LatestPromotionBlockerEncodeBudgetTicks,
        $summary.LatestPromotionBlockerTransitionGraceTicks,
        $summary.LatestPromotionEncodeSoftSpikeCount,
        $summary.LatestPromotionEncodeSoftSpikeResetSuppressedCount,
        $summary.PromotionBlockedByMissingHelperProofCount,
        $summary.PromotionBlockedByStaleHelperProofCount,
        $summary.PromotionBlockedByEncodeBudgetCount,
        $summary.PromotionBlockedByEncodeBudgetAloneCount,
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestHealthyTickResetReasonCounts)) { '(none)' } else { $summary.LatestHealthyTickResetReasonCounts }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] sender_cadence frames_deferred_to_send_slot={0} frames_replaced_before_send_slot={1} frames_dropped_by_queue_evict={2} send_slot_empty_count={3} slot_coalescing_active={4}; promotion_rate_gate_ticks={5} source_frames_queued={6}" -f `
        $summary.LatestFramesDeferredToSendSlot,
        $summary.LatestFramesReplacedBeforeSendSlot,
        $summary.LatestFramesDroppedByQueueEvict,
        $summary.LatestSendSlotEmptyCount,
        $summary.LatestSlotCoalescingActive,
        $summary.LatestPromotionBlockerRateGateTicks,
        $summary.LatestFramesQueued) -ForegroundColor Green
    Write-Host ("[NKN Soak] raw_cadence raw_frames_deferred_to_encode_slot={0} raw_frames_replaced_before_encode_slot={1} raw_encode_slot_empty_count={2} raw_slot_coalescing_active={3}; source_superseded_pending_frames={4}; promotion_capture_to_send_budget_ms={5}" -f `
        $summary.LatestRawFramesDeferredToEncodeSlot,
        $summary.LatestRawFramesReplacedBeforeEncodeSlot,
        $summary.LatestRawEncodeSlotEmptyCount,
        $summary.LatestRawSlotCoalescingActive,
        $summary.LatestSourceSupersededPendingFrames,
        $summary.LatestPromotionCaptureToSendBudgetMs) -ForegroundColor Green
    Write-Host ("[NKN Soak] ordinary_freshness_boundary raw_loss_count={0} sender_loss_count={1} helper_loss_count={2}; dominant_boundary={3}" -f `
        $summary.LatestOrdinaryRawLossCount,
        $summary.LatestOrdinarySenderLossCount,
        $summary.LatestOrdinaryHelperLossCount,
        $summary.DominantOrdinaryFreshnessLossBoundary) -ForegroundColor Green
    Write-Host ("[NKN Soak] reduced_promotion recent_entries={0}" -f `
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestReducedPromotionRecentEntries)) { '(none)' } else { $summary.LatestReducedPromotionRecentEntries }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] encoder_path summaries: persistent_transform={0} sink_writer_fallback={1}" -f `
        $summary.PersistentSummaryCount,
        $summary.SinkWriterSummaryCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] sender_mode summaries: normal={0} reduced={1} catch_up={2}; bridge_health advisory={3} actionable={4}" -f `
        $summary.NormalModeSummaryCount,
        $summary.ReducedModeSummaryCount,
        $summary.CatchUpModeSummaryCount,
        $summary.BridgeHealthAdvisorySummaryCount,
        $summary.BridgeHealthActionableSummaryCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] transport_shape frames_queued={0} avg_fragments_per_frame={1} avg_payloads_per_frame={2}; batched_payloads={3} legacy_fragment_payloads={4}; ordinary_non_key_batched={5} ordinary_non_key_legacy={6} keyframe_recovery_batched={7}" -f `
        $summary.LatestFramesQueued,
        $summary.LatestAvgFragmentsPerFrame,
        $summary.LatestAvgPayloadsPerFrame,
        $summary.LatestBatchPayloadCount,
        $summary.LatestLegacyPayloadCount,
        $summary.LatestOrdinaryNonKeyBatchedPayloadCount,
        $summary.LatestOrdinaryNonKeyLegacyPayloadCount,
        $summary.LatestKeyframeRecoveryBatchedPayloadCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] transport_mode effective_media_plane_active={0}; recovery_used_control_fallback={1}; steady_state_used_control_fallback={2}; bridge_media_messages_received={3}; media_plane_frames_sent={4}; media_plane_attached={5}" -f `
        $summary.EffectiveMediaPlaneActive,
        $summary.RecoveryUsedControlFallback,
        $summary.SteadyStateUsedControlFallback,
        $summary.LatestBridgeMediaMessagesReceived,
        $summary.LatestMediaPlaneFramesSent,
        $summary.LatestMediaPlaneAttached) -ForegroundColor Green
    Write-Host ("[NKN Soak] encoder_output displayable={0} non_displayable={1} idr_frames={2} p_frames={3} dropped_b_frames={4} dropped_multi_picture_units={5}; ratio={6}; idr_ratio={7}; avg_encoded_frame_bytes={8}; transport_ip_only_mode={9}; last_access_unit_kind={10}; low_delay_config_applied={11}" -f `
        $summary.LatestEmittedDisplayableFrames,
        $summary.LatestEmittedNonDisplayableUnits,
        $summary.LatestEmittedIdrFrames,
        $summary.LatestEmittedPFrames,
        $summary.LatestDroppedBFrames,
        $summary.LatestDroppedMultiPictureUnits,
        $summary.LatestDisplayableFrameRatio,
        $summary.LatestIdrFrameRatio,
        $summary.LatestAverageEncodedFrameBytes,
        $summary.LatestTransportIpOnlyMode,
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestLastAccessUnitKind)) { '(none)' } else { $summary.LatestLastAccessUnitKind })),
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestLowDelayConfigApplied)) { '(none)' } else { $summary.LatestLowDelayConfigApplied }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_bootstrap run_id={0} listener_generation={1}" -f `
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestHelperRunId)) { '(none)' } else { $summary.LatestHelperRunId })),
        $summary.LatestHelperListenerGeneration) -ForegroundColor DarkGray
    Write-Host ("[NKN Soak] baseline_compare strong_artifact={0}; safe_artifact={1}; current_latency_proxy_name={2}; current_latency_proxy_ms={3}; safe_latency_proxy_ms={4}; current_reassembler_loss_count={5}; safe_reassembler_loss_count={6}" -f `
        ($(if ($null -ne $strongBaselineMetrics) { $strongBaselineMetrics.artifact_dir } else { '(missing)' })),
        ($(if ($null -ne $safeBaselineMetrics) { $safeBaselineMetrics.artifact_dir } else { '(missing)' })),
        $currentComparisonMetrics.latency_proxy_name,
        ($(if ($null -ne $currentComparisonMetrics.latency_proxy_ms) { $currentComparisonMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ($(if ($null -ne $safeBaselineMetrics -and $null -ne $safeBaselineMetrics.latency_proxy_ms) { $safeBaselineMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ($(if ($null -ne $currentComparisonMetrics.reassembler_loss_count) { $currentComparisonMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ($(if ($null -ne $safeBaselineMetrics -and $null -ne $safeBaselineMetrics.reassembler_loss_count) { $safeBaselineMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] behavior_first_gate status={0}; invariant_failures={1}; regression_failures={2}; skip_gate={3}" -f `
        $stabilizationArtifacts.GateStatus,
        $stabilizationArtifacts.InvariantFailures.Count,
        $stabilizationArtifacts.RegressionFailures.Count,
        ($(if ($SkipBehaviorFirstGate.IsPresent) { 1 } else { 0 }))) -ForegroundColor $(if ($stabilizationArtifacts.GateStatus -eq 'pass') { 'Green' } else { 'Yellow' })
    Write-Host ("[NKN Soak] Artifacts: {0}" -f $soakArtifactDir) -ForegroundColor DarkGray
    Write-Host ("[NKN Soak] Log: {0}" -f $summary.LogPath) -ForegroundColor DarkGray

    $terminalFailures = New-Object System.Collections.Generic.List[string]
    if ($guiHarnessExitCode -ne 0) {
        $terminalFailures.Add("GUI soak harness exited with code $guiHarnessExitCode")
    }

    if ($stabilizationArtifacts.GateStatus -ne 'pass' -and -not $SkipBehaviorFirstGate.IsPresent) {
        $gateFailureDetail = @($stabilizationArtifacts.InvariantFailures + $stabilizationArtifacts.RegressionFailures) -join '; '
        $terminalFailures.Add(("behavior-first gate failed: {0}" -f $(if ([string]::IsNullOrWhiteSpace($gateFailureDetail)) { 'see stability-gates-summary.txt' } else { $gateFailureDetail })))
    }

    if ($terminalFailures.Count -gt 0) {
        throw ("{0}. Diagnostics were still collected at {1}." -f ($terminalFailures -join '; '), $soakArtifactDir)
    }
}
finally {
    Stop-NLinkProcesses -ResolvedExePath $resolvedExePath
    $env:NLINK_GUI_SMOKE_SCENARIOS = $previousScenarioEnv
    if ($null -eq $previousTransportEnv) {
        Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_TRANSPORT = $previousTransportEnv
    }

    if ($null -eq $previousDurationEnv) {
        Remove-Item Env:NLINK_SCREENSHARE_SOAK_SECONDS -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_SCREENSHARE_SOAK_SECONDS = $previousDurationEnv
    }
}
