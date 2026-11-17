param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs')
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
$installed='filetransfer_security_context_installed'; $retired='filetransfer_security_context_retired'
$names=[string[]]@(Get-GuiSmokeSemanticEvidenceEventNames)
$authority=Get-FileTransferCoordinatorEvidenceCatalog
$authorityNames=@($authority.SnapshotAuthorityV1+$authority.GenerationStampedEffects)
foreach($event in @($installed,$retired)) {
    if($event -cnotin $names -or $event -cin $authorityNames) { throw 'Security lifecycle diagnostic retention/authority catalog' }
}
$scope='security_transport_id=ftst1-original; security_installation_id=ftsi1-original; security_installation_generation=1; security_establishment_fingerprint=ftest1-original; security_session_id=fte1-original'
# Retention preserves evidence, including incomplete diagnostics. It does not validate a lifetime.
$lines=[string[]]@('event=unrelated',
    "event=$installed; $scope; security_context_status=captured; authority_effect=none",
    "event=${installed}_suffix; $scope; security_context_status=captured; authority_effect=none",
    "event=$installed; security_context_status=unbound; authority_effect=none",
    "event=$retired; $scope; security_context_status=retired; authority_effect=none",
    "event=${retired}_suffix; $scope; security_context_status=retired; authority_effect=none",
    "event=$retired; security_installation_generation=not-a-generation; authority_effect=none",'event=unrelated')
$expected=[string[]]@($lines[1],$lines[3],$lines[4],$lines[6])
$input=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes(($lines -join "`r`n")+"`r`n"),$false)
try {
    $result=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStream($input,[long]$lines.Length,0L,
        (Join-Path $Directory 'retained.log'),(Join-Path $Directory 'writer.log'),'run','scenario',[string[]]@(),
        (Join-Path $Directory 'semantic.log'),$names)
    $capture=$result.StreamCapture
    if($null -eq $capture -or -not $result.OwnsStreamCapture($capture) -or -not $capture.MatchesOriginalSource($input) -or
        ($capture.SemanticOrigins -join ',') -cne '2,4,5,7') { throw 'Original security lifecycle origins missing' }
    $retained=[IO.File]::ReadAllLines($capture.Semantic.CreationPath)
    if(($retained -join "`n") -cne ($expected -join "`n")) { throw 'Security lifecycle records changed' }
    $events=@(for($i=0;$i -lt $retained.Length;$i++) {
        ConvertFrom-FileTransferLogLine -Line $retained[$i] -FilePath $capture.Semantic.CreationPath -LineNumber ($i+1) -Sequence ($i+1)
    })
    $index=New-FileTransferRetainedEventIndex -Events $events -LogFiles @($capture.Semantic.CreationPath)
    for($i=0;$i -lt $retained.Length;$i++) {
        if(-not [object]::ReferenceEquals($index.AllEvents[$i].RawLine,$retained[$i])) { throw 'Index replaced original security lifecycle raw string' }
    }
    if(@($index.AllEvents | Where-Object { $_.EventName -cin $authorityNames }).Count -ne 0 -or
        $capture.LiveProofEligible -or $capture.NativeWriterCustodyEstablished -or $capture.PublicationCompletionIssued) {
        throw 'Security lifecycle retention granted authority'
    }
} finally { $input.Dispose() }
'security-lifecycle-retention=pass'
