param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPublicationEvidenceCarrier.ps1')
$source=Join-Path $Directory 'nlink.log'
$inputHash=Get-FileTransferEvidenceSha256 $source
$lines=[IO.File]::ReadAllLines($source)
$output=Join-Path $Directory ('ps'+$PSVersionTable.PSVersion.Major+'-'+[guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $output)
$writerPath=Join-Path $output 'writer.log'
$ledger=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::Process($source,$lines.Length,0,
    (Join-Path $output 'retained.log'),$writerPath,$RunId,$ScenarioId,[string[]]@())
$full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$catalog=Get-FileTransferCoordinatorEvidenceCatalog
$snapshotLines=[string[]]@($lines | Where-Object {
    $event=Get-FileTransferCoordinatorSnapshotEventName $_
    $event -cin $catalog.SnapshotAuthorityV1 -or $event -cin $catalog.GenerationStampedEffects
})
$snapshotPath=Join-Path $output 'snapshot-compact.log'
[IO.File]::WriteAllLines($snapshotPath,$snapshotLines,[Text.UTF8Encoding]::new($false))
$compact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines ([IO.File]::ReadAllLines($snapshotPath))
$parity=Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
if($full.ArtifactEvidenceVerdict -cne 'pass' -or $compact.ArtifactEvidenceVerdict -cne 'pass' -or
    -not $full.StreamComplete -or -not $compact.StreamComplete -or $parity.Verdict -cne 'pass') { throw 'Generated evaluation failed' }
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Carrier attempted a new authority evaluation' }
$fullBefore=ConvertTo-Json -InputObject $full -Depth 64 -Compress
$compactBefore=ConvertTo-Json -InputObject $compact -Depth 64 -Compress
$names=Get-GuiSmokeSemanticEvidenceEventNames
$captureLines=[string[]]@($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName $_) -cin $names })
$capturePath=Join-Path $output 'capture-compact.log'
[IO.File]::WriteAllLines($capturePath,$captureLines,[Text.UTF8Encoding]::new($false))
function Arguments($View,[string]$CapturePath,[string]$SnapshotPath) {
    @{SnapshotEvidence=$View; InputPath=$CapturePath; InputSha256=(Get-FileTransferEvidenceSha256 $CapturePath)
        WriterPath=$writerPath; WriterEvidence=$ledger; AcceptanceRunId=$RunId; ScenarioRunId=$ScenarioId
        SnapshotInputPath=$SnapshotPath; SnapshotInputSha256=(Get-FileTransferEvidenceSha256 $SnapshotPath)}
}
$fullArgs=Arguments $full $source $source
$compactArgs=Arguments $compact $capturePath $snapshotPath
$selected=[long[]]@(for($i=0;$i -lt $lines.Length;$i++) { if($lines[$i] -match '; publication_use_kind=') { $i+1L } })
function ExistingOutcomes {
    $index=New-FileTransferPublicationForwardingIndex @fullArgs
    $facts=@(foreach($line in $selected) {
        $raw=$lines[$line-1]
        @{
            A=(Resolve-FileTransferPublicationUseEvidence $index.Local $source $line $raw)
            B1=(Resolve-FileTransferPublicationForwardingEvidence $index $source $line $raw)
            Shared=(Resolve-FileTransferPublicationEvidence $index.Local.Publications ([pscustomobject]@{
                Fields=(ConvertFrom-GuiSmokeLogLedgerStructuredFields $raw)}))
        }
    })
    ConvertTo-FileTransferLocalCarrierJson $facts
}
$existingBefore=ExistingOutcomes
$request='ed25f935-1e38-467f-a76f-daa1a26c2e18'
$carrier=New-FileTransferLocalPublicationEvidenceCarrier $fullArgs $compactArgs $request $selected
if($carrier.Status -cne 'ready') { throw "Generated carrier omitted: $($carrier.Reason)" }
$read=Read-FileTransferLocalPublicationEvidenceCarrier $carrier.Json $request $carrier.Sha256 $carrier.InputsSha256
if($read.Status -cne 'validated_local_carrier' -or $read.Carrier.rows.Count -ne $selected.Count) { throw 'Generated reader failed' }
$body=$read.Carrier
if($selected.Count -ne 8 -or @($body.rows.coordinatorId | Sort-Object -Unique).Count -ne 2 -or
    @($body.rows.authorityId | Sort-Object -Unique).Count -ne 1 -or
    @($body.provenance | Where-Object { $_.full.publicationLine -ne $_.compact.publicationLine }).Count -ne $selected.Count) {
    throw 'Real local owners or original full/reduced locations collapsed'
}
foreach($entry in $body.inputs.PSObject.Properties) {
    if((Get-FileTransferEvidenceSha256 $entry.Value.absolutePath) -cne $entry.Value.sha256 -or
        [IO.File]::ReadAllLines($entry.Value.absolutePath).Length -ne $entry.Value.decodedLineCount) { throw 'Input descriptor not exact file bytes' }
}
for($i=0;$i -lt $body.rows.Count;$i++) {
    foreach($view in @('full','compact')) {
        $location=$body.provenance[$i].$view
        $capture=[IO.File]::ReadAllLines($body.inputs.($view+'Capture').absolutePath)
        if((Get-FileTransferLocalCarrierHash $capture[$location.useLine-1]) -cne $body.rows[$i].useContentSha256) { throw 'Use content binding lost' }
        $snapshot=[IO.File]::ReadAllLines($body.inputs.($view+'Snapshot').absolutePath)
        $publication=ConvertFrom-GuiSmokeLogLedgerStructuredFields $snapshot[$location.publicationLine-1]
        $effect=ConvertFrom-GuiSmokeLogLedgerStructuredFields $snapshot[$location.effectLine-1]
        if($publication.transition_id -cne $body.rows[$i].boundaryId -or $publication.snapshot_sha256 -cne $body.rows[$i].boundaryHash -or
            $effect.log_writer_sequence -cne [string]$body.rows[$i].effectWriterSequence) { throw 'Physical publication/effect location lost' }
    }
}
$reverse=$selected.Clone(); [Array]::Reverse($reverse)
$reordered=New-FileTransferLocalPublicationEvidenceCarrier $fullArgs $compactArgs $request $reverse
if($reordered.Json -cne $carrier.Json) { throw 'Canonical row ordering depends on request order' }
$single=New-FileTransferLocalPublicationEvidenceCarrier $fullArgs $compactArgs $request @($selected[0])
if($single.Status -cne 'ready' -or ($single.Json | ConvertFrom-Json).rows -isnot [array]) { throw 'Singleton rows collapsed' }
[IO.File]::WriteAllText((Join-Path $output 'carrier.json'),$carrier.Json,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output 'carrier-receipt.json'),(ConvertTo-FileTransferLocalCarrierJson @{
    requestId=$request; sha256=$carrier.Sha256; inputsSha256=$carrier.InputsSha256; semanticSha256=$carrier.SemanticSha256}),[Text.UTF8Encoding]::new($false))

