param($Context,$Custody,$Evaluations,$Index,$Failure,$Witness,$Prepared,$OriginalMismatchProof,$RouteState,
    [string]$ArtifactDir,[string]$SupportRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
foreach($name in @('PublicationServiceYieldMatcher','PublicationRejectionChainMatcher','PublicationDispatchWindowMatcher',
    'PublicationSecurityLifecycleMatcher','PublicationTunaPairMatcher','PublicationContentCoverageMatcher','PublicationDiagnosticRejectionInputs')) {
    . (Join-Path $SupportRoot ($name+'.ps1'))
}
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Content consumer cannot evaluate authority' }
function CCheck($C=$Context) {
    YRequire ($null -ne $C -and -not $C.Disposed -and $null -ne $Custody -and
        $Custody.Owns($Evaluations,$C,$C.FullLines,$C.CompactLines,$C.FullView,$C.CompactView) -and
        $C.NativeOwner.Owns($C.NativeInputs) -and $null -ne $diagnostic -and $diagnostic.Writer.IsVerified -and
        $C.SourceOwner.OwnsCompletedSource($C.SourceReader)) 'content_primary_custody'
    YRequire ($C.SourceOwner.ValidateInputReferences($C.SourceReader,$C.FullLines,$C.FullOriginal,$C.CompactLines,$C.CompactOriginal,$C.Work) -and
        $C.SourceOwner.ValidateFullInputOrigins($C.SourceReader,$C.NativeOwner,$C.NativeInputs,$Index.AllEvents,$C.FullLines,$C.FullOriginal,$C.FullOrigins,$C.Work)) 'content_primary_origins'
    DSourceCoverage $C.FullOrigins $C.SourceOwner $C.SourceReader $C.SourceOwner.WriterContext.Outputs[3]
}
$output=[ordered]@{Schema='nlink.content-coverage-shadow/v1';Policy='pre_rejection_tuna_content_coverage_v1';Status='unproven';
    FirstMissingEdge='content_primary_custody';Coverage=$null;Candidate=$null;WitnessOrigins=@();AddedAuthorityEvaluations=0;
    AcceptanceChanged=$false;ClassifierEnabled=$false;Live=$false}
$started=$false; $diagnostic=$null
try {
    YRequire ($null -ne $Context) 'content_primary_custody'
    $Context.Work.Start(); $started=$true
    # The selected primary failure is an unproven activation, not this rejection.
    YRequire ($Failure.Class -ceq 'filetransfer_tuna_activation_delivery_unproven' -and
        $Failure.Reason -ceq 'tuna_activation_1_matched_delivery_unproven' -and
        $null -eq $Context.WriterReader -and $null -eq $Witness -and $null -eq $Prepared) 'content_primary_shape'
    $primaryEvent=$Failure.Event; $primaryBefore=ConvertTo-Json $Failure -Depth 32 -Compress
    $diagnostic=Open-PublicationDiagnosticRejection $Context $Custody $Evaluations $Index $OriginalMismatchProof
    $position=$diagnostic.Position; CCheck
    YRequire ($RouteState.FullResult.Verdict -ceq 'pass' -and $RouteState.CompactResult.Verdict -ceq 'pass' -and
        $RouteState.Parity.Verdict -ceq 'pass' -and $Context.FullLines.Length -le 4096 -and
        $Context.FullView.Boundaries.Count -le 256 -and $Context.CompactView.Boundaries.Count -le 256) 'content_primary_bounds'
    $before=ConvertTo-Json @($Context.FullView,$Context.CompactView,$OriginalMismatchProof) -Depth 64 -Compress
    $output.Candidate=[ordered]@{SourceLine=$diagnostic.Event.LineNumber;Writer=$diagnostic.Fields.log_writer_id;
        WriterSequence=[long]$diagnostic.Fields.log_writer_sequence;SourceSha256=(Get-FileTransferLocalCarrierHash $diagnostic.Event.RawLine);
        OriginalReason=$diagnostic.Candidate.RejectionDiagnostic.Reason;OriginalRecovered=$false}
    $budget=@{Records=$diagnostic.Records;CodeUnits=$diagnostic.CodeUnits;InputCodeUnits=0L}
    foreach($line in @($Context.FullLines)+@($Context.CompactLines)) { $budget.InputCodeUnits+=$line.Length }
    CWork $Context.Work $budget
    $rows=@(for($i=0;$i -lt $Context.FullLines.Length;$i++) {
        $budget.Records++; $budget.CodeUnits+=$Context.FullLines[$i].Length
        CWork $Context.Work $budget
        YRequire ($Context.FullLines[$i].Split([char[]]@(';'),257).Length -le 256) 'content_field_limit'
        [pscustomobject]@{Position=$i;Fields=(ConvertFrom-GuiSmokeLogLedgerStructuredFields $Context.FullLines[$i])}
    })
    $writerContext=$Context.SourceOwner.WriterContext
    $endpoints=@(foreach($writer in @($diagnostic.Fields.log_writer_id,($rows | Where-Object {
        $_.Fields['event'] -ceq 'filetransfer_data_path_send_result' -and
        $_.Fields['data_path_envelope_fingerprint'] -ceq $rows[$position].Fields.data_path_envelope_fingerprint
    } | Select-Object -ExpandProperty Fields | ForEach-Object { $_.log_writer_id }))) {
        DCaptureEnd $writerContext.Writers $writer $writerContext.Outputs[1] $diagnostic.Writer.LinesRead
    })
    $witnesses=[Collections.Generic.HashSet[int]]::new(); [void]$witnesses.Add($diagnostic.Event.LineNumber)
    $found=@{}; $natives=@{}; $selectedBindings=@{}
    foreach($prefix in @('full','compact')) {
        $viewWitnesses=[Collections.Generic.HashSet[int]]::new([int[]]@($witnesses))
        $snapshot=$Context.FullLines; $view=$Context.FullView; $origins=$Context.FullOrigins
        if($prefix -ceq 'compact') { $snapshot=$Context.CompactLines; $view=$Context.CompactView; $origins=$Context.CompactOrigins }
        $native=@{Reason='';SnapshotPath='';SnapshotRole=$prefix+'Snapshot';SnapshotLines=$snapshot;Effects=$view.__RoutePublicationEffects;
            Local=@{Reason='';Path='';NativeRole=$prefix+'Capture';Lines=$Context.FullLines;Publications=(New-FileTransferPublicationEvidenceIndex $view)}}
        $natives[$prefix]=$native
        $binding=Get-FileTransferLocalPublicationRow $native ($position+1L) $native.Local.NativeRole
        $selectedBindings[$prefix]=[ordered]@{Row=$binding.Row;
            PublicationOrigin=$origins[$binding.Location.publicationLine-1];EffectOrigin=$origins[$binding.Location.effectLine-1]}
        [void]$viewWitnesses.Add($selectedBindings[$prefix].PublicationOrigin)
        [void]$viewWitnesses.Add($selectedBindings[$prefix].EffectOrigin)
        [void]$witnesses.Add($selectedBindings[$prefix].PublicationOrigin)
        [void]$witnesses.Add($selectedBindings[$prefix].EffectOrigin)
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
    YRequire ((ConvertTo-FileTransferLocalCarrierJson $selectedBindings.full) -ceq
        (ConvertTo-FileTransferLocalCarrierJson $selectedBindings.compact)) 'content_selected_binding_parity'
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
    YRequire ([object]::ReferenceEquals($primaryEvent,$Failure.Event) -and $null -eq $Context.WriterReader -and
        $null -eq $Witness -and $null -eq $Prepared -and
        $primaryBefore -ceq (ConvertTo-Json $Failure -Depth 32 -Compress)) 'content_primary_failure_changed'
    CWork $Context.Work $budget
    $output.Status=$found.full.Status; $output.FirstMissingEdge=$found.full.FirstMissingEdge; $output.Coverage=$found.full.Coverage
    $output.WitnessOrigins=if($null -ne $merged.Coverage) {$merged.Coverage.WitnessOrigins} else {@($witnesses | Sort-Object)}
    $output.FullCompactParity='pass'; $output.InputsUnchanged=$true
    $output.PriorResultsUnchanged=$true; $output.CustodyControl=$true; $output.CustodyControls=3
    $output.PrimaryFailureUnchanged=$true; $output.WriterMembershipVerified=$true
} catch { $output.Status='unproven'; $output.Coverage=$null; $output.FirstMissingEdge=$_.Exception.Message; $output.FailureLocation=$_.ScriptStackTrace }
finally { if($null -ne $diagnostic) {$diagnostic.Writer.Dispose()}; if($started) { $Context.Work.Stop() } }
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'content-coverage-shadow.json'),($output | ConvertTo-Json -Depth 32 -Compress))
