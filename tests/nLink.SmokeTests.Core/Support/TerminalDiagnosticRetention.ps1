param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs')
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
$names=[string[]]@(Get-GuiSmokeSemanticEvidenceEventNames)
$catalog=Get-FileTransferCoordinatorEvidenceCatalog
if('transfer_terminal' -cnotin $names -or
    'transfer_terminal' -cin @($catalog.SnapshotAuthorityV1+$catalog.GenerationStampedEffects)) {throw 'Terminal diagnostic retention/authority catalog'}
# Deliberately detached predicate controls, not genuine transfer histories.
# Retention must preserve even duplicate, canceled, errored and malformed rows.
$lines=[string[]]@('event=unrelated',
    'event=transfer_terminal; direction=inbound; terminal_transfer_id=fte1-aaaaaaaaaaaaaaaaaaaaaaaa; reason=Transfer complete.; error_code=(none)',
    'event=transfer_terminal_suffix; direction=inbound',
    'event=transfer_terminal; direction=outbound; reason=Canceled; error_code=canceled',
    'event=transfer_terminal; direction=inbound; error_code=hash_mismatch',
    'event=transfer_terminal; direction=inbound; terminal_transfer_id=fte1-aaaaaaaaaaaaaaaaaaaaaaaa; reason=Transfer complete.; error_code=(none)',
    'event=transfer_terminal; terminal_transfer_id=malformed',
    'event=unrelated')
$expected=[string[]]@($lines[1],$lines[3],$lines[4],$lines[5],$lines[6])
$input=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes(($lines -join "`r`n")+"`r`n"),$false)
try {
    $result=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStream($input,[long]$lines.Length,0L,
        (Join-Path $Directory 'retained.log'),(Join-Path $Directory 'writer.log'),'run','scenario',[string[]]@(),
        (Join-Path $Directory 'semantic.log'),$names)
    $capture=$result.StreamCapture
    if($null -eq $capture -or -not $result.OwnsStreamCapture($capture) -or -not $capture.MatchesOriginalSource($input) -or
        ($capture.SemanticOrigins -join ',') -cne '2,4,5,6,7') {throw 'Original terminal origins missing'}
    $actual=[IO.File]::ReadAllLines($capture.Semantic.CreationPath)
    if(($actual -join "`n") -cne ($expected -join "`n")) {throw 'Terminal records changed'}
    if($capture.LiveProofEligible -or $capture.NativeWriterCustodyEstablished -or $capture.PublicationCompletionIssued) {throw 'Retention granted authority'}
    [IO.File]::WriteAllText((Join-Path $Directory 'result.json'),(@{Records=$actual.Count;Origins=$capture.SemanticOrigins;
        AuthorityEvaluations=0;AuthorityGranted=$false;DetachedControls=$true} | ConvertTo-Json -Compress))
} finally {$input.Dispose()}
'terminal-evidence=pass'
