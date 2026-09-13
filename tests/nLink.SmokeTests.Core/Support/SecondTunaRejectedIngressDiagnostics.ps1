param([string]$RepoRoot, [string]$FixtureBase64, [string]$GeneratedLogPath = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')

function Get-SecondTunaProofProjection {
    param($Proof, [switch]$ExcludeRawLine)
    $records = @(foreach ($kind in @('CandidateRecords', 'EffectiveCandidateRecords')) {
        foreach ($record in @($Proof.$kind | Sort-Object { $_.Event.Sequence })) {
            [pscustomobject]@{
                Kind=$kind; Event=$record.Event.EventName; File=$record.Event.FilePath;
                Line=$record.Event.LineNumber; Sequence=$record.Event.Sequence; RawLine=$(if (-not $ExcludeRawLine) { $record.Event.RawLine });
                Key=(Get-FileTransferFileTunaV4DataPathMismatchCandidateKey -Event $record.Event);
                Recovered=$record.Recovered; Variant=$record.Variant; Diagnostic=$record.RejectionDiagnostic
            }
        }
    })
    return ([pscustomobject]@{
        Raw=$Proof.RawCount; Candidates=$Proof.RawCandidateCount; Distinct=$Proof.DistinctCount;
        Recovered=$Proof.RecoveredCount; Unresolved=$Proof.UnresolvedCount; Effective=$Proof.EffectiveCount;
        Records=$records
    } | ConvertTo-Json -Depth 8 -Compress)
}

function Assert-SecondTunaCompactParity {
    param([object[]]$Events, $Proof, [string]$Case)
    $retainedIndex = New-FileTransferRetainedEventIndex -Events $Events -IncludeFieldStatistics $false
    $selected = @(foreach ($name in @(Get-FileTransferFileTunaV4DataPathEventNames)) {
        if ($name -notin @('filetransfer_route_transition_physical_send_lease_registered',
                'filetransfer_route_transition_physical_send_lease_retired', 'filetransfer_route_transition_physical_send_lease_rejected')) {
            Get-FileTransferCompactIndexEventsByName -Index $retainedIndex -Name $name
        }
    })
    # Independently parse production-selected records; never share parsed events or proof caches.
    $compactEvents = @(foreach ($event in @($selected | Sort-Object Sequence, LineNumber)) {
        ConvertFrom-FileTransferLogLine -Line $event.RawLine -FilePath $event.FilePath -LineNumber $event.LineNumber -Sequence $event.Sequence
    })
    $compactIndex = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $compactEvents
    $sendDrain = Get-FileTransferRuntimeUnlockRegularV4RouteCommitSendDrainCorrelation -Events $compactEvents -DataPathIndex $compactIndex
    $compactProof = Get-FileTransferFileTunaV4DataPathMismatchProof -Events $compactEvents -DataPathIndex $compactIndex -RouteCommitSendDrainProof $sendDrain
    if ((Get-SecondTunaProofProjection $Proof) -cne (Get-SecondTunaProofProjection $compactProof)) {
        throw "${Case}: fresh full/compact shared-proof parity changed"
    }
    return [pscustomobject]@{ Events=$compactEvents; Proof=$compactProof }
}

if (-not [string]::IsNullOrWhiteSpace($GeneratedLogPath)) {
    $generatedLines = [IO.File]::ReadAllLines($GeneratedLogPath)
    $generatedEvents = @(for ($index=0; $index -lt $generatedLines.Length; $index++) {
        if ([string]::IsNullOrWhiteSpace($generatedLines[$index])) { continue }
        $parsed = ConvertFrom-FileTransferLogLine -Line $generatedLines[$index] -FilePath $GeneratedLogPath -LineNumber ($index+1) -Sequence ($index+1)
        if ($null -ne $parsed) { $parsed }
    })
    $provenance = @($generatedEvents | Where-Object { $_.Fields.ContainsKey('data_path_envelope_fingerprint') })
    $groups = @($provenance | Group-Object { $_.Fields.data_path_envelope_fingerprint } | Sort-Object Count)
    if ($provenance.Count -ne 7 -or $groups.Count -ne 2 -or $groups[0].Count -ne 3 -or $groups[1].Count -ne 4 -or
        @($groups[0].Group | Where-Object { $_.EventName -ceq 'filetransfer_data_path_send_result' }).Count -ne 0 -or
        @($groups[1].Group | Where-Object { $_.EventName -ceq 'filetransfer_data_path_send_result' }).Count -ne 1) {
        throw 'Generated transport evidence must retain the exact old-envelope4/fresh-envelope3 records.'
    }
    $generatedIndex = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $generatedEvents
    $generatedProof = Get-FileTransferFileTunaV4DataPathMismatchProof -Events $generatedEvents -DataPathIndex $generatedIndex
    $compact = Assert-SecondTunaCompactParity -Events $generatedEvents -Proof $generatedProof -Case 'generated_transport'
    foreach ($original in $provenance) {
        $copy = @($compact.Events | Where-Object { $_.Sequence -eq $original.Sequence })
        if ($copy.Count -ne 1 -or [object]::ReferenceEquals($original, $copy[0]) -or
            $original.RawLine -cne $copy[0].RawLine -or $original.FilePath -cne $copy[0].FilePath -or
            $original.LineNumber -ne $copy[0].LineNumber -or $original.Fields.Count -ne $copy[0].Fields.Count) {
            throw 'Generated compact evidence changed provenance or retained location.'
        }
        foreach ($key in $original.Fields.Keys) {
            if (-not $copy[0].Fields.ContainsKey($key) -or $original.Fields[$key] -cne $copy[0].Fields[$key]) {
                throw "Generated compact evidence changed field ${key}."
            }
        }
        if ($original.EventName -cne 'filetransfer_data_path_send_result' -and
            ($original.Fields.receive_disposition -cne 'rejected_before_dispatch' -or $original.Fields.authority_effect -cne 'none')) {
            throw 'Generated rejected ingress acquired dispatch or authority effects.'
        }
    }
    # The real fixture shares one process writer (or absent fallback metadata).
    # It proves preservation, not the historical opposite-writer recovery join.
    if (@($provenance | Where-Object { $_.EventName -ceq 'filetransfer_data_path_receive_result' }).Count -ne 2 -or
        $generatedProof.RecoveredCount -ne 0 -or $generatedProof.EffectiveCount -ne 4) {
        throw 'Generated old/fresh rejected ingress must remain unresolved.'
    }
    # Test-only field-ablation control, freshly parsed from this generated fixture.
    # Do not modify the producer log or treat publication capture as authority.
    $withoutUse = @(foreach ($event in $generatedEvents) {
        $line = $event.RawLine -replace '; publication_evidence_[a-z_]+=[^;\r\n]*', '' -replace '; publication_use_kind=[^;\r\n]*', ''
        ConvertFrom-FileTransferLogLine -Line $line -FilePath $event.FilePath -LineNumber $event.LineNumber -Sequence $event.Sequence
    })
    $withoutUseIndex = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $withoutUse
    $withoutUseProof = Get-FileTransferFileTunaV4DataPathMismatchProof -Events $withoutUse -DataPathIndex $withoutUseIndex
    if ((Get-SecondTunaProofProjection $generatedProof -ExcludeRawLine) -cne
        (Get-SecondTunaProofProjection $withoutUseProof -ExcludeRawLine)) {
        throw 'Publication-use diagnostic fields changed the existing proof result.'
    }
    Write-Output ('second-tuna-generated-parity=' + (Get-SecondTunaProofProjection $generatedProof))
    return
}

# Keep the existing fixture's authority topology, with the production batch and
# second-Tuna scope. The fixture deliberately omits a same-range Tuna receive.
$fixtureLines = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($FixtureBase64)) | ConvertFrom-Json
$base = @(foreach ($fixtureLine in $fixtureLines) {
    $line = $fixtureLine.Replace('payload_bytes=65536', 'payload_bytes=64512').Replace('batch_chunk_count=2', 'batch_chunk_count=3')
    $line = $line.Replace('frame_chunk_count=2', 'frame_chunk_count=3').Replace('chunk_index=0-1', 'chunk_index=4602-4604')
    $line = $line.Replace('start_chunk_index=0', 'start_chunk_index=4602').Replace('last_end_chunk_exclusive=2', 'last_end_chunk_exclusive=4605')
    if ($line -match '(?:^|; )route=file_tuna_v4;') {
        $line = $line.Replace('runtime_generation=7', 'runtime_generation=4')
        $line = $line.Replace('route_publication_generation=9', 'route_publication_generation=4').Replace('route_commit_generation=0', 'route_commit_generation=2')
    }
    if ($line -match 'event=filetransfer_data_path_receive_result;' -or
        $line -match 'event=filetransfer_tuna_data_path_mismatch;.*proof_direction=receive') {
        $line = $line.Replace('direction=outbound', 'direction=inbound')
    }
    $line
})
$receive = @($base | Where-Object { $_ -match 'event=filetransfer_data_path_receive_result;' -and $_ -match 'actual_transport=regular_nkn;' })
$mismatch = @($base | Where-Object { $_ -match 'event=filetransfer_tuna_data_path_mismatch;' })
$ignore = @($base | Where-Object { $_ -match 'event=filetransfer_data_frame_ignored;' })
$source = @($base | Where-Object { $_ -match 'event=filetransfer_data_path_send_result;' -and $_ -match 'actual_transport=regular_nkn;' })
$resend = @($base | Where-Object { $_ -match 'event=filetransfer_data_path_send_result;' -and $_ -match 'actual_transport=tuna;' })
$transition = @($base | Where-Object { $_ -match 'event=filetransfer_route_transitioned; direction=inbound;' })
foreach ($collection in @($receive, $mismatch, $ignore, $source, $resend, $transition)) {
    if ($collection.Count -ne 1) { throw 'Expected one exact fixture member.' }
}
$receive = $receive[0]; $mismatch = $mismatch[0]; $ignore = $ignore[0]
$source = $source[0]; $resend = $resend[0]; $transition = $transition[0]
$tunaReceive = $receive.Replace('actual_transport=regular_nkn', 'actual_transport=tuna')
$tunaReceive = $tunaReceive.Replace('delivery_transport=regular_nkn', 'delivery_transport=tuna').Replace('log_writer_sequence=10', 'log_writer_sequence=8')
$variant = 'second_tuna_rejected_regular_ingress_exact_tuna_receive_missing'
$cases = @(
    @{ Name='missing_exact_receive'; Reason='missing_exact_tuna_receive'; Variant=$variant },
    @{ Name='complete'; Recovered=$true; Reason='recovered' },
    @{ Name='first_cycle'; Recovered=$true; Reason='recovered' },
    @{ Name='missing_ignore'; Reason='missing_rejection_companion'; Unbound=$true },
    @{ Name='wrong_ignore_writer'; Reason='missing_rejection_companion'; Unbound=$true },
    @{ Name='wrong_ignore_range'; Reason='missing_rejection_companion'; Unbound=$true },
    @{ Name='missing_mismatch'; Reason='missing_rejection_companion'; Unbound=$true; EffectiveCount=1 },
    @{ Name='missing_source'; Reason='missing_earlier_source_send' },
    @{ Name='wrong_source_session'; Reason='missing_earlier_source_send' },
    @{ Name='missing_transition'; Reason='missing_receiver_route_transition' },
    @{ Name='wrong_transition_publication'; Reason='missing_receiver_route_transition' },
    @{ Name='ambiguous_transition'; Reason='ambiguous_receiver_route_transition' },
    @{ Name='missing_resend'; Reason='missing_tuna_resend' },
    @{ Name='wrong_resend_bytes'; Reason='missing_tuna_resend' },
    @{ Name='borrowed_receive_session'; Reason='missing_exact_tuna_receive'; Variant=$variant },
    @{ Name='borrowed_receive_transfer'; Reason='missing_exact_tuna_receive'; Variant=$variant },
    @{ Name='wrong_receive_range'; Reason='missing_exact_tuna_receive'; Variant=$variant },
    @{ Name='wrong_receive_count'; Reason='missing_exact_tuna_receive'; Variant=$variant },
    @{ Name='terminal_source_poison'; Reason='terminal_boundary' },
    @{ Name='terminal_rejection_poison'; Reason='missing_rejection_companion'; Unbound=$true },
    @{ Name='route_rejection_poison'; Reason='missing_rejection_companion'; Unbound=$true },
    @{ Name='adjacent_ignore_borrow'; Reason='missing_rejection_companion'; Unbound=$true; Candidates=2; RecoveredCount=1 }
)
$normalized = [Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($original in $base) {
        $line = $original
        if (($case.Name -in @('missing_ignore','adjacent_ignore_borrow') -and $line -ceq $ignore) -or
            ($case.Name -ceq 'missing_mismatch' -and $line -ceq $mismatch) -or
            ($case.Name -ceq 'missing_source' -and $line -ceq $source) -or
            ($case.Name -ceq 'missing_transition' -and $line -ceq $transition) -or
            ($case.Name -ceq 'missing_resend' -and $line -ceq $resend)) { continue }
        if ($case.Name -ceq 'first_cycle') { $line = $line.Replace('previous_route=post_tuna_fallback_v6', 'previous_route=regular_nkn_v4_fast') }
        if ($line -ceq $ignore) {
            switch ($case.Name) {
                'wrong_ignore_writer' { $line = $line.Replace('writer_receive_regular_rejected', 'foreign_writer') }
                'wrong_ignore_range' { $line = $line.Replace('chunk_index=4602-4604', 'chunk_index=4605-4607') }
                'terminal_rejection_poison' { $lines.Add('event=file_transfer_inbound_terminal; transfer_id=transfer_second_tuna_rejected_ingress; session_id=sess_a; log_writer_id=writer_receive_regular_rejected; log_writer_sequence=20') }
                'route_rejection_poison' { $lines.Add($transition.Replace('log_writer_sequence=7', 'log_writer_sequence=20')) }
            }
        }
        if ($line -ceq $source -and $case.Name -ceq 'wrong_source_session') { $line = $line.Replace('session_id=sess_a', 'session_id=sess_foreign') }
        if ($line -ceq $transition -and $case.Name -ceq 'wrong_transition_publication') { $line = $line.Replace('route_publication_generation=4', 'route_publication_generation=99') }
        if ($line -ceq $resend -and $case.Name -ceq 'wrong_resend_bytes') { $line = $line.Replace('raw_payload_bytes=64512', 'raw_payload_bytes=64511') }
        if ($line -ceq $receive) {
            if ($case.Name -in @('complete','adjacent_ignore_borrow','borrowed_receive_session','borrowed_receive_transfer','wrong_receive_range','wrong_receive_count')) {
                $proof = $tunaReceive
                switch ($case.Name) {
                    'borrowed_receive_session' { $proof = $proof.Replace('session_id=sess_a', 'session_id=sess_foreign') }
                    'borrowed_receive_transfer' { $proof = $proof.Replace('transfer_id=transfer_second_tuna_rejected_ingress', 'transfer_id=transfer_foreign') }
                    'wrong_receive_range' { $proof = $proof.Replace('chunk_index=4602-4604', 'chunk_index=4605-4607').Replace('start_chunk_index=4602', 'start_chunk_index=4605').Replace('last_end_chunk_exclusive=4605', 'last_end_chunk_exclusive=4608') }
                    'wrong_receive_count' { $proof = $proof.Replace('batch_chunk_count=3', 'batch_chunk_count=2') }
                }
                $lines.Add($proof)
            }
            if ($case.Name -ceq 'terminal_source_poison') {
                $lines.Add('event=file_transfer_inbound_terminal; transfer_id=transfer_second_tuna_rejected_ingress; session_id=sess_a; log_writer_id=writer_receive_regular_rejected; log_writer_sequence=9')
            }
        }
        $lines.Add($line)
        if ($original -ceq $transition -and $case.Name -ceq 'ambiguous_transition') {
            $lines.Add($transition.Replace('log_writer_sequence=7', 'log_writer_sequence=8'))
        }
        if ($original -ceq $mismatch -and $case.Name -ceq 'adjacent_ignore_borrow') {
            $lines.Add($receive.Replace('log_writer_sequence=10', 'log_writer_sequence=30'))
            $lines.Add($mismatch.Replace('log_writer_sequence=14', 'log_writer_sequence=31'))
            $lines.Add($ignore.Replace('log_writer_sequence=21', 'log_writer_sequence=32'))
        }
    }
    $events = @(for ($index=0; $index -lt $lines.Count; $index++) {
        $parsed = ConvertFrom-FileTransferLogLine -Line $lines[$index] -FilePath 'second-tuna-memory.log' -LineNumber ($index+1) -Sequence ($index+1)
        if ($null -ne $parsed) { $parsed }
    })
    $candidate = @($events | Where-Object {
        $_.EventName -ceq 'filetransfer_data_path_receive_result' -and
        (Get-FileTransferEventField $_ 'actual_transport') -ceq 'regular_nkn' -and
        (Get-FileTransferEventField $_ 'log_writer_sequence') -ceq '10'
    })[0]
    $before = $events | ConvertTo-Json -Depth 12 -Compress
    $dataIndex = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $events
    $diagnostic = Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult -Mismatch $candidate -Events $events -DataPathIndex $dataIndex
    $legacy = Test-FileTransferFileTunaV4DataPathMismatchRejectedFailClosed -Mismatch $candidate -Events $events -DataPathIndex $dataIndex
    $proof = Get-FileTransferFileTunaV4DataPathMismatchProof -Events $events -DataPathIndex $dataIndex
    $compact = Assert-SecondTunaCompactParity -Events $events -Proof $proof -Case $case.Name
    $recovered = $case.ContainsKey('Recovered') -and $case.Recovered
    $chainMatched = -not ($case.ContainsKey('Unbound') -and $case.Unbound)
    $expectedVariant = if ($case.ContainsKey('Variant')) { $case.Variant } else { '(none)' }
    $candidates = if ($case.ContainsKey('Candidates')) { $case.Candidates } else { 1 }
    $recoveredCount = if ($case.ContainsKey('RecoveredCount')) { $case.RecoveredCount } elseif ($recovered) { 1 } else { 0 }
    $effectiveCount = if ($case.ContainsKey('EffectiveCount')) { $case.EffectiveCount } elseif ($recovered) { 0 } else { 2 }
    if ($diagnostic.Recovered -ne $recovered -or $legacy -ne $recovered -or
        $diagnostic.RejectionChainMatched -ne $chainMatched -or $diagnostic.Reason -cne $case.Reason -or
        [string]$diagnostic.Variant -cne $expectedVariant -or
        ($chainMatched -and $diagnostic.ReceiveDisposition -cne 'rejected_before_dispatch') -or
        (-not $chainMatched -and $diagnostic.ReceiveDisposition -cne '(unproven)')) {
        throw "$($case.Name): diagnostic contract changed: $($diagnostic | ConvertTo-Json -Compress)"
    }
    if ($proof.RawCandidateCount -ne $candidates -or $proof.DistinctCount -ne $candidates -or
        $proof.RecoveredCount -ne $recoveredCount -or $proof.UnresolvedCount -ne ($candidates-$recoveredCount) -or
        $proof.EffectiveCount -ne $effectiveCount) {
        throw "$($case.Name): recovery counts changed"
    }
    $record = @($proof.CandidateRecords | Where-Object { $_.Event.Sequence -eq $candidate.Sequence })
    if ($record.Count -ne 1 -or $record[0].Variant -cne $expectedVariant -or
        $record[0].RejectionDiagnostic.Reason -cne $case.Reason -or
        -not [object]::ReferenceEquals($candidate, $record[0].Event)) {
        throw "$($case.Name): canonical candidate diagnostic or location changed"
    }
    $effectiveRecords = @($proof.EffectiveCandidateRecords)
    if ($effectiveRecords.Count -ne $effectiveCount) { throw "$($case.Name): effective candidate membership changed" }
    foreach ($effectiveRecord in $effectiveRecords) {
        if ($effectiveRecord.Variant -cne $expectedVariant -or $effectiveRecord.RejectionDiagnostic.Reason -cne $case.Reason -or
            -not [object]::ReferenceEquals($effectiveRecord.Event, @($proof.EffectiveEvents | Where-Object { $_.Sequence -eq $effectiveRecord.Event.Sequence })[0])) {
            throw "$($case.Name): effective candidate diagnostic or location changed"
        }
    }
    if (($events | ConvertTo-Json -Depth 12 -Compress) -cne $before) { throw "$($case.Name): input events mutated" }
    if ($case.Name -ceq 'complete') {
        $route = @($events | Where-Object { $_.EventName -ceq 'filetransfer_route_transitioned' -and $_.Fields.direction -ceq 'inbound' })[0]
        $receivedTuna = @($events | Where-Object { $_.EventName -ceq 'filetransfer_data_path_receive_result' -and $_.Fields.actual_transport -ceq 'tuna' })[0]
        if ($route.Sequence -ge $receivedTuna.Sequence -or $receivedTuna.Sequence -ge $candidate.Sequence -or
            [long]$route.Fields.log_writer_sequence -ge [long]$receivedTuna.Fields.log_writer_sequence) {
            throw 'Positive fixture must publish the receiver route before its exact Tuna receipt.'
        }
    }
    $normalized.Add([pscustomobject]@{ Case=$case.Name; Reason=$diagnostic.Reason; Variant=$diagnostic.Variant;
        Raw=$proof.RawCandidateCount; Distinct=$proof.DistinctCount; Recovered=$proof.RecoveredCount; Unresolved=$proof.UnresolvedCount;
        Effective=$proof.EffectiveCount; Writer=(Get-FileTransferEventField $candidate 'log_writer_id');
        WriterSequence=(Get-FileTransferEventField $candidate 'log_writer_sequence'); Line=$candidate.LineNumber })
}
Write-Output ('second-tuna-rejected-ingress=' + ($normalized.ToArray() | ConvertTo-Json -Depth 6 -Compress))