$negative=0
function Omitted($Result,[string]$Label) {
    if($Result.Status -cne 'omitted') { throw "Negative admitted: $Label" }
    foreach($property in @('Json','Sha256','InputsSha256','SemanticSha256','Carrier')) {
        if($property -cin $Result.PSObject.Properties.Name -and $null -ne $Result.$property) { throw "Omission retained $property : $Label" }
    }
    $script:negative++
}
function RejectReader([string]$Label,[scriptblock]$Mutation) {
    # Schema-only controls deliberately supply a matching transport digest. They
    # are not new validated authority evidence or a trusted production receipt.
    $copy=ConvertFrom-Json -InputObject $carrier.Json
    & $Mutation $copy
    $json=ConvertTo-FileTransferLocalCarrierJson $copy
    Omitted (Read-FileTransferLocalPublicationEvidenceCarrier $json $request (Get-FileTransferLocalCarrierHash $json) `
        (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $copy.inputs))) $Label
}
foreach($property in $body.PSObject.Properties.Name) {
    if($property -ceq 'inputs') { continue }
    RejectReader "missing top $property" {param($copy) $copy.PSObject.Properties.Remove($property)}
}
foreach($property in $body.rows[0].PSObject.Properties.Name) {
    RejectReader "missing row $property" {param($copy) $copy.rows[0].PSObject.Properties.Remove($property)}
    RejectReader "wrong row $property" {param($copy) $copy.rows[0].$property='wrong'}
}
foreach($property in $body.rows[0].routeState.PSObject.Properties.Name) {
    RejectReader "missing tuple $property" {param($copy) $copy.rows[0].routeState.PSObject.Properties.Remove($property)}
    RejectReader "wrong tuple $property" {param($copy) $copy.rows[0].routeState.$property='wrong'}
}
foreach($role in $body.inputs.PSObject.Properties.Name) {
    foreach($property in @('absolutePath','decodedLineCount','sha256','domain')) {
        RejectReader "missing input $role $property" {param($copy) $copy.inputs.$role.PSObject.Properties.Remove($property)}
    }
}
foreach($view in @('full','compact')) {
    foreach($property in @('useLine','publicationLine','effectLine')) {
        foreach($value in @(0,-1,[long]::MaxValue,'1')) {
            RejectReader "bad $view $property $value" {param($copy) $copy.provenance[0].$view.$property=$value}
        }
    }
}
RejectReader 'extra recovery authority' {param($copy) $copy | Add-Member recovered $true}
RejectReader 'extra row permission' {param($copy) $copy.rows[0] | Add-Member eligible $true}
RejectReader 'unproven peer lineage' {param($copy) $copy.peerLineageStatus='complete'}
RejectReader 'bad scope' {param($copy) $copy.scope='complete_publication_proof'}
RejectReader 'active Tuna6' {param($copy) $copy.rows[0].routeState.route='file_tuna_v6'; $copy.rows[0].routeState.protocolVersion=6}
RejectReader 'wrong regular protocol' {param($copy) $copy.rows[0].routeState.protocolVersion=6}
RejectReader 'operation receipt generation mismatch' {param($copy) $copy.rows[0].receiptGeneration++}
RejectReader 'foreign well-shaped effect' {param($copy) $copy.rows[0].effectIdentity='fte1-'+('a'*24)}
RejectReader 'effect before publication' {param($copy) $copy.rows[0].effectWriterSequence=$copy.rows[0].boundaryWriterSequence}
RejectReader 'effect after use' {param($copy) $copy.rows[0].effectWriterSequence=$copy.rows[0].useWriterSequence}
RejectReader 'use past writer input' {param($copy) $copy.rows[0].useWriterSequence=$copy.inputs.writer.decodedLineCount+1}
RejectReader 'observation before authored generation' {param($copy) $copy.rows[0].observedStateGeneration=0}
RejectReader 'duplicate logical use' {param($copy) $copy.rows[1]=$copy.rows[0]}
RejectReader 'noncanonical row order' {param($copy) [Array]::Reverse($copy.rows)}
RejectReader 'empty rows' {param($copy) $copy.rows=@(); $copy.provenance=@()}
RejectReader 'scalar rows' {param($copy) $copy.rows=$copy.rows[0]}
RejectReader 'location count mismatch' {param($copy) $copy.provenance=@()}
RejectReader 'reversed physical order' {param($copy) $copy.provenance[0].compact.effectLine=$copy.provenance[0].compact.publicationLine}
RejectReader 'relative input path' {param($copy) $copy.inputs.writer.absolutePath='writer.log'}
RejectReader 'wrong byte domain' {param($copy) $copy.inputs.writer.domain='utf8-lines-lf-v1'}
foreach($json in @(' '+$carrier.Json, ($carrier.Json+' '), ($carrier.Json -replace '"format":','"format":"duplicate","format":'),
    ($carrier.Json -replace '"contractVersion":1','"contractVersion":1.0'), ($carrier.Json -replace '"contractVersion":1','"contractVersion":1e0'),
    ($carrier.Json -replace '"format"','"\u0066ormat"'))) {
    Omitted (Read-FileTransferLocalPublicationEvidenceCarrier $json $request (Get-FileTransferLocalCarrierHash $json) $carrier.InputsSha256) 'noncanonical bytes'
}
Omitted (Read-FileTransferLocalPublicationEvidenceCarrier $carrier.Json ([guid]::NewGuid().ToString()) $carrier.Sha256 $carrier.InputsSha256) 'request binding'
Omitted (Read-FileTransferLocalPublicationEvidenceCarrier $carrier.Json $request ('a'*64) $carrier.InputsSha256) 'payload binding'
Omitted (Read-FileTransferLocalPublicationEvidenceCarrier $carrier.Json $request $carrier.Sha256 ('a'*64)) 'input binding'
Omitted (Read-FileTransferLocalPublicationEvidenceCarrier ($carrier.Json.Replace('not_evaluated','complete')) $request $carrier.Sha256 $carrier.InputsSha256) 'post-producer mutation'
foreach($selection in @(@(),@($selected[0],$selected[0]),@(0),@($lines.Length+1),@(1..65),@(1.5),@('1'),@($null),1L)) {
    Omitted (New-FileTransferLocalPublicationEvidenceCarrier $fullArgs $compactArgs $request $selection) 'invalid selection'
}
foreach($invalidRequest in @('',[guid]::Empty.ToString(),$request.ToUpperInvariant())) {
    Omitted (New-FileTransferLocalPublicationEvidenceCarrier $fullArgs $compactArgs $invalidRequest $selected) 'invalid request'
}
foreach($change in @('missing-private','wrong-hash','wrong-view','wrong-catalog','wrong-writer')) {
    $arguments=$compactArgs.Clone()
    switch($change) {
        'missing-private' { $arguments.SnapshotEvidence=$compact | Select-Object * -ExcludeProperty __RoutePublicationEffects }
        'wrong-hash' { $arguments.SnapshotInputSha256='a'*64 }
        'wrong-view' { $arguments.SnapshotEvidence=$full }
        'wrong-catalog' { $viewCopy=$compactBefore | ConvertFrom-Json; $viewCopy.CatalogHash='a'*64; $arguments.SnapshotEvidence=$viewCopy }
        'wrong-writer' { $arguments.WriterPath=$source }
    }
    Omitted (New-FileTransferLocalPublicationEvidenceCarrier $fullArgs $arguments $request $selected) $change
}
# Changed compact containers retain their original writer membership checks;
# no fake PASS/effect is manufactured and no authority evaluator is rerun.
$candidate=$lines[$selected[0]-1]
foreach($change in @('missing','duplicate','changed')) {
    $changed=[string[]]@($captureLines | Where-Object { $_ -cne $candidate })
    if($change -ceq 'duplicate') { $changed=$captureLines+@($candidate) }
    if($change -ceq 'changed') { $changed+=@($candidate+'; changed=1') }
    $changedPath=Join-Path $output ($change+'.log')
    [IO.File]::WriteAllLines($changedPath,$changed,[Text.UTF8Encoding]::new($false))
    $arguments=$compactArgs.Clone(); $arguments.InputPath=$changedPath; $arguments.InputSha256=Get-FileTransferEvidenceSha256 $changedPath
    Omitted (New-FileTransferLocalPublicationEvidenceCarrier $fullArgs $arguments $request $selected) "compact $change"
}
$duplicateFull=Join-Path $output 'duplicate-full.log'
[IO.File]::WriteAllLines($duplicateFull,($lines+@($candidate)),[Text.UTF8Encoding]::new($false))
$arguments=$fullArgs.Clone(); $arguments.InputPath=$duplicateFull; $arguments.InputSha256=Get-FileTransferEvidenceSha256 $duplicateFull
Omitted (New-FileTransferLocalPublicationEvidenceCarrier $arguments $compactArgs $request $selected) 'full duplicate physical use'

# Exact byte vectors and format-only bounds, not generated authority proofs.
$vector=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$vector['z']=@(); $vector['A']=@($null); $vector['a']=@{lineIndex=1L; evidenceLineIndex=2L}
$vector['b']=@($true,$false,[long]::MinValue,[long]::MaxValue)
$vector['text']='"\'+[char]9+[char]0+[char]233+[char]0xd83d+[char]0xde00+'<>''&'
$expected='{"A":[null],"a":{"evidenceLineIndex":2,"lineIndex":1},"b":[true,false,-9223372036854775808,9223372036854775807],"text":"\"\\\u0009\u0000\u00e9\ud83d\ude00<>''&","z":[]}'
$culture=[Globalization.CultureInfo]::CurrentCulture
try {
    foreach($name in @('en-US','tr-TR','de-DE')) {
        [Globalization.CultureInfo]::CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo($name)
        if((ConvertTo-FileTransferLocalCarrierJson $vector) -cne $expected) { throw "Canonical vector differs: $name" }
    }
}
finally { [Globalization.CultureInfo]::CurrentCulture=$culture }
if((ConvertTo-FileTransferLocalCarrierJson ([pscustomobject]@{})) -cne '{}' -or
    (ConvertTo-FileTransferLocalCarrierJson @()) -cne '[]' -or
    (ConvertTo-FileTransferLocalCarrierJson @($null)) -cne '[null]') { throw 'Empty object/array or singleton canonical vector differs' }
foreach($value in @(1.5,[decimal]1,[uint64]::MaxValue,([string][char]0xd800),([string][char]0xdc00),[datetime]::UtcNow)) {
    $threw=$false
    try { $null=ConvertTo-FileTransferLocalCarrierJson $value } catch { $threw=$true }
    if(-not $threw) { throw 'Unsupported canonical value admitted' }
    $negative++
}
if((ConvertTo-FileTransferLocalCarrierJson ('x'*262142)).Length -ne 262144) { throw 'Exact codec size boundary rejected' }
$threw=$false
try { $null=ConvertTo-FileTransferLocalCarrierJson ('x'*262143) } catch { $threw=$true }
if(-not $threw) { throw 'Codec overflow admitted' }
$negative++
$formatOnly=ConvertFrom-Json -InputObject $carrier.Json
$formatOnly.rows=@(for($i=0;$i -lt 64;$i++) {
    $row=ConvertFrom-Json -InputObject (ConvertTo-FileTransferLocalCarrierJson $body.rows[0]); $row.useWriterSequence=100L+$i; $row
})
$formatOnly.provenance=@(for($i=0;$i -lt 64;$i++) { $body.provenance[0] })
$formatJson=ConvertTo-FileTransferLocalCarrierJson $formatOnly
if((Read-FileTransferLocalPublicationEvidenceCarrier $formatJson $request (Get-FileTransferLocalCarrierHash $formatJson) $carrier.InputsSha256).Status -cne
    'validated_local_carrier') { throw 'Format-only 64-row boundary rejected' }
$formatOnly.rows+=@($formatOnly.rows[0]); $formatOnly.provenance+=@($body.provenance[0])
$formatJson=ConvertTo-FileTransferLocalCarrierJson $formatOnly
Omitted (Read-FileTransferLocalPublicationEvidenceCarrier $formatJson $request (Get-FileTransferLocalCarrierHash $formatJson) $carrier.InputsSha256) '65-row format bound'
$formatOnly=ConvertFrom-Json -InputObject $carrier.Json
$formatOnly.rows=@($formatOnly.rows[0],$formatOnly.rows[1]); $formatOnly.provenance=@($formatOnly.provenance[0],$formatOnly.provenance[1])
$formatOnly.rows[0].writer='a'; $formatOnly.rows[1].writer='a0'
$formatJson=ConvertTo-FileTransferLocalCarrierJson $formatOnly
if((Read-FileTransferLocalPublicationEvidenceCarrier $formatJson $request (Get-FileTransferLocalCarrierHash $formatJson) $carrier.InputsSha256).Status -cne
    'validated_local_carrier') { throw 'Format-only ordinal writer-prefix ordering rejected' }
[Array]::Reverse($formatOnly.rows)
$formatJson=ConvertTo-FileTransferLocalCarrierJson $formatOnly
Omitted (Read-FileTransferLocalPublicationEvidenceCarrier $formatJson $request (Get-FileTransferLocalCarrierHash $formatJson) $carrier.InputsSha256) 'reversed ordinal writers'
$padding=262144-$carrier.Json.Length
$formatOnly=ConvertFrom-Json -InputObject $carrier.Json
$formatOnly.scenarioRunId+='x'*$padding
$formatJson=ConvertTo-FileTransferLocalCarrierJson $formatOnly
if($formatJson.Length -ne 262144 -or (Read-FileTransferLocalPublicationEvidenceCarrier $formatJson $request `
    (Get-FileTransferLocalCarrierHash $formatJson) $carrier.InputsSha256).Status -cne 'validated_local_carrier') { throw 'Exact payload byte limit rejected' }
