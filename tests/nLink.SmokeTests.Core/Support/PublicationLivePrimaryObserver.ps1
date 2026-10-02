# Test-only scalar observation. No private arrays, views, custody or completion
# capabilities leave the real primary. The original CLI, reads and evaluations run.
if($env:NLINK_TEST_NATIVE_PRIMARY_FILES -ceq '1') {
    . (Join-Path $PSScriptRoot 'TerminalPrimaryFileObserver.ps1')
}
$script:livePrimaryEvaluations=0
$script:livePrimaryEvaluationCallers=[Collections.Generic.List[string]]::new()
$script:liveOriginalEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    if($env:NLINK_TEST_COLLECTOR_PRIMARY -ceq '1' -and
        $null -ne (Get-Variable collectorRawContext -ErrorAction SilentlyContinue)) {throw 'Private collector raw-input context leaked to evaluator'}
    $script:livePrimaryEvaluations++
    $script:livePrimaryEvaluationCallers.Add([string](Get-PSCallStack)[1].FunctionName)
    & $script:liveOriginalEvaluator -Lines $Lines
}
$primary=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.ToString()
$needle='$private:localPublicationWitness = $null' + "`n" + '        $private:localCompletedEvaluations = $null'
$primary=$primary.Replace("`r`n","`n")
if($env:NLINK_TEST_COLLECTOR_PRIMARY -ceq '1') {
    $primary=& (Join-Path $PSScriptRoot 'TerminalCollectorPrimaryAnalysis.ps1') $primary
}
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
if($env:NLINK_TEST_MEMBERSHIP_BORROWER -ceq '1') {
    $borrowerObserver=& (Join-Path $PSScriptRoot 'SelectedMembershipBorrowerObserver.ps1') -Primary $primary -CleanupSeam $needle
    $primary=$borrowerObserver.Primary
    Set-Item Function:Invoke-FileTransferRetainedAnalysis -Value ([scriptblock]::Create($borrowerObserver.Source))
}
if($env:NLINK_PUBLICATION_TERMINAL_SOURCE_PROBE -ceq '1') {
    $probePath=(Join-Path $PSScriptRoot 'PublicationTerminalSourceProbe.ps1').Replace("'","''")
    $probe="& ([scriptblock]::Create([IO.File]::ReadAllText('$probePath'))) -Context `$private:localPrimaryInputs -Custody `$private:localPrimaryCustody -Evaluations `$private:localCompletedEvaluations -Index `$Index -ArtifactDir `$ArtifactDir -GuiCompleted `$guiCompleted -GuiIntegrityOk `$guiIntegrityOk -PrimaryVerdict `$verdict -PrimaryGateStatus `$gateStatus"
    if($env:NLINK_TEST_COLLECTOR_MEMBERSHIP -ceq '1') {
        $membershipPath=(Join-Path $PSScriptRoot 'TerminalCollectorMembership.ps1').Replace("'","''")
        $probe+=" -MembershipProbe ([scriptblock]::Create([IO.File]::ReadAllText('$membershipPath')))"
    }
    if($env:NLINK_TEST_NATIVE_WITNESS_ADAPTER -ceq '1') {
        if($env:NLINK_TEST_COLLECTOR_MEMBERSHIP -ceq '1' -or $env:NLINK_TEST_TERMINAL_PAYLOAD_JOIN -ceq '1' -or
            $env:NLINK_TEST_PRE_VERDICT_INTEGRITY -ceq '1') {throw 'native_witness_adapter_acquisition_only'}
        $adapterPath=(Join-Path $PSScriptRoot 'NativeWitnessWriterAdapterProbe.ps1').Replace("'","''")
        $probe+=" -MembershipProbe ([scriptblock]::Create([IO.File]::ReadAllText('$adapterPath')))"
    }
    if($env:NLINK_TEST_TERMINAL_PAYLOAD_JOIN -ceq '1') {
        $payloadPath=(Join-Path $PSScriptRoot 'TerminalPayloadJoin.ps1').Replace("'","''")
        $probe+=" -PayloadProbe ([scriptblock]::Create([IO.File]::ReadAllText('$payloadPath')))"
    }
    if($env:NLINK_TEST_PRE_VERDICT_INTEGRITY -ceq '1') {
        $instrumented=& (Join-Path $PSScriptRoot 'TerminalPreVerdictObserver.ps1') -Primary $primary -SourceProbe $probe `
            -GuiReader (Get-Command Read-FileTransferCompactGuiSummary).ScriptBlock.ToString()
        $primary=$instrumented.Primary
        Set-Item Function:Read-FileTransferCompactGuiSummary -Value ([scriptblock]::Create($instrumented.GuiReader))
    } else {
        $probe='try { '+$probe+' } catch { [IO.File]::WriteAllText((Join-Path $ArtifactDir ''terminal-source-probe-error.txt''),[string]$_ + "`n" + $_.ScriptStackTrace) }'
        $primary=$primary.Replace($needle,$probe+"`n        "+$needle)
    }
}
if($env:NLINK_PUBLICATION_PEER_LINEAGE_PROTOTYPE -ceq '1') {
    # Test-only, after the original reports. Only detached diagnostics leave
    # the worker; a prototype failure cannot replace the primary result.
    # Keep the original primary's script scope (including its loaded catalogs).
    $probe="& ([scriptblock]::Create([IO.File]::ReadAllText('"+(Join-Path $PSScriptRoot 'PublicationPeerLineageProbe.ps1').Replace("'","''")+"'))) -Context `$private:localPrimaryInputs -Custody `$private:localPrimaryCustody -Evaluations `$private:localCompletedEvaluations -Index `$Index -Failure `$firstCompactProofChainFailure -RouteState `$routeConsistencyState -ArtifactDir `$ArtifactDir"
    $probe='try { '+$probe+' } catch { [IO.File]::WriteAllText((Join-Path $ArtifactDir ''peer-lineage-prototype-error.txt''),[string]$_ + "`n" + $_.ScriptStackTrace) }'
    $primary=$primary.Replace($needle,$probe+"`n        "+$needle)
}
if($env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER -ceq '1') {
    $probePath=(Join-Path $PSScriptRoot 'PublicationServiceYieldProbe.ps1').Replace("'","''")
    $matcherPath=(Join-Path $PSScriptRoot 'PublicationServiceYieldMatcher.ps1').Replace("'","''")
    $probe="& ([scriptblock]::Create([IO.File]::ReadAllText('$probePath'))) -Matcher ([scriptblock]::Create([IO.File]::ReadAllText('$matcherPath'))) -Context `$private:localPrimaryInputs -Custody `$private:localPrimaryCustody -Evaluations `$private:localCompletedEvaluations -Index `$Index -Failure `$firstCompactProofChainFailure -RouteState `$routeConsistencyState -ArtifactDir `$ArtifactDir"
    if($env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER -ceq '1') { $probe+=' -Reciprocal' }
    if($env:NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER -ceq '1') {
        $rejectionPath=(Join-Path $PSScriptRoot 'PublicationRejectionChainMatcher.ps1').Replace("'","''")
        $probe+=" -RejectionChain -RejectionMatcher ([scriptblock]::Create([IO.File]::ReadAllText('$rejectionPath')))"
    }
    if($env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONSUMER -ceq '1') {
        $dispatchPath=(Join-Path $PSScriptRoot 'PublicationDispatchWindowMatcher.ps1').Replace("'","''")
        $probe+=" -DispatchWindow -DispatchMatcher ([scriptblock]::Create([IO.File]::ReadAllText('$dispatchPath')))"
        if($env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONTROLS -ceq '1') {
            $controlsPath=(Join-Path $PSScriptRoot 'PublicationDispatchWindowControls.ps1').Replace("'","''")
            $probe+=" -DispatchControls ([scriptblock]::Create([IO.File]::ReadAllText('$controlsPath')))"
        }
    }
    if($env:NLINK_PUBLICATION_SECURITY_LIFECYCLE_CONSUMER -ceq '1') {
        $securityPath=(Join-Path $PSScriptRoot 'PublicationSecurityLifecycleMatcher.ps1').Replace("'","''")
        $securityControlsPath=(Join-Path $PSScriptRoot 'PublicationSecurityLifecycleControls.ps1').Replace("'","''")
        $probe+=" -SecurityLifecycle -SecurityMatcher ([scriptblock]::Create([IO.File]::ReadAllText('$securityPath'))) -SecurityControls ([scriptblock]::Create([IO.File]::ReadAllText('$securityControlsPath')))"
    }
    if($env:NLINK_PUBLICATION_ALTERNATIVE_ACCEPTANCE_SHADOW -ceq '1') {
        $shadowPath=(Join-Path $PSScriptRoot 'PublicationAlternativeAcceptanceShadow.ps1').Replace("'","''")
        $probe+=" -AlternativeShadow ([scriptblock]::Create([IO.File]::ReadAllText('$shadowPath'))) -OriginalMismatchProof `$fileTunaV4DataPathMismatchProof"
        if($env:NLINK_PUBLICATION_TUNA_PAIR_SHADOW -ceq '1') {
            $pairPath=(Join-Path $PSScriptRoot 'PublicationTunaPairMatcher.ps1').Replace("'","''")
            $probe+=" -TunaPairs ([scriptblock]::Create([IO.File]::ReadAllText('$pairPath')))"
            if($env:NLINK_PUBLICATION_TUNA_PAIR_CONTROLS -ceq '1') {
                $controlPath=(Join-Path $PSScriptRoot 'PublicationTunaPairControls.ps1').Replace("'","''")
                $probe+=" -TunaPairControls ([scriptblock]::Create([IO.File]::ReadAllText('$controlPath')))"
            }
        }
    }
    $probe='try { '+$probe+' } catch { [IO.File]::WriteAllText((Join-Path $ArtifactDir ''service-yield-consumer-error.txt''),[string]$_ + "`n" + $_.ScriptStackTrace) }'
    $primary=$primary.Replace($needle,$probe+"`n        "+$needle)
}
if($env:NLINK_PUBLICATION_CONTENT_COVERAGE_SHADOW -ceq '1') {
    $supportRoot=$PSScriptRoot.Replace("'","''")
    $probePath=(Join-Path $PSScriptRoot 'PublicationContentCoverageProbe.ps1').Replace("'","''")
    $probe="& ([scriptblock]::Create([IO.File]::ReadAllText('$probePath'))) -Context `$private:localPrimaryInputs -Custody `$private:localPrimaryCustody -Evaluations `$private:localCompletedEvaluations -Index `$Index -Failure `$firstCompactProofChainFailure -Witness `$preparedLocalFailureWitness -Prepared `$private:localPublicationWitness -OriginalMismatchProof `$fileTunaV4DataPathMismatchProof -RouteState `$routeConsistencyState -ArtifactDir `$ArtifactDir -SupportRoot '$supportRoot'"
    $probe='try { '+$probe+' } catch { [IO.File]::WriteAllText((Join-Path $ArtifactDir ''content-coverage-shadow-error.txt''),[string]$_ + "`n" + $_.ScriptStackTrace) }'
    $primary=$primary.Replace($needle,$probe+"`n        "+$needle)
}
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
