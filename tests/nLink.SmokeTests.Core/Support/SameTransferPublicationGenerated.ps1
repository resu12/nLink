param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# The real loopback Service producer lacks NKN security/envelope capture. Keep
# the original native witness omission; never splice in another session's keys.
$null=& (Join-Path $PSScriptRoot 'SelectedNativeWriterGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory -RunId $RunId -ScenarioId $ScenarioId -ExpectMissingSecurityCapture
$output=Join-Path $Directory ('selected-native-'+$PSVersionTable.PSVersion.Major)
$producer=Get-Content (Join-Path $Directory 'same-transfer-producer.json') -Raw|ConvertFrom-Json
$terminal=Get-Content (Join-Path $Directory 'same-transfer-terminals.json') -Raw|ConvertFrom-Json
$summary=Get-Content (Join-Path $output 'filetransfer-retained-compact-summary.json') -Raw|ConvertFrom-Json
$original=Get-Content (Join-Path $output 'live-primary-observation.json') -Raw|ConvertFrom-Json
# Detached files are post-run assertions only, never inputs to the primary or
# a substitute for the producer's exact-reference checks in the isolated host.
$lines=[IO.File]::ReadAllLines((Join-Path $output 'gui/valid/filetransfer-retained-log-slice.log'))
$selected=$lines[[int]$summary.first_proof_chain_failure_line_number-1]
if($producer.Status -cne 'verified_offline_same_transfer_producer' -or -not $producer.PrivateOwnerVerified -or
    -not $producer.RealServiceCompletion -or -not $producer.SeededObservation -or $producer.AnalyzerEvaluations -ne 0 -or
    $producer.TerminalConsumerEnabled -or $producer.AcceptanceChanged -or $producer.Live -or
    $terminal.Status -cne 'verified_offline_source_terminal_binding' -or $producer.PayloadBytes -ne 262144 -or
    $selected -cne $producer.Failure.RawLine -or $original.Evaluations -ne 3 -or $original.Witness -or $original.Completion -or
    $original.WitnessOmission -cne 'carrier_forwarding_unproven' -or $selected.Contains('security_context_status=') -or
    $summary.verdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $summary.gate_status -cne 'fail' -or
    $summary.first_proof_chain_failure_class -cne 'file_tuna_data_path_mismatch') {throw 'same_transfer_producer_contract'}
foreach($row in $terminal.TerminalRows) {
    if(-not $row.RawLine.Contains('terminal_session_id='+$producer.Publication.SessionId+';') -or
        -not $row.RawLine.Contains('terminal_transfer_id='+$producer.Publication.TransferId+';')) {throw 'same_transfer_terminal_scope'}
}
if($producer.ForeignControl -and @($producer.Controls.PSObject.Properties).Count -ne 6) {throw 'same_transfer_foreign_controls'}
'same-transfer-publication='+([ordered]@{ForeignControl=$producer.ForeignControl;SamePrivateTransfer=$true;PayloadBytes=262144
    PrimarySelectedOriginalFailure=$true;OriginalEvaluations=3;WitnessOmission='carrier_forwarding_unproven';TerminalConsumerEnabled=$false
    SeededObservation=$true;AcceptanceChanged=$false;Live=$false}|ConvertTo-Json -Compress)