$formatJson+=' '
Omitted (Read-FileTransferLocalPublicationEvidenceCarrier $formatJson $request (Get-FileTransferLocalCarrierHash $formatJson) $carrier.InputsSha256) 'payload byte overflow'
$read.Carrier.rows[0].coordinatorId='caller-change'
if((Read-FileTransferLocalPublicationEvidenceCarrier $carrier.Json $request $carrier.Sha256 $carrier.InputsSha256).Status -cne 'validated_local_carrier' -or
    $fullBefore -cne (ConvertTo-Json -InputObject $full -Depth 64 -Compress) -or
    $compactBefore -cne (ConvertTo-Json -InputObject $compact -Depth 64 -Compress) -or $existingBefore -cne (ExistingOutcomes) -or
    $inputHash -cne (Get-FileTransferEvidenceSha256 $source)) { throw 'Input/result isolation failed' }
'local-publication-carrier='+ (ConvertTo-Json -InputObject ([ordered]@{UseRecords=$selected.Count; LocalPublications=2; NegativeControls=$negative
    SemanticSha256=$carrier.SemanticSha256; CanonicalVectorSha256=(Get-FileTransferLocalCarrierHash $expected)
    FullReducedParity=$true; OriginalLocationsPreserved=$true; ExistingOutcomesUnchanged=$true; NoAuthorityReevaluation=$true
    PeerLineage='not_evaluated'; ProductionCaller=$false}) -Compress)
