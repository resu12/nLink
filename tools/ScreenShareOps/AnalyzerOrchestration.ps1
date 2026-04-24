Set-StrictMode -Version Latest

function Get-ScreenShareOpsManifestPath {
    return (Join-Path $PSScriptRoot "retained-analyzer-chain.json")
}

function Get-RequiredManifestString {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$PropertyName,
        [switch]$AllowEmpty
    )

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        throw "Retained analyzer manifest entry is missing '$PropertyName'."
    }

    $value = [string]$property.Value
    if (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($value)) {
        throw "Retained analyzer manifest entry has an empty '$PropertyName'."
    }

    return $value
}

function Assert-NoDuplicateManifestValues {
    param(
        [Parameter(Mandatory = $true)][string[]]$Values,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $duplicates = @(
        $Values |
            Group-Object |
            Where-Object { $_.Count -gt 1 } |
            Select-Object -ExpandProperty Name
    )

    if ($duplicates.Count -gt 0) {
        throw ("Retained analyzer manifest has duplicate {0}: {1}" -f $Name, ($duplicates -join ", "))
    }
}

function Get-ScreenShareRetainedAnalyzerManifest {
    $manifestPath = Get-ScreenShareOpsManifestPath
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Retained analyzer manifest not found: $manifestPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schema_version -ne 1) {
        throw ("Unsupported retained analyzer manifest schema version: {0}" -f $manifest.schema_version)
    }

    $rawAnalyzers = @($manifest.retained_analyzers)
    if ($rawAnalyzers.Count -eq 0) {
        throw "Retained analyzer manifest contains no analyzer entries."
    }

    $analyzers = foreach ($entry in $rawAnalyzers) {
        [pscustomobject]@{
            Id = Get-RequiredManifestString -Object $entry -PropertyName "id"
            Script = Get-RequiredManifestString -Object $entry -PropertyName "script"
            Report = Get-RequiredManifestString -Object $entry -PropertyName "report"
            ClassificationStage = Get-RequiredManifestString -Object $entry -PropertyName "classification_stage" -AllowEmpty
        }
    }

    Assert-NoDuplicateManifestValues -Values ([string[]]@($analyzers | ForEach-Object { $_.Id })) -Name "ids"
    Assert-NoDuplicateManifestValues -Values ([string[]]@($analyzers | ForEach-Object { $_.Script })) -Name "scripts"
    Assert-NoDuplicateManifestValues -Values ([string[]]@($analyzers | ForEach-Object { $_.Report })) -Name "reports"

    $classificationStages = [string[]]@(
        $analyzers |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.ClassificationStage) } |
            ForEach-Object { $_.ClassificationStage }
    )
    Assert-NoDuplicateManifestValues -Values $classificationStages -Name "classification stages"

    $externalClassifications = [string[]]@($manifest.external_transport_classifications | ForEach-Object { [string]$_ })
    if ($externalClassifications.Count -eq 0) {
        throw "Retained analyzer manifest contains no external transport classifications."
    }
    Assert-NoDuplicateManifestValues -Values $externalClassifications -Name "external transport classifications"

    return [pscustomobject]@{
        SchemaVersion = 1
        RetainedAnalyzers = @($analyzers)
        ExternalTransportClassifications = @($externalClassifications)
    }
}

function Get-ScreenShareRetainedClassificationReports {
    $manifest = Get-ScreenShareRetainedAnalyzerManifest
    foreach ($analyzer in @($manifest.RetainedAnalyzers)) {
        if ([string]::IsNullOrWhiteSpace($analyzer.ClassificationStage)) {
            continue
        }

        [pscustomobject]@{
            Stage = $analyzer.ClassificationStage
            FileName = $analyzer.Report
        }
    }
}

function Get-ScreenShareExternalTransportClassifications {
    $manifest = Get-ScreenShareRetainedAnalyzerManifest
    return [string[]]@($manifest.ExternalTransportClassifications)
}

function Resolve-ScreenShareAnalyzerScriptPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ScriptName
    )

    $analyzerRoot = $env:NLINK_SCREENSHARE_OPS_ANALYZER_ROOT
    if ([string]::IsNullOrWhiteSpace($analyzerRoot)) {
        $analyzerRoot = Join-Path $RepoRoot "tools"
    }
    elseif (-not [System.IO.Path]::IsPathRooted($analyzerRoot)) {
        $analyzerRoot = Join-Path $RepoRoot $analyzerRoot
    }

    return (Join-Path $analyzerRoot $ScriptName)
}

