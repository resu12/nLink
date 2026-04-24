param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "Test",
        "LocalSoak",
        "NknSoak",
        "AnalyzeRetained",
        "TrackBRetained",
        "SupportCapture"
    )]
    [string]$Mode,

    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [int]$DurationSeconds = 0,
    [int]$SampleIntervalSeconds = 5,
    [string]$ArtifactDir = "",
    [string]$ExePath = "",
    [switch]$Build,
    [int]$TimeoutSeconds = 0,
    [string]$StrongBaselineArtifactDir = "",
    [string]$SafeBaselineArtifactDir = "",
    [switch]$SkipBehaviorFirstGate,
    [string[]]$Logger = @()
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

function Assert-ParameterMode {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$AllowedModes
    )

    if ($script:boundScriptParameters.ContainsKey($Name) -and $AllowedModes -notcontains $Mode) {
        throw ("Parameter -{0} is only supported for mode(s): {1}." -f $Name, ($AllowedModes -join ", "))
    }
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Host ("[ScreenShareOps] {0} {1}" -f $Command, ($Arguments -join " ")) -ForegroundColor Cyan
    & $Command @Arguments
    return $LASTEXITCODE
}

function Invoke-PowerShellScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [System.Collections.IDictionary]$Parameters = ([ordered]@{})
    )

    if (-not (Test-Path -LiteralPath $ScriptPath)) {
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
                $displayArguments.Add("-$key")
                $scriptArguments.Add("-$key") | Out-Null
            }
            continue
        }

        if ($value -is [array]) {
            foreach ($item in $value) {
                $displayArguments.Add("-$key")
                $displayArguments.Add([string]$item)
                $scriptArguments.Add("-$key") | Out-Null
                $scriptArguments.Add([string]$item) | Out-Null
            }
            continue
        }

        $displayArguments.Add("-$key")
        $displayArguments.Add([string]$value)
        $scriptArguments.Add("-$key") | Out-Null
        $scriptArguments.Add([string]$value) | Out-Null
    }

    Write-Host ("[ScreenShareOps] powershell -ExecutionPolicy Bypass -File {0} {1}" -f $ScriptPath, ($displayArguments -join " ")) -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & powershell @scriptArguments | ForEach-Object { Write-Host $_ }
    return $LASTEXITCODE
}

function Get-ResolvedDurationSeconds {
    param([Parameter(Mandatory = $true)][int]$DefaultValue)

    $value = if ($DurationSeconds -gt 0) { $DurationSeconds } else { $DefaultValue }
    if ($value -le 0) {
        throw "DurationSeconds must be greater than zero."
    }

    return $value
}

function Write-SupportCaptureInstructions {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    Write-Host "ScreenShare support/debug capture" -ForegroundColor Green
    Write-Host ""
    Write-Host "1. In the app, open Diagnostics -> Copy diagnostics and include the copied text."
    Write-Host "   Diagnostics now includes a compact screenshare evidence summary when an analyzed soak artifact exists."
    Write-Host "2. If the issue involved a hang or freeze, open Diagnostics -> Save Hang Report."
    Write-Host "   Hang reports include screenshare-evidence.txt with the same summary."
    Write-Host "3. Attach app logs if available."
    Write-Host "4. Attach the full screenshare soak artifact only when Diagnostics evidence points to one or support asks for it."
    Write-Host ""

    $soakRoot = Join-Path $RepoRoot "artifacts\soak"
    if (-not (Test-Path -LiteralPath $soakRoot)) {
        Write-Host "Latest soak artifact: (none found)"
        return
    }

    $latest = Get-ChildItem -LiteralPath $soakRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $latest) {
        Write-Host "Latest soak artifact: (none found)"
        return
    }

    Write-Host ("Latest soak artifact: {0}" -f $latest.FullName)
    $verdictPath = Join-Path $latest.FullName "screenshare-operator-verdict.txt"
    if (Test-Path -LiteralPath $verdictPath) {
        Write-Host ("Latest operator verdict: {0}" -f $verdictPath)
    }
    else {
        Write-Host ("Latest operator verdict: (not present; run AnalyzeRetained for that artifact)")
    }
}

Assert-ParameterMode -Name "Configuration" -AllowedModes @("Test", "LocalSoak", "TrackBRetained")
Assert-ParameterMode -Name "NoBuild" -AllowedModes @("Test", "LocalSoak", "TrackBRetained")
Assert-ParameterMode -Name "Logger" -AllowedModes @("Test", "TrackBRetained")
Assert-ParameterMode -Name "DurationSeconds" -AllowedModes @("LocalSoak", "NknSoak")
Assert-ParameterMode -Name "SampleIntervalSeconds" -AllowedModes @("LocalSoak")
Assert-ParameterMode -Name "ArtifactDir" -AllowedModes @("AnalyzeRetained")
Assert-ParameterMode -Name "ExePath" -AllowedModes @("NknSoak")
Assert-ParameterMode -Name "Build" -AllowedModes @("NknSoak")
Assert-ParameterMode -Name "TimeoutSeconds" -AllowedModes @("NknSoak")
Assert-ParameterMode -Name "StrongBaselineArtifactDir" -AllowedModes @("NknSoak")
Assert-ParameterMode -Name "SafeBaselineArtifactDir" -AllowedModes @("NknSoak")
Assert-ParameterMode -Name "SkipBehaviorFirstGate" -AllowedModes @("NknSoak")

