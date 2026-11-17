param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/PublicationForwardingEvidence.ps1')
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
    -not $full.StreamComplete -or -not $compact.StreamComplete -or $parity.Verdict -cne 'pass') {
    throw 'Generated full/reduced Snapshot validation did not pass'
}
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Consumer attempted a new authority evaluation' }
$fullBefore=ConvertTo-Json -InputObject $full -Depth 64 -Compress
$compactBefore=ConvertTo-Json -InputObject $compact -Depth 64 -Compress
$names=Get-GuiSmokeSemanticEvidenceEventNames
$captureLines=[string[]]@($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName $_) -cin $names })
$capturePath=Join-Path $output 'capture-compact.log'
[IO.File]::WriteAllLines($capturePath,$captureLines,[Text.UTF8Encoding]::new($false))
function Index($View,[string]$CapturePath,[string]$SnapshotPath) {
    New-FileTransferPublicationForwardingIndex $View $CapturePath (Get-FileTransferEvidenceSha256 $CapturePath) `
        $writerPath $ledger $RunId $ScenarioId $SnapshotPath (Get-FileTransferEvidenceSha256 $SnapshotPath)
}
$fullIndex=Index $full $source $source
$compactIndex=Index $compact $capturePath $snapshotPath
function Results($Index) {
    if($Index.Reason) { throw "Generated index failed: $($Index.Reason)" }
    @(for($i=0;$i -lt $Index.Local.Lines.Length;$i++) {
        if($Index.Local.Lines[$i] -notmatch '; publication_use_kind=') { continue }
        Resolve-FileTransferPublicationForwardingEvidence $Index $Index.Local.Path ($i+1) $Index.Local.Lines[$i]
    })
}
function Semantic($Rows) {
    $facts=@($Rows | ForEach-Object {
        [ordered]@{
            Use=$_.Forwarding.Use | Select-Object * -ExcludeProperty SourcePath,SourceLine
            Effect=$_.Forwarding.Effect | Select-Object * -ExcludeProperty SourcePath,SourceLine
        }
    })
    ConvertTo-Json -InputObject $facts -Depth 6 -Compress
}
function LocalResults($Index) {
    $facts=@(for($i=0;$i -lt $Index.Local.Lines.Length;$i++) {
        $line=$Index.Local.Lines[$i]
        if($line -notmatch '; publication_use_kind=') { continue }
        $local=Resolve-FileTransferPublicationUseEvidence $Index.Local $Index.Local.Path ($i+1) $line
        $legacy=Resolve-FileTransferPublicationEvidence $Index.Local.Publications ([pscustomobject]@{
            Fields=(ConvertFrom-GuiSmokeLogLedgerStructuredFields $line)})
        [ordered]@{Local=$local.Reference | Select-Object * -ExcludeProperty SourcePath,SourceLine; Legacy=$legacy}
    })
    ConvertTo-Json -InputObject $facts -Depth 6 -Compress
}
$localBefore=LocalResults $fullIndex
$results=Results $fullIndex
$reduced=Results $compactIndex
if($results.Count -eq 0 -or @($results | Where-Object Status -CNE 'matched_local_forwarding').Count -ne 0 -or
    @($reduced | Where-Object Status -CNE 'matched_local_forwarding').Count -ne 0 -or
    (Semantic $results) -cne (Semantic $reduced)) { throw 'Generated forwarding references did not match with exact full/reduced parity' }
if((@($results.Forwarding.Use.UseKind | Sort-Object -Unique) -join '|') -cne
    'receive_dispatch|v4_physical_submission|v4_pre_pump_request') { throw 'Generated use kinds collapsed or missing' }
if(@($fullIndex.Effects).Count -ne 2 -or
    @($fullIndex.Effects.AuthorityId | Sort-Object -Unique).Count -ne 1 -or
    @($fullIndex.Effects.CoordinatorInstanceId | Sort-Object -Unique).Count -ne 2) {
    throw 'Fixture did not demonstrate independent local owners with equal authority values'
}
$candidate=$results[0].Forwarding.Use
$candidateLine=[long]$candidate.SourceLine
$raw=$fullIndex.Local.Lines[$candidateLine-1]
$effectNumber=-1
for($i=0;$i -lt $fullIndex.Effects.Count;$i++) {
    if($fullIndex.Effects[$i].CoordinatorInstanceId -ceq $candidate.CoordinatorId -and
        $fullIndex.Effects[$i].TransitionId -ceq $candidate.BoundaryId) { $effectNumber=$i }
}
if($effectNumber -lt 0) { throw 'Original forwarding effect missing' }
$negative=0
function Reject([string]$Label,[scriptblock]$Mutation) {
    # Predicate controls deliberately alter detached index copies. They are not
    # newly validated Snapshot/provenance fixtures or runtime-authority proof.
    $copy=$fullIndex.Clone()
    $detached=ConvertTo-Json -InputObject $fullIndex.Effects -Depth 8 -Compress | ConvertFrom-Json
    $copy.Effects=@($detached); $copy.SnapshotLines=$fullIndex.SnapshotLines.Clone()
    & $Mutation $copy
    $result=Resolve-FileTransferPublicationForwardingEvidence $copy $source $candidateLine $raw
    if($result.Status -cne 'unproven' -or $null -ne $result.Forwarding) { throw "Negative admitted: $Label" }
    $script:negative++
}
function SetRaw($Index,[string]$Name,[string]$Value) {
    $offset=[long]$Index.Effects[$effectNumber].LineIndex
    $pattern='(?<![A-Za-z0-9_])'+[regex]::Escape($Name)+'=[^;\r\n]*'
    if($Index.SnapshotLines[$offset] -notmatch $pattern) { throw "Mutation field missing: $Name" }
    $Index.SnapshotLines[$offset]=[regex]::Replace($Index.SnapshotLines[$offset],$pattern,($Name+'='+$Value))
}
$rawFields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $fullIndex.SnapshotLines[$fullIndex.Effects[$effectNumber].LineIndex]
foreach($name in @('event','coordinator_instance_id','session_id','transfer_id','direction','transition_id',
    'coordinator_state_generation','operation_family','operation_key','operation_authority_id','operation_generation',
    'effect_kind','effect_identity','effect_generation','receipt_identity','receipt_generation','receipt_status','receipt_fingerprint',
    'route','protocol_version','live_route_epoch','route_publication_generation','route_commit_generation','runtime_generation',
    'terminal_generation','leg_generation','transport_epoch','bridge_recovery_generation','checkpoint_generation','checkpoint_request_id',
    'data_session_identity','data_session_generation','authority_effect','log_writer_id','log_writer_sequence')) {
    Reject "wrong raw $name" {param($copy) SetRaw $copy $name 'wrong'}
    Reject "missing raw $name" {param($copy)
        $offset=[long]$copy.Effects[$effectNumber].LineIndex
        $copy.SnapshotLines[$offset]=[regex]::Replace($copy.SnapshotLines[$offset],('(?<![A-Za-z0-9_])'+[regex]::Escape($name)+'=[^;\r\n]*'),'')
    }
    Reject "duplicate raw $name" {param($copy)
        $offset=[long]$copy.Effects[$effectNumber].LineIndex
        $copy.SnapshotLines[$offset]+='; '+$name+'='+$rawFields[$name]
    }
}
foreach($name in $fullIndex.Effects[$effectNumber].PSObject.Properties.Name) {
    Reject "wrong detached $name" {param($copy) $copy.Effects[$effectNumber].$name='wrong'}
}
Reject 'missing effect' {param($copy) $copy.Effects=@()}
Reject 'duplicate exact effect' {param($copy) $copy.Effects+=@($copy.Effects[$effectNumber])}
Reject 'foreign equal-valued authority cannot supply effect' {param($copy) $copy.Effects=@($copy.Effects[1-$effectNumber])}
foreach($value in @('0','-1','01','+1','1.0','9223372036854775808')) {
    Reject "effect generation $value" {param($copy) SetRaw $copy 'effect_generation' $value}
}
foreach($sequence in @([long]$fullIndex.Local.Publications.Boundaries["$($candidate.CoordinatorId)|$($candidate.BoundaryId)"][0].writerSequence,
    $candidate.WriterSequence,($candidate.WriterSequence+1))) {
    Reject "invalid local effect order $sequence" {param($copy)
        $copy.Effects[$effectNumber].WriterSequence=[string]$sequence
        SetRaw $copy 'log_writer_sequence' ([string]$sequence)
    }
}
# Agreeing raw/view mutations must still fail the exact authored operation link.
foreach($pair in @(@('ReceiptFingerprint','receipt_fingerprint'),@('AuthorityId','operation_authority_id'),
    @('DataSessionIdentity','data_session_identity'),@('CheckpointRequestId','checkpoint_request_id'))) {
    Reject ("raw/view agree but authored tuple differs: "+$pair[0]) {param($copy)
        $copy.Effects[$effectNumber].($pair[0])='fte1-'+('a'*24)
        SetRaw $copy $pair[1] ('fte1-'+('a'*24))
    }
}
foreach($change in @('missing','null','empty')) {
    $view=$fullBefore | ConvertFrom-Json
    switch($change) {
        'missing' {$view=$view | Select-Object * -ExcludeProperty __RoutePublicationEffects}
        'null' {$view.__RoutePublicationEffects=$null}
        'empty' {$view.__RoutePublicationEffects=@()}
    }
    $unsupported=Index $view $source $source
    $result=Resolve-FileTransferPublicationForwardingEvidence $unsupported $source $candidateLine $raw
    if($result.Status -cne 'unproven' -or $unsupported.Local.Reason -or
        (Resolve-FileTransferPublicationUseEvidence $unsupported.Local $source $candidateLine $raw).Status -cne 'matched_local_reference') {
        throw "Missing private forwarding altered local-reference behavior: $change"
    }
    $negative++
}
$wrongHash=New-FileTransferPublicationForwardingIndex $full $source $inputHash $writerPath $ledger $RunId $ScenarioId $source ('a'*64)
if(-not $wrongHash.Reason) { throw 'Wrong Snapshot input hash admitted' }
$negative++
if((Resolve-FileTransferPublicationForwardingEvidence $fullIndex ($source+'.wrong') $candidateLine $raw).Status -cne 'unproven' -or
    (Resolve-FileTransferPublicationForwardingEvidence $fullIndex $source $candidateLine ($raw+'; changed=1')).Status -cne 'unproven') {
    throw 'Original use membership bypassed'
}
$negative+=2
$detached=Resolve-FileTransferPublicationForwardingEvidence $fullIndex $source $candidateLine $raw
$detached.Forwarding.Use.CoordinatorId='caller-change'; $detached.Forwarding.Effect.EffectIdentity='caller-change'
$originalIdentity=$full.__RoutePublicationEffects[$effectNumber].EffectIdentity
$full.__RoutePublicationEffects[$effectNumber].EffectIdentity='caller-change'
if((Semantic (Results $fullIndex)) -cne (Semantic $results)) { throw 'Input/result mutation changed frozen forwarding index' }
$full.__RoutePublicationEffects[$effectNumber].EffectIdentity=$originalIdentity
if($localBefore -cne (LocalResults $fullIndex) -or $localBefore -cne (LocalResults $compactIndex) -or
    $fullBefore -cne (ConvertTo-Json -InputObject $full -Depth 64 -Compress) -or
    $compactBefore -cne (ConvertTo-Json -InputObject $compact -Depth 64 -Compress) -or
    $inputHash -cne (Get-FileTransferEvidenceSha256 $source)) { throw 'Existing input or local/shared resolver outcome changed' }
$json=[ordered]@{Facts=(Semantic $results);UseRecords=$results.Count;LocalPublications=2;NegativeControls=$negative;
    FullReducedParity=$true;ExistingLocalAndSharedOutcomesUnchanged=$true;NoAuthorityReevaluation=$true} | ConvertTo-Json -Compress
'publication-forwarding='+$json
