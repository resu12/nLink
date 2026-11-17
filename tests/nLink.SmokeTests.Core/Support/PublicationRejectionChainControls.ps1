param([string]$OutputDirectory,$Candidate)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Detached structural query controls only; no input custody or authority evaluation.
. (Join-Path $PSScriptRoot 'PublicationServiceYieldMatcher.ps1')
. (Join-Path $PSScriptRoot 'PublicationRejectionChainMatcher.ps1')
. (Join-Path $PSScriptRoot '../../../tools/FileTransferOps/LocalPublicationRows.ps1')
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Rejection controls cannot evaluate authority' }
$lines=[IO.File]::ReadAllLines((Join-Path $OutputDirectory 'filetransfer-retained-log-slice.log'))
$records=@(for($i=0;$i -lt $lines.Length;$i++) {
    [pscustomobject]@{Position=$i;Fields=(ConvertFrom-GuiSmokeLogLedgerStructuredFields $lines[$i])}
})
function JCopy { @(foreach($r in $records) { [pscustomobject]@{Position=$r.Position;Fields=$r.Fields.Clone()} }) }
$source=$records[$Candidate.SourcePosition]; $receive=$records[$Candidate.ReceivePosition]
$baseline=Get-PublicationRejectionCompanions $records $source $receive
YRequire ($baseline.Count -eq 3 -and $baseline[1].Position -eq $Candidate.MismatchPosition -and
    $baseline[2].Position -eq $Candidate.IgnorePosition) 'rejection_detached_baseline'
foreach($key in @('start_chunk_index','last_end_chunk_exclusive','frame_chunk_count','batch_chunk_count','raw_payload_bytes','payload_bytes')) {
    YRequire (-not $baseline[2].Fields.ContainsKey($key)) 'rejection_sparse_ignore'
}
$cases=@('missing_receive','missing_mismatch','missing_ignore','duplicate_receive','duplicate_mismatch','duplicate_ignore',
    'reordered','writer','sequence','duplicate_fields','missing_field','payload','envelope','installation','security_sequence',
    'coordinator','boundary','token','operation','observed_generation','route','disposition','effect','mismatch_reason','ignore_reason',
    'source_range','source_bytes','mismatch_range','mismatch_bytes','ignore_chunk','noncanonical_count','empty_range','zero_bytes')
$reasons=[ordered]@{}
foreach($case in $cases) {
    $rs=JCopy; $s=$rs[$Candidate.SourcePosition]; $r=$rs[$Candidate.ReceivePosition]
    $m=$rs[$Candidate.MismatchPosition]; $g=$rs[$Candidate.IgnorePosition]
    switch($case) {
        'missing_receive' { $rs=@($rs | Where-Object Position -ne $r.Position) }
        'missing_mismatch' { $rs=@($rs | Where-Object Position -ne $m.Position) }
        'missing_ignore' { $rs=@($rs | Where-Object Position -ne $g.Position) }
        'duplicate_receive' { $rs+=@($r) }
        'duplicate_mismatch' { $rs+=@($m) }
        'duplicate_ignore' { $rs+=@($g) }
        'reordered' { $m.Fields.event='filetransfer_data_frame_ignored'; $g.Fields.event='filetransfer_tuna_data_path_mismatch' }
        'writer' { $m.Fields.log_writer_id='foreign' }
        'sequence' { $g.Fields.log_writer_sequence='99999' }
        'duplicate_fields' { $g.Fields['__nlink_duplicate_structured_field']='authority_effect' }
        'missing_field' { $g.Fields.Remove('security_payload_fingerprint') }
        'payload' { $g.Fields.security_payload_fingerprint='ftpay1-'+('0'*64) }
        'envelope' { $g.Fields.data_path_envelope_fingerprint='ftenv1-'+('0'*64) }
        'installation' { $m.Fields.security_installation_id='ftsi1-'+('0'*32) }
        'security_sequence' { $g.Fields.security_sequence='99999' }
        'coordinator' { $g.Fields.publication_evidence_coordinator_id='fte1-'+('0'*24) }
        'boundary' { $g.Fields.publication_evidence_boundary_id='fte1-'+('0'*24) }
        'token' { $g.Fields.publication_evidence_token_fingerprint='fte1-'+('0'*24) }
        'operation' { $g.Fields.publication_evidence_operation_key='foreign' }
        'observed_generation' { $g.Fields.publication_evidence_observed_state_generation='99999' }
        'route' { $g.Fields.route='regular_nkn_v4_fast' }
        'disposition' { $g.Fields.receive_disposition='dispatch_entered' }
        'effect' { $g.Fields.authority_effect='applied' }
        'mismatch_reason' { $m.Fields.mismatch_reason='foreign' }
        'ignore_reason' { $g.Fields.reason='foreign' }
        'source_range' { $s.Fields.start_chunk_index='13'; $s.Fields.last_end_chunk_exclusive='15'; $s.Fields.chunk_index='13-14' }
        'source_bytes' { $s.Fields.raw_payload_bytes='2049'; $s.Fields.payload_bytes='2049' }
        'mismatch_range' { $m.Fields.start_chunk_index='13'; $m.Fields.last_end_chunk_exclusive='15'; $m.Fields.chunk_index='13-14' }
        'mismatch_bytes' { $m.Fields.raw_payload_bytes='2049'; $m.Fields.payload_bytes='2049' }
        'ignore_chunk' { $g.Fields.chunk_index='13-14' }
        'noncanonical_count' { $m.Fields.frame_chunk_count='02' }
        'empty_range' { $m.Fields.last_end_chunk_exclusive=$m.Fields.start_chunk_index }
        'zero_bytes' { $s.Fields.raw_payload_bytes='0'; $s.Fields.payload_bytes='0' }
    }
    $reason=''; try { $null=Get-PublicationRejectionCompanions $rs $s $r } catch { $reason=$_.Exception.Message }
    YRequire (-not [string]::IsNullOrWhiteSpace($reason)) ('rejection_negative_admitted:'+ $case)
    $reasons[$case]=$reason
}
$reason=''; try { JParity $Candidate @{changed=$true} } catch { $reason=$_.Exception.Message }
YRequire ($reason -ceq 'rejection_parity') 'rejection_parity_control'
$witnesses=[Collections.Generic.HashSet[int]]::new(); foreach($i in 1..64) { [void]$witnesses.Add($i) }
JWitnessBound $witnesses; [void]$witnesses.Add(64); JWitnessBound $witnesses; [void]$witnesses.Add(65)
$reason=''; try { JWitnessBound $witnesses } catch { $reason=$_.Exception.Message }
YRequire ($reason -ceq 'rejection_witness_limit') 'rejection_shared_limit_control'
[pscustomobject]@{NegativeControls=$reasons;SparseIgnoreAccepted=$true;ParityControl=$true;SharedWitnessLimitControl=$true;AuthorityEvaluations=0}
