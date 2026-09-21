# Test-only scalar observation. No private arrays, views, custody or completion
# capabilities leave the real primary. The original CLI, reads and evaluations run.
$script:livePrimaryEvaluations=0
$script:livePrimaryEvaluationCallers=[Collections.Generic.List[string]]::new()
$script:liveOriginalEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    $script:livePrimaryEvaluations++
    $script:livePrimaryEvaluationCallers.Add([string](Get-PSCallStack)[1].FunctionName)
    & $script:liveOriginalEvaluator -Lines $Lines
}
$primary=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.ToString()
$needle='$private:localPublicationWitness = $null' + "`n" + '        $private:localCompletedEvaluations = $null'
$primary=$primary.Replace("`r`n","`n")
if($primary.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Original primary cleanup seam changed'}
$observation=@'
$facts=[ordered]@{Evaluations=$script:livePrimaryEvaluations;EvaluationCallers=$script:livePrimaryEvaluationCallers.ToArray();Prepared=$null -ne $private:localPrimaryInputs
    Witness=$null -ne $private:localPublicationWitness;Completion=$null -ne $private:localPublicationCompletion}
if($null -ne $preparedLocalFailureWitness) { $facts.FailureBucketRecords=$Index.EventsByName[$preparedLocalFailureWitness.EventName].Count }
if($env:NLINK_PUBLICATION_TUNA_PAYLOAD_OBSERVER -ceq '1') {
    # Copy original results, including recovered candidates which cannot own a failure witness.
    $facts.IngressCandidates=@($fileTunaV4DataPathMismatchProof.CandidateRecords | Where-Object {
        $_.Event.EventName -ceq 'filetransfer_data_path_receive_result'
    } | ForEach-Object { [ordered]@{Reason=[string]$_.RejectionDiagnostic.Reason
        ChainMatched=[bool]$_.RejectionDiagnostic.RejectionChainMatched;Recovered=[bool]$_.Recovered
        SourceLine=[int]$_.Event.LineNumber} })
}
if($null -ne $firstCompactProofChainFailure -and $firstCompactProofChainFailure.Class -ceq 'file_tuna_data_path_mismatch') {
    # Observe the original selected candidate only; do not run recovery again.
    $selectedRecords=@($fileTunaV4DataPathMismatchProof.EffectiveCandidateRecords | Where-Object {
        [object]::ReferenceEquals($_.Event,$firstCompactProofChainFailure.Event)
    })
    if($selectedRecords.Count -eq 1 -and $null -ne $selectedRecords[0].RejectionDiagnostic) {
        $diagnostic=$selectedRecords[0].RejectionDiagnostic
        $facts.SelectedRejectionReason=[string]$diagnostic.Reason
        $facts.SelectedRejectionChainMatched=[bool]$diagnostic.RejectionChainMatched
        $facts.SelectedRejectionRecovered=[bool]$diagnostic.Recovered
        $explanation=$diagnostic.PSObject.Properties['Explanation']
        $facts.SelectedRejectionExplanation=if($null -ne $explanation) {[string]$explanation.Value} else {$null}
    }
}
if($null -ne $private:localPrimaryInputs) {
    $facts.FullRecords=$private:localPrimaryInputs.FullLines.Length
    $facts.CompactRecords=$private:localPrimaryInputs.CompactLines.Length
    $facts.NativeRecordVisits=$private:localPrimaryInputs.NativeOwner.RecordVisits
    $facts.NativeCodeUnits=$private:localPrimaryInputs.NativeOwner.CodeUnitVisits
    $facts.NativeMilliseconds=$private:localPrimaryInputs.NativeOwner.ActiveMilliseconds
    $facts.OptionalMilliseconds=$private:localPrimaryInputs.Work.ElapsedMilliseconds
    $facts.WitnessOmission=$private:localPrimaryInputs.PublicationOmissionReason
    $facts.CompletionOmission=$private:localPrimaryInputs.PublicationCompletionOmissionReason
}
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'live-primary-observation.json'),($facts | ConvertTo-Json -Compress))
'@
$primary=$primary.Replace($needle,$observation+"`n        "+$needle)
if($env:NLINK_OFFLINE_PUBLICATION_PREPARATION_PROBE -ceq '1') {
    $before='$private:localPrimaryInputs = New-FileTransferPrimaryEvaluationInputs'
    $after='$fullSnapshotEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $primaryFullSnapshotLines'
    foreach($seam in @($before,$after)) {
        if($primary.Split([string[]]@($seam),[StringSplitOptions]::None).Length -ne 2) {throw 'Original preparation probe seam changed'}
    }
    $probe=". '"+(Join-Path $PSScriptRoot 'PublicationLivePreparationObserver.ps1').Replace("'","''")+"'"
    $primary=$primary.Replace($before,$probe+"`n            "+$before)
    # Deliberately stop this diagnostic-only attempt before either evaluator.
    # No report/completion from this probe may stand in for the positive gate.
    $stop="throw 'offline_preparation_probe_complete'`n    "
    if($env:NLINK_OFFLINE_PUBLICATION_WRITER_PROBE -ceq '1') {
        $writerProbe="& '"+(Join-Path $PSScriptRoot 'PublicationLiveWriterPreparationProbe.ps1').Replace("'","''")+"' -Context `$private:localPrimaryInputs -Index `$Index -ArtifactDir `$ArtifactDir"
        $stop=$writerProbe+"`n    "+$stop
    }
    $primary=$primary.Replace($after,$stop+$after)
}
$moduleRoot=Join-Path $repoRoot 'tools/FileTransferOps'
$primary=$primary.Replace('$PSScriptRoot',("'"+$moduleRoot.Replace("'","''")+"'"))
Set-Item Function:Write-FileTransferCompactRetainedAnalysisArtifacts -Value ([scriptblock]::Create($primary))
