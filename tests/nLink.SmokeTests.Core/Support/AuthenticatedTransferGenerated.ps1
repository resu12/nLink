param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$foreignControl=$env:NLINK_PUBLICATION_SAME_TRANSFER_FIXTURE -ceq 'authenticated-foreign'
# This fixture proves one authenticated transfer and rejection of a seeded
# transport fault, not a two-activation route cycle. Live acceptance is unchanged.
$null=& (Join-Path $PSScriptRoot 'SelectedNativeWriterGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory -RunId $RunId -ScenarioId $ScenarioId -ExpectWriterBudgetOmission:$foreignControl -LiveRouteProofMode None
$output=Join-Path $Directory ('selected-native-'+$PSVersionTable.PSVersion.Major)
$producer=Get-Content (Join-Path $Directory 'authenticated-producer.json') -Raw|ConvertFrom-Json
$terminal=Get-Content (Join-Path $Directory 'authenticated-terminals.json') -Raw|ConvertFrom-Json
$summary=Get-Content (Join-Path $output 'filetransfer-retained-compact-summary.json') -Raw|ConvertFrom-Json
$original=Get-Content (Join-Path $output 'live-primary-observation.json') -Raw|ConvertFrom-Json
$lines=[IO.File]::ReadAllLines((Join-Path $output 'gui/valid/filetransfer-retained-log-slice.log'))
$selected=$lines[[int]$summary.first_proof_chain_failure_line_number-1]
# Post-run diagnostics only: none of these JSON fields enters the primary.
if($producer.Status -cne 'verified_offline_authenticated_transfer_producer' -or -not $producer.PrivateOwnerVerified -or
    -not $producer.ActualEncryptedEnvelopeVerified -or -not $producer.OriginalPublicationCurrentAtSeed -or $producer.ReceiverBytesAtSeed -ne 0 -or
    $producer.AnalyzerEvaluations -ne 0 -or $producer.TerminalConsumerEnabled -or $producer.AcceptanceChanged -or $producer.Live -or
    $terminal.Status -cne 'verified_offline_source_terminal_binding' -or $producer.PayloadBytes -ne 262144 -or
    $producer.ForeignControl -ne $foreignControl -or $selected -cne $producer.Seeded.RawLine -or $original.Evaluations -ne 3 -or
    -not $selected.Contains('security_context_status=captured;') -or
    $null -ne $summary.file_tuna_route_cycle_acceptance -or $summary.first_proof_chain_failure_class -cne 'file_tuna_data_path_mismatch' -or
    $summary.verdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $summary.gate_status -cne 'fail') {throw 'authenticated_transfer_contract'}
foreach($row in $terminal.TerminalRows) {
    if(-not $row.RawLine.Contains('terminal_session_id='+$producer.Publication.SessionId+';') -or
        -not $row.RawLine.Contains('terminal_transfer_id='+$producer.Publication.TransferId+';')) {throw 'authenticated_terminal_scope'}
}
if($foreignControl) {
    if(@($producer.Controls.PSObject.Properties).Count -ne 7 -or $original.Witness -or $original.Completion -or
        $original.WitnessOmission -cne 'primary_witness_writer_unavailable') {throw 'authenticated_foreign_controls'}
} else {
    $native=Get-Content (Join-Path $output 'selected-native-witness.json') -Raw|ConvertFrom-Json
    if(-not $original.Witness -or -not $original.Completion -or -not $native.NoTerminalOwner) {throw 'authenticated_original_witness'}
}
'authenticated-transfer='+([ordered]@{ForeignControl=$producer.ForeignControl;ActualEncryptedEnvelope=$true;SameServicePublication=$true
    OriginalWitnessAndCompletion=(-not $foreignControl);OriginalEvaluations=3;TerminalConsumerEnabled=$false;SeededObservation=$true
    AcceptanceChanged=$false;RouteCycleProven=$false;Live=$false}|ConvertTo-Json -Compress)
