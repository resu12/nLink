param($Context,$Custody,$Evaluations,$Index,$Failure,$Witness,$Prepared,$OriginalMismatchProof,$RouteState,
    [string]$ArtifactDir,[string]$SupportRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
foreach($name in @('PublicationServiceYieldMatcher','PublicationRejectionChainMatcher','PublicationDispatchWindowMatcher',
    'PublicationSecurityLifecycleMatcher','PublicationTunaPairMatcher','PublicationContentCoverageMatcher')) {
    . (Join-Path $SupportRoot ($name+'.ps1'))
}
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Content consumer cannot evaluate authority' }
function CCheck($C=$Context) {
    YRequire ($null -ne $C -and -not $C.Disposed -and $null -ne $Custody -and
        $Custody.Owns($Evaluations,$C,$C.FullLines,$C.CompactLines,$C.FullView,$C.CompactView) -and
        $C.NativeOwner.Owns($C.NativeInputs) -and $C.WriterReader.IsVerified -and
        $C.SourceOwner.OwnsCompletedSource($C.SourceReader)) 'content_primary_custody'
    YRequire ($C.SourceOwner.ValidateInputReferences($C.SourceReader,$C.FullLines,$C.FullOriginal,$C.CompactLines,$C.CompactOriginal,$C.Work) -and
        $C.SourceOwner.ValidateFullInputOrigins($C.SourceReader,$C.NativeOwner,$C.NativeInputs,$Index.AllEvents,$C.FullLines,$C.FullOriginal,$C.FullOrigins,$C.Work)) 'content_primary_origins'
    DSourceCoverage $C.FullOrigins $C.SourceOwner $C.SourceReader $C.SourceOwner.WriterContext.Outputs[3]
}
$output=[ordered]@{Schema='nlink.content-coverage-shadow/v1';Policy='pre_rejection_tuna_content_coverage_v1';Status='unproven';
    FirstMissingEdge='content_primary_custody';Coverage=$null;Candidate=$null;WitnessOrigins=@();AddedAuthorityEvaluations=0;
    AcceptanceChanged=$false;ClassifierEnabled=$false;Live=$false}
$started=$false
try {
    YRequire ($null -ne $Context) 'content_primary_custody'
    $Context.Work.Start(); $started=$true; CCheck
    YRequire ($null -ne $Prepared -and $null -ne $Witness -and $Failure.Class -ceq 'file_tuna_data_path_mismatch' -and
        $Witness.Policy -ceq 'primary_reported_failure_witnesses_v1' -and $Witness.ReportClass -ceq $Failure.Class -and
        $Witness.ReportReason -ceq $Failure.Reason -and $Witness.SourcePath -ceq $Context.SourceOwner.SourcePath -and
        $Witness.SourceLine -eq $Failure.Event.LineNumber -and $Witness.EventName -ceq $Failure.Event.EventName -and
        $Prepared.Status -ceq 'prepared_primary_publication_witness') 'content_selected_failure'
    $selected=@($OriginalMismatchProof.EffectiveCandidateRecords | Where-Object { [object]::ReferenceEquals($_.Event,$Failure.Event) })
    YRequire ($selected.Count -eq 1 -and -not $selected[0].Recovered) 'content_selected_candidate'
    $position=[Array]::BinarySearch($Context.FullOrigins,[int]$Witness.SourceLine)
    YRequire ($position -ge 0 -and [object]::ReferenceEquals($Failure.Event,$Index.AllEvents[$position]) -and
        (Get-FileTransferLocalCarrierHash $Context.FullLines[$position]) -ceq $Witness.ContentSha256) 'content_failure_origin'
    YRequire ($RouteState.FullResult.Verdict -ceq 'pass' -and $RouteState.CompactResult.Verdict -ceq 'pass' -and
        $RouteState.Parity.Verdict -ceq 'pass' -and $Context.FullLines.Length -le 4096 -and
        $Context.FullView.Boundaries.Count -le 256 -and $Context.CompactView.Boundaries.Count -le 256) 'content_primary_bounds'
    $before=ConvertTo-Json @($Context.FullView,$Context.CompactView,$OriginalMismatchProof) -Depth 64 -Compress
    $output.Candidate=[ordered]@{SourceLine=$Witness.SourceLine;Writer=$Witness.Writer;WriterSequence=$Witness.WriterSequence;
        SourceSha256=$Witness.ContentSha256;OriginalReason=$selected[0].RejectionDiagnostic.Reason;OriginalRecovered=$false}
    $budget=@{Records=[long]$Prepared.Work.records;CodeUnits=[long]$Prepared.Work.codeUnits;InputCodeUnits=0L}
    foreach($line in @($Context.FullLines)+@($Context.CompactLines)) { $budget.InputCodeUnits+=$line.Length }
    CWork $Context.Work $budget
    $rows=@(for($i=0;$i -lt $Context.FullLines.Length;$i++) {
        CWork $Context.Work $budget
        YRequire ($Context.FullLines[$i].Split([char[]]@(';'),257).Length -le 256) 'content_field_limit'
        [pscustomobject]@{Position=$i;Fields=(ConvertFrom-GuiSmokeLogLedgerStructuredFields $Context.FullLines[$i])}
    })
    $writerContext=$Context.SourceOwner.WriterContext
    $endpoints=@(foreach($writer in @($Witness.Writer,($rows | Where-Object {
        $_.Fields['event'] -ceq 'filetransfer_data_path_send_result' -and
        $_.Fields['data_path_envelope_fingerprint'] -ceq $rows[$position].Fields.data_path_envelope_fingerprint
    } | Select-Object -ExpandProperty Fields | ForEach-Object { $_.log_writer_id }))) {
        DCaptureEnd $writerContext.Writers $writer $writerContext.Outputs[1] $Context.WriterReader.LinesRead
    })
    $witnesses=[Collections.Generic.HashSet[int]]::new(); [void]$witnesses.Add([int]$Witness.SourceLine)
    $priorOrigins=$Prepared.ProvenanceJson | ConvertFrom-Json
    [void]$witnesses.Add([int]$priorOrigins.publicationSourceLine); [void]$witnesses.Add([int]$priorOrigins.effectSourceLine)
    $found=@{}; $natives=@{}
    foreach($prefix in @('full','compact')) {
        $viewWitnesses=[Collections.Generic.HashSet[int]]::new([int[]]@($witnesses))
        $snapshot=$Context.FullLines; $view=$Context.FullView; $origins=$Context.FullOrigins
        if($prefix -ceq 'compact') { $snapshot=$Context.CompactLines; $view=$Context.CompactView; $origins=$Context.CompactOrigins }
        $native=@{Reason='';SnapshotPath='';SnapshotRole=$prefix+'Snapshot';SnapshotLines=$snapshot;Effects=$view.__RoutePublicationEffects;
            Local=@{Reason='';Path='';NativeRole=$prefix+'Capture';Lines=$Context.FullLines;Publications=(New-FileTransferPublicationEvidenceIndex $view)}}
        $natives[$prefix]=$native
        $query=Get-PublicationContentCoverage $rows $native $view $position $endpoints $Context.Work $budget
        $found[$prefix]=$query.Result
        if($query.Result.Status -ceq 'pass') {
            foreach($p in $query.Result.Coverage.Positions) { [void]$viewWitnesses.Add($Context.FullOrigins[$p]) }
            foreach($binding in $query.Bindings) {
                [void]$viewWitnesses.Add($origins[$binding.Location.publicationLine-1]); [void]$viewWitnesses.Add($origins[$binding.Location.effectLine-1])
            }
            foreach($publication in $query.Publications) {
                $b=$publication.Boundary; $physical=$origins[$b.evidenceLineIndex]; $f=$rows[$physical-1].Fields
                YRequire ($f.event -ceq 'filetransfer_coordinator_transition_snapshot' -and $f.transition_id -ceq $b.boundaryId -and
                    $f.snapshot_sha256 -ceq $b.boundaryHash -and $f.log_writer_id -ceq $b.writer -and
                    $f.log_writer_sequence -ceq [string]$b.writerSequence) 'content_publication_origin'
                [void]$viewWitnesses.Add($physical)
            }
            $query.Result.Coverage.WitnessOrigins=@($viewWitnesses | Sort-Object)
        }
        CWitnesses $viewWitnesses
        CCheck; CWork $Context.Work $budget
    }
    $merged=Merge-PublicationContentCoverage $found.full $found.compact
    YRequire ($merged.FirstMissingEdge -cne 'content_parity') 'content_parity'
    if($env:NLINK_PUBLICATION_CONTENT_COVERAGE_CONTROLS -ceq '1' -and $merged.Status -ceq 'pass') {
        $output.Controls=& ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $SupportRoot 'PublicationContentCoverageControls.ps1')))) -Rows $rows -Native $natives.full `
            -View $Context.FullView -FailurePosition $position -Endpoints $endpoints -Work $Context.Work -Budget $budget -Positive $merged
    }
    # These exercise real invocation custody, not a synthetic replacement owner.
    $reason=''; try { CCheck $null } catch { $reason=$_.Exception.Message }
    YRequire ($reason -ceq 'content_primary_custody') 'content_custody_control'
    foreach($name in @('FullLines','CompactLines')) {
        $copy=$Context.PSObject.Copy(); $copy.$name=$Context.$name.Clone()
        $reason=''; try { CCheck $copy } catch { $reason=$_.Exception.Message }
        YRequire ($reason -ceq 'content_primary_custody') 'content_custody_control'
    }
    CCheck
    YRequire ($before -ceq (ConvertTo-Json @($Context.FullView,$Context.CompactView,$OriginalMismatchProof) -Depth 64 -Compress)) 'content_prior_results_changed'
    CWork $Context.Work $budget
    $output.Status=$found.full.Status; $output.FirstMissingEdge=$found.full.FirstMissingEdge; $output.Coverage=$found.full.Coverage
    $output.WitnessOrigins=if($null -ne $merged.Coverage) {$merged.Coverage.WitnessOrigins} else {@($witnesses | Sort-Object)}
    $output.FullCompactParity='pass'; $output.InputsUnchanged=$true
    $output.PriorResultsUnchanged=$true; $output.CustodyControl=$true; $output.CustodyControls=3
} catch { $output.Status='unproven'; $output.Coverage=$null; $output.FirstMissingEdge=$_.Exception.Message; $output.FailureLocation=$_.ScriptStackTrace }
finally { if($started) { $Context.Work.Stop() } }
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'content-coverage-shadow.json'),($output | ConvertTo-Json -Depth 32 -Compress))
