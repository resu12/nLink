param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$checks=[Collections.Generic.List[string]]::new()
function Line([string]$Writer,[long]$Sequence,[string]$Run,[string]$Scenario) {
    $event=if($Sequence -eq 1) { 'operational_log_writer_started' } else { 'filetransfer_data_path_send_result' }
    return "event=$event; acceptance_run_id=$Run; scenario_run_id=$Scenario; log_writer_id=$Writer; log_writer_sequence=$Sequence; log_writer_batch_generation=$Sequence; log_writer_batch_first_sequence=$Sequence; log_writer_batch_last_sequence=$Sequence; log_writer_attempt_utc_ticks=$Sequence"
}
foreach($mode in @('valid','bookmark','no-semantic','empty-semantic','gap','duplicate','wrong-run','duplicate-field','chronology',
    'run-limit','writer-id-limit','64-writers','65-writers','metadata-limit')) {
    $run=if($mode -ceq 'run-limit') { 'R'*1025 } else { 'native-writer-run' }; $scenario='native-writer-run:scenario:1'
    $writerId=if($mode -ceq 'writer-id-limit') { 'W'*1025 } else { 'writer-a' }
    $lines=[Collections.Generic.List[string]]::new()
    $writerCount=if($mode -ceq '64-writers') { 64 } elseif($mode -ceq '65-writers') { 65 } elseif($mode -ceq 'metadata-limit') { 32 } else { 1 }
    for($i=0;$i -lt $writerCount;$i++) {
        $name=if($writerCount -gt 1) { "writer-$i"+$(if($mode -ceq 'metadata-limit') { 'W'*600 } else { '' }) } else { $writerId }
        $lines.Add((Line $name 1 $run $scenario)); $lines.Add((Line $name 2 $run $scenario))
    }
    switch($mode) {
        'gap' { $lines[1]=Line $writerId 3 $run $scenario }
        'duplicate' { $lines[1]=Line $writerId 1 $run $scenario }
        'wrong-run' { $run='foreign-run' }
        'duplicate-field' { $lines[1]+='; log_writer_sequence=2' }
        'chronology' { $lines[1]=$lines[1].Replace('log_writer_attempt_utc_ticks=2','log_writer_attempt_utc_ticks=1') }
    }
    $path=Join-Path $Directory ($mode+'.log')
    [IO.File]::WriteAllLines($path,$lines.ToArray(),[Text.UTF8Encoding]::new($false))
    $bookmark=if($mode -ceq 'bookmark') { 1L } else { 0L }
    $baseline=$null; $owned=$null
    foreach($enabled in @($false,$true)) {
        $output=Join-Path $Directory ($mode+'-'+$enabled); [void][IO.Directory]::CreateDirectory($output)
        $retained=Join-Path $output 'retained.log'; $writer=Join-Path $output 'writer.log'
        $semantic=if($mode -ceq 'no-semantic') { $null } else { Join-Path $output 'semantic.log' }
        $events=if($mode -ceq 'empty-semantic') { [string[]]@('not-present') } else { [string[]]@('filetransfer_data_path_send_result') }
        $method=if($enabled) { 'ProcessWithNativeCapture' } else { 'Process' }
        $result=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::$method($path,[long]$lines.Count,$bookmark,$retained,$writer,$run,$scenario,[string[]]@(),$semantic,$events)
        $json=$result | Select-Object * -ExcludeProperty NativeCapture | ConvertTo-Json -Depth 8 -Compress
        if(-not $enabled) {
            if($null -ne $result.NativeCapture) { throw 'Default processor opted into native capture' }
            $baseline=$json
        }
        else {
            if($json -cne $baseline) { throw "Native capture changed primary result: $mode" }
            $expected=$mode -cin @('valid','bookmark','no-semantic','empty-semantic','64-writers')
            $capture=$result.NativeCapture
            if(($null -ne $capture) -ne $expected -or $result.OwnsNativeCapture($capture) -ne $expected) { throw "Wrong native capture admission: $mode" }
            if($expected) {
                if($capture.Bookmark -ne $bookmark -or $capture.FrozenLineCount -ne $lines.Count -or
                    $capture.MetadataCodeUnits -gt 16384 -or $capture.Writers.Count -ne $writerCount -or $capture.AcceptanceRunId -cne $run) { throw 'Capture scope/bounds changed' }
                foreach($pair in @(@($capture.Retained,$retained),@($capture.Writer,$writer),@($capture.Semantic,$semantic))) {
                    if($null -eq $pair[0]) { continue }
                    $moved=$pair[1]+'.final'; [IO.File]::Move($pair[1],$moved)
                    $hashResult=[NLink.Tools.LocalInputReadLease]::HashAndHold($moved)
                    try {
                        if($hashResult.Sha256 -cne $pair[0].Sha256 -or $hashResult.Lease.FileIdentity -cne $pair[0].FileIdentity -or
                            $hashResult.Lease.ByteCount -ne $pair[0].ByteCount -or [IO.File]::ReadAllLines($moved).Count -ne $pair[0].LineCount) {
                            throw 'Captured original write identity/hash did not survive primary rename'
                        }
                    }
                    finally { $hashResult.Lease.Dispose() }
                }
                if($mode -ceq 'valid') { $owned=$result; $validPath=$writer+'.final' }
            }
        }
    }
    if($mode -ceq 'valid') {
        $valid=$owned; $validCapture=$owned.NativeCapture
        $before=$validCapture | ConvertTo-Json -Depth 8 -Compress
        $copy=[NLink.Tools.GuiSmokeLedgerStreamingResult]::new()
        if($copy.OwnsNativeCapture($validCapture) -or $valid.OwnsNativeCapture($null)) { throw 'Caller-created result adopted provenance' }
        $valid.WriterSha256='0'*64; $valid.WriterLineCount=999; $valid.Writers[0].WriterId='mutated'; $valid.Writers[0].LastSequence=999
        if(($validCapture | ConvertTo-Json -Depth 8 -Compress) -cne $before -or -not $valid.OwnsNativeCapture($validCapture)) { throw 'Mutable primary DTO rewrote native provenance' }
        $setRejected=$false
        try { $validCapture.Writer.Sha256='0'*64 } catch { $setRejected=$true }
        if(-not $setRejected) { throw 'Captured output digest has a public setter' }
        $setRejected=$false
        try { $validCapture.Writers[0]=[NLink.Tools.GuiSmokeLedgerWriterResult]::new() } catch { $setRejected=$true }
        if(-not $setRejected) { throw 'Captured writer collection is mutable' }
        $checks.Add('immutable-output-and-writer-copies'); $checks.Add('exact-result-reference-not-self-checksum')
        # Capturing an original write does not freeze a future pathname. C1e's
        # later verification must reject mutated bytes even when file ID agrees.
        $bytes=[IO.File]::ReadAllBytes($validPath); $bytes[0]=[byte][char]'E'; [IO.File]::WriteAllBytes($validPath,$bytes)
        $candidate=[NLink.Tools.LocalInputReadLease]::OpenExpected($validPath,$validCapture.Writer.Sha256,$validCapture.Writer.LineCount)
        try { try { foreach($line in $candidate.VerifyAndReadLines()) { } } catch { }; if($candidate.IsVerified) { throw 'Native provenance was promoted to current-file verification' } }
        finally { $candidate.Dispose() }
        $checks.Add('current-file-mutation-requires-independent-verification')
    }
    $checks.Add($mode)
}
foreach($type in @([NLink.Tools.GuiSmokeLedgerNativeCapture],[NLink.Tools.GuiSmokeLedgerCapturedOutput],[NLink.Tools.GuiSmokeLedgerCapturedWriter])) {
    if($type.GetConstructors().Length -ne 0) { throw 'Public provenance constructor exposed' }
}
# Test-only invoke the internal bounded assembler with an exhausted clock. This
# is fault injection, never the production capture/issuer API.
$valid.Writers[0].WriterId=$validCapture.Writers[0].WriterId; $valid.Writers[0].LastSequence=$validCapture.Writers[0].LastSequence
$identityField=[NLink.Tools.GuiSmokeLedgerNativeCapture].GetField('ownerIdentity',[Reflection.BindingFlags]'Instance,NonPublic')
if($identityField.GetValue($validCapture).GetType() -ne [object]) { throw 'Bounded capture retains the whole mutable primary result' }
$checks.Add('bounded-owner-identity-does-not-retain-primary-arrays')
$clock=[Diagnostics.Stopwatch]::StartNew(); Start-Sleep -Milliseconds 275
$assembler=[NLink.Tools.GuiSmokeLedgerNativeCapture].GetMethod('TryCreate',[Reflection.BindingFlags]'Static,NonPublic')
$args=[object[]]@([object]::new(),$valid,$validCapture.SourcePath,$validCapture.AcceptanceRunId,$validCapture.ScenarioRunId,
    $validCapture.FrozenLineCount,$validCapture.Bookmark,$validCapture.Retained,$validCapture.Writer,$validCapture.Semantic,$clock)
if($null -ne $assembler.Invoke($null,$args)) { throw 'Exhausted metadata budget admitted' }
$args[$args.Length-1]=[Diagnostics.Stopwatch]::StartNew()
if($null -eq $assembler.Invoke($null,$args)) { throw 'Budget control had another invalid precondition' }
$checks.Add('private-construction'); $checks.Add('elapsed-budget-omission')
'native-writer-capture='+(ConvertTo-Json -InputObject ([ordered]@{Checks=$checks.ToArray();PrimaryPasses=28;AdditionalSourceVisits=0
    FinalizerCompletionIssued=$false;CurrentFileVerified=$false;RetainedWorkerBound=$false;PublicationCompletionIssued=$false}) -Depth 4 -Compress)