function Invoke-ScreenShareRetainedAnalyzerChain {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    if ($env:NLINK_SCREENSHARE_OPS_VERDICT_ONLY -eq "1") {
        Write-Host "[ScreenShareOps] verdict-only test hook active; retained analyzers are not invoked." -ForegroundColor Yellow
        return 0
    }

    if ($null -eq (Get-Command Invoke-PowerShellScript -ErrorAction SilentlyContinue)) {
        throw "Invoke-PowerShellScript must be defined before invoking the retained analyzer chain."
    }

    $manifest = Get-ScreenShareRetainedAnalyzerManifest
    foreach ($analyzer in @($manifest.RetainedAnalyzers)) {
        $exitCode = Invoke-PowerShellScript `
            -ScriptPath (Resolve-ScreenShareAnalyzerScriptPath -RepoRoot $RepoRoot -ScriptName $analyzer.Script) `
            -Parameters ([ordered]@{ CandidateArtifactDir = $ArtifactDir })
        if ($exitCode -ne 0) {
            return $exitCode
        }
    }

    return 0
}

function Read-KeyValueReport {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $values = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*([A-Za-z0-9_]+)=(.*)$') {
            $values[$matches[1]] = $matches[2].Trim()
        }
    }

    return $values
}

function Get-KeyValue {
    param(
        [AllowNull()][System.Collections.IDictionary]$Values,
        [Parameter(Mandatory = $true)][string]$Key
    )

    if ($null -eq $Values -or -not $Values.Contains($Key)) {
        return $null
    }

    return [string]$Values[$Key]
}

function Add-MissingVerdictInput {
    param(
        [Parameter(Mandatory = $true)]$MissingInputs,
        [Parameter(Mandatory = $true)][string]$FileName,
        [string]$Key = ""
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        $MissingInputs.Add($FileName) | Out-Null
        return
    }

    $MissingInputs.Add(("{0}:{1}" -f $FileName, $Key)) | Out-Null
}

