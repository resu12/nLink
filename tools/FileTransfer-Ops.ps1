param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("AnalyzeRetained", "LocalFast", "LocalImpaired", "LocalMixed", "NknFast", "NknMixed", "SupportCapture", "Test")]
    [string]$Mode,

    [ValidateSet("Default", "PinnedMainnetRpc", "PinnedSeedHttps", "MediaFanout8", "MediaFanout12", "BulkSingle1", "BulkFanout8", "BulkFanout12", "DefaultKeepAlive")]
    [string]$ExternalTopologyProfile = "Default",
    [string]$LogDir = "",
    [string[]]$LogPath = @(),
    [string]$ArtifactDir = "",
    [string]$TransferId = "",
    [int]$TailMinutes = 0,
    [switch]$IncludeRawSlices,
    [switch]$FailOnGate,
    [string]$Configuration = "Debug",
    [string]$PayloadSizes = "",
    [ValidateSet("Auto", "Current", "Packed3x20KiB", "Packed3x21KiB", "LargeSingle48KiB")]
    [string]$PayloadEfficiencyProfile = "Auto",
    [int]$Cycles = 0,
    [int]$Seed = 1313625684,
    [ValidateSet("alternate", "helper-to-helpee", "helpee-to-helper")]
    [string]$Direction = "alternate",
    [ValidateSet("None", "DelayJitter", "ReorderBurst", "LossBurst", "ScreenSharePressure")]
    [string]$ImpairmentProfile = "",
    [int]$CycleTimeoutSeconds = 120,
    [int]$ProgressTimeoutSeconds = 120,
    [switch]$NoBuild,
    [string]$ExePath = "",
    [switch]$Build,
    [int]$TimeoutSeconds = 600,
    [string]$SafeBaselineArtifactDir = "",
    [string]$StrongBaselineArtifactDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$boundScriptParameters = @{} + $PSBoundParameters

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

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Host ("[FileTransferOps] {0} {1}" -f $Command, ($Arguments -join " ")) -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Command @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
    if ($null -ne $LASTEXITCODE) {
        return [int]$LASTEXITCODE
    }

    if ($?) {
        return 0
    }

    return 1
}

function Invoke-PowerShellScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [System.Collections.IDictionary]$Parameters = ([ordered]@{})
    )

    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw "Required script not found: $ScriptPath"
    }

    $displayArguments = New-Object System.Collections.Generic.List[string]
    $scriptArguments = New-Object System.Collections.Generic.List[string]
    $scriptArguments.Add("-NoProfile") | Out-Null
    $scriptArguments.Add("-ExecutionPolicy") | Out-Null
    $scriptArguments.Add("Bypass") | Out-Null
    $scriptArguments.Add("-File") | Out-Null
    $scriptArguments.Add($ScriptPath) | Out-Null

    foreach ($key in $Parameters.Keys) {
        $value = $Parameters[$key]
        if ($value -is [bool]) {
            if ($value) {
                $displayArguments.Add("-$key") | Out-Null
                $scriptArguments.Add("-$key") | Out-Null
            }
            continue
        }

        if ($value -is [array]) {
            foreach ($item in $value) {
                $displayArguments.Add("-$key") | Out-Null
                $displayArguments.Add([string]$item) | Out-Null
                $scriptArguments.Add("-$key") | Out-Null
                $scriptArguments.Add([string]$item) | Out-Null
            }
            continue
        }

        $displayArguments.Add("-$key") | Out-Null
        $displayArguments.Add([string]$value) | Out-Null
        $scriptArguments.Add("-$key") | Out-Null
        $scriptArguments.Add([string]$value) | Out-Null
    }

    Write-Host ("[FileTransferOps] powershell -ExecutionPolicy Bypass -File {0} {1}" -f $ScriptPath, ($displayArguments -join " ")) -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & powershell @scriptArguments | ForEach-Object { Write-Host $_ }
    return $LASTEXITCODE
}

function Assert-ParameterMode {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$AllowedModes
    )

    if ($script:boundScriptParameters.ContainsKey($Name) -and $AllowedModes -notcontains $Mode) {
        throw ("Parameter -{0} is only supported for mode(s): {1}." -f $Name, ($AllowedModes -join ", "))
    }
}

function Test-UnsafeMixedPayloadEfficiencyProfileAllowed {
    $value = [System.Environment]::GetEnvironmentVariable("NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE")
    return -not [string]::IsNullOrWhiteSpace($value) -and $value -match '^(1|true|yes|on)$'
}

