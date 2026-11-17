param([string]$RepoRoot,[string]$SettingsPath)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$settings=[IO.File]::ReadAllText($SettingsPath) | ConvertFrom-Json
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPrimaryFrontierPublicationWitness.ps1')
$evaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
$resolver=(Get-Command Resolve-FileTransferPrimaryFrontierPublicationWitness).ScriptBlock
$script:evaluations=0; $script:preparations=0; $script:omission=''; $script:unchanged=$false
$script:optionalStarted=$false
$script:negativeCases=0
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    $script:evaluations++
    if($script:optionalStarted -or $script:evaluations -gt 3 -or
        ($script:evaluations -eq 3 -and (Get-PSCallStack)[1].FunctionName -cne 'Get-FileTransferRecoveryFailureClassification')) {
        throw 'Additional authority evaluation forbidden'
    }
    & $evaluator -Lines $Lines
}
# The actual primary dot-sources its optional module. Observe that call by
# replacing only that load in a test copy of the function; every evaluation,
# correlator, first-failure choice, custody seam and report remains original.
$definition=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.ToString()
$needle=". (Join-Path `$PSScriptRoot 'LocalPrimaryFrontierPublicationWitness.ps1')"
if($definition.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Optional load seam changed' }
$definition=$definition.Replace($needle,'# Already loaded, test observation only.')
$script:orchestrationRoot=Join-Path $RepoRoot 'tools/FileTransferOps'
$definition=$definition.Replace('$PSScriptRoot','$script:orchestrationRoot')
$definition=$definition.Replace('catch { } # Optional preparation must not replace either primary evaluation.',
    'catch { throw ("Primary input preparation: "+$_.Exception.Message) }')
$definition=$definition.Replace('$primaryFullSnapshotLines = $null',@'
if ($null -eq $private:localPrimaryInputs -or $null -eq $private:localPrimaryCustody -or $null -eq $private:localCompletedEvaluations) {
    throw ("Actual custody setup omitted: inputs="+($null -ne $private:localPrimaryInputs)+" custody="+($null -ne $private:localPrimaryCustody)+" evaluations="+($null -ne $private:localCompletedEvaluations))
}
$primaryFullSnapshotLines = $null
'@)
Invoke-Expression ('function Write-FileTransferCompactRetainedAnalysisArtifacts {'+$definition+'}')
function Resolve-FileTransferPrimaryFrontierPublicationWitness {
    param($Context,$Custody,$Evaluations,$Index,$Failure,$Candidates,$RecentProof,[string]$ReceiptJson)
    $script:preparations++
    $script:optionalStarted=$true
    $full=$Context.FullView | ConvertTo-Json -Depth 100 -Compress
    $compact=$Context.CompactView | ConvertTo-Json -Depth 100 -Compress
    $proof=$RecentProof | ConvertTo-Json -Depth 8 -Compress
    $chosen=$Failure | ConvertTo-Json -Depth 8 -Compress
    $result=& $resolver @PSBoundParameters
    $script:omission=$Context.FrontierPublicationOmissionReason
    if($null -ne $result) {
        foreach($mode in @('receipt-byte','receipt-truncated','receipt-overflow','history-missing','foreign-completion',
            'failure-copy','failure-event-copy','context-copy','view-copy','custody-missing','evaluations-missing',
            'candidate-missing','recovered-proof','full-input-changed','compact-origin-changed','required-write-missing')) {
            $attempt=@{}; foreach($key in $PSBoundParameters.Keys) { $attempt[$key]=$PSBoundParameters[$key] }
            $originalFull=$Context.FullView; $originalLine=$Context.FullLines[0]; $originalOrigin=$Context.CompactOrigins[0]
            $changedPosition=-1; $changedLine=$null
            try {
                switch($mode) {
                    'receipt-byte' { $attempt.ReceiptJson=$ReceiptJson+' ' }
                    'receipt-truncated' { $attempt.ReceiptJson=$ReceiptJson.Substring(0,$ReceiptJson.Length-1) }
                    'receipt-overflow' { $attempt.ReceiptJson=' '*262145 }
                    'history-missing' {
                        $copy=$ReceiptJson | ConvertFrom-Json; $copy.Receipt.Lifecycle=$null
                        $attempt.ReceiptJson=$copy | ConvertTo-Json -Depth 100 -Compress
                    }
                    'foreign-completion' {
                        $copy=$ReceiptJson | ConvertFrom-Json; $copy.Receipt.Completion.TransitionId='foreign'
                        $attempt.ReceiptJson=$copy | ConvertTo-Json -Depth 100 -Compress
                    }
                    'failure-copy' { $attempt.Failure=$Failure.PSObject.Copy() }
                    'failure-event-copy' {
                        $copy=$Failure.PSObject.Copy(); $copy.Event=$Failure.Event.PSObject.Copy()
                        $attempt.Failure=$copy; $attempt.Candidates=@($copy)
                        $proofCopy=$RecentProof.PSObject.Copy(); $record=$RecentProof.UnresolvedCandidateRecords[0].PSObject.Copy()
                        $record.Event=$copy.Event; $proofCopy.UnresolvedCandidateRecords=@($record); $attempt.RecentProof=$proofCopy
                    }
                    'context-copy' { $attempt.Context=$Context.PSObject.Copy() }
                    'view-copy' { $Context.FullView=$full | ConvertFrom-Json }
                    'custody-missing' { $attempt.Custody=$null }
                    'evaluations-missing' { $attempt.Evaluations=$null }
                    'candidate-missing' { $attempt.Candidates=@() }
                    'recovered-proof' {
                        $copy=$RecentProof.PSObject.Copy(); $copy.UnresolvedCount=0; $copy.UnresolvedCandidateRecords=@(); $attempt.RecentProof=$copy
                    }
                    'full-input-changed' { $Context.FullLines[0]+=':changed' }
                    'compact-origin-changed' { $Context.CompactOrigins[0]=999999 }
                    'required-write-missing' {
                        $firstCommit=($ReceiptJson | ConvertFrom-Json).Receipt.Commits[0]
                        $matches=@($Index.AllEvents | Where-Object { $_.EventName -ceq 'filetransfer_v6_sparse_write_committed' -and
                            $_.RawLine -cmatch ('; next_chunk_index='+$firstCommit.After+';') })
                        if($matches.Count -ne 1) { throw 'Required-write control fixture missing' }
                        $changedPosition=[Array]::BinarySearch($Context.FullOrigins,[int]$matches[0].LineNumber)
                        $changedLine=$Context.FullLines[$changedPosition]; $Context.FullLines[$changedPosition]=''
                    }
                }
                if($null -ne (& $resolver @attempt)) { throw "Negative mapping admitted: $mode" }
                if($Context.FrontierPublicationOmissionReason -ceq 'frontier_primary_custody_or_budget' -and
                    $Context.Work.ElapsedMilliseconds -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds) {
                    throw "Budget masked negative control: $mode"
                }
                $script:negativeCases++
            } finally {
                $Context.FullView=$originalFull; $Context.FullLines[0]=$originalLine; $Context.CompactOrigins[0]=$originalOrigin
                if($changedPosition -ge 0) { $Context.FullLines[$changedPosition]=$changedLine }
            }
        }
        if(-not $Custody.Owns($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView)) {
            throw 'Negative controls changed original custody'
        }
    }
    $script:unchanged=$full -ceq ($Context.FullView | ConvertTo-Json -Depth 100 -Compress) -and
        $compact -ceq ($Context.CompactView | ConvertTo-Json -Depth 100 -Compress) -and
        $proof -ceq ($RecentProof | ConvertTo-Json -Depth 8 -Compress) -and $chosen -ceq ($Failure | ConvertTo-Json -Depth 8 -Compress)
    if($settings.mode -ceq 'post-cancel' -and $null -ne $result) { $Context.SourceReader.Dispose() }
    return $result
}
$owner=$null; $source=$null
try {
    $owner=[NLink.Tools.LocalRetainedSourceRequest]::TryAttach($env:NLINK_RETAINED_SOURCE_REQUEST,[guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID,
        $settings.path,$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)
    if($null -eq $owner -or $null -eq $owner.WriterContext) { throw 'Actual worker/source/writer owner missing' }
    $source=$owner.TryOpenSource()
    $events=@(Read-FileTransferLogEvents -LogFiles @($settings.path) -TailMinutes 0 -OwnedSource $source)
    $index=New-FileTransferRetainedEventIndex -Events $events -LogFiles @($settings.path)
    $receipt=[IO.File]::ReadAllText($settings.receipt)
    $witness=$null; $oldWitness=$null
    $result=Write-FileTransferCompactRetainedAnalysisArtifacts -ArtifactDir $settings.output -Index $index -LocalSourceOwner $owner `
        -LocalSourceReader $source -LocalFailureWitness ([ref]$oldWitness) -LocalFrontierReceiptJson $receipt -LocalFrontierWitness ([ref]$witness)
    $summary=[IO.File]::ReadAllText((Join-Path $settings.output 'filetransfer-retained-compact-summary.json')) | ConvertFrom-Json
    if($summary.first_proof_chain_failure_class -cne 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing' -or
        $summary.filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_distinct_count -ne 1 -or
        $summary.filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_recovered_count -ne 0 -or
        $summary.filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_unresolved_count -ne 1 -or
        $result.GateStatus -cne 'fail' -or $null -ne $oldWitness -or $script:evaluations -ne 3 -or
        $script:preparations -ne 1 -or -not $script:unchanged) { throw 'Primary result/ownership invariance failed' }
    $count=0
    $r=($receipt | ConvertFrom-Json).Receipt
    $rows=@(foreach($event in $events) {
        [pscustomobject]@{Line=$event.LineNumber;Raw=$event.RawLine;Fields=(ConvertFrom-GuiSmokeLogLedgerStructuredFields $event.RawLine)}
    })
    if($settings.mode -cin @('bounded','history')) {
        if($null -eq $witness) { throw "Primary mapping omitted: $script:omission; preparations=$script:preparations" }
        if($summary.first_proof_chain_failure_class -cne $witness.ReportClass -or
            $summary.first_proof_chain_failure_line_number -ne $witness.FailureSourceLine -or
            $witness.LiveProofEligible -or $witness.PublicationCompletionIssued -or
            $witness.Witnesses.Count -lt 1 -or $witness.Witnesses.Count -gt 64 -or $script:negativeCases -ne 16) { throw 'Bounded witness failed' }
        $count=$witness.Witnesses.Count
        $selected=[Collections.Generic.HashSet[int]]::new()
        foreach($item in $witness.Witnesses) {
            $row=@($rows | Where-Object Line -EQ $item.SourceLine)
            if($row.Count -ne 1 -or -not $selected.Add([int]$item.SourceLine) -or
                (Get-FileTransferLocalCarrierHash $row[0].Raw) -cne $item.ContentSha256) { throw 'Witness physical bytes changed' }
        }
        $progress=@($rows | Where-Object { $_.Fields.event -ceq 'filetransfer_fallback_frontier_repair_progress_observed' -and
            $_.Fields.authority_id -ceq $r.RepairAuthority.AuthorityId.Value -and $_.Fields.current_frontier_chunk_index -ceq [string]$r.RecentArms[0].Frontier })
        $state=@($rows | Where-Object { $_.Fields.event -ceq 'filetransfer_v6_receiver_state_sent' -and
            $_.Fields.epoch -ceq [string]$r.Frame.Epoch -and $_.Fields.transport_epoch -ceq [string]$r.Frame.TransportEpoch })
        if($progress.Count -ne 1 -or $state.Count -ne 1 -or $state[0].Line -le $witness.ManifestSourceLine) { throw 'Episode endpoints missing' }
        foreach($line in @($progress[0].Line,$state[0].Line,$witness.FailureSourceLine,$witness.CompletionSourceLine,$witness.ManifestSourceLine)) {
            if(-not $selected.Contains([int]$line)) { throw 'Required endpoint omitted' }
        }
        $completion=@($rows | Where-Object Line -EQ $witness.CompletionSourceLine)[0]
        foreach($row in $rows) {
            $isAnchor=$row.Fields.event -cin @('filetransfer_coordinator_snapshot_stream_started','filetransfer_coordinator_snapshot_stream_completed')
            if($row.Fields['coordinator_instance_id'] -ceq $completion.Fields.coordinator_instance_id) {
                $required=$isAnchor -or ($row.Line -ge $progress[0].Line -and $row.Line -le $witness.CompletionSourceLine)
                if($selected.Contains([int]$row.Line) -ne $required) { throw 'Coordinator episode coverage changed' }
            }
            if(($row.Raw -cmatch 'reason=before_publication_episode_fixture;' -or
                ($row.Line -gt $state[0].Line -and -not $isAnchor)) -and $selected.Contains([int]$row.Line)) {
                throw 'Unrelated history consumed a witness'
            }
        }
        if($settings.mode -ceq 'history') {
            if(@($events | Where-Object { $_.RawLine -cmatch 'reason=before_publication_episode_fixture;' }).Count -lt 16 -or
                @($events | Where-Object { $_.RawLine -cmatch 'event=filetransfer_v6_receiver_state_sent;' -and $_.LineNumber -gt $witness.ManifestSourceLine }).Count -lt 17) {
                throw 'Unrelated traffic fixture missing'
            }
        }
    } elseif($settings.mode -ceq 'overflow') {
        if($null -ne $witness -or $script:omission -cne 'frontier_witness_budget' -or $script:negativeCases -ne 0) {
            throw "Actual witness overflow did not omit: $script:omission"
        }
        $writes=[Collections.Generic.HashSet[int]]::new()
        foreach($commit in $r.Commits) {
            $matches=@($rows | Where-Object { $_.Fields.event -ceq 'filetransfer_v6_sparse_write_committed' -and
                $_.Fields.next_chunk_index -ceq [string]$commit.After -and $_.Fields.contiguous_chunks_committed -ceq [string]$commit.Count -and
                $_.Fields.highest_received_chunk_index -ceq [string]$commit.Highest })
            if($matches.Count -ne 1 -or -not $writes.Add([int]$matches[0].Line)) { throw 'Required overflow write missing or reused' }
        }
        # These 56 distinct writes plus failure, progress, recent arm, stall,
        # recheck, state, completion, marker and both stream anchors require 66.
        if($writes.Count -ne 56 -or $r.Lifecycle.Entries.Count -gt 64) { throw 'Required-chain overflow not exercised' }
    } elseif($settings.mode -ceq 'post-cancel') {
        if($null -ne $witness -or $script:omission -cne '' -or $script:negativeCases -ne 16) { throw 'Post-preparation cancellation did not omit' }
    } elseif($settings.mode -ceq 'conflict') {
        if($null -ne $witness -or $script:omission -cne 'frontier_state_identity' -or $script:negativeCases -ne 0) {
            throw "Conflicting frame identity did not omit: $script:omission"
        }
    } else { throw 'Unexpected test mode' }
    [IO.File]::WriteAllText((Join-Path $settings.output 'witness.json'),($witness | ConvertTo-Json -Depth 8))
    'frontier-primary-owned='+(@{OriginalFullCompactEvaluations=2;ExistingClassifierEvaluations=1;AdditionalEvaluations=0;ActualPrimaryFailure=$true;ActualNativeCustody=$true
        Mode=$settings.mode;NegativeCases=$script:negativeCases;WitnessCount=$count;LiveProofEligible=$false
        PrimaryCountsUnchanged=$true;OriginalViewsUnchanged=$true} | ConvertTo-Json -Compress)
} finally { if($null -ne $source) { $source.Dispose() }; if($null -ne $owner) { $owner.Dispose() } }
