param($Context,$Custody,$Evaluations,$Index,$Failure,$RouteState,[string]$ArtifactDir,[scriptblock]$Matcher,[switch]$Reciprocal,
    [switch]$RejectionChain,[scriptblock]$RejectionMatcher,[switch]$DispatchWindow,[scriptblock]$DispatchMatcher,
    [scriptblock]$DispatchControls,[switch]$SecurityLifecycle,[scriptblock]$SecurityMatcher,[scriptblock]$SecurityControls,
    [scriptblock]$AlternativeShadow,$OriginalMismatchProof,[scriptblock]$TunaPairs,[scriptblock]$TunaPairControls)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. $Matcher
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Service yield consumer cannot evaluate authority' }
function YCheckCustody($C,$F,$K,$Origins) {
    YRequire ($null -ne $C -and -not $C.Disposed -and $null -ne $Custody -and
        $Custody.Owns($Evaluations,$C,$F,$K,$C.FullView,$C.CompactView) -and
        $null -ne $C.WriterReader -and $C.WriterReader.IsVerified -and $C.NativeOwner.Owns($C.NativeInputs)) 'primary_custody'
    YRequire ($C.SourceOwner.ValidateInputReferences($C.SourceReader,$F,$C.FullOriginal,$K,$C.CompactOriginal,$C.Work) -and
        $C.SourceOwner.ValidateFullInputOrigins($C.SourceReader,$C.NativeOwner,$C.NativeInputs,$Index.AllEvents,$F,$C.FullOriginal,$Origins,$C.Work)) 'primary_origins'
}
function YBounds($FullCount,$FullBoundaries,$CompactBoundaries,$WitnessCount) {
    YRequire ($FullCount -le 4096 -and $FullBoundaries -le 256 -and $CompactBoundaries -le 256) 'fixture_limit'
    YRequire ($WitnessCount -le 64) 'witness_limit'
}
function YExpectedOmission([scriptblock]$Action,[string]$Expected) {
    $reason=''; try { & $Action } catch { $reason=$_.Exception.Message }
    YRequire ($reason -ceq $Expected) ('omission_control:'+ $Expected+':'+$reason)
}
function DCheckCapture($C,$Pin) {
    YCheckCustody $C $C.FullLines $C.CompactLines $C.FullOrigins
    YRequire ([object]::ReferenceEquals($C.SourceOwner,$Pin.Owner) -and
        [object]::ReferenceEquals($C.SourceReader,$Pin.SourceReader) -and
        [object]::ReferenceEquals($C.WriterReader,$Pin.WriterReader) -and
        [object]::ReferenceEquals($C.SourceOwner.WriterContext,$Pin.WriterContext) -and
        [object]::ReferenceEquals($Pin.WriterContext.Outputs,$Pin.Outputs) -and
        [object]::ReferenceEquals($Pin.WriterContext.Writers,$Pin.Writers) -and
        [object]::ReferenceEquals($Pin.Outputs[3],$Pin.SourceOutput) -and
        [object]::ReferenceEquals($Pin.Outputs[1],$Pin.WriterOutput) -and
        $C.SourceOwner.OwnsCompletedSource($C.SourceReader) -and $C.WriterReader.IsVerified -and
        $C.FullLines.Length -eq $C.SourceOwner.SourceLines) 'dispatch_capture_custody'
    DSourceCoverage $C.FullOrigins $C.SourceOwner $C.SourceReader $Pin.SourceOutput
}
$output=[ordered]@{Status='unproven';FirstMissingEdge='primary_custody';ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false}
$shadowOutput=[ordered]@{Schema='nlink.alternative-acceptance-shadow/v1';FirstMissingEdge='primary_custody';
    Decision=@{Verdict='unproven'};ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false}
