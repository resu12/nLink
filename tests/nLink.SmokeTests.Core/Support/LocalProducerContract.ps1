param([string]$RepoRoot,[string]$ArtifactRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/GuiSmokeContentHash.ps1')

function Parse([string]$Path) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if(@($errors).Count) { throw "Parse failed: $Path" }
    return $ast
}
function Definition($Ast,[string]$Name) {
    $found=@($Ast.FindAll({param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
    },$true))
    if($found.Count -ne 1) { throw "Missing or ambiguous function: $Name" }
    return $found[0]
}
function Assignment($Ast,[string]$Variable) {
    $found=@($Ast.FindAll({param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -ceq $Variable
    },$true))
    if($found.Count -ne 1) { throw "Missing or ambiguous assignment: $Variable" }
    return $found[0].Extent.Text
}
function SameLines([string[]]$Expected,[string[]]$Actual) {
    if($Expected.Count -ne $Actual.Count) { throw 'Native input count changed' }
    for($i=0; $i -lt $Expected.Count; $i++) {
        if($Expected[$i] -cne $Actual[$i]) { throw "Native input order/content changed: $i" }
    }
}
function Hash([byte[]]$Bytes) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
# Contract reference only. This whole-input implementation is deliberately not
# a production pre-exit helper or a claim about bounded preparation cost.
function NativeHash($Lines) {
    if($Lines -isnot [string[]]) { throw 'native_input_type' }
    $stream=[IO.MemoryStream]::new(); $writer=[IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('nlink-ordered-utf16le-v1'+[char]0))
        $writer.Write([uint64]$Lines.Length)
        foreach($line in $Lines) {
            if($null -eq $line) { throw 'native_null_line' }
            $writer.Write([uint64]$line.Length)
            foreach($character in $line.ToCharArray()) { $writer.Write([uint16]$character) }
        }
        $writer.Flush()
        return Hash $stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

$orchestration=Parse (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
$compact=Definition $orchestration 'Write-FileTransferCompactRetainedAnalysisArtifacts'
$full=Definition $orchestration 'Invoke-FileTransferRetainedAnalysis'
$snapshotCatalog=Get-FileTransferCoordinatorEvidenceCatalog
# Capture the exact argument arrays, not an authority evaluation or fixture PASS.
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    $script:inputs.Add([string[]]@($Lines))
    return [pscustomobject]@{ContractInputCaptureOnly=$true}
}
function Invoke-FileTransferFullReportAction { param($StageObserver,[scriptblock]$Action) & $Action }
$fullReportStageObserver=$null
$compactCode=(Assignment $compact '[string[]]$primaryFullSnapshotLines')+"`n"+
    (Assignment $compact '[string[]]$primaryCompactSnapshotLines')+"`n"+
    (Assignment $compact '$fullSnapshotEvaluation')+"`n"+(Assignment $compact '$compactSnapshotEvaluation')
$fullCode=(Assignment $full '[string[]]$fullSnapshotLines')+"`n"+(Assignment $full '$fullSnapshotEvaluation')+"`n"+
    (Assignment $full '[string[]]$fullCompactSnapshotLines')+"`n"+(Assignment $full '$compactSnapshotEvaluation')
$eventNames=@('unrelated','filetransfer_coordinator_snapshot_stream_started','filetransfer_data_path_send_result',
    'filetransfer_coordinator_authority_effect','filetransfer_data_path_send_result',
    'filetransfer_coordinator_transition_snapshot','unrelated','filetransfer_coordinator_snapshot_stream_completed')
$events=@(for($i=0; $i -lt $eventNames.Count; $i++) {
    [pscustomobject]@{EventName=$eventNames[$i];RawLine="event=$($eventNames[$i]); ordinal=$i"}
})
$cases=@(
    @{Name='empty';Events=@();Compact=@()},
    @{Name='one-use';Events=@($events[2]);Compact=@()},
    @{Name='one-snapshot';Events=@($events[1]);Compact=@($events[1].RawLine)},
    @{Name='mixed';Events=$events;Compact=@($events[1].RawLine,$events[3].RawLine,$events[5].RawLine,$events[7].RawLine)},
    @{Name='duplicate';Events=@($events[3],$events[3]);Compact=@($events[3].RawLine,$events[3].RawLine)},
    @{Name='reordered';Events=@($events[5],$events[3]);Compact=@($events[5].RawLine,$events[3].RawLine)}
)
$rows=[Collections.Generic.List[object]]::new()
foreach($owner in @('compact-writer','full-writer')) {
    foreach($case in $cases) {
        $Index=@{AllEvents=$case.Events}; $eventsForArtifacts=$case.Events
        $before=ConvertTo-Json -InputObject $case.Events -Depth 5 -Compress
        $script:inputs=[Collections.Generic.List[object]]::new()
        Invoke-Expression $(if($owner -ceq 'compact-writer') { $compactCode } else { $fullCode })
        if($script:inputs.Count -ne 2) { throw 'Primary input assembly changed its evaluation call count' }
        SameLines @($case.Events | ForEach-Object RawLine) $script:inputs[0]
        SameLines $case.Compact $script:inputs[1]
        if((ConvertTo-Json -InputObject $case.Events -Depth 5 -Compress) -cne $before) { throw 'Input assembly mutated events' }
        $rows.Add([ordered]@{Owner=$owner;Case=$case.Name;FullCount=$script:inputs[0].Count;CompactCount=$script:inputs[1].Count})
    }
}

# Execute the real file parser: physical source offsets survive skipped lines.
$source=Join-Path $ArtifactRoot 'source.log'
[IO.File]::WriteAllText($source,"ignored text`n`nevent=unrelated; value=A`nevent=unrelated; value=B",[Text.UTF8Encoding]::new($false))
$parsed=@(Read-FileTransferLogEvents -LogFiles @($source) -TailMinutes 0)
if($parsed.Count -ne 2 -or $parsed[0].LineNumber -ne 3 -or $parsed[1].LineNumber -ne 4 -or
    $parsed[0].Sequence -ne 3 -or $parsed[1].Sequence -ne 4) { throw 'Physical source and native input positions conflated' }
$originHash=Hash ([IO.File]::ReadAllBytes($source))
Invoke-Expression (Definition $orchestration 'Add-FileTransferLiveHarnessEvidence').Extent.Text
function Get-FileTransferLiveProgressTimeoutEvidence {
    param($CandidateStdoutPaths)
    [pscustomobject]@{Count=1;Reason='contract-only';TotalWaitSeconds=1;ProgressTimeoutSeconds=1;
        ReceiverNextChunk=0;ReceiverHighestChunk=0;ProgressEventCount=0;StdoutPath=(Join-Path $ArtifactRoot 'fixture-stdout.log')}
}
$augmented=@(Add-FileTransferLiveHarnessEvidence -Events $parsed -LogFiles @($source) -ArtifactDir $ArtifactRoot -TransferId 'contract-only')
if($augmented.Count -ne 4 -or $augmented[2].LineNumber -ne 0 -or $augmented[3].LineNumber -ne 0 -or
    $augmented[2].Fields.ContainsKey('log_writer_id') -or $augmented[3].Fields.ContainsKey('log_writer_id') -or
    (Hash ([IO.File]::ReadAllBytes($source))) -cne $originHash) { throw 'Synthetic augmentation premise changed' }

$vectors=[ordered]@{
    empty=[string[]]@();blank=[string[]]@('');pair=[string[]]@('A','B');embeddedLf=[string[]]@("A`nB")
    reordered=[string[]]@('B','A');duplicate=[string[]]@('A','A');case=[string[]]@('a','B')
    trailingBlank=[string[]]@('A','B','');bom=[string[]]@(([char]0xfeff+'A'),'B')
    highSurrogate=[string[]]@([string][char]0xd800);replacement=[string[]]@([string][char]0xfffd)
    composed=[string[]]@([string][char]0xe9);decomposed=[string[]]@('e'+[char]0x301)
}
$hashes=[ordered]@{}
foreach($name in $vectors.Keys) { $hashes[$name]=NativeHash $vectors[$name] }
if(@($hashes.Values | Sort-Object -Unique).Count -ne $vectors.Count) { throw 'Native input framing collision' }
$pinned=@{
    empty='e0d5da8ece8aa85f55de0491cfa4a8a0b0024f40b01d5474a499cc244679e670'
    blank='17e6c97aa6bc619bdfc959ad84158419aee8404a32d0bc7ff0df90189d31b644'
    pair='53b9fc58d0134ef0a2f9ec7563a61bd21a11401c87e0432cb5589b268cbc2f54'
    highSurrogate='33a66b724245f06e5f2531f74081467d22b6a34a4195b763d3ce851406c3706f'
}
foreach($name in $pinned.Keys) { if($hashes[$name] -cne $pinned[$name]) { throw "Native byte vector changed: $name" } }
$lfPair=Get-GuiSmokeLogLedgerHash ([string]::Join("`n",$vectors.pair))
$lfEmbedded=Get-GuiSmokeLogLedgerHash ([string]::Join("`n",$vectors.embeddedLf))
if($lfPair -cne $lfEmbedded) { throw 'LF-domain counterexample changed' }
$invalid=0
foreach($value in @(@{Value=$null},@{Value=[object[]]@('A')},@{Value=[string[]]::new(1)})) {
    $rejected=$false
    try { $null=NativeHash $value.Value } catch { $rejected=$true }
    if(-not $rejected) { throw 'Invalid native input admitted' }
    $invalid++
}
$other=Join-Path $ArtifactRoot 'same-lines-different-bytes.log'
[IO.File]::WriteAllText($other,"ignored text`r`n`r`nevent=unrelated; value=A`r`nevent=unrelated; value=B`r`n",[Text.UTF8Encoding]::new($true))
if((NativeHash ([IO.File]::ReadAllLines($source))) -cne (NativeHash ([IO.File]::ReadAllLines($other))) -or
    (Hash ([IO.File]::ReadAllBytes($other))) -ceq $originHash) { throw 'Native and original file-byte identities conflated' }

$attempts=0
foreach($pair in @(@('tools/Run-FileTransferRouteAcceptance.ps1','Invoke-RouteAcceptanceRetainedAnalysis'),
    @('tools/Run-FileTransferTunaGuiSmoke.ps1','Invoke-TunaGuiRetainedAnalysis'))) {
    $parent=Definition (Parse (Join-Path $RepoRoot $pair[0])) $pair[1]
    Invoke-Expression (Assignment $parent '$attemptId')
    $request=[guid]::ParseExact($attemptId,'N').ToString('D')
    if([guid]::ParseExact($request,'D').ToString('N') -cne $attemptId -or $request -ceq $attemptId) { throw 'Attempt/request identity conversion changed' }
    $attempts++
}
'local-producer-contract='+(ConvertTo-Json -InputObject ([ordered]@{
    InputAssemblyCases=$rows.Count;Rows=@($rows.ToArray());ParserPhysicalLines=@(3,4);SyntheticRecords=2
    NativeDomain='nlink-ordered-utf16le-v1';NativeVectors=$hashes;InvalidNativeInputs=$invalid
    ParentIdentityMappings=$attempts;AuthorityEvaluations=0;ProductionCaller=$false;ProducerCompletionBinding='not_implemented'
}) -Depth 8 -Compress)
