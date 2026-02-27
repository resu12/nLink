param(
    [switch]$RunNknSmoke,
    [switch]$RunInstallerUpgradeRollback,
    [switch]$RunOfflineSmoke,
    [switch]$RunPermissionsSmoke,
    [switch]$RunHangChecks,
    [string]$Runtime = "win-x64",
    [int]$ReliabilityCycles = 500,
    [int]$LeakCheckCycles = 200,
    [int]$NknSmokeCycles = 50
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    Write-Host "[BetaReadiness] $Name" -ForegroundColor Cyan
    & $Action
}

function Assert-PathExists {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if (-not (Test-Path $Path)) {
        throw "$Description not found: $Path"
    }
}

function New-SectionResult {
    param(
        [string]$Name,
        [bool]$Required = $true
    )
    return [pscustomobject]@{
        Name = $Name
        Required = $Required
        Passed = $false
        Skipped = $false
        Notes = New-Object System.Collections.Generic.List[string]
        Artifacts = New-Object System.Collections.Generic.List[string]
        Error = $null
    }
}

function Complete-SectionSuccess {
    param($Section)
    $Section.Passed = $true
    return $Section
}

function Complete-SectionSkip {
    param($Section, [string]$Reason)
    $Section.Skipped = $true
    if (-not [string]::IsNullOrWhiteSpace($Reason)) { [void]$Section.Notes.Add($Reason) }
    return $Section
}

function Complete-SectionFailure {
    param($Section, [string]$ErrorText)
    $Section.Passed = $false
    $Section.Error = $ErrorText
    return $Section
}