$started=$false
try {
    YRequire ($null -ne $Context) 'primary_custody'
    $Context.Work.Start(); $started=$true
    YCheckCustody $Context $Context.FullLines $Context.CompactLines $Context.FullOrigins
    YBounds $Context.FullLines.Length $Context.FullView.Boundaries.Count $Context.CompactView.Boundaries.Count 0
    YRequire ($RouteState.FullResult.Verdict -ceq 'pass' -and $RouteState.CompactResult.Verdict -ceq 'pass' -and $RouteState.Parity.Verdict -ceq 'pass') 'route_consistency'
    $before=ConvertTo-Json @($Context.FullView,$Context.CompactView) -Depth 64 -Compress
    $position=[Array]::BinarySearch($Context.FullOrigins,[int]$Failure.Event.LineNumber)
    YRequire ($position -ge 0 -and [object]::ReferenceEquals($Failure.Event,$Index.AllEvents[$position])) 'failure_origin'
    $records=[Collections.Generic.List[object]]::new(); $transport=[Collections.Generic.List[object]]::new()
    $rejectionRows=[Collections.Generic.List[object]]::new(); $rejectionNatives=@{}
    for($i=0;$i -lt $Context.FullLines.Length;$i++) {
        $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $Context.FullLines[$i]
        if($RejectionChain) { $rejectionRows.Add([pscustomobject]@{Position=$i;Fields=$f}) }
        if($f.event -ceq 'filetransfer_fallback_tail_yield_evidence') { $records.Add([pscustomobject]@{Position=$i;Fields=$f}) }
        if($f.event -cin @('filetransfer_data_path_send_result','filetransfer_data_path_receive_result')) {
            YRequire (-not $f.ContainsKey('__nlink_duplicate_structured_field')) 'transport_fields'
            $transport.Add([pscustomobject]@{Position=$i;Fields=$f})
        }
    }
    $receive=YOne @($transport | Where-Object Position -eq $position) 'failure_transport'
    $source=YOne @($transport | Where-Object { $_.Fields.event -ceq 'filetransfer_data_path_send_result' -and
        $_.Fields.data_path_envelope_fingerprint -ceq $receive.Fields.data_path_envelope_fingerprint }) 'old_source'
    YSame $source.Fields $receive.Fields @('frame_type','data_path_envelope_fingerprint','data_path_session_id','data_path_transfer_id',
        'security_payload_fingerprint','security_sequence','security_establishment_fingerprint','security_session_id','security_transfer_id') 'old_envelope'
    YRequire ($source.Fields.delivery_result -ceq 'sent' -and $source.Fields.route -ceq 'regular_nkn_v4_fast' -and
        $receive.Fields.receive_disposition -ceq 'rejected_before_dispatch' -and $receive.Fields.route -ceq 'file_tuna_v4' -and
        $receive.Fields.authority_effect -ceq 'none' -and $source.Fields.security_context_status -ceq 'captured' -and
        $receive.Fields.security_context_status -ceq 'captured') 'old_packet_topology'
    $witnesses=[Collections.Generic.HashSet[int]]::new(); $normalized=@{}; $reverseNormalized=@{}
    foreach($prefix in @('full','compact')) {
        $view=$Context.FullView; $snapshot=$Context.FullLines; $origins=$Context.FullOrigins
        if($prefix -ceq 'compact') { $view=$Context.CompactView; $snapshot=$Context.CompactLines; $origins=$Context.CompactOrigins }
        YRequire ($view.StreamComplete -and $view.ArtifactEvidenceVerdict -ceq 'pass' -and @($view.Boundaries | Where-Object verdict -CNE 'pass').Count -eq 0) 'authority_view'
        $native=@{Reason='';SnapshotPath='';SnapshotRole=$prefix+'Snapshot';SnapshotLines=$snapshot;Effects=$view.__RoutePublicationEffects
            Local=@{Reason='';Path='';NativeRole=$prefix+'Capture';Lines=$Context.FullLines;Publications=(New-FileTransferPublicationEvidenceIndex $view)}}
        if($RejectionChain) { $rejectionNatives[$prefix]=$native }
        foreach($row in @($source,$receive)) {
            $forward=Resolve-FileTransferPublicationForwardingEvidence $native '' ($row.Position+1L) $Context.FullLines[$row.Position] ($prefix+'Capture')
            YRequire ($forward.Status -ceq 'matched_local_forwarding') 'local_forwarding'
            [void]$witnesses.Add($Context.FullOrigins[$row.Position])
        }
        $old=Get-FileTransferLocalPublicationRow $native ($source.Position+1L) ($prefix+'Capture')
        $current=Get-FileTransferLocalPublicationRow $native ($receive.Position+1L) ($prefix+'Capture')
        $candidate=Get-ServiceYieldReceiptCandidate $records.ToArray() $view.Boundaries $old.Row $current.Row
        # Local security installations must not change across the selected
        # packet/yield episode. Never compare these counters across peers.
        YSame $source.Fields $candidate.Copy.Fields @('security_transport_id','security_installation_id','security_installation_generation') 'source_security_episode'
        YSame $receive.Fields $candidate.Ingress.Fields @('security_transport_id','security_installation_id','security_installation_generation') 'receiver_security_episode'
        $rows=@($candidate.Local.Record,$candidate.Copy,$candidate.Ingress,$candidate.Peer.Record)
        $boundaries=@($candidate.OldBoundary,$candidate.Local.Boundary,$candidate.Commit)+@($candidate.Chain)
        if($Reciprocal) {
            $reverse=Get-ServiceYieldReciprocalCandidate $records.ToArray() $view.Boundaries $old.Row $current.Row $candidate
            $rows+=@($reverse.Local.Record,$reverse.Copy,$reverse.Ingress,$reverse.Peer.Record)
            $boundaries+=@($reverse.Chain)
            $reverseNormalized[$prefix]=[ordered]@{LocalBoundary=$reverse.Local.Boundary.boundaryId;PeerBoundary=$reverse.Peer.Boundary.boundaryId;
                ProofIdentity=$reverse.ProofIdentity;Copy=$reverse.Copy.Fields.yield_evidence_copy;
                LocalPosition=$reverse.Local.Record.Position;CopyPosition=$reverse.Copy.Position;
                IngressPosition=$reverse.Ingress.Position;PeerPosition=$reverse.Peer.Record.Position;
                SourceRelease=$reverse.SourcePair.Release;ReceiverRelease=$reverse.ReceiverPair.Release}
        }
        foreach($row in $rows) {
            [void]$witnesses.Add($Context.FullOrigins[$row.Position])
        }
        foreach($b in $boundaries) {
            YRequire ($b.evidenceLineIndex -ge 0 -and $b.evidenceLineIndex -lt $snapshot.Length) 'boundary_origin'
            $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $snapshot[$b.evidenceLineIndex]
            YRequire (-not $f.ContainsKey('__nlink_duplicate_structured_field') -and $f.event -ceq 'filetransfer_coordinator_transition_snapshot' -and
                $f.transition_id -ceq $b.boundaryId -and $f.snapshot_sha256 -ceq $b.boundaryHash -and
                $f.log_writer_id -ceq $b.writer -and $f.log_writer_sequence -ceq [string]$b.writerSequence) 'boundary_origin'
            [void]$witnesses.Add($origins[$b.evidenceLineIndex])
        }
        YBounds $Context.FullLines.Length $Context.FullView.Boundaries.Count $Context.CompactView.Boundaries.Count $witnesses.Count
        $normalized[$prefix]=[ordered]@{Old=$old.Row;Current=$current.Row;LocalBoundary=$candidate.Local.Boundary.boundaryId;
            PeerBoundary=$candidate.Peer.Boundary.boundaryId;PairingBoundary=$candidate.Pairing.boundaryId;ProofIdentity=$candidate.ProofIdentity;
            Copy=$candidate.Copy.Fields.yield_evidence_copy;LocalPosition=$candidate.Local.Record.Position;CopyPosition=$candidate.Copy.Position;
            IngressPosition=$candidate.Ingress.Position;PeerPosition=$candidate.Peer.Record.Position;Handoff=$candidate.Handoff}
    }
    YRequire ((ConvertTo-FileTransferLocalCarrierJson $normalized.full) -ceq (ConvertTo-FileTransferLocalCarrierJson $normalized.compact)) 'candidate_parity'
    if($Reciprocal) {
        YRequire ((ConvertTo-FileTransferLocalCarrierJson $reverseNormalized.full) -ceq (ConvertTo-FileTransferLocalCarrierJson $reverseNormalized.compact)) 'reciprocal_candidate_parity'
    }
    # Actual ownership checks: replacement arrays/context/origins cannot borrow
    # this invocation's custody. Do not dispose or mutate the original capability.
    YExpectedOmission { YCheckCustody $null $Context.FullLines $Context.CompactLines $Context.FullOrigins } 'primary_custody'
    YExpectedOmission { YCheckCustody $Context ($Context.FullLines.Clone()) $Context.CompactLines $Context.FullOrigins } 'primary_custody'
    YExpectedOmission { YCheckCustody $Context $Context.FullLines ($Context.CompactLines.Clone()) $Context.FullOrigins } 'primary_custody'
    $badOrigins=$Context.FullOrigins.Clone(); $badOrigins[0]++
    YExpectedOmission { YCheckCustody $Context $Context.FullLines $Context.CompactLines $badOrigins } 'primary_origins'
    YExpectedOmission { YBounds 4097 1 1 0 } 'fixture_limit'
    YExpectedOmission { YBounds 1 257 1 0 } 'fixture_limit'
    YExpectedOmission { YBounds 1 1 257 0 } 'fixture_limit'
    YExpectedOmission { YBounds 1 1 1 65 } 'witness_limit'
    YCheckCustody $Context $Context.FullLines $Context.CompactLines $Context.FullOrigins
    YRequire ($before -ceq (ConvertTo-Json @($Context.FullView,$Context.CompactView) -Depth 64 -Compress)) 'views_changed'
    YCheckCustody $Context $Context.FullLines $Context.CompactLines $Context.FullOrigins
    $output.Status='matched_original_yield_receipts'; $output.FirstMissingEdge='(none)'
    $output.Candidate=$normalized.full; $output.FullCompactParity='pass'; $output.InputsUnchanged=$true
    $output.DiagnosticRecords=$records.Count; $output.WitnessOrigins=@($witnesses | Sort-Object); $output.OmissionControls=8
    if($Reciprocal) { $output.ReciprocalStatus='matched_original_reciprocal_receipts'; $output.ReciprocalCandidate=$reverseNormalized.full }
    if($RejectionChain) {
        # An optional chain failure must not erase the already matched yield result.
        $output.RejectionChainStatus='unproven'
        try {
            YRequire ($Reciprocal -and $null -ne $RejectionMatcher) 'rejection_requires_reciprocal'
            . $RejectionMatcher
            YCheckCustody $Context $Context.FullLines $Context.CompactLines $Context.FullOrigins
            $chainWitnesses=[Collections.Generic.HashSet[int]]::new($witnesses); $chains=@{}
            foreach($prefix in @('full','compact')) {
                $found=Get-PublicationRejectionChain $rejectionRows.ToArray() $source $receive $rejectionNatives[$prefix] $normalized[$prefix].Current
                $chains[$prefix]=$found.Candidate
                $origins=$Context.FullOrigins
                if($prefix -ceq 'compact') { $origins=$Context.CompactOrigins }
                foreach($row in $found.Rows) { [void]$chainWitnesses.Add($Context.FullOrigins[$row.Position]) }
                foreach($binding in $found.Bindings) {
                    [void]$chainWitnesses.Add($origins[$binding.Location.publicationLine-1])
                    [void]$chainWitnesses.Add($origins[$binding.Location.effectLine-1])
                }
                JWitnessBound $chainWitnesses
                YCheckCustody $Context $Context.FullLines $Context.CompactLines $Context.FullOrigins
            }
            JParity $chains.full $chains.compact
            YRequire ($Context.Work.ElapsedMilliseconds -le [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds) 'rejection_work_limit'
            YRequire ($before -ceq (ConvertTo-Json @($Context.FullView,$Context.CompactView) -Depth 64 -Compress)) 'rejection_views_changed'
            YCheckCustody $Context $Context.FullLines $Context.CompactLines $Context.FullOrigins
            $output.RejectionChainStatus='matched_original_rejection_chain'; $output.RejectionChainFirstMissingEdge='(none)'
            $output.RejectionChainCandidate=$chains.full; $output.RejectionChainFullCompactParity='pass'
            $output.WitnessOrigins=@($chainWitnesses | Sort-Object)
        } catch { $output.RejectionChainFirstMissingEdge=$_.Exception.Message; $output.RejectionChainFailureLocation=$_.ScriptStackTrace }
    }
    if($DispatchWindow) {
        $output.DispatchWindowStatus='unproven'
        try {
            YRequire ($RejectionChain -and $output.RejectionChainStatus -ceq 'matched_original_rejection_chain' -and
                $null -ne $DispatchMatcher) 'dispatch_requires_rejection_chain'
            . $DispatchMatcher
            $owner=$Context.SourceOwner; $writerContext=$owner.WriterContext
            YRequire ($writerContext.Outputs.Count -eq 4) 'dispatch_capture_outputs'
            $pin=@{Owner=$owner;SourceReader=$Context.SourceReader;WriterReader=$Context.WriterReader;WriterContext=$writerContext;
                Outputs=$writerContext.Outputs;Writers=$writerContext.Writers;SourceOutput=$writerContext.Outputs[3];WriterOutput=$writerContext.Outputs[1]}
            DCheckCapture $Context $pin
            $endpoint=DCaptureEnd $pin.Writers $normalized.full.Current.writer $pin.WriterOutput $Context.WriterReader.LinesRead
            $output.DispatchWindow=DInterval $normalized.full.Current $chains.full $endpoint
            $dispatchWitnesses=[Collections.Generic.HashSet[int]]::new($chainWitnesses); $windows=@{}
            foreach($prefix in @('full','compact')) {
                $found=Get-PublicationDispatchWindow $rejectionRows.ToArray() $receive $normalized[$prefix].Current $chains[$prefix] $endpoint $rejectionNatives[$prefix]
                $windows[$prefix]=$found.Result
                $origins=$Context.FullOrigins
                if($prefix -ceq 'compact') { $origins=$Context.CompactOrigins }
                foreach($position in $found.Positions) { [void]$dispatchWitnesses.Add($Context.FullOrigins[$position]) }
                foreach($binding in $found.Bindings) {
                    [void]$dispatchWitnesses.Add($origins[$binding.Location.publicationLine-1])
                    [void]$dispatchWitnesses.Add($origins[$binding.Location.effectLine-1])
                }
                DWitnessBound $dispatchWitnesses
                DCheckCapture $Context $pin
            }
            DParity $windows.full $windows.compact
            # Controls use detached replacement rows only and never acquire
            # custody, issue authority or replace the original query result.
            if($null -ne $DispatchControls) {
                $output.DispatchStructuralControls=& $DispatchControls -Rows $rejectionRows.ToArray() -Receive $receive -Current $normalized.full.Current `
                    -Chain $chains.full -Endpoint $endpoint -Natives $rejectionNatives -Origins $Context.FullOrigins `
                    -Source $owner -Reader $Context.SourceReader -SourceOutput $pin.SourceOutput -Writers $pin.Writers `
                    -WriterOutput $pin.WriterOutput -WriterLinesRead $Context.WriterReader.LinesRead
            }
            $badPin=$pin.Clone(); $badPin.WriterContext=$null
            YExpectedOmission { DCheckCapture $Context $badPin } 'dispatch_capture_custody'
            DCheckCapture $Context $pin
            YRequire ([object]::ReferenceEquals($endpoint,(DCaptureEnd $pin.Writers $normalized.full.Current.writer $pin.WriterOutput $Context.WriterReader.LinesRead))) 'dispatch_endpoint_changed'
            YRequire ($Context.Work.ElapsedMilliseconds -le [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds) 'dispatch_work_limit'
            YRequire ($before -ceq (ConvertTo-Json @($Context.FullView,$Context.CompactView) -Depth 64 -Compress)) 'dispatch_views_changed'
            DCheckCapture $Context $pin
            $output.DispatchWindowStatus=$windows.full.Status; $output.DispatchWindow=$windows.full
            $output.DispatchWindowFirstMissingEdge='(none)'; $output.DispatchWindowFullCompactParity='pass'
            $output.DispatchWindowCustodyControl=$true; $output.WitnessOrigins=@($dispatchWitnesses | Sort-Object)
        } catch {
            $output.DispatchWindowFirstMissingEdge=$_.Exception.Message
            $output.DispatchWindowUnboundObservationReason=$_.Exception.Message
            $output.DispatchWindowFailureLocation=$_.ScriptStackTrace
        }
    }
    if($SecurityLifecycle) {
        $output.SecurityLifecycleStatus='unproven'
        try {
            YRequire ($DispatchWindow -and $output.Contains('DispatchWindowFullCompactParity') -and
                $output.DispatchWindowFullCompactParity -ceq 'pass' -and $null -ne $SecurityMatcher) 'security_span_requires_dispatch_capture'
            . $SecurityMatcher
            DCheckCapture $Context $pin
            $endpoints=@(foreach($writer in @($normalized.full.Old.writer,$normalized.full.Current.writer)) {
                DCaptureEnd $pin.Writers $writer $pin.WriterOutput $Context.WriterReader.LinesRead
            })
            $spanWitnesses=[Collections.Generic.HashSet[int]]::new($dispatchWitnesses); $spans=@{}; $selections=@{}
            foreach($prefix in @('full','compact')) {
                $c=$chains[$prefix]; $forward=$normalized[$prefix]; $reverse=$reverseNormalized[$prefix]
                $selections[$prefix]=[int[]]@($c.SourcePosition,$forward.CopyPosition,$forward.IngressPosition,
                    $reverse.CopyPosition,$reverse.IngressPosition,$c.ReceivePosition,$c.MismatchPosition,$c.IgnorePosition)
                $found=Get-RecordedSecurityContextSpans $rejectionRows.ToArray() $selections[$prefix] $endpoints $Context.Work
                $spans[$prefix]=$found.Result
                foreach($position in $found.Positions) { [void]$spanWitnesses.Add($Context.FullOrigins[$position]) }
                SWitnessBound $spanWitnesses
                DCheckCapture $Context $pin
            }
            SParity $spans.full $spans.compact
            if($null -ne $SecurityControls) {
                $output.SecurityLifecycleControls=& $SecurityControls -Rows $rejectionRows.ToArray() -SelectedPositions $selections.full -Endpoints $endpoints -Work $Context.Work
            }
            $badPin=$pin.Clone(); $badPin.SourceReader=$null
            YExpectedOmission { DCheckCapture $Context $badPin } 'dispatch_capture_custody'
            DCheckCapture $Context $pin
            foreach($end in $endpoints) {
                YRequire ([object]::ReferenceEquals($end,(DCaptureEnd $pin.Writers $end.WriterId $pin.WriterOutput $Context.WriterReader.LinesRead))) 'security_span_endpoint_changed'
            }
            YRequire ($Context.Work.ElapsedMilliseconds -le [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds) 'security_span_work_limit'
            YRequire ($before -ceq (ConvertTo-Json @($Context.FullView,$Context.CompactView) -Depth 64 -Compress)) 'security_span_views_changed'
            DCheckCapture $Context $pin
            $output.SecurityLifecycleStatus=$spans.full.Status; $output.SecurityLifecycle=$spans.full
            $output.SecurityLifecycleFirstMissingEdge='(none)'; $output.SecurityLifecycleFullCompactParity='pass'
            $output.SecurityLifecycleCustodyControl=$true; $output.WitnessOrigins=@($spanWitnesses | Sort-Object)
        } catch { $output.SecurityLifecycleFirstMissingEdge=$_.Exception.Message; $output.SecurityLifecycleFailureLocation=$_.ScriptStackTrace }
    }
    if($null -ne $AlternativeShadow) {
        try {
            . $AlternativeShadow
            YCheckCustody $Context $Context.FullLines $Context.CompactLines $Context.FullOrigins
            $prior=ConvertTo-Json @($output,$OriginalMismatchProof) -Depth 64 -Compress
            $candidateShadow=Get-AlternativeAcceptanceShadow $output $OriginalMismatchProof $Failure
            # Independent positive route-use evidence does not depend on stale
            # rejection admission. Preserve any earlier diagnostic gap verbatim.
            if($null -ne $TunaPairs) {
                . $TunaPairs
                DCheckCapture $Context $pin
                $payloadGates=[ordered]@{}
                foreach($activation in @(0,1)) {
                    $pairs=@{}
                    foreach($prefix in @('full','compact')) {
                        $view=if($prefix -ceq 'full') {$Context.FullView} else {$Context.CompactView}
                        $pairs[$prefix]=Get-TunaPublicationPayloadGate $rejectionRows.ToArray() $rejectionNatives[$prefix] $view `
                            $normalized[$prefix].Old $normalized[$prefix].Current $activation $Context.Work
                    }
                    $name=if($activation -eq 0) {'first_tuna_payload'} else {'second_tuna_payload'}
                    $payloadGates[$name]=Merge-TunaPayloadGate $pairs.full $pairs.compact
                    $candidateShadow.Gates[$name]=$payloadGates[$name].Status
                }
                $candidateShadow.PayloadGates=$payloadGates
                if($null -ne $TunaPairControls) {
                    $candidateShadow.PayloadControls=& $TunaPairControls -Rows $rejectionRows.ToArray() -Native $rejectionNatives.full `
                        -View $Context.FullView -Sender $normalized.full.Old -Receiver $normalized.full.Current -Work $Context.Work
                }
                $candidateShadow.Decision=Get-AlternativeAcceptanceShadowDecision $candidateShadow.Gates
                DCheckCapture $Context $pin
            }
            YBounds $Context.FullLines.Length $Context.FullView.Boundaries.Count $Context.CompactView.Boundaries.Count $output.WitnessOrigins.Count
            YRequire ($before -ceq (ConvertTo-Json @($Context.FullView,$Context.CompactView) -Depth 64 -Compress)) 'shadow_views_changed'
            YRequire ($prior -ceq (ConvertTo-Json @($output,$OriginalMismatchProof) -Depth 64 -Compress)) 'shadow_prior_results_changed'
            YCheckCustody $Context $Context.FullLines $Context.CompactLines $Context.FullOrigins
            YRequire ($Context.Work.ElapsedMilliseconds -le [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds) 'shadow_work_limit'
            if($null -ne $TunaPairs) { $candidateShadow.PayloadWorkElapsedMilliseconds=$Context.Work.ElapsedMilliseconds }
            $candidateShadow.InputsUnchanged=$true
            $candidateShadow.PriorResultsUnchanged=$true
            $candidateShadow.WitnessOrigins=$output.WitnessOrigins
            $shadowOutput=$candidateShadow
        } catch { $shadowOutput.FirstMissingEdge=$_.Exception.Message }
    }
} catch { $output.FirstMissingEdge=$_.Exception.Message; $output.FailureLocation=$_.ScriptStackTrace }
finally { if($started) { $Context.Work.Stop() } }
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'service-yield-consumer.json'),($output | ConvertTo-Json -Depth 32 -Compress))
if($null -ne $AlternativeShadow) {
    [IO.File]::WriteAllText((Join-Path $ArtifactDir 'alternative-acceptance-shadow.json'),($shadowOutput | ConvertTo-Json -Depth 16 -Compress))
}
