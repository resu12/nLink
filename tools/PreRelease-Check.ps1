param(
    [switch]$RunGuiSmoke,
    [string]$GuiScenarios = "A",
    [switch]$RunFormatCheck,
    [switch]$RunResources,
    [switch]$RunLeakCheck,
    [switch]$RunBetaReadiness,
    [string]$Runtime = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host "[PreRelease] $Name" -ForegroundColor Cyan
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

function Get-LatestBenchMetricsJsonPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $benchDir = Join-Path $RepoRoot "artifacts\bench"
    if (-not (Test-Path $benchDir)) {
        return $null
    }

    $latest = Get-ChildItem -Path $benchDir -File -Filter "metrics-*.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        return $null
    }

    return $latest.FullName
}

function Format-NullableNumber {
    param(
        [AllowNull()]$Value,
        [string]$Format = "0.0"
    )

    if ($null -eq $Value) {
        return "n/a"
    }

    try {
        return ([double]$Value).ToString($Format, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return "n/a"
    }
}

function Write-ReliabilitySummary {
    param(
        [Parameter(Mandatory = $true)][string]$BenchJsonPath,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$ReleaseDir,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $bench = Get-Content -Path $BenchJsonPath -Raw | ConvertFrom-Json
    $summary = $bench.Summary
    $gate = $bench.ReliabilityGate

    $topFailures = @()
    if ($null -ne $summary -and $null -ne $summary.TopFailureCategories) {
        foreach ($item in $summary.TopFailureCategories) {
            if ($null -eq $item) { continue }

            if ($item -is [string]) {
                if (-not [string]::IsNullOrWhiteSpace($item)) {
                    $topFailures += $item.Trim()
                }
                continue
            }

            $name = ""
            if ($null -ne $item.Category) { $name = [string]$item.Category }
            elseif ($null -ne $item.Name) { $name = [string]$item.Name }

            $countText = $null
            if ($null -ne $item.Count) {
                $countText = [string]$item.Count
            }
            elseif ($null -ne $item.Value) {
                $countText = [string]$item.Value
            }

            if (-not [string]::IsNullOrWhiteSpace($name)) {
                if ([string]::IsNullOrWhiteSpace($countText)) {
                    $topFailures += $name
                }
                else {
                    $topFailures += ("{0} ({1})" -f $name, $countText)
                }
            }
        }
    }

    if ($topFailures.Count -eq 0) {
        $topFailures = @("(none)")
    }

    $successRateText = Format-NullableNumber -Value $summary.SuccessRatePercent -Format "0.0"
    $avgConnectText = Format-NullableNumber -Value $summary.AvgConnectMs -Format "0.0"
    $p95ConnectText = Format-NullableNumber -Value $summary.P95ConnectMs -Format "0.0"
    $avgHandshakeText = Format-NullableNumber -Value $summary.AvgHandshakeMs -Format "0.0"
    $p95HandshakeText = Format-NullableNumber -Value $summary.P95HandshakeMs -Format "0.0"
    $warmStartRatioText = Format-NullableNumber -Value $summary.WarmStartRatio -Format "0.0"

    $bridgeCrashTotal = if ($null -ne $gate -and $null -ne $gate.BridgeCrashTotal) { [string]$gate.BridgeCrashTotal } else { "n/a" }
    $gatePass = if ($null -ne $gate -and $true -eq $gate.Passed) { "PASS" } elseif ($null -ne $gate) { "FAIL" } else { "n/a" }
    $cyclesText = if ($null -ne $summary) { "{0}/{1}" -f $summary.CyclesSucceeded, $summary.CyclesRequested } else { "n/a" }
    $transportText = if ($null -ne $bench.Options -and $null -ne $bench.Options.Transport) { [string]$bench.Options.Transport } else { "n/a" }
    $reuseModeText = if ($null -ne $bench.Options -and $null -ne $bench.Options.BridgeReuseMode) { [string]$bench.Options.BridgeReuseMode } else { "n/a" }

    $lines = @(
        "Reliability Summary (nLink pre-release)",
        ("Version: {0}" -f $Version),
        ("Runtime: {0}" -f $Runtime),
        ("Transport: {0}" -f $transportText),
        ("Bridge reuse mode: {0}" -f $reuseModeText),
        ("Cycles: {0}" -f $cyclesText),
        ("Reliability gate: {0}" -f $gatePass),
        "",
        ("Success rate: {0}%" -f $successRateText),
        ("Connect avg/p95 (ms): {0} / {1}" -f $avgConnectText, $p95ConnectText),
        ("Handshake avg/p95 (ms): {0} / {1}" -f $avgHandshakeText, $p95HandshakeText),
        ("Bridge crash count: {0}" -f $bridgeCrashTotal),
        ("Warm start ratio: {0}%" -f $warmStartRatioText),
        ("Top failure categories: {0}" -f ($topFailures -join ", ")),
        "",
        ("Benchmark metrics JSON: {0}" -f (Resolve-Path $BenchJsonPath).Path)
    )

    $releaseSummaryPath = Join-Path $ReleaseDir "reliability-summary.txt"
    Set-Content -Path $releaseSummaryPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8

    $latestSummaryDir = Join-Path $RepoRoot "artifacts\release"
    New-Item -ItemType Directory -Force -Path $latestSummaryDir | Out-Null
    $latestSummaryPath = Join-Path $latestSummaryDir "reliability-summary.txt"
    Set-Content -Path $latestSummaryPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8

    return [pscustomobject]@{
        ReleaseSummaryPath = $releaseSummaryPath
        LatestSummaryPath = $latestSummaryPath
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$versionPath = Join-Path $repoRoot "VERSION"
Assert-PathExists -Path $versionPath -Description "VERSION file"
$version = (Get-Content $versionPath -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "VERSION file is empty: $versionPath"
}

Write-Host "[PreRelease] Version: $version" -ForegroundColor Green

$guiSmokeArtifactRoot = Join-Path $repoRoot "artifacts\gui-smoke"
$guiSmokeArtifactDirs = @()
$reliabilityBenchJsonPath = $null
$resourceArtifacts = @()

Push-Location $repoRoot
try {
    Invoke-Step -Name "Smoke tests (Category=Smoke)" -Action {
        dotnet test -c Release --filter Category=Smoke
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    if ($RunFormatCheck) {
        Write-Host "[PreRelease] Optional format check (non-blocking)" -ForegroundColor Cyan
        try {
            dotnet format .\nLink.sln --verify-no-changes
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet format --verify-no-changes returned exit code $LASTEXITCODE"
            }
        }
        catch {
            Write-Warning "[PreRelease] Format check found changes needed (non-blocking). Run: dotnet format nLink.sln"
            Write-Warning ("[PreRelease] Format check detail: {0}" -f $_.Exception.Message)
        }
    }

    Invoke-Step -Name "Reliability gate benchmark (DevLocal, fast)" -Action {
        dotnet run -c Release --no-build --project .\src\nLink.App -- --bench --cycles 20 --delay-ms 0 --transport devlocal --bridge-reuse-mode persession --reliability-gate
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    $reliabilityBenchJsonPath = Get-LatestBenchMetricsJsonPath -RepoRoot $repoRoot
    if ($null -eq $reliabilityBenchJsonPath) {
        throw "Could not find benchmark metrics JSON under artifacts\\bench after reliability gate run."
    }

    if ($RunGuiSmoke) {
        $oldGuiSmoke = $env:NLINK_RUN_GUI_SMOKE
        $oldGuiScenarios = $env:NLINK_GUI_SMOKE_SCENARIOS
        try {
            $env:NLINK_RUN_GUI_SMOKE = "1"
            $env:NLINK_GUI_SMOKE_SCENARIOS = $GuiScenarios
            Invoke-Step -Name "Optional GUI smoke tests (Category=GuiSmoke)" -Action {
                dotnet test -c Release --filter Category=GuiSmoke
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            }

            if (Test-Path $guiSmokeArtifactRoot) {
                $guiSmokeArtifactDirs = @(
                    Get-ChildItem -Path $guiSmokeArtifactRoot -Directory -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTimeUtc -Descending
                )
                if ($guiSmokeArtifactDirs.Count -gt 0) {
                    Write-Host "[PreRelease] GUI smoke artifacts:" -ForegroundColor Green
                    foreach ($dir in $guiSmokeArtifactDirs) {
                        Write-Host ("  {0}" -f $dir.FullName)
                    }
                }
                else {
                    Write-Host "[PreRelease] GUI smoke artifacts: none created (no failures or no dumps)." -ForegroundColor DarkGray
                }
            }
        }
        finally {
            if ($null -eq $oldGuiSmoke) {
                Remove-Item Env:NLINK_RUN_GUI_SMOKE -ErrorAction SilentlyContinue
            }
            else {
                $env:NLINK_RUN_GUI_SMOKE = $oldGuiSmoke
            }

            if ($null -eq $oldGuiScenarios) {
                Remove-Item Env:NLINK_GUI_SMOKE_SCENARIOS -ErrorAction SilentlyContinue
            }
            else {
                $env:NLINK_GUI_SMOKE_SCENARIOS = $oldGuiScenarios
            }
        }
    }

    if ($RunResources) {
        Invoke-Step -Name "Resource benchmark + gate (DevLocal)" -Action {
            dotnet run -c Release --no-build --project .\src\nLink.App -- --resource-bench --transport devlocal --bridge-reuse-mode persession --sample-ms 1000 --idle-seconds 5 --connected-idle-seconds 5 --final-idle-seconds 5 --fail-on-gate
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }

    if ($RunLeakCheck) {
        Invoke-Step -Name "Leak check + gate (DevLocal)" -Action {
            dotnet run -c Release --no-build --project .\src\nLink.App -- --leak-check --cycles 50 --transport devlocal --bridge-reuse-mode persession --delay-ms 0 --fail-on-gate --leak-growth-fail-percent 200
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }

    if ($RunBetaReadiness) {
        Invoke-Step -Name "Beta readiness check" -Action {
            & powershell -ExecutionPolicy Bypass -File ".\tools\BetaReadiness-Check.ps1" -Runtime $Runtime
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }

    Invoke-Step -Name "Build bridge bundle" -Action {
        & powershell -ExecutionPolicy Bypass -File ".\installer\Build-BridgeBundle.ps1" -Runtime $Runtime
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Invoke-Step -Name "Build portable ZIP" -Action {
        & powershell -ExecutionPolicy Bypass -File ".\installer\Build-Portable.ps1" -Runtime $Runtime
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Invoke-Step -Name "Build installer" -Action {
        & powershell -ExecutionPolicy Bypass -File ".\installer\Build-Installer.ps1" -Runtime $Runtime
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}

$releaseDir = Join-Path (Join-Path $repoRoot "artifacts\releases") $version
$portableZip = Join-Path $releaseDir ("nLink-Portable-{0}-{1}.zip" -f $Runtime, $version)
$installerExe = Join-Path $releaseDir ("nLink-Setup-{0}-{1}.exe" -f $Runtime, $version)

Assert-PathExists -Path $releaseDir -Description "Release folder"
Assert-PathExists -Path $portableZip -Description "Portable ZIP"
Assert-PathExists -Path $installerExe -Description "Installer EXE"

$portableStage = Join-Path $repoRoot ("artifacts\portable\nLink\win-x64")
$helperStage = Join-Path $repoRoot ("artifacts\portable\helper\win-x64")

$portableBridgeRid = Join-Path $portableStage (Join-Path "bridge" $Runtime)
$helperBridgeRid = Join-Path $helperStage (Join-Path "bridge" $Runtime)

Assert-PathExists -Path (Join-Path $portableBridgeRid "index.js") -Description "Portable bridge index.js"
Assert-PathExists -Path (Join-Path $portableBridgeRid "node.exe") -Description "Portable bridge node.exe"
Assert-PathExists -Path (Join-Path $portableBridgeRid "node_modules") -Description "Portable bridge node_modules"

Assert-PathExists -Path (Join-Path $helperBridgeRid "index.js") -Description "Helper staging bridge index.js"
Assert-PathExists -Path (Join-Path $helperBridgeRid "node.exe") -Description "Helper staging bridge node.exe"
Assert-PathExists -Path (Join-Path $helperBridgeRid "node_modules") -Description "Helper staging bridge node_modules"

$portableZipAbs = (Resolve-Path $portableZip).Path
$installerExeAbs = (Resolve-Path $installerExe).Path
$releaseDirAbs = (Resolve-Path $releaseDir).Path
$portableBridgeRidAbs = (Resolve-Path $portableBridgeRid).Path
$helperBridgeRidAbs = (Resolve-Path $helperBridgeRid).Path
$reliabilitySummaryInfo = $null
if ($null -ne $reliabilityBenchJsonPath) {
    $reliabilitySummaryInfo = Write-ReliabilitySummary -BenchJsonPath $reliabilityBenchJsonPath -Version $version -Runtime $Runtime -ReleaseDir $releaseDir -RepoRoot $repoRoot
}

Write-Host "" 
Write-Host "[PreRelease] Final checklist summary" -ForegroundColor Green
Write-Host ("  Version: {0}" -f $version)
Write-Host ("  Runtime: {0}" -f $Runtime)
Write-Host ("  Smoke tests: PASS")
Write-Host ("  Format check: {0}" -f ($(if ($RunFormatCheck) { "NON-BLOCKING (see warnings above if drift detected)" } else { "SKIPPED" })))
Write-Host ("  GUI smoke: {0}" -f ($(if ($RunGuiSmoke) { "PASS (scenarios: $GuiScenarios)" } else { "SKIPPED" })))
Write-Host ("  Beta readiness: {0}" -f ($(if ($RunBetaReadiness) { "PASS" } else { "SKIPPED" })))
Write-Host ("  Bridge runtime verified in portable stage: {0}" -f $portableBridgeRidAbs)
Write-Host ("  Bridge runtime verified in helper stage: {0}" -f $helperBridgeRidAbs)
Write-Host ""
Write-Host "[PreRelease] Final upload assets:" -ForegroundColor Green
Write-Host ("  Portable ZIP: {0}" -f $portableZipAbs)
Write-Host ("  Installer EXE: {0}" -f $installerExeAbs)
Write-Host ("  Release folder: {0}" -f $releaseDirAbs)
if ($null -ne $reliabilitySummaryInfo) {
    Write-Host ("  Reliability summary (release): {0}" -f (Resolve-Path $reliabilitySummaryInfo.ReleaseSummaryPath).Path)
    Write-Host ("  Reliability summary (latest alias): {0}" -f (Resolve-Path $reliabilitySummaryInfo.LatestSummaryPath).Path)
}
if ($guiSmokeArtifactDirs.Count -gt 0) {
    Write-Host ("  GUI smoke artifacts root: {0}" -f (Resolve-Path $guiSmokeArtifactRoot).Path)
}
if ($RunResources -or $RunLeakCheck) {
    $resourcesDir = Join-Path $repoRoot "artifacts\resources"
    if (Test-Path $resourcesDir) {
        Write-Host ("  Resource artifacts: {0}" -f (Resolve-Path $resourcesDir).Path)
    }
}
if ($RunBetaReadiness) {
    $betaReport = Join-Path $repoRoot "artifacts\beta-readiness\report.md"
    if (Test-Path $betaReport) {
        Write-Host ("  Beta readiness report: {0}" -f (Resolve-Path $betaReport).Path)
    }
}
Write-Host ""
Write-Host "[PreRelease] READY TO RELEASE" -ForegroundColor Green
