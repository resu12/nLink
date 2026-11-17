param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs')
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
$event='filetransfer_fallback_tail_yield_evidence'
$names=[string[]]@(Get-GuiSmokeSemanticEvidenceEventNames)
$authority=Get-FileTransferCoordinatorEvidenceCatalog
if($event -cnotin $names -or $event -cin @($authority.SnapshotAuthorityV1+$authority.GenerationStampedEffects)) { throw 'Yield diagnostic retention/authority catalog' }
$lines=[string[]]@('event=unrelated',"event=$event; yield_evidence_role=send_copy; authority_effect=none",
    "event=$event; yield_evidence_role=receive_dispatch; authority_effect=none",
    "event=$event; yield_evidence_role=receipt_association; authority_effect=none",'event=unrelated')
$input=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes(($lines -join "`r`n")+"`r`n"),$false)
try {
    $result=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStream($input,5L,0L,
        (Join-Path $Directory 'retained.log'),(Join-Path $Directory 'writer.log'),'run','scenario',[string[]]@(),
        (Join-Path $Directory 'semantic.log'),$names)
    $capture=$result.StreamCapture
    if($null -eq $capture -or -not $result.OwnsStreamCapture($capture) -or -not $capture.MatchesOriginalSource($input) -or
        ($capture.SemanticOrigins -join ',') -cne '2,3,4') { throw 'Original semantic origins missing' }
    $retained=[IO.File]::ReadAllLines($capture.Semantic.CreationPath)
    if(($retained -join "`n") -cne ($lines[1..3] -join "`n")) { throw 'Semantic records changed' }
    $events=@(for($i=0;$i -lt $retained.Length;$i++) {
        ConvertFrom-FileTransferLogLine -Line $retained[$i] -FilePath $capture.Semantic.CreationPath -LineNumber ($i+1) -Sequence ($i+1)
    })
    $index=New-FileTransferRetainedEventIndex -Events $events -LogFiles @($capture.Semantic.CreationPath)
    for($i=0;$i -lt $retained.Length;$i++) {
        if(-not [object]::ReferenceEquals($index.AllEvents[$i].RawLine,$retained[$i])) { throw 'Index replaced original raw string' }
    }
    if(@($index.AllEvents | Where-Object { $_.EventName -cin @($authority.SnapshotAuthorityV1+$authority.GenerationStampedEffects) }).Count -ne 0 -or
        $capture.LiveProofEligible -or $capture.NativeWriterCustodyEstablished -or $capture.PublicationCompletionIssued) { throw 'Diagnostic retention granted authority' }
} finally { $input.Dispose() }
'yield-retention=pass'
