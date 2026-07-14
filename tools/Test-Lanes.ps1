param(
    [Parameter(Mandatory = $true)]
    [string[]]$Lane,

    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [string[]]$Logger = @(),
    [string]$GuiScenarios = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$validLanes = @(
    "Core",
    "Gui",
    "ScreenShare",
    "RemoteControl",
    "Contracts",
    "CoreContract",
    "FileTransferContract",
    "FileTransferCoordinatorContract",
    "FileTransferInfraContract",
    "FileTransferNknTransportContract",
    "FileTransferCoreRuntimeContract",
    "FileTransferAnalyzer",
    "FileTransferAnalyzerAuthority",
    "FileTransferAnalyzerRouteLaw",
    "FileTransferAnalyzerFallback",
    "FileTransferAnalyzerRuntimeUnlock",
    "FileTransferAnalyzerV4Transport",
    "FileTransferAcceptanceFake",
    "Smoke",
    "NonGui",
    "GuiSmoke",
    "ContractFreeze",
    "BridgeStabilityPromotion",
    "TrackBRetained",
    "All"
)

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

function Get-DomainProjectPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ProjectName
    )

    return Join-Path $RepoRoot "tests\$ProjectName\$ProjectName.csproj"
}

function Invoke-DotnetTest {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [string]$Filter = "",
        [string]$ResultSuffix = "",
        [switch]$EnableGuiSmoke
    )

    $argsList = @("test", $Target, "-c", $Configuration)
    if ($NoBuild) {
        $argsList += "--no-build"
    }
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $argsList += @("--filter", $Filter)
    }
    foreach ($loggerValue in $Logger) {
        if (-not [string]::IsNullOrWhiteSpace($loggerValue)) {
            $effectiveLoggerValue = $loggerValue
            if (-not [string]::IsNullOrWhiteSpace($ResultSuffix)) {
                $effectiveLoggerValue = [regex]::Replace(
                    $loggerValue,
                    "LogFilePrefix=([^;]+)",
                    { param($match) "LogFilePrefix=$($match.Groups[1].Value)-$ResultSuffix" })
            }
            $argsList += @("--logger", $effectiveLoggerValue)
        }
    }

    $oldGuiSmoke = $env:NLINK_RUN_GUI_SMOKE
    $oldGuiScenarios = $env:NLINK_GUI_SMOKE_SCENARIOS
    try {
        if ($EnableGuiSmoke) {
            $env:NLINK_RUN_GUI_SMOKE = "1"
            if (-not [string]::IsNullOrWhiteSpace($GuiScenarios)) {
                $env:NLINK_GUI_SMOKE_SCENARIOS = $GuiScenarios
            }
        }

        Write-Host ("[TestLanes] dotnet {0}" -f ($argsList -join " ")) -ForegroundColor Cyan
        & dotnet @argsList
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet test failed for target '$Target' with exit code $LASTEXITCODE."
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

function Invoke-DomainProjectsWithFilter {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Filter,
        [string[]]$ProjectNames = @(
            "nLink.SmokeTests.Core",
            "nLink.SmokeTests.Gui",
            "nLink.SmokeTests.ScreenShare",
            "nLink.SmokeTests.RemoteControl",
            "nLink.SmokeTests.Contracts"
        ),
        [switch]$EnableGuiSmoke
    )

    foreach ($projectName in $ProjectNames) {
        Invoke-DotnetTest `
            -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName $projectName) `
            -Filter $Filter `
            -EnableGuiSmoke:$EnableGuiSmoke
    }
}

function Invoke-TestLane {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Name
    )

    switch ($Name) {
        "Core" {
            Invoke-DotnetTest -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core")
        }
        "Gui" {
            Invoke-DotnetTest -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Gui")
        }
        "ScreenShare" {
            $screenShareProject = Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.ScreenShare"
            Invoke-DotnetTest `
                -Target $screenShareProject `
                -Filter "FullyQualifiedName!~ScreenSharePreviewIntegrationTests"
            foreach ($previewTest in @(
                "ScreenSharePreviewIntegrationTests.HelpeePreview_ToggleOnOff_Repeatedly_DoesNotCrash_AndCleansUp",
                "ScreenSharePreviewIntegrationTests.HelpeePreview_ScreenSharePreviewFrame_Progresses_UnderRapidFrames_AndSlowDecode",
                "ScreenSharePreviewIntegrationTests.HelpeePreview_ScreenSharePreviewFrame_AppliesLatestFrame_WhenDecodeSlowerThanArrival",
                "ScreenSharePreviewIntegrationTests.HelpeePreview_Stop_PreventsFurtherPreviewApplies"
            )) {
                Invoke-DotnetTest `
                    -Target $screenShareProject `
                    -Filter "FullyQualifiedName~$previewTest"
            }
        }
        "RemoteControl" {
            Invoke-DotnetTest -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.RemoteControl")
        }
        "Contracts" {
            Invoke-DotnetTest -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Contracts")
        }
        "CoreContract" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "Category=Smoke&FullyQualifiedName!~NknAccelerationTransportTests&FullyQualifiedName!~NknFileTransferTransportTests&FullyQualifiedName!~SessionFileTransferRuntimeTests&OpsSuite!=ScriptSurface&OpsSuite!=GuiHarness&OpsSuite!=RetainedAuthorityProof&OpsSuite!=RetainedRouteLaw&OpsSuite!=RetainedFallback&OpsSuite!=RetainedRuntimeUnlock&OpsSuite!=RetainedV4Transport&OpsSuite!=AcceptancePhase4&OpsSuite!=AcceptancePhase5Fast&OpsSuite!=AcceptancePhase5Full&OpsSuite!=NknSoakAndBaseline&FullyQualifiedName!~FileTransferSoakRunnerTests"
        }
        "FileTransferContract" {
            foreach ($contractLane in @(
                "FileTransferCoordinatorContract",
                "FileTransferInfraContract",
                "FileTransferNknTransportContract",
                "FileTransferCoreRuntimeContract"
            )) {
                Invoke-TestLane -RepoRoot $RepoRoot -Name $contractLane
            }
        }
        "FileTransferCoordinatorContract" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "FullyQualifiedName~FileTransferCoordinatorTests" `
                -ResultSuffix "coordinator"
        }
        "FileTransferInfraContract" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "FullyQualifiedName~NknAccelerationTransportTests" `
                -ResultSuffix "infra"
        }
        "FileTransferNknTransportContract" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "FullyQualifiedName~NknFileTransferTransportTests" `
                -ResultSuffix "nkn-transport"
        }
        "FileTransferCoreRuntimeContract" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "FullyQualifiedName~SessionFileTransferRuntimeUnlockRetryAuthorityTests|FullyQualifiedName~SessionFileTransferRuntimeTests|FileTransferRuntimeSuite=SparseWorkerHandoff|FileTransferRuntimeSuite=FallbackTailChurn" `
                -ResultSuffix "core-runtime"
        }
        "FileTransferAnalyzer" {
            foreach ($analyzerLane in @(
                "FileTransferAnalyzerAuthority",
                "FileTransferAnalyzerRouteLaw",
                "FileTransferAnalyzerFallback",
                "FileTransferAnalyzerRuntimeUnlock",
                "FileTransferAnalyzerV4Transport"
            )) {
                Invoke-TestLane -RepoRoot $RepoRoot -Name $analyzerLane
            }
        }
        "FileTransferAnalyzerAuthority" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "OpsSuite=RetainedAuthorityProof" `
                -ResultSuffix "authority"
        }
        "FileTransferAnalyzerRouteLaw" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "OpsSuite=RetainedRouteLaw" `
                -ResultSuffix "route-law"
        }
        "FileTransferAnalyzerFallback" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "OpsSuite=RetainedFallback" `
                -ResultSuffix "fallback"
        }
        "FileTransferAnalyzerRuntimeUnlock" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "OpsSuite=RetainedRuntimeUnlock" `
                -ResultSuffix "runtime-unlock"
        }
        "FileTransferAnalyzerV4Transport" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "OpsSuite=RetainedV4Transport" `
                -ResultSuffix "v4-transport"
        }
        "FileTransferAcceptanceFake" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "OpsSuite=ScriptSurface|OpsSuite=GuiHarness|OpsSuite=AcceptancePhase4|OpsSuite=AcceptancePhase5Fast|OpsSuite=AcceptancePhase5Full|OpsSuite=NknSoakAndBaseline|FullyQualifiedName~FileTransferSoakRunnerTests"
        }
        "Smoke" {
            Invoke-DomainProjectsWithFilter `
                -RepoRoot $RepoRoot `
                -Filter "Category=Smoke" `
                -ProjectNames @(
                    "nLink.SmokeTests.Gui",
                    "nLink.SmokeTests.ScreenShare",
                    "nLink.SmokeTests.RemoteControl",
                    "nLink.SmokeTests.Contracts"
                )
        }
        "NonGui" {
            foreach ($domainLane in @("Core", "ScreenShare", "RemoteControl", "Contracts")) {
                Invoke-TestLane -RepoRoot $RepoRoot -Name $domainLane
            }
        }
        "GuiSmoke" {
            Invoke-DomainProjectsWithFilter `
                -RepoRoot $RepoRoot `
                -Filter "Category=GuiSmoke" `
                -ProjectNames @("nLink.SmokeTests.Gui") `
                -EnableGuiSmoke
        }
        "ContractFreeze" {
            Invoke-DomainProjectsWithFilter `
                -RepoRoot $RepoRoot `
                -Filter "Category=ContractFreeze" `
                -ProjectNames @("nLink.SmokeTests.Contracts")
        }
        "BridgeStabilityPromotion" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.Core") `
                -Filter "Category=BridgeStabilityPromotion"
        }
        "TrackBRetained" {
            Invoke-DotnetTest `
                -Target (Get-DomainProjectPath -RepoRoot $RepoRoot -ProjectName "nLink.SmokeTests.ScreenShare") `
                -Filter "FullyQualifiedName~ScreenShareRetainedAnalyzerScriptsTests|FullyQualifiedName~ScreenShareExternalTransportHealthAnalysisTests|FullyQualifiedName~ScreenShareExternalDeliveryAnalysisTests|FullyQualifiedName~ScreenShareHelperSocketReceiveAnalysisTests|FullyQualifiedName~JsonlSmokeTests|FullyQualifiedName~RealNknClientAdapterReceivePathTests"
        }
        "All" {
            foreach ($domainLane in @("Core", "Gui", "ScreenShare", "RemoteControl", "Contracts")) {
                Invoke-TestLane -RepoRoot $RepoRoot -Name $domainLane
            }
        }
        default {
            throw "Unsupported lane: $Name"
        }
    }
}

$repoRoot = Resolve-RepoRoot
$resolvedLanes = @(
    foreach ($laneValue in $Lane) {
        foreach ($part in ([string]$laneValue).Split(',', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $part.Trim()
        }
    }
)
if ($resolvedLanes.Count -eq 0) {
    throw "At least one lane is required."
}

foreach ($laneName in $resolvedLanes) {
    if ($validLanes -notcontains $laneName) {
        throw "Unsupported lane '$laneName'. Valid lanes: $($validLanes -join ', ')."
    }
}

Push-Location $repoRoot
try {
    foreach ($laneName in $resolvedLanes) {
        Write-Host ("[TestLanes] Lane: {0}" -f $laneName) -ForegroundColor Green
        Invoke-TestLane -RepoRoot $repoRoot -Name $laneName
    }
}
finally {
    Pop-Location
}
