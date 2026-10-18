# Test-only acquisition for optional diagnostics, never a primary failure witness.
# The caller owns the returned writer and the original cumulative work interval.
function Assert-PublicationDiagnosticInputs($Context,$Custody,$Evaluations,$Index) {
    YRequire ($null -ne $Context -and -not $Context.Disposed -and $null -ne $Custody -and
        $Custody.Owns($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView) -and
        $Context.NativeOwner.Owns($Context.NativeInputs) -and
        $Context.SourceOwner.OwnsCompletedSource($Context.SourceReader)) 'diagnostic_primary_custody'
    YRequire ($Context.Work.ElapsedMilliseconds -le [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds -and
        $Context.FullLines.Length -le 4096 -and $Context.FullView.Boundaries.Count -le 256 -and
        $Context.CompactView.Boundaries.Count -le 256) 'diagnostic_primary_bounds'
    YRequire ($Index.LogFiles.Count -eq 1 -and $Index.LogFiles[0] -ceq $Context.SourceOwner.SourcePath -and
        $Index.AllEvents.Count -eq $Context.FullLines.Length -and
        $Context.SourceOwner.ValidateInputReferences($Context.SourceReader,$Context.FullLines,$Context.FullOriginal,
            $Context.CompactLines,$Context.CompactOriginal,$Context.Work) -and
        $Context.SourceOwner.ValidateFullInputOrigins($Context.SourceReader,$Context.NativeOwner,$Context.NativeInputs,
            $Index.AllEvents,$Context.FullLines,$Context.FullOriginal,$Context.FullOrigins,$Context.Work)) 'diagnostic_primary_origins'
}
function Open-PublicationDiagnosticRejection($Context,$Custody,$Evaluations,$Index,$Proof) {
    Assert-PublicationDiagnosticInputs $Context $Custody $Evaluations $Index
    YRequire ($null -ne $Proof -and @($Proof.CandidateRecords).Count -le 4096) 'diagnostic_candidate_shape'
    $candidate=YOne @($Proof.CandidateRecords | Where-Object {
        $_.Event.EventName -ceq 'filetransfer_data_path_receive_result'
    }) 'diagnostic_candidate'
    $rejection=$candidate.RejectionDiagnostic
    YRequire ($candidate.Recovered -is [bool] -and -not $candidate.Recovered -and $null -ne $rejection -and
        $rejection.Recovered -is [bool] -and -not $rejection.Recovered -and
        $rejection.RejectionChainMatched -is [bool] -and $rejection.RejectionChainMatched -and
        $rejection.Reason -cin @('missing_exact_tuna_receive','missing_tuna_resend')) 'diagnostic_rejection_shape'
    $event=$candidate.Event
    YRequire ($event.LineNumber -is [int] -and $event.LineNumber -gt 0 -and
        $event.LineNumber -le $Context.SourceOwner.SourceLines -and $event.Sequence -is [int] -and
        $event.Sequence -eq $event.LineNumber -and $event.FilePath -ceq $Context.SourceOwner.SourcePath) 'diagnostic_event_shape'
    $position=[Array]::BinarySearch($Context.FullOrigins,$event.LineNumber)
    YRequire ($position -ge 0 -and [object]::ReferenceEquals($event,$Index.AllEvents[$position]) -and
        [object]::ReferenceEquals($event.RawLine,$Context.FullLines[$position]) -and
        [object]::ReferenceEquals($event.RawLine,$Context.FullOriginal[$position])) 'diagnostic_event_origin'
    $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $event.RawLine
    YRequire (-not $fields.ContainsKey('__nlink_duplicate_structured_field') -and
        $fields.log_writer_id -is [string] -and -not [string]::IsNullOrWhiteSpace($fields.log_writer_id) -and
        $fields.log_writer_sequence -cmatch '\A[1-9][0-9]*\z') 'diagnostic_writer_shape'
    $descriptor=$Context.SourceOwner.WriterContext.Outputs[1]
    YRequire ($Context.RecordVisitCeiling+$Context.FullLines.Length+$Context.CompactLines.Length+
        2L*$descriptor.LineCount -le [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords) 'diagnostic_writer_record_limit'
    $writer=$null; $retained=$false
    try {
        $writer=$Context.SourceOwner.TryOpenWriter()
        YRequire ($null -ne $writer) 'diagnostic_writer_unavailable'
        $membership=$writer.TryVerifyMembership($Context.FullLines,$Context.FullOriginal,$fields.log_writer_id,
            [long]$fields.log_writer_sequence,$Context.NativeOwner.CodeUnitVisits,$Context.Work)
        YRequire ($null -ne $membership -and $writer.IsVerified -and
            $membership.SelectedIndex -eq $position) 'diagnostic_writer_membership'
        $records=$Context.RecordVisitCeiling+$membership.RecordVisits
        YRequire ($records -le [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords -and
            $membership.CodeUnitVisits -le [NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits) 'diagnostic_writer_input_limit'
        Assert-PublicationDiagnosticInputs $Context $Custody $Evaluations $Index
        $retained=$true
        [pscustomobject]@{Candidate=$candidate;Event=$event;Position=$position;Writer=$writer;
            Records=[long]$records;CodeUnits=[long]$membership.CodeUnitVisits;Fields=$fields}
    } finally { if(-not $retained -and $null -ne $writer) {$writer.Dispose()} }
}
