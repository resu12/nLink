param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[switch]$TestBinding,[switch]$TestReadLease,[switch]$TestWitness,[switch]$TestWriterCapture)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeInputCapture.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/PublicationForwardingEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
function Parse([string]$Path) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if(@($errors).Count) { throw "Parse failed: $Path" }
    return $ast
}
function Definition($Ast,[string]$Name) {
    $found=@($Ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name},$true))
    if($found.Count -ne 1) { throw "Function missing or ambiguous: $Name" }
    return $found[0]
}
function Assignment($Ast,[string]$Variable) {
    $found=@($Ast.FindAll({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq $Variable},$true))
    if($found.Count -ne 1) { throw "Assignment missing or ambiguous: $Variable" }
    return $found[0].Extent.Text
}
# Reuse the separately pinned test reference, never a second authority evaluator.
$reference=Parse (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalProducerContract.ps1')
foreach($name in @('Hash','NativeHash')) { Invoke-Expression (Definition $reference $name).Extent.Text }
$source=Join-Path $Directory 'nlink.log'
$sourceReadLease=$null; $writerReadLease=$null
$inputHash=if($TestReadLease) { Get-FileTransferEvidenceSha256 $source -ReadLease ([ref]$sourceReadLease) }
    else { Get-FileTransferEvidenceSha256 $source }
$lines=[IO.File]::ReadAllLines($source)
$output=Join-Path $Directory ('ps'+$PSVersionTable.PSVersion.Major+'-'+[guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $output)
$writerPath=Join-Path $output 'writer.log'
$nativeWriterMethod=if($TestWriterCapture) { 'ProcessWithNativeCapture' } else { 'Process' }
$ledger=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::$nativeWriterMethod($source,$lines.Length,0,
    (Join-Path $output 'retained.log'),$writerPath,$RunId,$ScenarioId,[string[]]@())
if($TestReadLease) {
    $writerReadLease=[NLink.Tools.LocalInputReadLease]::OpenExpected($writerPath,$ledger.WriterSha256,$ledger.WriterLineCount)
    if($null -eq $sourceReadLease -or -not $sourceReadLease.IsVerified -or $null -eq $writerReadLease -or $writerReadLease.IsVerified) {
        throw 'Expected source ownership / pending writer identity unavailable'
    }
    $ownedWriterLines=[Collections.Generic.List[string]]::new()
    foreach($line in $writerReadLease.VerifyAndReadLines()) { $ownedWriterLines.Add($line) }
    if(-not $writerReadLease.IsVerified -or $writerReadLease.VerifiedLineCount -ne $ledger.WriterLineCount) { throw 'Writer did not verify at EOF' }
}
if($TestWriterCapture) {
    $writerCapture=$ledger.NativeCapture
    if(-not $ledger.OwnsNativeCapture($writerCapture) -or -not $TestReadLease -or
        $writerCapture.SourcePath -cne $source -or $writerCapture.AcceptanceRunId -cne $RunId -or $writerCapture.ScenarioRunId -cne $ScenarioId -or
        $writerCapture.Writer.Sha256 -cne $writerReadLease.ExpectedSha256 -or $writerCapture.Writer.FileIdentity -cne $writerReadLease.FileIdentity -or
        $writerCapture.Writer.LineCount -ne $writerReadLease.VerifiedLineCount -or $writerCapture.FrozenLineCount -ne $lines.Length) {
        throw 'Generated native writer provenance did not match the independently verified original file'
    }
}
$events=@(Read-FileTransferLogEvents -LogFiles @($source) -TailMinutes 0)
$nativeFull=[string[]]@($events | ForEach-Object RawLine)
for($i=0; $i -lt $events.Count; $i++) {
    if($events[$i].LineNumber -lt 1 -or $events[$i].RawLine -cne $lines[$events[$i].LineNumber-1]) { throw 'Native-to-physical source binding lost' }
}
$Index=@{AllEvents=$events}
if($TestWitness) {
    . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPublicationFailureWitness.ps1')
    $Index=New-FileTransferRetainedEventIndex -Events $events -LogFiles @($source)
}
$request=[guid]::NewGuid()
$owner=[NLink.Tools.LocalNativeInputCapture]::new($request)
$realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
$script:evaluations=0; $script:nativeInputs=@{}; $script:captureWallMilliseconds=0.0
function Capture([string]$Role,[string[]]$Lines) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    try {
        if(-not $owner.BeginInput($Role,$Lines.Length)) { throw "Begin omitted: $($owner.OmissionReason)" }
        for($i=0; $i -lt $Lines.Length; $i++) { if(-not $owner.AppendLine($i+1,$Lines[$i])) { throw "Capture omitted: $($owner.OmissionReason)" } }
        if(-not $owner.EndInput()) { throw "End omitted: $($owner.OmissionReason)" }
        $script:nativeInputs[$Role]=$Lines.Clone()
    }
    finally { $watch.Stop(); $script:captureWallMilliseconds+=$watch.Elapsed.TotalMilliseconds }
}
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([string[]]$Lines)
    $script:evaluations++
    # Test-only observation at the real input-call seam, before evaluation.
    # Its caller-loop cost is recorded separately from time inside the C# API.
    if($script:evaluations -eq 1) {
        Capture 'fullCapture' $Lines; Capture 'fullSnapshot' $Lines; Capture 'compactCapture' $Lines
    }
    elseif($script:evaluations -eq 2) { Capture 'compactSnapshot' $Lines }
    else { throw 'Unexpected authority reevaluation' }
    return & $realEvaluator -Lines $Lines
}
try {
    $orchestration=Parse (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
    $writer=Definition $orchestration 'Write-FileTransferCompactRetainedAnalysisArtifacts'
    $snapshotCatalog=Get-FileTransferCoordinatorEvidenceCatalog
    Invoke-Expression (Assignment $writer '[string[]]$primaryFullSnapshotLines')
    Invoke-Expression (Assignment $writer '[string[]]$primaryCompactSnapshotLines')
    Invoke-Expression (Assignment $writer '$fullSnapshotEvaluation')
    Invoke-Expression (Assignment $writer '$compactSnapshotEvaluation')
    $full=$fullSnapshotEvaluation; $compact=$compactSnapshotEvaluation
    $parity=Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
    if($script:evaluations -ne 2 -or $full.ArtifactEvidenceVerdict -cne 'pass' -or $compact.ArtifactEvidenceVerdict -cne 'pass' -or
        -not $full.StreamComplete -or -not $compact.StreamComplete -or $parity.Verdict -cne 'pass') { throw 'Generated views did not independently pass' }
    function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Preparation consumer reevaluated authority' }
    $prepared=$owner.Take()
    if($null -eq $prepared -or -not $owner.Owns($prepared) -or $prepared.RequestId -ne $request) { throw 'Prepared native inputs unavailable' }
    $fullBefore=ConvertTo-Json -InputObject $full -Depth 64 -Compress
    $compactBefore=ConvertTo-Json -InputObject $compact -Depth 64 -Compress
    $digests=[ordered]@{}
    $expectedRecords=0L; $expectedUnits=0L
    foreach($role in @('fullCapture','fullSnapshot','compactCapture','compactSnapshot')) {
        $native=$script:nativeInputs[$role]; $receipt=$prepared.Get($role)
        if($receipt.RecordCount -ne $native.Length -or $receipt.Sha256 -cne (NativeHash $native)) { throw "Exact native hash mismatch: $role" }
        $units=0L; foreach($line in $native) { $units+=$line.Length }
        if($receipt.CodeUnitCount -ne $units) { throw 'Native character accounting changed' }
        $expectedRecords+=$native.Length; $expectedUnits+=$units; $digests[$role]=$receipt.Sha256
    }
    if($owner.RecordVisits -ne $expectedRecords -or $owner.CodeUnitVisits -ne $expectedUnits -or
        $digests.fullCapture -cne $digests.fullSnapshot -or $digests.fullCapture -cne $digests.compactCapture -or
        $digests.fullSnapshot -ceq $digests.compactSnapshot) { throw 'Actual role identities collapsed or work accounting changed' }
    $snapshotPath=Join-Path $output 'snapshot-compact.log'
    [IO.File]::WriteAllLines($snapshotPath,$script:nativeInputs['compactSnapshot'],[Text.UTF8Encoding]::new($false))
    # Test-only materialization of the exact captured native arrays for the
    # existing file-based A/B1 oracle. Do not relabel original physical offsets.
    $nativePath=Join-Path $output 'full-native.log'
    [IO.File]::WriteAllLines($nativePath,$nativeFull,[Text.UTF8Encoding]::new($false))
    $nativeFileHash=Get-FileTransferEvidenceSha256 $nativePath
    $fullIndex=New-FileTransferPublicationForwardingIndex $full $nativePath $nativeFileHash $writerPath $ledger $RunId $ScenarioId $nativePath $nativeFileHash
    $compactIndex=New-FileTransferPublicationForwardingIndex $compact $nativePath $nativeFileHash $writerPath $ledger $RunId $ScenarioId `
        $snapshotPath (Get-FileTransferEvidenceSha256 $snapshotPath)
    if($fullIndex.Reason -or $compactIndex.Reason) { throw 'Existing A/B1 original-input validation failed' }
    if($TestBinding) { . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalNativeInputBinding.ps1') }
    $uses=0
    for($i=0; $i -lt $nativeFull.Length; $i++) {
        if($nativeFull[$i] -notmatch '; publication_use_kind=') { continue }
        $a=Resolve-FileTransferPublicationForwardingEvidence $fullIndex $nativePath ($i+1) $nativeFull[$i]
        $b=Resolve-FileTransferPublicationForwardingEvidence $compactIndex $nativePath ($i+1) $nativeFull[$i]
        if($a.Status -cne 'matched_local_forwarding' -or $b.Status -cne 'matched_local_forwarding' -or
            $a.Forwarding.Use.BoundaryId -cne $b.Forwarding.Use.BoundaryId -or
            $a.Forwarding.Effect.WriterSequence -ne $b.Forwarding.Effect.WriterSequence -or
            $a.Forwarding.Effect.SourceLine -eq $b.Forwarding.Effect.SourceLine) { throw 'Generated local forwarding or native locations changed' }
        if($TestWitness) {
            # Exact generated capture, but deliberately synthetic failure labels:
            # this successful transfer is not a real failed-primary scenario.
            $selected=Resolve-FileTransferPrimaryFailureWitness -Index $Index -Failure ([pscustomobject]@{
                Class='fixture_selection_control';Reason='exact_generated_capture';Event=$events[$i]})
            $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $nativeFull[$i]
            if($null -eq $selected -or $selected.SourceLine -ne $events[$i].LineNumber -or
                $selected.Writer -cne $fields.log_writer_id -or $selected.WriterSequence -ne [long]$fields.log_writer_sequence -or
                $selected.ContentSha256 -cne (Hash ([Text.UTF8Encoding]::new($false,$true).GetBytes($nativeFull[$i])))) {
                throw 'Generated witness lost physical source/writer/content identity'
            }
        }
        $uses++
    }
    if($uses -ne 8 -or (ConvertTo-Json -InputObject $full -Depth 64 -Compress) -cne $fullBefore -or
        (ConvertTo-Json -InputObject $compact -Depth 64 -Compress) -cne $compactBefore -or
        (Get-FileTransferEvidenceSha256 $source) -cne $inputHash) { throw 'Primary views/input changed' }
    if($TestWitness) {
        'failure-witness-generated='+(ConvertTo-Json -InputObject ([ordered]@{Uses=$uses;AuthorityEvaluations=2
            AdditionalAuthorityEvaluations=0;ExactOriginalSourceAndWriterIdentity=$true;PrimaryViewsUnchanged=$true
            FailureLabels='synthetic_selection_controls';ParentWorkerBindingVerified=$false;PublicationCompletionIssued=$false;LiveParentCaller=$false}) -Compress)
    }
    if($TestWriterCapture) {
        'native-writer-generated='+(ConvertTo-Json -InputObject ([ordered]@{Uses=$uses;AuthorityEvaluations=2;AdditionalAuthorityEvaluations=0
            SourceLineVisits=$ledger.SourceLineVisitCount;WriterLineCount=$writerCapture.Writer.LineCount;WriterCount=$writerCapture.Writers.Count
            OriginalWriteIdentityIndependentlyVerified=$true;PrimaryViewsUnchanged=$true;FinalizerCompletionIssued=$false
            ParentWorkerBound=$false;PublicationCompletionIssued=$false;LiveCaller=$false}) -Compress)
    }
    if($TestReadLease) {
        if(-not $sourceReadLease.IsVerified -or -not $writerReadLease.IsVerified -or
            (Get-FileTransferEvidenceLineCount $source) -ne $lines.Length -or
            (Get-FileTransferEvidenceSha256 $writerPath) -cne $writerReadLease.ExpectedSha256 -or
            ([IO.File]::ReadAllLines($writerPath) -join "`n") -cne ($ownedWriterLines -join "`n")) { throw 'Default input readers drifted under leases' }
        'read-lease-generated='+(ConvertTo-Json -InputObject ([ordered]@{Uses=$uses;SourceVerified=$true;WriterVerified=$true
            SourceLineCount=$lines.Length;WriterLineCount=$writerReadLease.VerifiedLineCount
            WriterRecordVisits=$writerReadLease.VerificationRecordVisits;WriterCodeUnitVisits=$writerReadLease.VerificationCodeUnitVisits
            AuthorityEvaluations=2;AdditionalAuthorityEvaluations=0;PrimaryViewsUnchanged=$true;DefaultReadersCompatible=$true
            ParentWorkerBindingVerified=$false;PublicationCompletionIssued=$false;ProductionCaller=$false}) -Compress)
    }
    [IO.File]::WriteAllText((Join-Path $output 'work.json'),(ConvertTo-Json -InputObject @{
        apiActiveMilliseconds=$owner.ActiveMilliseconds;captureWallMilliseconds=$script:captureWallMilliseconds
        recordVisits=$owner.RecordVisits;codeUnitVisits=$owner.CodeUnitVisits;framedByteVisits=$owner.FramedByteVisits
    } -Compress))
    'native-input-generated='+(ConvertTo-Json -InputObject ([ordered]@{Uses=$uses;AuthorityEvaluations=2;AdditionalAuthorityEvaluations=0
        Hashes=$digests;RecordVisits=$owner.RecordVisits;CodeUnitVisits=$owner.CodeUnitVisits;FramedByteVisits=$owner.FramedByteVisits
        PrimaryViewsUnchanged=$true;PublicationCompletionIssued=$false;ProductionCaller=$false
    }) -Depth 5 -Compress)
}
finally {
    $owner.Dispose()
    if($null -ne $sourceReadLease) { $sourceReadLease.Dispose() }
    if($null -ne $writerReadLease) { $writerReadLease.Dispose() }
}