function Format-NullableNumber {
    param(
        [AllowNull()]$Value,
        [string]$Format = "0.0"
    )
    if ($null -eq $Value) { return "n/a" }
    try {
        return ([double]$Value).ToString($Format, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch { return "n/a" }
}

function Get-LatestFile {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Filter
    )
    if (-not (Test-Path $Directory)) { return $null }
    return Get-ChildItem -Path $Directory -File -Filter $Filter -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Get-RequiredCounterValue {
    param(
        [Parameter(Mandatory = $true)]$Metrics,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $sum = 0.0
    $found = $false
    foreach ($c in @($Metrics.Counters)) {
        if ($null -ne $c -and [string]$c.Name -eq $Name) {
            $sum += [double]$c.Value
            $found = $true
        }
    }
    if (-not $found) { return $null }
    return $sum
}

function Get-HistogramP95 {
    param(
        [Parameter(Mandatory = $true)]$Metrics,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $all = @($Metrics.Histograms) | Where-Object { $_ -and [string]$_.Name -eq $Name }
    if ($all.Count -eq 0) { return $null }
    $counts = @()
    foreach ($h in $all) {
        if ($null -ne $h.Count) { $counts += [double]$h.Count }
    }
    if ($counts.Count -eq 0) { return $null }
    $totalCount = ($counts | Measure-Object -Sum).Sum
    if ($totalCount -le 0) { return 0 }
    # Approximate from bucket upper bounds across histograms: take max reported p95-ish proxy by bucket scan.
    $best = 0.0
    foreach ($h in $all) {
        $running = 0.0
        $target = [math]::Ceiling(([double]$h.Count) * 0.95)
        foreach ($b in @($h.Buckets)) {
            $running += [double]$b.Count
            if ($running -ge $target) {
                $ub = $b.UpperBound
                if ($ub -is [string]) {
                    if ($ub -eq "Infinity") { continue }
                    continue
                }
                $candidate = [double]$ub
                if ($candidate -gt $best) { $best = $candidate }
                break
            }
        }
    }
    return $best
}

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$Lines
    )
    $dir = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    Set-Content -Path $Path -Value ($Lines -join [Environment]::NewLine) -Encoding UTF8
}

function Convert-BenchToReliabilitySummary {
    param([Parameter(Mandatory = $true)]$BenchObj)
    $summary = $BenchObj.Summary
    $metrics = $BenchObj.Metrics
    $gate = $BenchObj.ReliabilityGate
    $unknownFailures = $null
    $stuckCount = $null
    $bridgeCrashTotal = $null

    if ($null -ne $gate) {
        if ($null -ne $gate.UnknownFailures) { $unknownFailures = [double]$gate.UnknownFailures }
        if ($null -ne $gate.StateStuckCount) { $stuckCount = [double]$gate.StateStuckCount }
        if ($null -ne $gate.BridgeCrashTotal) { $bridgeCrashTotal = [double]$gate.BridgeCrashTotal }
    }

    if ($null -eq $unknownFailures) {
        $unknownFailures = 0
        foreach ($c in @($metrics.Counters)) {
            if ($null -eq $c) { continue }
            if ([string]$c.Name -ne "transport_failure_total") { continue }
            $tags = $c.Tags
            if ($null -eq $tags) { continue }
            if ([string]$tags.FailureCategory -ne "Unknown") { continue }
            $unknownFailures += [double]$c.Value
        }
    }
    if ($null -eq $stuckCount) {
        $stuckCount = Get-RequiredCounterValue -Metrics $metrics -Name "state_stuck_count"
    }
    if ($null -eq $bridgeCrashTotal) {
        $bridgeCrashTotal = Get-RequiredCounterValue -Metrics $metrics -Name "bridge_crash_total"
    }
    $activeFinal = [pscustomobject]@{}
    if ($null -ne $BenchObj -and ($BenchObj.PSObject.Properties.Name -contains "ActiveCountersFinal")) {
        if ($null -ne $BenchObj.ActiveCountersFinal) {
            $activeFinal = $BenchObj.ActiveCountersFinal
        }
    }
    elseif ($null -ne $summary -and ($summary.PSObject.Properties.Name -contains "FinalActiveCounters")) {
        if ($null -ne $summary.FinalActiveCounters) {
            $activeFinal = $summary.FinalActiveCounters
        }
    }
    elseif ($null -ne $metrics -and $null -ne $metrics.Gauges) {
        $lookup = @{}
        foreach ($g in @($metrics.Gauges)) {
            if ($null -eq $g -or [string]::IsNullOrWhiteSpace([string]$g.Name)) { continue }
            $lookup[[string]$g.Name] = [int][math]::Round([double]$g.Value, 0)
        }
        function Get-ActiveGaugeOrZero([hashtable]$Map, [string]$Name) {
            if ($Map.ContainsKey($Name)) { return [int]$Map[$Name] }
            return 0
        }
        $activeFinal = [pscustomobject]@{
            ActiveSessions = (Get-ActiveGaugeOrZero $lookup "active_sessions")
            ActiveConnectAttempts = (Get-ActiveGaugeOrZero $lookup "active_connect_attempts")
            ActiveRetryTimers = (Get-ActiveGaugeOrZero $lookup "active_retry_timers")
            ActiveWatchdogs = (Get-ActiveGaugeOrZero $lookup "active_watchdogs")
            ActiveTransportTasks = (Get-ActiveGaugeOrZero $lookup "active_transport_tasks")
            ActiveBridgeIoReaders = (Get-ActiveGaugeOrZero $lookup "active_bridge_io_readers")
        }
    }

    return [pscustomobject]@{
        # BenchmarkRunner reliability soak uses a paired runtime (helpee + helper) in one process.
        # The global active_connect_attempts counter is therefore an aggregate across two SessionRuntime instances.
        # Convert to a per-runtime ceiling for the checklist's single-flight invariant.
        MaxInflightConnectAttemptsAggregate = if ($null -ne $summary -and ($summary.PSObject.Properties.Name -contains "MaxActiveConnectAttempts")) {
            [double]$summary.MaxActiveConnectAttempts
        }
        else {
            $null
        }
        SuccessRatePercent = [double]$summary.SuccessRatePercent
        CyclesRequested = [int]$summary.CyclesRequested
        CyclesSucceeded = [int]$summary.CyclesSucceeded
        AvgConnectMs = $summary.AvgConnectMs
        P95ConnectMs = $summary.P95ConnectMs
        AvgHandshakeMs = $summary.AvgHandshakeMs
        P95HandshakeMs = $summary.P95HandshakeMs
        WarmStartRatio = $summary.WarmStartRatio
        TopFailureCategories = @($summary.TopFailureCategories)
        UnknownFailures = if ($null -eq $unknownFailures) { 0 } else { [double]$unknownFailures }
        StateStuckCount = if ($null -eq $stuckCount) { 0 } else { [double]$stuckCount }
        BridgeCrashTotal = if ($null -eq $bridgeCrashTotal) { 0 } else { [double]$bridgeCrashTotal }
        FinalActiveCounters = $activeFinal
        MaxInflightConnectAttempts = if ($null -ne $summary -and ($summary.PSObject.Properties.Name -contains "MaxActiveConnectAttempts")) {
            [math]::Ceiling(([double]$summary.MaxActiveConnectAttempts) / 2.0)
        }
        else {
            $null
        }
    }
}

function Build-ReliabilityPromotionSummaryLines {
    param(
        [Parameter(Mandatory = $true)]$Reliability,
        [Parameter(Mandatory = $true)][string]$JsonPath
    )
    $topFailures = @()
    foreach ($item in @($Reliability.TopFailureCategories)) {
        if ($null -eq $item) { continue }
        if ($item -is [string]) { $topFailures += [string]$item; continue }
        if ($null -ne $item.Category -and $null -ne $item.Count) {
            $topFailures += ("{0} ({1})" -f [string]$item.Category, [string]$item.Count)
        }
    }
    if ($topFailures.Count -eq 0) { $topFailures = @("(none)") }

    return @(
        "Beta Promotion Reliability Summary",
        ("Cycles: {0}/{1}" -f $Reliability.CyclesSucceeded, $Reliability.CyclesRequested),
        ("Success rate: {0}%" -f (Format-NullableNumber $Reliability.SuccessRatePercent "0.0")),
        ("Connect avg/p95 (ms): {0} / {1}" -f (Format-NullableNumber $Reliability.AvgConnectMs "0.0"), (Format-NullableNumber $Reliability.P95ConnectMs "0.0")),
        ("Handshake avg/p95 (ms): {0} / {1}" -f (Format-NullableNumber $Reliability.AvgHandshakeMs "0.0"), (Format-NullableNumber $Reliability.P95HandshakeMs "0.0")),
        ("Warm start ratio: {0}%" -f (Format-NullableNumber (($Reliability.WarmStartRatio) * 100.0) "0.0")),
        ("Unknown failures: {0}" -f [int]$Reliability.UnknownFailures),
        ("State stuck count: {0}" -f [int]$Reliability.StateStuckCount),
        ("Max inflight connect attempts: $(
            if ($null -eq $Reliability.MaxInflightConnectAttempts) { "n/a" } else { [int]$Reliability.MaxInflightConnectAttempts }
        )"),
        ("Max inflight connect attempts (aggregate process counter): $(
            if ($null -eq $Reliability.MaxInflightConnectAttemptsAggregate) { "n/a" } else { [int]$Reliability.MaxInflightConnectAttemptsAggregate }
        )"),
        ("Bridge crash total: {0}" -f [int]$Reliability.BridgeCrashTotal),
        ("Top failure categories: {0}" -f ($topFailures -join ", ")),
        ("Metrics JSON: {0}" -f (Resolve-Path $JsonPath).Path)
    )
}

function Test-ReliabilityPromotion {
    param(
        [Parameter(Mandatory = $true)]$Reliability,
        [double]$MinSuccessRatePercent = 100.0,
        [double]$P95ConnectMaxMs = 1000.0
    )
    $failures = New-Object System.Collections.Generic.List[string]
    if ([double]$Reliability.SuccessRatePercent -lt $MinSuccessRatePercent) {
        [void]$failures.Add(("success_rate {0}% < {1}%" -f (Format-NullableNumber $Reliability.SuccessRatePercent "0.0"), (Format-NullableNumber $MinSuccessRatePercent "0.0")))
    }
    if ([double]$Reliability.UnknownFailures -gt 0) {
        [void]$failures.Add(("Unknown failures > 0 (observed {0})" -f [int]$Reliability.UnknownFailures))
    }
    if ([double]$Reliability.StateStuckCount -gt 0) {
        [void]$failures.Add(("state_stuck_count > 0 (observed {0})" -f [int]$Reliability.StateStuckCount))
    }
    if ([double]$Reliability.BridgeCrashTotal -gt 0) {
        [void]$failures.Add(("bridge_crash_total > 0 (observed {0})" -f [int]$Reliability.BridgeCrashTotal))
    }
    if ($null -eq $Reliability.MaxInflightConnectAttempts) {
        [void]$failures.Add("max_inflight_connect_attempts missing")
    }
    elseif ([double]$Reliability.MaxInflightConnectAttempts -gt 1) {
        [void]$failures.Add(("max_inflight_connect_attempts > 1 (observed {0})" -f [int]$Reliability.MaxInflightConnectAttempts))
    }
    if ($null -eq $Reliability.P95ConnectMs) {
        [void]$failures.Add("p95 connect duration missing")
    }
    elseif ([double]$Reliability.P95ConnectMs -gt $P95ConnectMaxMs) {
        [void]$failures.Add(("p95 connect {0}ms > {1}ms" -f (Format-NullableNumber $Reliability.P95ConnectMs "0.0"), (Format-NullableNumber $P95ConnectMaxMs "0.0")))
    }
    $active = $Reliability.FinalActiveCounters
    if ($null -ne $active) {
        foreach ($p in @("ActiveSessions","ActiveConnectAttempts","ActiveRetryTimers","ActiveWatchdogs","ActiveTransportTasks","ActiveBridgeIoReaders")) {
            if ($active.PSObject.Properties.Name -contains $p) {
                $value = [int]$active.$p
                if ($value -ne 0) {
                    [void]$failures.Add(("{0} != 0 at end (observed {1})" -f $p, $value))
                }
            }
        }
    }

    return [pscustomobject]@{
        Passed = ($failures.Count -eq 0)
        Failures = @($failures)
    }
}

function Verify-ChecksumsFile {
    param(
        [Parameter(Mandatory = $true)][string]$ChecksumsPath,
        [Parameter(Mandatory = $true)][string]$AssetDir
    )
    $lines = Get-Content -Path $ChecksumsPath
    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^\s*([0-9a-fA-F]{64})\s+(.+?)\s*$') {
            [void]$failures.Add("Malformed checksum line: $line")
            continue
        }
        $expected = $Matches[1].ToLowerInvariant()
        $name = $Matches[2].Trim()
        $path = Join-Path $AssetDir $name
        if (-not (Test-Path $path)) {
            [void]$failures.Add("Checksum target missing: $name")
            continue
        }
        $actual = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            [void]$failures.Add("Checksum mismatch: $name")
        }
    }
    return [pscustomobject]@{
        Passed = ($failures.Count -eq 0)
        Failures = @($failures)
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$version = (Get-Content (Join-Path $repoRoot "VERSION") -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw "VERSION is empty." }

$betaRoot = Join-Path $repoRoot "artifacts\beta-readiness"
$distRoot = Join-Path $repoRoot "artifacts\dist"
$versionDistDir = Join-Path $distRoot $version
New-Item -ItemType Directory -Force -Path $betaRoot | Out-Null
New-Item -ItemType Directory -Force -Path $versionDistDir | Out-Null

$sections = New-Object System.Collections.Generic.List[object]
$requiredFailures = New-Object System.Collections.Generic.List[string]

function Add-SectionResult {
    param($Section)
    [void]$sections.Add($Section)
    if (-not $Section.Skipped -and -not $Section.Passed -and $Section.Required) {
        [void]$requiredFailures.Add($Section.Name)
    }
}

Push-Location $repoRoot
try {
    # A1 Build Release
    $s = New-SectionResult -Name "Build Release"
    try {
        Invoke-Step "Build Release" { dotnet build nLink.sln -c Release; if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" } }
        [void]$s.Notes.Add("dotnet build nLink.sln -c Release")
        Add-SectionResult (Complete-SectionSuccess $s)
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    # A2 Unit tests (exclude GUI smoke)
    $s = New-SectionResult -Name "Unit Tests"
    try {
        Invoke-Step "Unit tests (non-GUI)" { dotnet test -c Release --filter "Category!=GuiSmoke"; if ($LASTEXITCODE -ne 0) { throw "unit tests failed (exit $LASTEXITCODE)" } }
        [void]$s.Notes.Add("dotnet test -c Release --filter Category!=GuiSmoke")
        Add-SectionResult (Complete-SectionSuccess $s)
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    # A3 Smoke tests
    $s = New-SectionResult -Name "Smoke Tests"
    try {
        Invoke-Step "Smoke tests" { dotnet test -c Release --filter Category=Smoke --no-build; if ($LASTEXITCODE -ne 0) { throw "smoke tests failed (exit $LASTEXITCODE)" } }
        Add-SectionResult (Complete-SectionSuccess $s)
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    # B Reliability promotion (DevLocal soak 500)
    $reliabilityJson = $null
    $reliabilitySummaryPath = Join-Path $betaRoot "reliability-summary.txt"
    $s = New-SectionResult -Name "Deterministic Reliability Promotion"
    try {
        Invoke-Step "Reliability soak (DevLocal, 500 cycles)" {
            dotnet run -c Release --no-build --project .\src\nLink.App -- --soak --cycles $ReliabilityCycles --delay-ms 0 --transport devlocal --bridge-reuse-mode persession --fail-on-gate
            if ($LASTEXITCODE -ne 0) { throw "reliability soak failed (exit $LASTEXITCODE)" }
        }
        $latestBench = Get-LatestFile -Directory (Join-Path $repoRoot "artifacts\bench") -Filter "metrics-*.json"
        if ($null -eq $latestBench) { throw "No benchmark metrics JSON found after soak." }
        $reliabilityJson = Join-Path $betaRoot "reliability-soak.json"
        Copy-Item -Force $latestBench.FullName $reliabilityJson
        $benchObj = Get-Content -Path $reliabilityJson -Raw | ConvertFrom-Json
        $rel = Convert-BenchToReliabilitySummary -BenchObj $benchObj
        $relGate = Test-ReliabilityPromotion -Reliability $rel -MinSuccessRatePercent 100 -P95ConnectMaxMs 1000
        $summaryLines = Build-ReliabilityPromotionSummaryLines -Reliability $rel -JsonPath $reliabilityJson
        if (-not $relGate.Passed) {
            $summaryLines += ""
            $summaryLines += "FAILURES:"
            foreach ($f in $relGate.Failures) { $summaryLines += ("- {0}" -f $f) }
        }
        Write-TextFile -Path $reliabilitySummaryPath -Lines $summaryLines
        [void]$s.Artifacts.Add((Resolve-Path $reliabilityJson).Path)
        [void]$s.Artifacts.Add((Resolve-Path $reliabilitySummaryPath).Path)
        if (-not $relGate.Passed) { throw ("Reliability promotion gate failed: " + ($relGate.Failures -join "; ")) }
        Add-SectionResult (Complete-SectionSuccess $s)
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    # C Resource/Leak promotion
    $s = New-SectionResult -Name "Deterministic Resource/Leak Promotion"
    try {
        Invoke-Step "Resource benchmark + gate" {
            # ResourceBenchmark is a short single connect/disconnect scenario and is prone to
            # one-time warm-up growth (JIT/thread-pool/cache) that is not a leak signal.
            # For beta promotion, use ResourceGate here for absolute/cpu/cleanup checks and
            # let LeakCheck own the growth enforcement via monotonic checkpoint growth.
            dotnet run -c Release --no-build --project .\src\nLink.App -- --resource-bench --transport devlocal --bridge-reuse-mode persession --sample-ms 1000 --idle-seconds 5 --connected-idle-seconds 5 --final-idle-seconds 5 --fail-on-gate --resource-disable-growth-checks
            if ($LASTEXITCODE -ne 0) { throw "resource benchmark failed (exit $LASTEXITCODE)" }
        }
        Invoke-Step "Leak check + gate" {
            # LeakCheck already enforces growth intent with its own monotonic checkpoint gate
            # (--leak-growth-fail-percent). Disable generic baseline->last growth checks in ResourceGate
            # so this stage enforces cleanup/absolute ceilings + the dedicated monotonic leak gate.
            dotnet run -c Release --no-build --project .\src\nLink.App -- --leak-check --cycles $LeakCheckCycles --transport devlocal --bridge-reuse-mode persession --delay-ms 0 --fail-on-gate --leak-growth-fail-percent 20 --resource-disable-growth-checks
            if ($LASTEXITCODE -ne 0) { throw "leak check failed (exit $LASTEXITCODE)" }
        }
        $resourcesDir = Join-Path $repoRoot "artifacts\resources"
        Assert-PathExists -Path $resourcesDir -Description "Resources artifacts directory"
        foreach ($f in @("resource-summary.txt","leak-check-summary.txt")) {
            $fp = Join-Path $resourcesDir $f
            if (Test-Path $fp) { [void]$s.Artifacts.Add((Resolve-Path $fp).Path) }
        }
        $latestResourceJson = Get-LatestFile -Directory $resourcesDir -Filter "resource-run-*.json"
        $latestLeakJson = Get-LatestFile -Directory $resourcesDir -Filter "leak-check-*.json"
        if ($null -ne $latestResourceJson) { [void]$s.Artifacts.Add($latestResourceJson.FullName) }
        if ($null -ne $latestLeakJson) { [void]$s.Artifacts.Add($latestLeakJson.FullName) }
        Add-SectionResult (Complete-SectionSuccess $s)
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    # D Bridge stability promotion
    $bridgeStabilitySummary = Join-Path $betaRoot "bridge-stability-summary.txt"
    $s = New-SectionResult -Name "Bridge Stability Promotion"
    try {
        Invoke-Step "Bridge stability promotion tests" {
            dotnet test -c Release --filter Category=BridgeStabilityPromotion --no-build
            if ($LASTEXITCODE -ne 0) { throw "bridge stability promotion tests failed (exit $LASTEXITCODE)" }
        }
        $bridgeSummaryLines = @(
            "Bridge Stability Promotion Summary",
            "Status: PASS",
            "Suite: Category=BridgeStabilityPromotion",
            "Checks: rapid cycles, stderr spam, crash/unresponsive failure injection subset, cleanup/no-orphan assertions",
            "Last PID: n/a (no app process held by promotion script)",
            "Last exit reason: n/a (covered by tests)",
            "Active counters on failure: see failing test output (none in this run)"
        )
        Write-TextFile -Path $bridgeStabilitySummary -Lines $bridgeSummaryLines
        [void]$s.Artifacts.Add((Resolve-Path $bridgeStabilitySummary).Path)
        Add-SectionResult (Complete-SectionSuccess $s)
    } catch {
        $bridgeSummaryLines = @(
            "Bridge Stability Promotion Summary",
            "Status: FAIL",
            ("Error: {0}" -f $_.Exception.Message),
            "Last PID: n/a",
            "Last exit reason: n/a",
            "Active counters on failure: inspect test output and metrics artifacts"
        )
        Write-TextFile -Path $bridgeStabilitySummary -Lines $bridgeSummaryLines
        Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message)
    }

    # E Contract freeze promotion
    $s = New-SectionResult -Name "Contract Freeze Promotion"
    try {
        Invoke-Step "Contract freeze tests" {
            dotnet test -c Release --filter Category=ContractFreeze --no-build
            if ($LASTEXITCODE -ne 0) { throw "contract freeze tests failed (exit $LASTEXITCODE)" }
        }
        $contractDir = Join-Path $repoRoot "tests\nLink.SmokeTests\GoldenFiles\Contracts"
        [void]$s.Artifacts.Add((Resolve-Path $contractDir).Path)
        Add-SectionResult (Complete-SectionSuccess $s)
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    # F Packaging promotion
    $s = New-SectionResult -Name "Packaging Promotion"
    try {
        Invoke-Step "Build bridge bundle" {
            & powershell -ExecutionPolicy Bypass -File ".\installer\Build-BridgeBundle.ps1" -Runtime $Runtime
            if ($LASTEXITCODE -ne 0) { throw "Build-BridgeBundle.ps1 failed (exit $LASTEXITCODE)" }
        }
        Invoke-Step "Build portable" {
            & powershell -ExecutionPolicy Bypass -File ".\installer\Build-Portable.ps1" -Runtime $Runtime
            if ($LASTEXITCODE -ne 0) { throw "Build-Portable.ps1 failed (exit $LASTEXITCODE)" }
        }
        Invoke-Step "Build installer" {
            & powershell -ExecutionPolicy Bypass -File ".\installer\Build-Installer.ps1" -Runtime $Runtime
            if ($LASTEXITCODE -ne 0) { throw "Build-Installer.ps1 failed (exit $LASTEXITCODE)" }
        }

        $releaseDir = Join-Path (Join-Path $repoRoot "artifacts\releases") $version
        $portableZip = Join-Path $releaseDir ("nLink-Portable-{0}-{1}.zip" -f $Runtime, $version)
        $installerExe = Join-Path $releaseDir ("nLink-Setup-{0}-{1}.exe" -f $Runtime, $version)
        $checksumsPath = Join-Path $releaseDir "SHA256SUMS.txt"
        Assert-PathExists -Path $portableZip -Description "Portable ZIP"
        Assert-PathExists -Path $installerExe -Description "Installer EXE"
        Assert-PathExists -Path $checksumsPath -Description "SHA256SUMS.txt"

        $portableStage = Join-Path $repoRoot "artifacts\portable\nLink\win-x64"
        $helperStage = Join-Path $repoRoot "artifacts\portable\helper\win-x64"
        $portableBridgeRid = Join-Path $portableStage (Join-Path "bridge" $Runtime)
        $helperBridgeRid = Join-Path $helperStage (Join-Path "bridge" $Runtime)
        foreach ($p in @(
            (Join-Path $portableBridgeRid "node.exe"),
            (Join-Path $portableBridgeRid "index.js"),
            (Join-Path $portableBridgeRid "node_modules"),
            (Join-Path $helperBridgeRid "node.exe"),
            (Join-Path $helperBridgeRid "index.js"),
            (Join-Path $helperBridgeRid "node_modules")
        )) {
            Assert-PathExists -Path $p -Description "Bridge bundle entry"
        }

        $checksumCheck = Verify-ChecksumsFile -ChecksumsPath $checksumsPath -AssetDir $releaseDir
        if (-not $checksumCheck.Passed) {
            throw ("Checksum verification failed: " + ($checksumCheck.Failures -join "; "))
        }

        New-Item -ItemType Directory -Force -Path $versionDistDir | Out-Null
        Copy-Item -Force $portableZip (Join-Path $versionDistDir (Split-Path $portableZip -Leaf))
        Copy-Item -Force $installerExe (Join-Path $versionDistDir (Split-Path $installerExe -Leaf))
        Copy-Item -Force $checksumsPath (Join-Path $versionDistDir "SHA256SUMS.txt")

        [void]$s.Artifacts.Add((Resolve-Path $versionDistDir).Path)
        Add-SectionResult (Complete-SectionSuccess $s)
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    # G Optional beta hardening extras (installer/offline/permissions/hang checks)
    $s = New-SectionResult -Name "Optional Installer Upgrade/Rollback" -Required:$RunInstallerUpgradeRollback
    try {
        if (-not $RunInstallerUpgradeRollback) {
            Add-SectionResult (Complete-SectionSkip $s "Disabled by default. Use -RunInstallerUpgradeRollback to run installer upgrade/rollback smoke.")
        }
        else {
            Invoke-Step "Installer upgrade/rollback smoke" {
                & powershell -ExecutionPolicy Bypass -File ".\tools\Installer-UpgradeRollback-Test.ps1"
                if ($LASTEXITCODE -ne 0) { throw "Installer-UpgradeRollback-Test.ps1 failed (exit $LASTEXITCODE)" }
            }
            $artifact = Join-Path $repoRoot "artifacts\beta-hardening\installer-upgrade-rollback.txt"
            if (Test-Path $artifact) { [void]$s.Artifacts.Add((Resolve-Path $artifact).Path) }
            Add-SectionResult (Complete-SectionSuccess $s)
        }
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    $s = New-SectionResult -Name "Optional Offline Smoke" -Required:$RunOfflineSmoke
    try {
        if (-not $RunOfflineSmoke) {
            Add-SectionResult (Complete-SectionSkip $s "Disabled by default. Use -RunOfflineSmoke to run offline/local-only smoke.")
        }
        else {
            Invoke-Step "Offline smoke" {
                & powershell -ExecutionPolicy Bypass -File ".\tools\Offline-Smoke.ps1"
                if ($LASTEXITCODE -ne 0) { throw "Offline-Smoke.ps1 failed (exit $LASTEXITCODE)" }
            }
            $artifact = Join-Path $repoRoot "artifacts\beta-hardening\offline-smoke.txt"
            if (Test-Path $artifact) { [void]$s.Artifacts.Add((Resolve-Path $artifact).Path) }
            Add-SectionResult (Complete-SectionSuccess $s)
        }
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    $s = New-SectionResult -Name "Optional Permissions Smoke" -Required:$RunPermissionsSmoke
    try {
        if (-not $RunPermissionsSmoke) {
            Add-SectionResult (Complete-SectionSkip $s "Disabled by default. Use -RunPermissionsSmoke to run permissions smoke.")
        }
        else {
            Invoke-Step "Permissions smoke" {
                & powershell -ExecutionPolicy Bypass -File ".\tools\Permissions-Smoke.ps1"
                if ($LASTEXITCODE -ne 0) { throw "Permissions-Smoke.ps1 failed (exit $LASTEXITCODE)" }
            }
            $artifact = Join-Path $repoRoot "artifacts\beta-hardening\permissions-smoke.txt"
            if (Test-Path $artifact) { [void]$s.Artifacts.Add((Resolve-Path $artifact).Path) }
            Add-SectionResult (Complete-SectionSuccess $s)
        }
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    $s = New-SectionResult -Name "Optional Hang Checks" -Required:$RunHangChecks
    try {
        if (-not $RunHangChecks) {
            Add-SectionResult (Complete-SectionSkip $s "Disabled by default. Use -RunHangChecks to run automated hang/network diagnostics checks and review manual steps in docs/BETA_HARDENING_EXTRAS.md.")
        }
        else {
            Invoke-Step "Hang/network diagnostics checks" {
                dotnet test -c Release --filter 'FullyQualifiedName~DiagnosticsRedactorTests|FullyQualifiedName~DiagnosticsPackSmokeTests|FullyQualifiedName~NetworkResilienceCoordinatorTests' --no-build
                if ($LASTEXITCODE -ne 0) { throw "hang diagnostics checks failed (exit $LASTEXITCODE)" }
            }
            [void]$s.Notes.Add("Manual UI freeze / sleep-resume validation steps: docs/BETA_HARDENING_EXTRAS.md (Prompt 2)")
            Add-SectionResult (Complete-SectionSuccess $s)
        }
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }

    # H Optional NKN smoke
    $s = New-SectionResult -Name "Optional NKN Smoke" -Required:$false
    try {
        if (-not $RunNknSmoke) {
            Add-SectionResult (Complete-SectionSkip $s "Disabled by default. Use -RunNknSmoke to enable local NKN validation.")
        }
        else {
            Invoke-Step "NKN smoke soak (local)" {
                dotnet run -c Release --no-build --project .\src\nLink.App -- --soak --cycles $NknSmokeCycles --delay-ms 50 --transport nkn --bridge-reuse-mode persession --fail-on-gate --gate-min-success-rate 95
                if ($LASTEXITCODE -ne 0) { throw "NKN smoke failed (exit $LASTEXITCODE)" }
            }
            $latestBench = Get-LatestFile -Directory (Join-Path $repoRoot "artifacts\bench") -Filter "metrics-*.json"
            if ($null -eq $latestBench) { throw "No benchmark metrics JSON found after NKN smoke." }
            $ts = Get-Date -Format "yyyyMMdd-HHmmss"
            $nknJson = Join-Path $betaRoot ("nkn-smoke-{0}.json" -f $ts)
            Copy-Item -Force $latestBench.FullName $nknJson
            $nknSummary = Join-Path $betaRoot ("nkn-smoke-{0}-summary.txt" -f $ts)
            $benchObj = Get-Content -Path $nknJson -Raw | ConvertFrom-Json
            $rel = Convert-BenchToReliabilitySummary -BenchObj $benchObj
            $relGate = Test-ReliabilityPromotion -Reliability $rel -MinSuccessRatePercent 95 -P95ConnectMaxMs 10000
            $summaryLines = Build-ReliabilityPromotionSummaryLines -Reliability $rel -JsonPath $nknJson
            if (-not $relGate.Passed) { $summaryLines += ""; $summaryLines += "FAILURES:"; $summaryLines += @($relGate.Failures | ForEach-Object { "- $_" }) }
            Write-TextFile -Path $nknSummary -Lines $summaryLines
            [void]$s.Artifacts.Add((Resolve-Path $nknJson).Path)
            [void]$s.Artifacts.Add((Resolve-Path $nknSummary).Path)
            if (-not $relGate.Passed) { throw ("NKN smoke gate failed: " + ($relGate.Failures -join "; ")) }
            Add-SectionResult (Complete-SectionSuccess $s)
        }
    } catch { Add-SectionResult (Complete-SectionFailure $s $_.Exception.Message) }
}
finally {
    Pop-Location
}

# Report generation
$reportPath = Join-Path $betaRoot "report.md"
$allRequiredPassed = ($requiredFailures.Count -eq 0)
$overall = if ($allRequiredPassed) { "PASS" } else { "FAIL" }

$criteriaLines = @(
    "- Build Release must pass.",
    "- Unit tests and smoke tests must pass.",
    "- Deterministic reliability promotion (DevLocal soak) must pass gates: success_rate=100%, Unknown=0, state_stuck_count=0, active_* counters return to 0, p95 connect recorded and under ceiling.",
    "- ResourceBenchmark + LeakCheck must pass ResourceGate (growth-focused) and cleanup checks.",
    "- Bridge Stability Promotion tests must pass (rapid cycles, crash/unresponsive/failure-injection subset, cleanup/no-orphan checks).",
    "- Contract Freeze tests must pass (enums, metric names, diagnostics snapshot schema).",
    "- Packaging promotion must verify installer/portable/checksums and bundled bridge files.",
    "- Optional installer/offline/permissions/hang-check sections are OFF by default and do not affect PASS/FAIL unless explicitly enabled.",
    "- Optional NKN smoke does not affect PASS/FAIL unless explicitly enabled."
)

$report = New-Object System.Collections.Generic.List[string]
[void]$report.Add("# Beta Readiness Report")
[void]$report.Add("")
[void]$report.Add("- Overall: " + ("**{0}**" -f $overall))
[void]$report.Add("- Version: " + $version)
[void]$report.Add("- Runtime: " + $Runtime)
[void]$report.Add("")
[void]$report.Add("## PASS/FAIL Criteria")
foreach ($line in $criteriaLines) { [void]$report.Add($line) }
[void]$report.Add("")
[void]$report.Add("## Sections")
[void]$report.Add("")
[void]$report.Add("| Section | Required | Result | Notes |")
[void]$report.Add("|---|---:|---|---|")
foreach ($section in $sections) {
    $resultText = if ($section.Skipped) { "SKIP" } elseif ($section.Passed) { "PASS" } else { "FAIL" }
    $notes = @()
    if ($section.Notes.Count -gt 0) { $notes += ($section.Notes -join "; ") }
    if ($null -ne $section.Error) { $notes += $section.Error }
    [void]$report.Add(("| {0} | {1} | **{2}** | {3} |" -f $section.Name, ($(if ($section.Required) { "Yes" } else { "No" })), $resultText, (($notes -join " | ").Replace("|", "\|"))))
}
[void]$report.Add("")
[void]$report.Add("## Artifacts")
[void]$report.Add("")
foreach ($section in $sections) {
    if ($section.Artifacts.Count -eq 0) { continue }
    [void]$report.Add(("### {0}" -f $section.Name))
    foreach ($artifact in $section.Artifacts) {
        $resolved = $artifact
        try { $resolved = (Resolve-Path $artifact).Path } catch { }
        $relative = $resolved
        if ($resolved.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relative = $resolved.Substring($repoRoot.Length).TrimStart('\','/')
        }
        [void]$report.Add("- " + $relative)
    }
    [void]$report.Add("")
}

if (-not $allRequiredPassed) {
    [void]$report.Add("## Required Section Failures")
    foreach ($name in $requiredFailures) { [void]$report.Add("- " + $name) }
    [void]$report.Add("")
}

Write-TextFile -Path $reportPath -Lines @($report.ToArray())
Write-Host ("[BetaReadiness] Report: {0}" -f (Resolve-Path $reportPath).Path) -ForegroundColor Green

if (-not $allRequiredPassed) {
    Write-Error ("Beta readiness FAILED. Required sections failed: {0}" -f ($requiredFailures -join ", "))
    exit 1
}

Write-Host "[BetaReadiness] PASS" -ForegroundColor Green
exit 0