$repoRoot = Resolve-RepoRoot
$analyzerOrchestrationPath = Join-Path $repoRoot "tools\ScreenShareOps\AnalyzerOrchestration.ps1"
if (-not (Test-Path -LiteralPath $analyzerOrchestrationPath)) {
    throw "Required ScreenShare ops orchestration module not found: $analyzerOrchestrationPath"
}
. $analyzerOrchestrationPath

Push-Location $repoRoot
try {
    switch ($Mode) {
        "Test" {
            $parameters = [ordered]@{
                Lane = @("ScreenShare")
                Configuration = $Configuration
            }
            if ($NoBuild) {
                $parameters["NoBuild"] = $true
            }
            $loggerValues = @($Logger | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($loggerValues.Count -gt 0) {
                $parameters["Logger"] = $loggerValues
            }

            exit (Invoke-PowerShellScript -ScriptPath (Join-Path $repoRoot "tools\Test-Lanes.ps1") -Parameters $parameters)
        }
        "LocalSoak" {
            if ($SampleIntervalSeconds -le 0) {
                throw "SampleIntervalSeconds must be greater than zero."
            }

            $duration = Get-ResolvedDurationSeconds -DefaultValue 300
            $arguments = @(
                "run",
                "--project", (Join-Path $repoRoot "src\nLink.App\nLink.App.csproj"),
                "-c", $Configuration
            )
            if ($NoBuild) {
                $arguments += "--no-build"
            }
            $arguments += @(
                "--",
                "--screenshare-soak",
                "--seconds", ([string]$duration),
                "--sample-interval-seconds", ([string]$SampleIntervalSeconds)
            )

            exit (Invoke-ExternalCommand -Command "dotnet" -Arguments $arguments)
        }
        "NknSoak" {
            $duration = Get-ResolvedDurationSeconds -DefaultValue 30
            $parameters = [ordered]@{
                DurationSeconds = $duration
            }
            if (-not [string]::IsNullOrWhiteSpace($ExePath)) {
                $parameters["ExePath"] = $ExePath
            }
            if ($Build) {
                $parameters["Build"] = $true
            }
            if ($TimeoutSeconds -gt 0) {
                $parameters["TimeoutSeconds"] = $TimeoutSeconds
            }
            if (-not [string]::IsNullOrWhiteSpace($StrongBaselineArtifactDir)) {
                $parameters["StrongBaselineArtifactDir"] = $StrongBaselineArtifactDir
            }
            if (-not [string]::IsNullOrWhiteSpace($SafeBaselineArtifactDir)) {
                $parameters["SafeBaselineArtifactDir"] = $SafeBaselineArtifactDir
            }
            if ($SkipBehaviorFirstGate) {
                $parameters["SkipBehaviorFirstGate"] = $true
            }

            exit (Invoke-PowerShellScript -ScriptPath (Join-Path $repoRoot "tools\Run-ScreenShareNknSoak.ps1") -Parameters $parameters)
        }
        "AnalyzeRetained" {
            if ([string]::IsNullOrWhiteSpace($ArtifactDir)) {
                throw "Mode AnalyzeRetained requires -ArtifactDir."
            }

            $resolvedArtifactDir = $ArtifactDir
            if (-not [System.IO.Path]::IsPathRooted($resolvedArtifactDir)) {
                $resolvedArtifactDir = Join-Path $repoRoot $resolvedArtifactDir
            }
            if (-not (Test-Path -LiteralPath $resolvedArtifactDir)) {
                throw "ArtifactDir not found: $resolvedArtifactDir"
            }

            $retainedAnalyzerExitCode = Invoke-ScreenShareRetainedAnalyzerChain -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir
            if ($retainedAnalyzerExitCode -ne 0) {
                exit $retainedAnalyzerExitCode
            }

            $verdictResult = Write-ScreenShareOperatorVerdictReport -ArtifactDir $resolvedArtifactDir
            if ([string]::Equals($verdictResult.OperatorVerdict, "inconclusive_missing_artifact", [System.StringComparison]::Ordinal)) {
                Write-Error ("ScreenShare operator verdict is missing required inputs: {0}" -f $verdictResult.MissingRequiredInputs)
                exit 1
            }

            exit 0
        }
        "TrackBRetained" {
            $parameters = [ordered]@{
                Lane = @("TrackBRetained")
                Configuration = $Configuration
            }
            if ($NoBuild) {
                $parameters["NoBuild"] = $true
            }
            $loggerValues = @($Logger | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($loggerValues.Count -gt 0) {
                $parameters["Logger"] = $loggerValues
            }

            exit (Invoke-PowerShellScript -ScriptPath (Join-Path $repoRoot "tools\Test-Lanes.ps1") -Parameters $parameters)
        }
        "SupportCapture" {
            Write-SupportCaptureInstructions -RepoRoot $repoRoot
            exit 0
        }
        default {
            throw "Unsupported mode: $Mode"
        }
    }
}
finally {
    Pop-Location
}