function Write-ScreenShareOperatorVerdictReport {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $missingInputs = New-Object System.Collections.Generic.List[string]

    $stabilityFile = "stability-gates-summary.txt"
    $latencyFile = "latency-regression-analysis.txt"
    $transportFile = "transport-mode-summary.txt"
    $recoveryFile = "recovery-burst-summary.txt"

    $stability = Read-KeyValueReport -Path (Join-Path $ArtifactDir $stabilityFile)
    $latency = Read-KeyValueReport -Path (Join-Path $ArtifactDir $latencyFile)
    $transport = Read-KeyValueReport -Path (Join-Path $ArtifactDir $transportFile)
    $recovery = Read-KeyValueReport -Path (Join-Path $ArtifactDir $recoveryFile)

    if ($null -eq $stability) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $stabilityFile }
    if ($null -eq $latency) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $latencyFile }
    if ($null -eq $transport) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $transportFile }
    if ($null -eq $recovery) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $recoveryFile }

    $behaviorFirstGateStatus = Get-KeyValue -Values $stability -Key "behavior_first_gate_status"
    $regressionClassification = Get-KeyValue -Values $latency -Key "regression_classification"
    $effectiveMediaPlaneActive = Get-KeyValue -Values $transport -Key "effective_media_plane_active"
    $steadyStateUsedControlFallback = Get-KeyValue -Values $transport -Key "steady_state_used_control_fallback"
    $recoveryCompletionAccountingMismatch = Get-KeyValue -Values $recovery -Key "recovery_completion_accounting_mismatch"

    if ($null -eq $behaviorFirstGateStatus) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $stabilityFile -Key "behavior_first_gate_status" }
    if ($null -eq $regressionClassification) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $latencyFile -Key "regression_classification" }
    if ($null -eq $effectiveMediaPlaneActive) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $transportFile -Key "effective_media_plane_active" }
    if ($null -eq $steadyStateUsedControlFallback) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $transportFile -Key "steady_state_used_control_fallback" }
    if ($null -eq $recoveryCompletionAccountingMismatch) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $recoveryFile -Key "recovery_completion_accounting_mismatch" }

    $classificationEvidence = [ordered]@{}
    $deepestStage = "(none)"
    $deepestClassification = "(missing)"
    $deepestSmallestNextFixArea = "(none)"

    foreach ($report in @(Get-ScreenShareRetainedClassificationReports)) {
        $stage = [string]$report.Stage
        $fileName = [string]$report.FileName
        $values = Read-KeyValueReport -Path (Join-Path $ArtifactDir $fileName)

        if ($null -eq $values) {
            Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $fileName
            continue
        }

        $classification = Get-KeyValue -Values $values -Key "classification"
        if ($null -eq $classification) {
            Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $fileName -Key "classification"
            continue
        }

        $classificationEvidence[$stage] = $classification
        $deepestStage = $stage
        $deepestClassification = $classification

        $smallestNextFixArea = Get-KeyValue -Values $values -Key "smallest_next_fix_area"
        if ($null -ne $smallestNextFixArea) {
            $deepestSmallestNextFixArea = $smallestNextFixArea
        }
    }

    $hasMissingInputs = $missingInputs.Count -gt 0
    $isPass = -not $hasMissingInputs -and
        [string]::Equals($behaviorFirstGateStatus, "pass", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($regressionClassification, "no_material_latency_regression", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($effectiveMediaPlaneActive, "1", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($steadyStateUsedControlFallback, "0", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($recoveryCompletionAccountingMismatch, "0", [System.StringComparison]::OrdinalIgnoreCase)

    $operatorVerdict = "fail_local_regression"
    if ($hasMissingInputs) {
        $operatorVerdict = "inconclusive_missing_artifact"
    }
    elseif ([string]::Equals($deepestClassification, "mixed_or_inconclusive", [System.StringComparison]::OrdinalIgnoreCase)) {
        $operatorVerdict = "inconclusive_mixed"
    }
    elseif ($isPass) {
        $operatorVerdict = "pass"
    }
    elseif ((Get-ScreenShareExternalTransportClassifications) -contains $deepestClassification) {
        $operatorVerdict = "fail_live_transport_evidence"
    }

    $operatorSummary = switch ($operatorVerdict) {
        "pass" { "Screenshare artifact passed behavior, media-plane, recovery-accounting, and retained closeout checks." }
        "fail_live_transport_evidence" { "Screenshare did not meet pass gates, and retained evidence points at live or external transport delivery." }
        "inconclusive_missing_artifact" { "Screenshare verdict could not be completed because required artifact reports or fields are missing." }
        "inconclusive_mixed" { "Screenshare retained evidence is mixed or inconclusive; do not patch forward from this artifact." }
        default { "Screenshare did not meet pass gates, and retained evidence points at a local or code-owned regression." }
    }

    $nextOperatorAction = switch ($operatorVerdict) {
        "pass" { "Use this artifact as operator evidence and continue with the planned workstream." }
        "fail_live_transport_evidence" { "Use this artifact for the external NKN/network reliability lane; do not start local runtime tuning without a new plan." }
        "inconclusive_missing_artifact" { "Rerun AnalyzeRetained on a complete NKN soak artifact, or validate why the retained reports were not materialized." }
        "inconclusive_mixed" { "Stop and prepare a new investigation plan before extending Track B or local runtime diagnostics." }
        default { "Fix the local regression indicated by the retained classification before collecting more live transport evidence." }
    }

    $missingInputText = if ($missingInputs.Count -eq 0) { "(none)" } else { $missingInputs -join "," }
    $reportLines = New-Object System.Collections.Generic.List[string]
    $reportLines.Add(("operator_verdict={0}" -f $operatorVerdict)) | Out-Null
    $reportLines.Add(("operator_summary={0}" -f $operatorSummary)) | Out-Null
    $reportLines.Add(("next_operator_action={0}" -f $nextOperatorAction)) | Out-Null
    $reportLines.Add(("artifact_dir={0}" -f $ArtifactDir)) | Out-Null
    $reportLines.Add(("missing_required_inputs={0}" -f $missingInputText)) | Out-Null
    $reportLines.Add(("behavior_first_gate_status={0}" -f $(if ($null -eq $behaviorFirstGateStatus) { "(missing)" } else { $behaviorFirstGateStatus }))) | Out-Null
    $reportLines.Add(("regression_classification={0}" -f $(if ($null -eq $regressionClassification) { "(missing)" } else { $regressionClassification }))) | Out-Null
    $reportLines.Add(("effective_media_plane_active={0}" -f $(if ($null -eq $effectiveMediaPlaneActive) { "(missing)" } else { $effectiveMediaPlaneActive }))) | Out-Null
    $reportLines.Add(("steady_state_used_control_fallback={0}" -f $(if ($null -eq $steadyStateUsedControlFallback) { "(missing)" } else { $steadyStateUsedControlFallback }))) | Out-Null
    $reportLines.Add(("recovery_completion_accounting_mismatch={0}" -f $(if ($null -eq $recoveryCompletionAccountingMismatch) { "(missing)" } else { $recoveryCompletionAccountingMismatch }))) | Out-Null
    $reportLines.Add(("deepest_track_b_stage={0}" -f $deepestStage)) | Out-Null
    $reportLines.Add(("deepest_track_b_classification={0}" -f $deepestClassification)) | Out-Null
    $reportLines.Add(("deepest_track_b_smallest_next_fix_area={0}" -f $deepestSmallestNextFixArea)) | Out-Null

    foreach ($stage in $classificationEvidence.Keys) {
        $reportLines.Add(("classification_{0}={1}" -f $stage, $classificationEvidence[$stage])) | Out-Null
    }

    $reportPath = Join-Path $ArtifactDir "screenshare-operator-verdict.txt"
    Set-Content -LiteralPath $reportPath -Value $reportLines
    Write-Host ("[ScreenShareOps] operator verdict: {0}" -f $operatorVerdict) -ForegroundColor Cyan
    Write-Host ("[ScreenShareOps] wrote {0}" -f $reportPath) -ForegroundColor Cyan

    return [pscustomobject]@{
        OperatorVerdict = $operatorVerdict
        ReportPath = $reportPath
        MissingRequiredInputs = $missingInputText
    }
}
