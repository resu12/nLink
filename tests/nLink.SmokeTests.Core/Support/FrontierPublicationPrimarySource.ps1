param([string]$RepoRoot,[string]$InputPath)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
$events=@(Read-FileTransferLogEvents -LogFiles @($InputPath) -TailMinutes 0)
$proof=Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $events
if($proof.DistinctCount -ne 1 -or $proof.UnresolvedCount -ne 1 -or $proof.RecoveredCount -ne 0) {
    throw ('Fresh primary failure missing: '+($proof | ConvertTo-Json -Depth 4 -Compress))
}
$failure=$proof.UnresolvedCandidateRecords[0]
if($failure.Event.Fields.next_chunk_index -cne '101' -or
    -not [object]::ReferenceEquals($failure.Event,$events[$failure.Event.LineNumber-1])) {
    throw 'Actual primary candidate origin changed'
}
'frontier-primary-source='+(@{Distinct=1;Unresolved=1;Recovered=0;SourceLine=$failure.Event.LineNumber;
    EventName=$failure.Event.EventName;SyntheticFailure=$false} | ConvertTo-Json -Compress)