function Assert-PayloadEfficiencyProfileIsSafeForMode {
    if ($Mode -eq "NknMixed" -and
        $PayloadEfficiencyProfile -ne "Auto" -and
        $PayloadEfficiencyProfile -ne "Current" -and
        -not (Test-UnsafeMixedPayloadEfficiencyProfileAllowed)) {
        throw ("Payload efficiency profile '{0}' is not supported for NknMixed by default. Public NKN bridge-only probes reproduced receive stalls when screen-share-sized media was mixed with near-budget bulk payloads. Use NknFast or local modes for candidate profiles, or set NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE=1 only for controlled stall reproduction." -f $PayloadEfficiencyProfile)
    }
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

function Write-FileTransferSupportCaptureInstructions {
    Write-Host "File-transfer support/debug capture" -ForegroundColor Green
    Write-Host ""
    Write-Host "1. In the app, open Diagnostics and copy diagnostics."
    Write-Host "2. Keep retained logs from %LOCALAPPDATA%\nLink\logs."
    Write-Host "3. Run AnalyzeRetained to create the file-transfer artifact directory."
    Write-Host "4. Read filetransfer-operator-verdict.txt first."
    Write-Host "5. Attach the full artifact directory when support asks for raw retained analyzer output."
    Write-Host "6. Local soak artifacts also include filetransfer-impairment-summary.txt and mixed-screenshare-summary.txt."
    Write-Host "7. Live NKN soak artifacts also include filetransfer-live-nkn-summary.txt, filetransfer-live-nkn-summary.json, filetransfer-live-nkn-cycles.jsonl, and baseline-comparison.txt."
    Write-Host "8. For V4 file-only runs, keep protocol-shape-summary.txt, payload-efficiency-summary.txt, bridge-bulk-summary.txt, throughput-decomposition-summary.txt, baseline-comparison.txt, and v4-promotion-decision.txt."
    Write-Host "9. For receive stalls, keep external-transport-health-summary.txt and throughput-decomposition-summary.txt."
    Write-Host "10. Optional receive-stall matrix: powershell -ExecutionPolicy Bypass -File .\tools\Run-FileTransferReceiveStallMatrix.ps1"
    Write-Host "11. Optional bridge-only probe: powershell -ExecutionPolicy Bypass -File .\tools\Run-NknBridgeReceiveProbe.ps1"
    Write-Host "12. Record the packaged app version, selected ExternalTopologyProfile, data_protocol_version, and any safe/strong baseline artifact directory used."
    Write-Host ""
        Write-Host "Example:"
        Write-Host "powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode AnalyzeRetained -IncludeRawSlices"
        Write-Host "powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast"
        Write-Host "powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalImpaired -PayloadSizes 64KiB -Cycles 1"
        Write-Host "powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalMixed -PayloadSizes 64KiB -Cycles 1"
        Write-Host "powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -Build -PayloadSizes 1MiB -Cycles 1"
        Write-Host "powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknMixed -PayloadSizes 1MiB -Cycles 1"
}

$repoRoot = Resolve-RepoRoot

Assert-ParameterMode -Name "ImpairmentProfile" -AllowedModes @("LocalImpaired", "LocalMixed")
Assert-ParameterMode -Name "NoBuild" -AllowedModes @("LocalFast", "LocalImpaired", "LocalMixed")
Assert-ParameterMode -Name "ExePath" -AllowedModes @("NknFast", "NknMixed")
Assert-ParameterMode -Name "Build" -AllowedModes @("NknFast", "NknMixed")
Assert-ParameterMode -Name "TimeoutSeconds" -AllowedModes @("NknFast", "NknMixed")
Assert-ParameterMode -Name "ProgressTimeoutSeconds" -AllowedModes @("NknFast", "NknMixed")
Assert-ParameterMode -Name "ExternalTopologyProfile" -AllowedModes @("NknFast", "NknMixed")
Assert-ParameterMode -Name "PayloadEfficiencyProfile" -AllowedModes @("LocalFast", "LocalImpaired", "LocalMixed", "NknFast", "NknMixed")
Assert-PayloadEfficiencyProfileIsSafeForMode

function Invoke-LocalFileTransferSoakMode {
    param(
        [Parameter(Mandatory = $true)][string]$AppMode,
        [Parameter(Mandatory = $true)][string]$DefaultImpairmentProfile
    )

    $resolvedArtifactDir = Resolve-FileTransferArtifactDir -RepoRoot $repoRoot -RequestedArtifactDir $ArtifactDir
    New-Item -ItemType Directory -Path $resolvedArtifactDir -Force | Out-Null

    if (-not $NoBuild) {
        $buildArguments = @(
            "build",
            (Join-Path $repoRoot "src\nLink.App\nLink.App.csproj"),
            "-c",
            $Configuration
        )
        $buildExit = Invoke-ExternalCommand -Command "dotnet" -Arguments $buildArguments
        if ($buildExit -ne 0) {
            exit $buildExit
        }
    }

    $runArguments = @(
        "run",
        "--project",
        (Join-Path $repoRoot "src\nLink.App\nLink.App.csproj"),
        "-c",
        $Configuration
    )
    if ($NoBuild) {
        $runArguments += "--no-build"
    }

    $effectiveImpairmentProfile = $ImpairmentProfile
    if ([string]::IsNullOrWhiteSpace($effectiveImpairmentProfile)) {
        $effectiveImpairmentProfile = $DefaultImpairmentProfile
    }
    $effectivePayloadEfficiencyProfile = if ($PayloadEfficiencyProfile -eq "Auto") { "Current" } else { $PayloadEfficiencyProfile }

    $runArguments += @(
        "--",
        "--filetransfer-soak",
        $AppMode,
        "--artifact-dir",
        $resolvedArtifactDir,
        "--seed",
        ([string]$Seed),
        "--direction",
        $Direction,
        "--cycle-timeout-seconds",
        ([string]$CycleTimeoutSeconds),
        "--impairment-profile",
        $effectiveImpairmentProfile,
        "--payload-efficiency-profile",
        $effectivePayloadEfficiencyProfile
    )

    if (-not [string]::IsNullOrWhiteSpace($PayloadSizes)) {
        $runArguments += @("--payload-sizes", $PayloadSizes)
    }

    if ($Cycles -gt 0) {
        $runArguments += @("--cycles", ([string]$Cycles))
    }

    if ($FailOnGate) {
        $runArguments += "--fail-on-gate"
    }

    $runExit = Invoke-ExternalCommand -Command "dotnet" -Arguments $runArguments

    . (Join-Path $repoRoot "tools\FileTransferOps\AnalyzerOrchestration.ps1")
    . (Join-Path $repoRoot "tools\FileTransferSoak\BaselineComparison.ps1")

    $gateVerdict = "INVALID_SETUP"
    $logSlicePath = Join-Path $resolvedArtifactDir "filetransfer-retained-log-slice.log"
    if (Test-Path -LiteralPath $logSlicePath -PathType Leaf) {
        $analysis = Invoke-FileTransferRetainedAnalysis `
            -RepoRoot $repoRoot `
            -LogPath @($logSlicePath) `
            -ArtifactDir $resolvedArtifactDir `
            -TailMinutes 0 `
            -IncludeRawSlices:$IncludeRawSlices `
            -AllTransfers
        $gateVerdict = $analysis.GateResult.Verdict
    }
    else {
        Write-Warning "$AppMode did not produce filetransfer-retained-log-slice.log; retained analyzer was skipped."
    }

    $baseline = Write-FileTransferBaselineComparison `
        -ArtifactDir $resolvedArtifactDir `
        -SafeBaselineArtifactDir $SafeBaselineArtifactDir `
        -StrongBaselineArtifactDir $StrongBaselineArtifactDir

    if ($baseline.RegressionFailed) {
        Set-FileTransferRegressionVerdict `
            -ArtifactDir $resolvedArtifactDir `
            -RegressionFindings $baseline.RegressionFindings
    }

    Write-Host ("[FileTransferOps] artifact_dir={0}" -f $resolvedArtifactDir) -ForegroundColor Green
    Write-Host ("[FileTransferOps] first_read=filetransfer-operator-verdict.txt") -ForegroundColor Green

    if ($runExit -ne 0) {
        exit $runExit
    }

    if ($FailOnGate -and ($gateVerdict -eq "FAIL_PROTOCOL_OR_INTEGRITY" -or $gateVerdict -like "INCONCLUSIVE*" -or $gateVerdict -eq "INVALID_SETUP" -or $baseline.RegressionFailed)) {
        exit 1
    }

    exit 0
}

function Invoke-LiveFileTransferNknSoakMode {
    param([Parameter(Mandatory = $true)][string]$LiveMode)

    $resolvedArtifactDir = Resolve-FileTransferArtifactDir -RepoRoot $repoRoot -RequestedArtifactDir $ArtifactDir
    New-Item -ItemType Directory -Path $resolvedArtifactDir -Force | Out-Null
    $effectiveCycleTimeoutSeconds = $CycleTimeoutSeconds
    if (-not $script:boundScriptParameters.ContainsKey("CycleTimeoutSeconds")) {
        $effectiveCycleTimeoutSeconds = 600
    }

    $scriptPath = Join-Path $repoRoot "tools\Run-FileTransferNknSoak.ps1"
    $parameters = [ordered]@{
        Mode = $LiveMode
        ArtifactDir = $resolvedArtifactDir
        Seed = $Seed
        Direction = $Direction
        CycleTimeoutSeconds = $effectiveCycleTimeoutSeconds
        ProgressTimeoutSeconds = $ProgressTimeoutSeconds
        TimeoutSeconds = $TimeoutSeconds
        ExternalTopologyProfile = $ExternalTopologyProfile
        PayloadEfficiencyProfile = $PayloadEfficiencyProfile
        IncludeRawSlices = $IncludeRawSlices.IsPresent
        FailOnGate = $FailOnGate.IsPresent
        Build = $Build.IsPresent
    }

    if (-not [string]::IsNullOrWhiteSpace($SafeBaselineArtifactDir)) {
        $parameters["SafeBaselineArtifactDir"] = $SafeBaselineArtifactDir
    }

    if (-not [string]::IsNullOrWhiteSpace($StrongBaselineArtifactDir)) {
        $parameters["StrongBaselineArtifactDir"] = $StrongBaselineArtifactDir
    }

    if (-not [string]::IsNullOrWhiteSpace($ExePath)) {
        $parameters["ExePath"] = $ExePath
    }

    if (-not [string]::IsNullOrWhiteSpace($PayloadSizes)) {
        $parameters["PayloadSizes"] = $PayloadSizes
    }

    if ($Cycles -gt 0) {
        $parameters["Cycles"] = $Cycles
    }

    $exitCode = Invoke-PowerShellScript -ScriptPath $scriptPath -Parameters $parameters
    exit $exitCode
}

switch ($Mode) {
    "SupportCapture" {
        Write-FileTransferSupportCaptureInstructions
        exit 0
    }

    "Test" {
        $project = Join-Path $repoRoot "tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj"
        $arguments = @(
            "test",
            $project,
            "-c",
            $Configuration,
            "--filter",
            "FullyQualifiedName~FileTransferOpsScriptsTests|FullyQualifiedName~FileTransferSoakRunnerTests"
        )
        exit (Invoke-ExternalCommand -Command "dotnet" -Arguments $arguments)
    }

    "LocalImpaired" {
        Invoke-LocalFileTransferSoakMode -AppMode "local-impaired" -DefaultImpairmentProfile "ReorderBurst"
    }

    "LocalMixed" {
        Invoke-LocalFileTransferSoakMode -AppMode "local-mixed" -DefaultImpairmentProfile "ScreenSharePressure"
    }

    "NknFast" {
        Invoke-LiveFileTransferNknSoakMode -LiveMode "nkn-fast"
    }

    "NknMixed" {
        Invoke-LiveFileTransferNknSoakMode -LiveMode "nkn-mixed"
    }

    "LocalFast" {
        $resolvedArtifactDir = Resolve-FileTransferArtifactDir -RepoRoot $repoRoot -RequestedArtifactDir $ArtifactDir
        New-Item -ItemType Directory -Path $resolvedArtifactDir -Force | Out-Null

        if (-not $NoBuild) {
            $buildArguments = @(
                "build",
                (Join-Path $repoRoot "src\nLink.App\nLink.App.csproj"),
                "-c",
                $Configuration
            )
            $buildExit = Invoke-ExternalCommand -Command "dotnet" -Arguments $buildArguments
            if ($buildExit -ne 0) {
                exit $buildExit
            }
        }

        $runArguments = @(
            "run",
            "--project",
            (Join-Path $repoRoot "src\nLink.App\nLink.App.csproj"),
            "-c",
            $Configuration
        )
        if ($NoBuild) {
            $runArguments += "--no-build"
        }

        $effectivePayloadEfficiencyProfile = if ($PayloadEfficiencyProfile -eq "Auto") { "Current" } else { $PayloadEfficiencyProfile }
        $runArguments += @(
            "--",
            "--filetransfer-soak",
            "local-fast",
            "--artifact-dir",
            $resolvedArtifactDir,
            "--seed",
            ([string]$Seed),
            "--direction",
            $Direction,
            "--cycle-timeout-seconds",
            ([string]$CycleTimeoutSeconds),
            "--payload-efficiency-profile",
            $effectivePayloadEfficiencyProfile
        )

        if (-not [string]::IsNullOrWhiteSpace($PayloadSizes)) {
            $runArguments += @("--payload-sizes", $PayloadSizes)
        }

        if ($Cycles -gt 0) {
            $runArguments += @("--cycles", ([string]$Cycles))
        }

        if ($FailOnGate) {
            $runArguments += "--fail-on-gate"
        }

        $runExit = Invoke-ExternalCommand -Command "dotnet" -Arguments $runArguments

        . (Join-Path $repoRoot "tools\FileTransferOps\AnalyzerOrchestration.ps1")
        . (Join-Path $repoRoot "tools\FileTransferSoak\BaselineComparison.ps1")

        $gateVerdict = "INVALID_SETUP"
        $logSlicePath = Join-Path $resolvedArtifactDir "filetransfer-retained-log-slice.log"
        if (Test-Path -LiteralPath $logSlicePath -PathType Leaf) {
            $analysis = Invoke-FileTransferRetainedAnalysis `
                -RepoRoot $repoRoot `
                -LogPath @($logSlicePath) `
                -ArtifactDir $resolvedArtifactDir `
                -TailMinutes 0 `
                -IncludeRawSlices:$IncludeRawSlices `
                -AllTransfers
            $gateVerdict = $analysis.GateResult.Verdict
        }
        else {
            Write-Warning "LocalFast did not produce filetransfer-retained-log-slice.log; retained analyzer was skipped."
        }

        $baseline = Write-FileTransferBaselineComparison `
            -ArtifactDir $resolvedArtifactDir `
            -SafeBaselineArtifactDir $SafeBaselineArtifactDir `
            -StrongBaselineArtifactDir $StrongBaselineArtifactDir

        if ($baseline.RegressionFailed) {
            Set-FileTransferRegressionVerdict `
                -ArtifactDir $resolvedArtifactDir `
                -RegressionFindings $baseline.RegressionFindings
        }

        Write-Host ("[FileTransferOps] artifact_dir={0}" -f $resolvedArtifactDir) -ForegroundColor Green
        Write-Host ("[FileTransferOps] first_read=filetransfer-operator-verdict.txt") -ForegroundColor Green

        if ($runExit -ne 0) {
            exit $runExit
        }

        if ($FailOnGate -and ($gateVerdict -eq "FAIL_PROTOCOL_OR_INTEGRITY" -or $gateVerdict -like "INCONCLUSIVE*" -or $gateVerdict -eq "INVALID_SETUP" -or $baseline.RegressionFailed)) {
            exit 1
        }

        exit 0
    }

    "AnalyzeRetained" {
        . (Join-Path $repoRoot "tools\FileTransferOps\AnalyzerOrchestration.ps1")
        $result = Invoke-FileTransferRetainedAnalysis `
            -RepoRoot $repoRoot `
            -LogDir $LogDir `
            -LogPath $LogPath `
            -ArtifactDir $ArtifactDir `
            -TransferId $TransferId `
            -TailMinutes $TailMinutes `
            -IncludeRawSlices:$IncludeRawSlices

        Write-Host ("[FileTransferOps] artifact_dir={0}" -f $result.ArtifactDir) -ForegroundColor Green
        Write-Host ("[FileTransferOps] verdict={0}" -f $result.GateResult.Verdict) -ForegroundColor Green
        Write-Host ("[FileTransferOps] first_read=filetransfer-operator-verdict.txt") -ForegroundColor Green

        if ($FailOnGate -and ($result.GateResult.Verdict -eq "FAIL_PROTOCOL_OR_INTEGRITY" -or $result.GateResult.Verdict -like "INCONCLUSIVE*" -or $result.GateResult.Verdict -eq "INVALID_SETUP")) {
            exit 1
        }

        exit 0
    }
}
